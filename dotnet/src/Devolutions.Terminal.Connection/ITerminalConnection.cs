namespace Devolutions.Terminal.Connection;

public interface ITerminalConnection : IAsyncDisposable
{
    event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    event EventHandler<int>? Exited;
    event EventHandler<Exception>? Faulted;

    bool IsRunning { get; }
    int Columns { get; }
    int Rows { get; }

    Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default);
    Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default);
    void Write(ReadOnlySpan<byte> data);
    void Write(string text);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    void Resize(int columns, int rows);
}

public interface IRestartableTerminalConnection : ITerminalConnection
{
    event EventHandler<TerminalExitInfo>? SessionExited;

    TerminalConnectionCapabilities Capabilities { get; }
    TerminalConnectionState State { get; }
    TerminalProcessMetadata? ProcessMetadata { get; }
    TerminalExitInfo? LastExitInfo { get; }

    Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}
