using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Microsoft.Terminal.Connection;

public sealed class AzureDeviceCodeAuthenticator : IAzureCloudShellAuthenticator
{
    private const string DeviceCodeGrantType = "device_code";
    private readonly HttpClient _httpClient;
    private readonly AzureCloudShellOptions _options;
    private readonly IAzureCloudShellTokenCache? _tokenCache;
    private readonly TimeProvider _timeProvider;

    public AzureDeviceCodeAuthenticator(
        HttpClient httpClient,
        AzureCloudShellOptions options,
        IAzureCloudShellTokenCache? tokenCache = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClientId == Guid.Empty)
        {
            throw new ArgumentException("An Azure public-client application ID is required.", nameof(options));
        }

        _httpClient = httpClient;
        _options = options;
        _tokenCache = tokenCache;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<AzureCloudShellCredential> AuthenticateAsync(
        AzureCloudShellAuthenticationCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AuthenticateCoreAsync(callbacks, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                "AuthenticationTimedOut",
                "Azure authentication timed out.",
                isTransient: true,
                innerException: ex);
        }
    }

    private async ValueTask<AzureCloudShellCredential> AuthenticateCoreAsync(
        AzureCloudShellAuthenticationCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();

        var cached = _tokenCache is null
            ? null
            : await _tokenCache.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            if (cached.ExpiresAt > _timeProvider.GetUtcNow() + _options.TokenRefreshBuffer)
            {
                return cached;
            }

            if (!string.IsNullOrWhiteSpace(cached.RefreshToken))
            {
                try
                {
                    var refreshed = await RefreshAsync(
                        cached.Tenant,
                        cached.RefreshToken,
                        cancellationToken).ConfigureAwait(false);
                    await StoreAsync(refreshed, cancellationToken).ConfigureAwait(false);
                    return refreshed;
                }
                catch (AzureCloudShellException ex)
                    when (string.Equals(ex.ServiceErrorCode, "invalid_grant", StringComparison.Ordinal))
                {
                    await _tokenCache!.ClearAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var commonToken = await AcquireByDeviceCodeAsync(callbacks, cancellationToken)
            .ConfigureAwait(false);
        var tenants = await GetTenantsAsync(commonToken.AccessToken, cancellationToken)
            .ConfigureAwait(false);
        var tenant = await SelectTenantAsync(callbacks, tenants, cancellationToken)
            .ConfigureAwait(false);

        AzureCloudShellCredential credential;
        if (!string.IsNullOrWhiteSpace(commonToken.RefreshToken))
        {
            credential = await RefreshAsync(
                tenant,
                commonToken.RefreshToken,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            credential = new AzureCloudShellCredential(
                commonToken.AccessToken,
                null,
                commonToken.ExpiresAt,
                tenant);
        }

        await StoreAsync(credential, cancellationToken).ConfigureAwait(false);
        return credential;
    }

    private async ValueTask<AzureCloudShellCredential> AcquireByDeviceCodeAsync(
        AzureCloudShellAuthenticationCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildAuthorityUri(_options.AuthorityTenant, "oauth2/devicecode"))
        {
            Content = new FormUrlEncodedContent(
            [
                new("client_id", _options.ClientId.ToString("D")),
                new("resource", _options.Environment.ManagementResource),
            ]),
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        var device = await DeserializeAsync(
            response,
            AzureCloudShellJsonContext.Default.AzureDeviceCodeResponse,
            "DeviceCodeRequestFailed",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(device.DeviceCode))
        {
            throw ProtocolError("DeviceCodeMissing", "The device authorization response did not contain a device code.");
        }

        var expiresAt = _timeProvider.GetUtcNow() +
            TimeSpan.FromSeconds(device.ExpiresIn > 0 ? device.ExpiresIn : 900);
        var verificationText = device.VerificationUri ?? device.VerificationUrl;
        _ = Uri.TryCreate(verificationText, UriKind.Absolute, out var verificationUri);
        await callbacks.ShowDeviceCodeAsync(
            new AzureDeviceCodePrompt(
                device.Message ?? "Complete Azure sign-in in your browser.",
                device.UserCode,
                verificationUri,
                expiresAt),
            cancellationToken).ConfigureAwait(false);

        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 1));
        while (_timeProvider.GetUtcNow() < expiresAt)
        {
            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            var token = await PollTokenAsync(
                device.DeviceCode,
                cancellationToken).ConfigureAwait(false);
            if (token.Error is null)
            {
                return CreateCredential(
                    token,
                    new AzureCloudShellTenant(_options.AuthorityTenant, null, null));
            }

            if (string.Equals(token.Error, "authorization_pending", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(token.Error, "slow_down", StringComparison.Ordinal))
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }

            throw TokenError(token);
        }

        throw new AzureCloudShellException(
            AzureCloudShellStage.Authentication,
            "DeviceCodeExpired",
            "The Azure device code expired before authentication completed.",
            serviceErrorCode: "expired_token");
    }

    private async ValueTask<AzureTokenResponse> PollTokenAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildAuthorityUri(_options.AuthorityTenant, "oauth2/token"))
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", DeviceCodeGrantType),
                new("client_id", _options.ClientId.ToString("D")),
                new("resource", _options.Environment.ManagementResource),
                new("code", deviceCode),
            ]),
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        return await DeserializeTokenAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<AzureCloudShellTenant>> GetTenantsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_options.Environment.ManagementEndpoint, "tenants?api-version=2020-01-01"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        var result = await DeserializeAsync(
            response,
            AzureCloudShellJsonContext.Default.AzureTenantListResponse,
            "TenantRequestFailed",
            cancellationToken).ConfigureAwait(false);
        var tenants = result.Value?
            .Select(static value => new AzureCloudShellTenant(
                value.TenantId ?? string.Empty,
                value.DisplayName,
                value.DefaultDomain))
            .Where(static tenant => !string.IsNullOrWhiteSpace(tenant.TenantId))
            .ToArray() ?? [];
        if (tenants.Length == 0)
        {
            throw ProtocolError("TenantListEmpty", "Azure did not return any accessible tenants.");
        }

        return tenants;
    }

    private static async ValueTask<AzureCloudShellTenant> SelectTenantAsync(
        AzureCloudShellAuthenticationCallbacks callbacks,
        IReadOnlyList<AzureCloudShellTenant> tenants,
        CancellationToken cancellationToken)
    {
        if (tenants.Count == 1)
        {
            return tenants[0];
        }

        if (callbacks.SelectTenantAsync is null)
        {
            throw new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                "TenantSelectionRequired",
                "The account has multiple Azure tenants, but no tenant selection callback was provided.");
        }

        var selected = await callbacks.SelectTenantAsync(tenants, cancellationToken)
            .ConfigureAwait(false);
        if (!tenants.Any(tenant =>
            string.Equals(tenant.TenantId, selected.TenantId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                "InvalidTenantSelection",
                "The tenant selection callback returned a tenant that was not offered.");
        }

        return selected;
    }

    private async ValueTask<AzureCloudShellCredential> RefreshAsync(
        AzureCloudShellTenant tenant,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildAuthorityUri(tenant.TenantId, "oauth2/token"))
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "refresh_token"),
                new("client_id", _options.ClientId.ToString("D")),
                new("resource", _options.Environment.ManagementResource),
                new("refresh_token", refreshToken),
            ]),
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        var token = await DeserializeTokenAsync(response, cancellationToken).ConfigureAwait(false);
        if (token.Error is not null)
        {
            throw TokenError(token, response.StatusCode);
        }

        return CreateCredential(token, tenant, refreshToken);
    }

    private async ValueTask<AzureTokenResponse> DeserializeTokenAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        AzureTokenResponse? token;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            token = await JsonSerializer.DeserializeAsync(
                stream,
                AzureCloudShellJsonContext.Default.AzureTokenResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw ProtocolError("InvalidTokenResponse", "Azure returned an invalid token response.", ex);
        }

        if (token is null)
        {
            throw ProtocolError("EmptyTokenResponse", "Azure returned an empty token response.");
        }

        if (!response.IsSuccessStatusCode && token.Error is null)
        {
            throw new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                "TokenRequestFailed",
                $"Azure token request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        if (token.Error is null && string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw ProtocolError("AccessTokenMissing", "The token response did not contain an access token.");
        }

        return token;
    }

    private AzureCloudShellCredential CreateCredential(
        AzureTokenResponse token,
        AzureCloudShellTenant tenant,
        string? fallbackRefreshToken = null)
    {
        var expiresAt = token.ExpiresOn > 0
            ? DateTimeOffset.FromUnixTimeSeconds(token.ExpiresOn)
            : _timeProvider.GetUtcNow() +
                TimeSpan.FromSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600);
        return new AzureCloudShellCredential(
            token.AccessToken!,
            token.RefreshToken ?? fallbackRefreshToken,
            expiresAt,
            tenant);
    }

    private async ValueTask StoreAsync(
        AzureCloudShellCredential credential,
        CancellationToken cancellationToken)
    {
        if (_tokenCache is not null)
        {
            await _tokenCache.StoreAsync(credential, cancellationToken).ConfigureAwait(false);
        }
    }

    private Uri BuildAuthorityUri(string tenant, string path)
    {
        var escapedTenant = Uri.EscapeDataString(tenant);
        return new Uri(_options.Environment.Authority, $"{escapedTenant}/{path}");
    }

    private static AzureCloudShellException TokenError(
        AzureTokenResponse token,
        HttpStatusCode? statusCode = null) =>
        new(
            AzureCloudShellStage.Authentication,
            "TokenRequestRejected",
            token.ErrorDescription ?? token.Error ?? "Azure rejected the token request.",
            statusCode,
            token.Error);

    private static AzureCloudShellException ProtocolError(
        string code,
        string message,
        Exception? innerException = null) =>
        new(
            AzureCloudShellStage.Authentication,
            code,
            message,
            innerException: innerException);

    private static async ValueTask<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string errorCode,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                errorCode,
                $"Azure request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProtocolError(errorCode, "Azure returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw ProtocolError(errorCode, "Azure returned invalid JSON.", ex);
        }
    }
}
