namespace Microsoft.Terminal.Connection;

public interface ITerminalConnection : IAsyncDisposable
{
    event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    event EventHandler<int>? Exited;

    bool IsRunning { get; }
    int Columns { get; }
    int Rows { get; }

    Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default);
    void Write(ReadOnlySpan<byte> data);
    void Write(string text);
    void Resize(int columns, int rows);
}
