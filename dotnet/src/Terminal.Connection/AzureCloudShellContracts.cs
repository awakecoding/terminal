using System.Net;
using System.Net.WebSockets;

namespace Microsoft.Terminal.Connection;

public sealed record AzureCloudShellEnvironment(
    string Name,
    Uri Authority,
    Uri ManagementEndpoint,
    string ManagementResource)
{
    public static AzureCloudShellEnvironment Public { get; } = new(
        "AzureCloud",
        new Uri("https://login.microsoftonline.com/"),
        new Uri("https://management.azure.com/"),
        "https://management.core.windows.net/");

    public static AzureCloudShellEnvironment UsGovernment { get; } = new(
        "AzureUSGovernment",
        new Uri("https://login.microsoftonline.us/"),
        new Uri("https://management.usgovcloudapi.net/"),
        "https://management.core.usgovcloudapi.net/");
}

public sealed record AzureCloudShellOptions
{
    public required Guid ClientId { get; init; }

    public AzureCloudShellEnvironment Environment { get; init; } =
        AzureCloudShellEnvironment.Public;

    public string AuthorityTenant { get; init; } = "common";

    public string? ShellType { get; init; }

    public TimeSpan TokenRefreshBuffer { get; init; } = TimeSpan.FromMinutes(45);

    public TimeSpan WebSocketKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan WebSocketKeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan WebSocketCloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaximumReconnectAttempts { get; init; } = 3;

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed record AzureDeviceCodePrompt(
    string Message,
    string? UserCode,
    Uri? VerificationUri,
    DateTimeOffset ExpiresAt);

public sealed record AzureCloudShellTenant(
    string TenantId,
    string? DisplayName,
    string? DefaultDomain);

public sealed record AzureCloudShellAuthenticationCallbacks
{
    public required Func<AzureDeviceCodePrompt, CancellationToken, ValueTask>
        ShowDeviceCodeAsync { get; init; }

    public Func<IReadOnlyList<AzureCloudShellTenant>, CancellationToken, ValueTask<AzureCloudShellTenant>>?
        SelectTenantAsync { get; init; }
}

public sealed record AzureCloudShellCredential(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    AzureCloudShellTenant Tenant)
{
    public override string ToString() =>
        $"{nameof(AzureCloudShellCredential)} {{ AccessToken = <redacted>, RefreshToken = <redacted>, ExpiresAt = {ExpiresAt:O}, Tenant = {Tenant} }}";
}

public interface IAzureCloudShellTokenCache
{
    ValueTask<AzureCloudShellCredential?> LoadAsync(CancellationToken cancellationToken);

    ValueTask StoreAsync(
        AzureCloudShellCredential credential,
        CancellationToken cancellationToken);

    ValueTask ClearAsync(CancellationToken cancellationToken);
}

public interface IAzureCloudShellAuthenticator
{
    ValueTask<AzureCloudShellCredential> AuthenticateAsync(
        AzureCloudShellAuthenticationCallbacks callbacks,
        CancellationToken cancellationToken);
}

public sealed record AzureCloudShellUserSettings(string PreferredShellType);

public sealed record AzureCloudShellTerminal(
    string Id,
    Uri CloudShellUri,
    Uri WebSocketUri,
    string ShellType,
    string? RequestId);

public interface IAzureCloudShellService
{
    ValueTask<AzureCloudShellUserSettings> GetUserSettingsAsync(
        AzureCloudShellCredential credential,
        CancellationToken cancellationToken);

    ValueTask<AzureCloudShellTerminal> ProvisionTerminalAsync(
        AzureCloudShellCredential credential,
        string shellType,
        int columns,
        int rows,
        CancellationToken cancellationToken);

    ValueTask ResizeTerminalAsync(
        AzureCloudShellCredential credential,
        AzureCloudShellTerminal terminal,
        int columns,
        int rows,
        CancellationToken cancellationToken);
}

public interface IAzureCloudShellWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }

    WebSocketCloseStatus? CloseStatus { get; }

    string? CloseStatusDescription { get; }

    ValueTask ConnectAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    ValueTask CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);

    void Abort();
}

public interface IAzureCloudShellWebSocketFactory
{
    IAzureCloudShellWebSocket Create(
        TimeSpan keepAliveInterval,
        TimeSpan keepAliveTimeout);
}

public enum AzureCloudShellStage
{
    Authentication,
    UserSettings,
    Provisioning,
    WebSocket,
    Resize,
    Reconnect,
    Lifecycle,
}

public enum AzureCloudShellDiagnosticSeverity
{
    Trace,
    Information,
    Warning,
    Error,
}

public sealed record AzureCloudShellDiagnostic(
    AzureCloudShellDiagnosticSeverity Severity,
    AzureCloudShellStage Stage,
    string Code,
    string Message,
    DateTimeOffset Timestamp,
    string? RequestId = null);

public sealed record AzureCloudShellSessionMetadata(
    Guid SessionId,
    string TerminalId,
    string TenantId,
    string ShellType,
    Uri CloudShellUri,
    string WebSocketHost,
    DateTimeOffset StartedAt);

public sealed record AzureCloudShellExitMetadata(
    AzureCloudShellSessionMetadata? Session,
    WebSocketCloseStatus? CloseStatus,
    string? CloseDescription,
    int ReconnectAttempts,
    TerminalExitReason Reason,
    DateTimeOffset ExitedAt);

public sealed record AzureCloudShellFaultMetadata(
    AzureCloudShellStage Stage,
    string Code,
    string Message,
    HttpStatusCode? StatusCode,
    string? ServiceErrorCode,
    string? RequestId,
    bool IsTransient,
    DateTimeOffset OccurredAt);

public sealed class AzureCloudShellException : Exception
{
    public AzureCloudShellException(
        AzureCloudShellStage stage,
        string code,
        string message,
        HttpStatusCode? statusCode = null,
        string? serviceErrorCode = null,
        string? requestId = null,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        Code = code;
        StatusCode = statusCode;
        ServiceErrorCode = serviceErrorCode;
        RequestId = requestId;
        IsTransient = isTransient;
    }

    public AzureCloudShellStage Stage { get; }

    public string Code { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? ServiceErrorCode { get; }

    public string? RequestId { get; }

    public bool IsTransient { get; }

    public AzureCloudShellFaultMetadata ToMetadata() => new(
        Stage,
        Code,
        Message,
        StatusCode,
        ServiceErrorCode,
        RequestId,
        IsTransient,
        DateTimeOffset.UtcNow);
}
