using System.Net.WebSockets;

namespace Microsoft.Terminal.Connection;

public sealed class AzureCloudShellWebSocketFactory : IAzureCloudShellWebSocketFactory
{
    public IAzureCloudShellWebSocket Create(
        TimeSpan keepAliveInterval,
        TimeSpan keepAliveTimeout) =>
        new AzureCloudShellWebSocket(keepAliveInterval, keepAliveTimeout);
}

internal sealed class AzureCloudShellWebSocket : IAzureCloudShellWebSocket
{
    private readonly ClientWebSocket _webSocket = new();

    public AzureCloudShellWebSocket(
        TimeSpan keepAliveInterval,
        TimeSpan keepAliveTimeout)
    {
        _webSocket.Options.KeepAliveInterval = keepAliveInterval;
        _webSocket.Options.KeepAliveTimeout = keepAliveTimeout;
    }

    public WebSocketState State => _webSocket.State;

    public WebSocketCloseStatus? CloseStatus => _webSocket.CloseStatus;

    public string? CloseStatusDescription => _webSocket.CloseStatusDescription;

    public ValueTask ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        new(_webSocket.ConnectAsync(uri, cancellationToken));

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        _webSocket.SendAsync(data, messageType, endOfMessage, cancellationToken);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken) =>
        _webSocket.ReceiveAsync(buffer, cancellationToken);

    public ValueTask CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        new(_webSocket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken));

    public void Abort() => _webSocket.Abort();

    public ValueTask DisposeAsync()
    {
        _webSocket.Dispose();
        return ValueTask.CompletedTask;
    }
}
