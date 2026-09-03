using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Devolutions.Terminal.Connection;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LinuxPtyConnection : IRestartableTerminalConnection
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Stream? _input;
    private CancellationTokenSource? _lifetime;
    private Task? _readTask;
    private Task? _stderrTask;
    private Task? _waitTask;
    private CancellationTokenRegistration _cancellationRegistration;
    private TerminalLaunchOptions? _lastOptions;
    private long _generation;
    private TerminalExitReason? _requestedExitReason;
    private bool _exitPublished;
    private bool _hasStarted;
    private bool _disposed;
    private string _stderr = string.Empty;

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;
    public event EventHandler<TerminalExitInfo>? SessionExited;
    public event EventHandler<Exception>? Faulted;

    public bool IsRunning { get; private set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public TerminalConnectionCapabilities Capabilities { get; } =
        TerminalConnectionCapabilities.Resize |
        TerminalConnectionCapabilities.Restart |
        TerminalConnectionCapabilities.ProcessMetadata;
    public TerminalConnectionState State { get; private set; } =
        TerminalConnectionState.NotConnected;
    public TerminalProcessMetadata? ProcessMetadata { get; private set; }
    public TerminalExitInfo? LastExitInfo { get; private set; }

    public Task StartAsync(
        string commandLine,
        string? workingDirectory,
        int columns,
        int rows,
        CancellationToken cancellationToken = default) =>
        StartAsync(
            new TerminalLaunchOptions
            {
                CommandLine = commandLine,
                WorkingDirectory = workingDirectory,
                Columns = columns,
                Rows = rows,
            },
            cancellationToken);

    public async Task StartAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, cancellationToken);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hasStarted)
            {
                throw new InvalidOperationException(
                    "The Unix PTY connection has already been started. Use RestartAsync.");
            }

            StartCore(options, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(
        TerminalLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var restartOptions = options ?? _lastOptions
                ?? throw new InvalidOperationException("No Unix PTY launch options are available.");
            ValidateOptions(restartOptions, cancellationToken);
            await StopCoreAsync(TerminalExitReason.Closed, cancellationToken).ConfigureAwait(false);
            StartCore(restartOptions, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync(TerminalExitReason.Closed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _writeLock.Wait();
        try
        {
            WriteFrame(GetInput(), data);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Write(Encoding.UTF8.GetBytes(text));
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var input = GetInput();
            await WriteFrameAsync(input, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, ushort.MaxValue);
        rows = Math.Clamp(rows, 1, ushort.MaxValue);
        _writeLock.Wait();
        try
        {
            var header = Encoding.ASCII.GetBytes($"R {columns} {rows}\n");
            var input = GetInput();
            input.Write(header);
            input.Flush();
            Columns = columns;
            Rows = rows;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCoreAsync(TerminalExitReason.Disposed, CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
            State = TerminalConnectionState.Disposed;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        GC.SuppressFinalize(this);
    }

    private void StartCore(TerminalLaunchOptions options, CancellationToken cancellationToken)
    {
        State = TerminalConnectionState.Connecting;
        LastExitInfo = null;
        var helper = Path.Combine(AppContext.BaseDirectory, "dt-pty-host");
        if (!File.Exists(helper))
        {
            var error = new FileNotFoundException("The Unix PTY host is missing.", helper);
            RecordStartupFailure(error);
            throw error;
        }

        var process = new Process
        {
            StartInfo = CreateStartInfo(helper, options),
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the Unix PTY host.");
            }
        }
        catch (Exception exception)
        {
            process.Dispose();
            RecordStartupFailure(exception);
            throw;
        }

        var lifetime = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _generation);
        _process = process;
        _input = process.StandardInput.BaseStream;
        _lifetime = lifetime;
        _lastOptions = options;
        _hasStarted = true;
        _exitPublished = false;
        _requestedExitReason = null;
        _stderr = string.Empty;
        Columns = Math.Clamp(options.Columns, 1, ushort.MaxValue);
        Rows = Math.Clamp(options.Rows, 1, ushort.MaxValue);
        ProcessMetadata = new TerminalProcessMetadata(
            Guid.NewGuid(),
            process.Id,
            options.CommandLine,
            options.WorkingDirectory ?? Environment.CurrentDirectory,
            DateTimeOffset.UtcNow);
        LastExitInfo = null;
        IsRunning = true;
        State = TerminalConnectionState.Connected;
        _readTask = ReadOutputAsync(process.StandardOutput.BaseStream, lifetime.Token);
        _stderrTask = ReadErrorAsync(process.StandardError, lifetime.Token);
        _waitTask = ObserveExitAsync(process, generation, options);
        _cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var cancellation = (CancellationState)state!;
                cancellation.Connection.Cancel(cancellation.Generation);
            },
            new CancellationState(this, generation));
    }

    private static ProcessStartInfo CreateStartInfo(
        string helper,
        TerminalLaunchOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Math.Clamp(options.Columns, 1, ushort.MaxValue).ToString());
        startInfo.ArgumentList.Add(Math.Clamp(options.Rows, 1, ushort.MaxValue).ToString());
        startInfo.ArgumentList.Add(options.WorkingDirectory ?? Environment.CurrentDirectory);
        startInfo.ArgumentList.Add(options.CommandLine);
        if (!options.InheritEnvironment)
        {
            startInfo.Environment.Clear();
        }

        foreach (var pair in options.EnvironmentVariables)
        {
            if (pair.Value is null)
            {
                startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        startInfo.Environment.TryAdd("TERM", "xterm-256color");
        startInfo.Environment.TryAdd("COLORTERM", "truecolor");
        return startInfo;
    }

    private async Task ReadOutputAsync(Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }

                OutputReceived?.Invoke(this, buffer.AsMemory(0, count).ToArray());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Faulted?.Invoke(this, exception);
        }
    }

    private async Task ReadErrorAsync(
        StreamReader error,
        CancellationToken cancellationToken)
    {
        try
        {
            _stderr = await error.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveExitAsync(
        Process process,
        long generation,
        TerminalLaunchOptions options)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _generation) ||
                !ReferenceEquals(process, _process))
            {
                return;
            }

            if (_readTask is not null)
            {
                await _readTask.ConfigureAwait(false);
            }

            if (_stderrTask is not null)
            {
                await _stderrTask.ConfigureAwait(false);
            }

            TerminalExitReason reason;
            lock (_stateLock)
            {
                reason = _requestedExitReason ?? TerminalExitReason.ProcessExited;
            }

            PublishExit(process.ExitCode, reason, options, generation);
            CleanupProcess(process);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void PublishExit(
        int? exitCode,
        TerminalExitReason reason,
        TerminalLaunchOptions options,
        long generation)
    {
        TerminalExitInfo exit;
        lock (_stateLock)
        {
            if (_exitPublished || generation != _generation)
            {
                return;
            }

            _exitPublished = true;
            IsRunning = false;
            State = reason == TerminalExitReason.ProcessExited && exitCode != 0
                ? TerminalConnectionState.Failed
                : TerminalConnectionState.Closed;
            exit = new TerminalExitInfo(
                ProcessMetadata,
                exitCode,
                reason,
                TerminalCloseOnExit.ShouldClose(
                    options.CloseOnExit,
                    reason,
                    exitCode,
                    options.IsDefaultTerminalSession),
                DateTimeOffset.UtcNow);
            LastExitInfo = exit;
        }

        SessionExited?.Invoke(this, exit);
        Exited?.Invoke(this, exitCode.GetValueOrDefault());
        if (exitCode is not 0 && !string.IsNullOrWhiteSpace(_stderr))
        {
            Faulted?.Invoke(
                this,
                new IOException($"Linux PTY host exited with code {exitCode}: {_stderr.Trim()}"));
        }
    }

    private async Task StopCoreAsync(
        TerminalExitReason reason,
        CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        State = TerminalConnectionState.Closing;
        var generation = _generation;
        lock (_stateLock)
        {
            _requestedExitReason ??= reason;
        }

        _cancellationRegistration.Dispose();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_input is not null)
            {
                var close = Encoding.ASCII.GetBytes("C\n");
                await _input.WriteAsync(close, cancellationToken).ConfigureAwait(false);
                await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            _writeLock.Release();
        }

        var completed = await Task.WhenAny(
            process.WaitForExitAsync(cancellationToken),
            Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
        if (!process.HasExited && completed.IsCompleted)
        {
            KillIfRunning(process);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_readTask is not null)
        {
            await _readTask.ConfigureAwait(false);
        }

        if (_stderrTask is not null)
        {
            await _stderrTask.ConfigureAwait(false);
        }

        PublishExit(
            process.HasExited ? process.ExitCode : null,
            reason,
            _lastOptions!,
            generation);
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        CleanupProcess(process);
    }

    private void CleanupProcess(Process process)
    {
        _cancellationRegistration.Dispose();
        var input = _input;
        var lifetime = _lifetime;
        lock (_stateLock)
        {
            _process = null;
            _input = null;
            _lifetime = null;
        }

        input?.Dispose();
        process.Dispose();
        lifetime?.Dispose();
        _readTask = null;
        _stderrTask = null;
        _waitTask = null;
    }

    private void RecordStartupFailure(Exception? error)
    {
        lock (_stateLock)
        {
            IsRunning = false;
            State = TerminalConnectionState.Failed;
            LastExitInfo = new TerminalExitInfo(
                null,
                null,
                TerminalExitReason.StartupFailure,
                false,
                DateTimeOffset.UtcNow);
        }

        if (error is not null)
        {
            Faulted?.Invoke(this, error);
        }
    }

    private Stream GetInput()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _input ?? throw new InvalidOperationException(
                "The Linux PTY connection is not running.");
        }

    }

    private void Cancel(long generation)
    {
        lock (_stateLock)
        {
            if (_disposed || generation != _generation || _process is null)
            {
                return;
            }

            _requestedExitReason = TerminalExitReason.Cancelled;
            _lifetime?.Cancel();
            KillIfRunning(_process);
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
    }

    private static void WriteFrame(Stream input, ReadOnlySpan<byte> data)
    {
        var header = Encoding.ASCII.GetBytes($"D {data.Length}\n");
        input.Write(header);
        input.Write(data);
        input.Flush();
    }

    private static async Task WriteFrameAsync(
        Stream input,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes($"D {data.Length}\n");
        await input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOptions(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CommandLine);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux PTY requires Linux.");
        }

        if (options.Columns is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TerminalLaunchOptions.Columns),
                "Columns must be between 1 and 65535.");
        }

        if (options.Rows is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TerminalLaunchOptions.Rows),
                "Rows must be between 1 and 65535.");
        }
    }

    private sealed record CancellationState(LinuxPtyConnection Connection, long Generation);
}
