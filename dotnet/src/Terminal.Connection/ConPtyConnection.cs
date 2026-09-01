using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Terminal.Connection.Native;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Terminal.Connection;

[SupportedOSPlatform("windows")]
public sealed class ConPtyConnection : IRestartableTerminalConnection
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SessionResources? _session;
    private TerminalLaunchOptions? _lastOptions;
    private long _generation;
    private bool _hasStarted;
    private bool _disposed;

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
        TerminalConnectionCapabilities.ProcessMetadata |
        TerminalConnectionCapabilities.WslPathTranslation;
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
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_hasStarted)
                {
                    throw new InvalidOperationException(
                        "The ConPTY connection has already been started. Use RestartAsync to replace its session.");
                }
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
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var restartOptions = options ?? _lastOptions
                ?? throw new InvalidOperationException("No previous ConPTY launch options are available.");
            ValidateOptions(restartOptions, cancellationToken);
            await StopCoreAsync(TerminalExitReason.Closed).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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
            await StopCoreAsync(TerminalExitReason.Closed).ConfigureAwait(false);
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
            var stream = GetWritableStream();
            stream.Write(data);
            stream.Flush();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0)
        {
            Write(Encoding.UTF8.GetBytes(text));
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        CancellationToken lifetimeToken;
        lock (_stateLock)
        {
            lifetimeToken = _session?.Lifetime.Token ?? CancellationToken.None;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        await _writeLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            var stream = GetWritableStream();
            await stream.WriteAsync(data, linkedCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        var validatedColumns = ValidateDimension(columns, nameof(columns));
        var validatedRows = ValidateDimension(rows, nameof(rows));
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pseudoConsole = _session?.PseudoConsole;
            if (pseudoConsole is not null && !pseudoConsole.IsInvalid && !pseudoConsole.IsClosed)
            {
                var size = new Kernel32.Coord { X = (short)validatedColumns, Y = (short)validatedRows };
                var hr = Kernel32.ResizePseudoConsole(pseudoConsole, size);
                if (hr != 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }

            Columns = validatedColumns;
            Rows = validatedRows;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            await StopCoreAsync(TerminalExitReason.Disposed).ConfigureAwait(false);
            lock (_stateLock)
            {
                State = TerminalConnectionState.Disposed;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StartCore(TerminalLaunchOptions options, CancellationToken cancellationToken)
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeKernelObjectHandle? process = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;
        CancellationTokenSource? lifetime = null;

        lock (_stateLock)
        {
            State = TerminalConnectionState.Connecting;
            LastExitInfo = null;
        }

        try
        {
            CreatePipe(out inputRead, out inputWrite);
            CreatePipe(out outputRead, out outputWrite);

            var size = new Kernel32.Coord
            {
                X = (short)options.Columns,
                Y = (short)options.Rows,
            };
            var hr = Kernel32.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsoleValue);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleValue);
            var processResult = StartProcess(options, pseudoConsole);
            process = processResult.Handle;

            inputStream = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false);
            inputWrite = null;
            outputStream = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
            outputRead = null;
            lifetime = new CancellationTokenSource();

            var generation = ++_generation;
            var metadata = new TerminalProcessMetadata(
                Guid.NewGuid(),
                processResult.ProcessId,
                options.CommandLine,
                ResolveWorkingDirectory(options.WorkingDirectory),
                DateTimeOffset.UtcNow);
            var session = new SessionResources(
                generation,
                options,
                metadata,
                pseudoConsole,
                process,
                inputStream,
                outputStream,
                lifetime);

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _session = session;
                _lastOptions = options;
                _hasStarted = true;
                ProcessMetadata = metadata;
                Columns = options.Columns;
                Rows = options.Rows;
                IsRunning = true;
                State = TerminalConnectionState.Connected;
            }

            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;
            pseudoConsole = null;
            process = null;
            inputStream = null;
            outputStream = null;
            lifetime = null;

            session.ReadTask = Task.Run(() => ReadLoop(session));
            session.WaitTask = Task.Run(() => WaitLoop(session));
            session.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var registration = (CancellationState)state!;
                    registration.Connection.Cancel(registration.Generation);
                },
                new CancellationState(this, generation));
        }
        catch (Exception ex)
        {
            var exit = new TerminalExitInfo(
                null,
                null,
                TerminalExitReason.StartupFailure,
                false,
                DateTimeOffset.UtcNow);
            lock (_stateLock)
            {
                IsRunning = false;
                State = TerminalConnectionState.Failed;
                LastExitInfo = exit;
            }

            Faulted?.Invoke(this, ex);
            throw;
        }
        finally
        {
            inputStream?.Dispose();
            outputStream?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            process?.Dispose();
            pseudoConsole?.Dispose();
            lifetime?.Dispose();
        }
    }

    private async Task StopCoreAsync(TerminalExitReason reason)
    {
        SessionResources? session;
        lock (_stateLock)
        {
            session = _session;
            if (session is null)
            {
                IsRunning = false;
                if (!_disposed)
                {
                    State = TerminalConnectionState.Closed;
                }

                return;
            }

            session.RequestedExitReason ??= reason;
            IsRunning = false;
            State = TerminalConnectionState.Closing;
        }

        session.CancellationRegistration.Dispose();
        await session.Lifetime.CancelAsync().ConfigureAwait(false);
        if (!session.Process.IsInvalid &&
            !session.Process.IsClosed &&
            session.WaitTask?.IsCompleted != true)
        {
            _ = Kernel32.TerminateProcess(session.Process, 0);
        }

        var taskErrors = await CleanupSessionResourcesAsync(
            session,
            observeWaitTask: true,
            drainOutput: false).ConfigureAwait(false);
        lock (_stateLock)
        {
            if (!_disposed && State == TerminalConnectionState.Closing)
            {
                State = session.RequestedExitReason == TerminalExitReason.ConnectionFailure
                    ? TerminalConnectionState.Failed
                    : TerminalConnectionState.Closed;
            }
        }

        if (taskErrors.Count > 0)
        {
            throw new AggregateException("ConPTY background task failed.", taskErrors);
        }
    }

    private void Cancel(long generation)
    {
        SessionResources? session;
        lock (_stateLock)
        {
            session = _session;
            if (_disposed || session?.Generation != generation)
            {
                return;
            }

            session.RequestedExitReason = TerminalExitReason.Cancelled;
        }

        session.Lifetime.Cancel();
        if (!session.Process.IsInvalid && !session.Process.IsClosed)
        {
            _ = Kernel32.TerminateProcess(session.Process, 0);
        }
    }

    private FileStream GetWritableStream()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRunning || _session is null)
            {
                throw new InvalidOperationException("The ConPTY connection is not running.");
            }

            return _session.Input;
        }
    }

    private void ReadLoop(SessionResources session)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!session.Lifetime.IsCancellationRequested)
            {
                var read = session.Output.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                OutputReceived?.Invoke(this, buffer.AsMemory(0, read).ToArray());
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (session.Lifetime.IsCancellationRequested || _disposed)
        {
        }
        catch (IOException) when (session.Lifetime.IsCancellationRequested || _disposed)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PublishFault(session, ex);
        }
    }

    private void WaitLoop(SessionResources session)
    {
        var waitResult = Kernel32.WaitForSingleObject(session.Process, Kernel32.Infinite);
        if (waitResult == Kernel32.WaitFailed)
        {
            PublishFault(session, new Win32Exception(Marshal.GetLastPInvokeError()));
            return;
        }

        if (!Kernel32.GetExitCodeProcess(session.Process, out var code))
        {
            PublishFault(session, new Win32Exception(Marshal.GetLastPInvokeError()));
            return;
        }

        TerminalExitInfo exit;
        lock (_stateLock)
        {
            if (session.ExitPublished)
            {
                return;
            }

            session.ExitPublished = true;
            var reason = session.RequestedExitReason ?? TerminalExitReason.ProcessExited;
            var exitCode = unchecked((int)code);
            exit = new TerminalExitInfo(
                session.Metadata,
                exitCode,
                reason,
                TerminalCloseOnExit.ShouldClose(
                    session.Options.CloseOnExit,
                    reason,
                    exitCode,
                    session.Options.IsDefaultTerminalSession),
                DateTimeOffset.UtcNow);
            if (ReferenceEquals(_session, session))
            {
                IsRunning = false;
                State = reason == TerminalExitReason.ProcessExited && exitCode != 0
                    ? TerminalConnectionState.Failed
                    : TerminalConnectionState.Closed;
                LastExitInfo = exit;
            }
        }

        _ = CleanupCompletedSessionAsync(session);
        SessionExited?.Invoke(this, exit);
        Exited?.Invoke(this, exit.ExitCode.GetValueOrDefault());
    }

    private void PublishFault(SessionResources session, Exception exception)
    {
        TerminalExitInfo exit;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_session, session) || session.ExitPublished)
            {
                return;
            }

            session.ExitPublished = true;
            session.RequestedExitReason = TerminalExitReason.ConnectionFailure;
            exit = new TerminalExitInfo(
                session.Metadata,
                null,
                TerminalExitReason.ConnectionFailure,
                TerminalCloseOnExit.ShouldClose(
                    session.Options.CloseOnExit,
                    TerminalExitReason.ConnectionFailure,
                    null,
                    session.Options.IsDefaultTerminalSession),
                DateTimeOffset.UtcNow);
            IsRunning = false;
            State = TerminalConnectionState.Failed;
            LastExitInfo = exit;
        }

        _ = CleanupFailedSessionAsync(session);
        SessionExited?.Invoke(this, exit);
        Faulted?.Invoke(this, exception);
    }

    private async Task CleanupCompletedSessionAsync(SessionResources session)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }
            }

            var errors = await CleanupSessionResourcesAsync(
                session,
                observeWaitTask: false,
                drainOutput: true).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                Faulted?.Invoke(
                    this,
                    new AggregateException("ConPTY completed-session cleanup failed.", errors));
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task CleanupFailedSessionAsync(SessionResources session)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }
            }

            await session.Lifetime.CancelAsync().ConfigureAwait(false);
            if (!session.Process.IsInvalid &&
                !session.Process.IsClosed &&
                session.WaitTask?.IsCompleted != true)
            {
                _ = Kernel32.TerminateProcess(session.Process, 1);
            }

            var errors = await CleanupSessionResourcesAsync(
                session,
                observeWaitTask: true,
                drainOutput: false).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                Faulted?.Invoke(
                    this,
                    new AggregateException("ConPTY failed-session cleanup failed.", errors));
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<List<Exception>> CleanupSessionResourcesAsync(
        SessionResources session,
        bool observeWaitTask,
        bool drainOutput)
    {
        session.CancellationRegistration.Dispose();
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            session.Input.Dispose();
        }
        finally
        {
            _writeLock.Release();
        }

        var taskErrors = new List<Exception>();
        if (drainOutput)
        {
            if (session.ReadTask is not null)
            {
                _ = await Task.WhenAny(
                    session.ReadTask,
                    Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);
            }

            await session.Lifetime.CancelAsync().ConfigureAwait(false);
            session.Output.Dispose();
            session.PseudoConsole.Dispose();
            await ObserveAsync(session.ReadTask, taskErrors).ConfigureAwait(false);
        }
        else
        {
            await session.Lifetime.CancelAsync().ConfigureAwait(false);
            session.Output.Dispose();
            session.PseudoConsole.Dispose();
            await ObserveAsync(session.ReadTask, taskErrors).ConfigureAwait(false);
        }

        if (observeWaitTask)
        {
            await ObserveAsync(session.WaitTask, taskErrors).ConfigureAwait(false);
        }

        session.Process.Dispose();
        session.Lifetime.Dispose();
        lock (_stateLock)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }

        return taskErrors;
    }

    private static ProcessResult StartProcess(
        TerminalLaunchOptions options,
        SafePseudoConsoleHandle pseudoConsole)
    {
        using var attributes = SafeProcThreadAttributeList.Create();
        if (!Kernel32.UpdateProcThreadAttribute(
                attributes.DangerousGetHandle(),
                0,
                Kernel32.ProcThreadAttributePseudoConsole,
                pseudoConsole.DangerousGetHandle(),
                nint.Size,
                0,
                0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var startup = new Kernel32.StartupInfoExW
        {
            StartupInfo = new Kernel32.StartupInfoW
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfoExW>(),
                dwFlags = Kernel32.StartfUseStdHandles,
            },
            lpAttributeList = attributes.DangerousGetHandle(),
        };
        var processSecurity = new Kernel32.SecurityAttributes
        {
            nLength = Marshal.SizeOf<Kernel32.SecurityAttributes>(),
        };
        var threadSecurity = processSecurity;
        var commandBuffer = (options.CommandLine + '\0').ToCharArray();
        var currentDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? null
            : Environment.ExpandEnvironmentVariables(options.WorkingDirectory);
        var environment = CreateEnvironmentBlock(options);
        try
        {
            if (!Kernel32.CreateProcessW(
                    null,
                    ref commandBuffer[0],
                    ref processSecurity,
                    ref threadSecurity,
                    false,
                    Kernel32.ExtendedStartupinfoPresent | Kernel32.CreateUnicodeEnvironment,
                    environment,
                    currentDirectory,
                    ref startup,
                    out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            using var thread = new SafeKernelObjectHandle(info.hThread);
            return new ProcessResult(
                new SafeKernelObjectHandle(info.hProcess),
                info.dwProcessId);
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    private static nint CreateEnvironmentBlock(TerminalLaunchOptions options)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.InheritEnvironment)
        {
            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                if (pair.Key is string key && pair.Value is string value)
                {
                    variables[key] = value;
                }
            }
        }

        foreach (var pair in options.EnvironmentVariables)
        {
            if (pair.Value is null)
            {
                variables.Remove(pair.Key);
            }
            else
            {
                variables[pair.Key] = pair.Value;
            }
        }

        var builder = new StringBuilder();
        foreach (var pair in variables)
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!Kernel32.CreatePipe(out read, out write, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void ValidateOptions(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CommandLine);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ConPTY requires Windows.");
        }

        _ = ValidateDimension(options.Columns, nameof(options.Columns));
        _ = ValidateDimension(options.Rows, nameof(options.Rows));
    }

    private static int ValidateDimension(int value, string parameterName)
    {
        if (value is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"ConPTY dimensions must be between 1 and {short.MaxValue}.");
        }

        return value;
    }

    private static string ResolveWorkingDirectory(string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(workingDirectory));

    private static async Task ObserveAsync(Task? task, ICollection<Exception> errors)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private sealed class SessionResources(
        long generation,
        TerminalLaunchOptions options,
        TerminalProcessMetadata metadata,
        SafePseudoConsoleHandle pseudoConsole,
        SafeKernelObjectHandle process,
        FileStream input,
        FileStream output,
        CancellationTokenSource lifetime)
    {
        public long Generation { get; } = generation;
        public TerminalLaunchOptions Options { get; } = options;
        public TerminalProcessMetadata Metadata { get; } = metadata;
        public SafePseudoConsoleHandle PseudoConsole { get; } = pseudoConsole;
        public SafeKernelObjectHandle Process { get; } = process;
        public FileStream Input { get; } = input;
        public FileStream Output { get; } = output;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public Task? ReadTask { get; set; }
        public Task? WaitTask { get; set; }
        public TerminalExitReason? RequestedExitReason { get; set; }
        public bool ExitPublished { get; set; }
    }

    private sealed record ProcessResult(SafeKernelObjectHandle Handle, int ProcessId);
    private sealed record CancellationState(ConPtyConnection Connection, long Generation);
}
