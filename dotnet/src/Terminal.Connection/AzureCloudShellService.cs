using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Microsoft.Terminal.Connection;

public sealed class AzureCloudShellService : IAzureCloudShellService
{
    private const string CloudShellApiVersion = "2025-09-01-preview";
    private readonly HttpClient _httpClient;
    private readonly AzureCloudShellOptions _options;

    public AzureCloudShellService(HttpClient httpClient, AzureCloudShellOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
    }

    public async ValueTask<AzureCloudShellUserSettings> GetUserSettingsAsync(
        AzureCloudShellCredential credential,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            new Uri(
                _options.Environment.ManagementEndpoint,
                $"providers/Microsoft.Portal/userSettings/cloudconsole?api-version={CloudShellApiVersion}"),
            credential);
        using var response = await SendAsync(
            request,
            AzureCloudShellStage.UserSettings,
            "UserSettingsRequestFailed",
            cancellationToken).ConfigureAwait(false);
        var settings = await DeserializeAsync(
            response,
            AzureCloudShellJsonContext.Default.AzureCloudShellSettingsResponse,
            AzureCloudShellStage.UserSettings,
            "InvalidUserSettingsResponse",
            cancellationToken).ConfigureAwait(false);
        ThrowIfServiceError(
            settings.Error,
            AzureCloudShellStage.UserSettings,
            response.StatusCode,
            GetRequestId(response));
        return new AzureCloudShellUserSettings(
            string.IsNullOrWhiteSpace(settings.Properties?.PreferredShellType)
                ? "pwsh"
                : settings.Properties.PreferredShellType);
    }

    public async ValueTask<AzureCloudShellTerminal> ProvisionTerminalAsync(
        AzureCloudShellCredential credential,
        string shellType,
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        ValidateDimensions(columns, rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(shellType);

        using var consoleRequest = CreateAuthorizedRequest(
            HttpMethod.Put,
            new Uri(
                _options.Environment.ManagementEndpoint,
                $"providers/Microsoft.Portal/consoles/default?api-version={CloudShellApiVersion}"),
            credential);
        consoleRequest.Content = JsonContent.Create(
            new AzureCloudShellConsoleRequest(new AzureCloudShellConsoleRequestProperties("linux")),
            AzureCloudShellJsonContext.Default.AzureCloudShellConsoleRequest);
        using var consoleResponse = await SendAsync(
            consoleRequest,
            AzureCloudShellStage.Provisioning,
            "ConsoleProvisioningFailed",
            cancellationToken).ConfigureAwait(false);
        var cloudConsole = await DeserializeAsync(
            consoleResponse,
            AzureCloudShellJsonContext.Default.AzureCloudShellConsoleResponse,
            AzureCloudShellStage.Provisioning,
            "InvalidConsoleResponse",
            cancellationToken).ConfigureAwait(false);
        var requestId = GetRequestId(consoleResponse);
        ThrowIfServiceError(
            cloudConsole.Error,
            AzureCloudShellStage.Provisioning,
            consoleResponse.StatusCode,
            requestId);
        if (!Uri.TryCreate(cloudConsole.Properties?.Uri, UriKind.Absolute, out var cloudShellUri))
        {
            throw ProtocolError(
                AzureCloudShellStage.Provisioning,
                "CloudShellUriMissing",
                "Azure did not return a Cloud Shell service URI.",
                requestId);
        }

        cloudShellUri = EnsureTrailingSlash(cloudShellUri);
        var terminalUri = new Uri(
            cloudShellUri,
            $"terminals?cols={columns}&rows={rows}&version=2019-01-01&shell={Uri.EscapeDataString(shellType)}");
        using var terminalRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            terminalUri,
            credential);
        terminalRequest.Headers.Referrer = cloudShellUri;
        terminalRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var terminalResponseMessage = await SendAsync(
            terminalRequest,
            AzureCloudShellStage.Provisioning,
            "TerminalProvisioningFailed",
            cancellationToken).ConfigureAwait(false);
        var terminalResponse = await DeserializeAsync(
            terminalResponseMessage,
            AzureCloudShellJsonContext.Default.AzureCloudShellTerminalResponse,
            AzureCloudShellStage.Provisioning,
            "InvalidTerminalResponse",
            cancellationToken).ConfigureAwait(false);
        requestId ??= GetRequestId(terminalResponseMessage);
        ThrowIfServiceError(
            terminalResponse.Error,
            AzureCloudShellStage.Provisioning,
            terminalResponseMessage.StatusCode,
            requestId);
        if (string.IsNullOrWhiteSpace(terminalResponse.Id))
        {
            throw ProtocolError(
                AzureCloudShellStage.Provisioning,
                "TerminalIdMissing",
                "Azure did not return a terminal identifier.",
                requestId);
        }

        var webSocketUri = BuildWebSocketUri(
            cloudShellUri,
            terminalResponse.Id,
            terminalResponse.SocketUri,
            requestId);
        return new AzureCloudShellTerminal(
            terminalResponse.Id,
            cloudShellUri,
            webSocketUri,
            shellType,
            requestId);
    }

    public async ValueTask ResizeTerminalAsync(
        AzureCloudShellCredential credential,
        AzureCloudShellTerminal terminal,
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        ValidateDimensions(columns, rows);
        ArgumentNullException.ThrowIfNull(terminal);
        var uri = new Uri(
            terminal.CloudShellUri,
            $"terminals/{Uri.EscapeDataString(terminal.Id)}/size?cols={columns}&rows={rows}&version=2019-01-01");
        using var request = CreateAuthorizedRequest(HttpMethod.Post, uri, credential);
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        using var response = await SendAsync(
            request,
            AzureCloudShellStage.Resize,
            "TerminalResizeFailed",
            cancellationToken).ConfigureAwait(false);
    }

    internal static Uri BuildWebSocketUri(
        Uri cloudShellUri,
        string terminalId,
        string? socketUri,
        string? requestId = null)
    {
        if (!cloudShellUri.Host.Contains("servicebus", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(cloudShellUri)
            {
                Scheme = Uri.UriSchemeWss,
                Port = -1,
                Path = $"{cloudShellUri.AbsolutePath.TrimEnd('/')}/terminals/{Uri.EscapeDataString(terminalId)}",
            };
            return builder.Uri;
        }

        if (!Uri.TryCreate(socketUri, UriKind.Absolute, out var serviceBusUri))
        {
            throw ProtocolError(
                AzureCloudShellStage.Provisioning,
                "SocketUriMissing",
                "Azure did not return the Service Bus socket URI required by this Cloud Shell instance.",
                requestId);
        }

        var segments = serviceBusUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw ProtocolError(
                AzureCloudShellStage.Provisioning,
                "SocketUriInvalid",
                "Azure returned an invalid Service Bus socket URI.",
                requestId);
        }

        var serviceBusBuilder = new UriBuilder(serviceBusUri)
        {
            Scheme = Uri.UriSchemeWss,
            Port = -1,
            Query = string.Empty,
            Fragment = string.Empty,
            Path = $"/$hc/{segments[0]}/terminals/{Uri.EscapeDataString(terminalId)}",
        };
        return serviceBusBuilder.Uri;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        Uri uri,
        AzureCloudShellCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0) Terminal/1.0");
        return request;
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        AzureCloudShellStage stage,
        string code,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AzureCloudShellException(
                stage,
                code,
                $"Azure Cloud Shell request failed: {ex.Message}",
                ex.StatusCode,
                isTransient: true,
                innerException: ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AzureCloudShellException(
                stage,
                code,
                "Azure Cloud Shell request timed out.",
                isTransient: true,
                innerException: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var statusCode = response.StatusCode;
        var requestId = GetRequestId(response);
        AzureServiceErrorResponse? error = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            error = await JsonSerializer.DeserializeAsync(
                stream,
                AzureCloudShellJsonContext.Default.AzureServiceErrorResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }
        finally
        {
            response.Dispose();
        }

        var (serviceCode, message) = ReadServiceError(error?.Error ?? default);
        throw new AzureCloudShellException(
            stage,
            code,
            error?.ErrorDescription ??
                message ??
                $"Azure Cloud Shell request failed with HTTP {(int)statusCode}.",
            statusCode,
            serviceCode,
            requestId,
            IsTransient(statusCode));
    }

    private static async ValueTask<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        AzureCloudShellStage stage,
        string code,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProtocolError(stage, code, "Azure returned an empty response.", GetRequestId(response));
        }
        catch (JsonException ex)
        {
            throw new AzureCloudShellException(
                stage,
                code,
                "Azure returned invalid JSON.",
                response.StatusCode,
                requestId: GetRequestId(response),
                innerException: ex);
        }
    }

    private static void ThrowIfServiceError(
        JsonElement error,
        AzureCloudShellStage stage,
        HttpStatusCode statusCode,
        string? requestId)
    {
        var (code, message) = ReadServiceError(error);
        if (code is null && message is null)
        {
            return;
        }

        throw new AzureCloudShellException(
            stage,
            "ServiceRejectedRequest",
            message ?? code ?? "Azure rejected the Cloud Shell request.",
            statusCode,
            code,
            requestId,
            IsTransient(statusCode));
    }

    private static (string? Code, string? Message) ReadServiceError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            var value = error.GetString();
            return (value, value);
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? code = null;
        string? message = null;
        if (error.TryGetProperty("code", out var codeElement) &&
            codeElement.ValueKind == JsonValueKind.String)
        {
            code = codeElement.GetString();
        }

        if (error.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString();
        }

        return (code, message);
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    private static void ValidateDimensions(int columns, int rows)
    {
        if (columns is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (rows is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }
    }

    private static string? GetRequestId(HttpResponseMessage response) =>
        TryGetHeader(response, "x-ms-request-id") ??
        TryGetHeader(response, "x-ms-correlation-request-id") ??
        TryGetHeader(response, "request-id");

    private static string? TryGetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static AzureCloudShellException ProtocolError(
        AzureCloudShellStage stage,
        string code,
        string message,
        string? requestId = null) =>
        new(stage, code, message, requestId: requestId);
}
