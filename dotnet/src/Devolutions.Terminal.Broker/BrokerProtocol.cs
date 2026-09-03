using System.Text.Json.Serialization;

namespace Devolutions.Terminal.Broker;

public static class BrokerProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 1024 * 1024;

    public static IReadOnlyList<string> Capabilities { get; } =
    [
        "activation.v1",
        "window-id",
        "window-name",
        "target:use-new",
        "target:use-any",
        "target:use-existing",
        "actions.v1",
    ];
}

public enum BrokerStatus
{
    Success,
    InvalidRequest,
    Unauthorized,
    VersionMismatch,
    WindowNotFound,
    Unsupported,
    Failed,
    Unavailable,
}

public sealed record BrokerEndpoint(
    int ProtocolVersion,
    string PipeName,
    string AuthenticationToken,
    int ProcessId);

public sealed record BrokerRequest(
    int ProtocolVersion,
    string RequestId,
    string UserIdentity,
    string AuthenticationToken,
    string TargetWindow,
    string Payload);

public sealed record BrokerResponse(
    int ProtocolVersion,
    string RequestId,
    BrokerStatus Status,
    string Message,
    int? WindowId = null,
    string? WindowName = null,
    IReadOnlyList<string>? Capabilities = null)
{
    public bool IsSuccess => Status == BrokerStatus.Success;

    public static BrokerResponse Unavailable(string message) =>
        new(BrokerProtocol.Version, string.Empty, BrokerStatus.Unavailable, message);
}

public sealed record BrokerDispatchResult(
    BrokerStatus Status,
    string Message,
    int? WindowId = null,
    string? WindowName = null);

public interface IBrokerRequestHandler
{
    ValueTask<BrokerDispatchResult> HandleAsync(
        string targetWindow,
        string payload,
        CancellationToken cancellationToken);
}

[JsonSerializable(typeof(BrokerEndpoint))]
[JsonSerializable(typeof(BrokerRequest))]
[JsonSerializable(typeof(BrokerResponse))]
internal sealed partial class BrokerJsonContext : JsonSerializerContext;
