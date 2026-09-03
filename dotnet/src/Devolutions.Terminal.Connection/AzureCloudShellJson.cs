using System.Text.Json;
using System.Text.Json.Serialization;

namespace Devolutions.Terminal.Connection;

internal sealed record AzureDeviceCodeResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; init; }

    [JsonPropertyName("user_code")]
    public string? UserCode { get; init; }

    [JsonPropertyName("verification_url")]
    public string? VerificationUrl { get; init; }

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; init; }

    [JsonPropertyName("interval")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Interval { get; init; }

    [JsonPropertyName("expires_in")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ExpiresIn { get; init; }
}

internal sealed record AzureTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long ExpiresIn { get; init; }

    [JsonPropertyName("expires_on")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long ExpiresOn { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

internal sealed record AzureTenantListResponse
{
    [JsonPropertyName("value")]
    public List<AzureTenantResponse>? Value { get; init; }
}

internal sealed record AzureTenantResponse
{
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("defaultDomain")]
    public string? DefaultDomain { get; init; }
}

internal sealed record AzureCloudShellSettingsResponse
{
    [JsonPropertyName("properties")]
    public AzureCloudShellSettingsProperties? Properties { get; init; }

    [JsonPropertyName("error")]
    public JsonElement Error { get; init; }
}

internal sealed record AzureCloudShellSettingsProperties
{
    [JsonPropertyName("preferredShellType")]
    public string? PreferredShellType { get; init; }
}

internal sealed record AzureCloudShellConsoleRequest(
    [property: JsonPropertyName("properties")] AzureCloudShellConsoleRequestProperties Properties);

internal sealed record AzureCloudShellConsoleRequestProperties(
    [property: JsonPropertyName("osType")] string OsType);

internal sealed record AzureCloudShellConsoleResponse
{
    [JsonPropertyName("properties")]
    public AzureCloudShellConsoleResponseProperties? Properties { get; init; }

    [JsonPropertyName("error")]
    public JsonElement Error { get; init; }
}

internal sealed record AzureCloudShellConsoleResponseProperties
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

internal sealed record AzureCloudShellTerminalResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("socketUri")]
    public string? SocketUri { get; init; }

    [JsonPropertyName("error")]
    public JsonElement Error { get; init; }
}

internal sealed record AzureServiceErrorResponse
{
    [JsonPropertyName("error")]
    public JsonElement Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AzureDeviceCodeResponse))]
[JsonSerializable(typeof(AzureTokenResponse))]
[JsonSerializable(typeof(AzureTenantListResponse))]
[JsonSerializable(typeof(AzureCloudShellSettingsResponse))]
[JsonSerializable(typeof(AzureCloudShellConsoleRequest))]
[JsonSerializable(typeof(AzureCloudShellConsoleResponse))]
[JsonSerializable(typeof(AzureCloudShellTerminalResponse))]
[JsonSerializable(typeof(AzureServiceErrorResponse))]
internal partial class AzureCloudShellJsonContext : JsonSerializerContext;
