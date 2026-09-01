using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Terminal.Connection.Native;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Terminal.Connection;

[SupportedOSPlatform("windows")]
public sealed class ConPtyConnection : ITerminalConnection
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SafePseudoConsoleHandle? _pseudoConsole;
    private SafeKernelObjectHandle? _process;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _readTask;
    private Task? _waitTask;
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _started;
    private bool _disposed;

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;
    public event EventHandler<Exception>? Faulted;

    public bool IsRunning { get; private set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }

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

    public Task StartAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CommandLine);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ConPTY requires Windows.");
        }

        var validatedColumns = ValidateDimension(options.Columns, nameof(options.Columns));
        var validatedRows = ValidateDimension(options.Rows, nameof(options.Rows));
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("A ConPTY connection can only be started once.");
            }

            _started = true;
        }

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeKernelObjectHandle? process = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;

        try
        {
            CreatePipe(out inputRead, out inputWrite);
            CreatePipe(out outputRead, out outputWrite);

            var size = new Kernel32.Coord { X = (short)validatedColumns, Y = (short)validatedRows };
            var hr = Kernel32.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsoleValue);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleValue);
            process = StartProcess(options, pseudoConsole);

            inputStream = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false);
            inputWrite = null;
            outputStream = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
            outputRead = null;

            var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _pseudoConsole = pseudoConsole;
                _process = process;
                _inputStream = inputStream;
                _outputStream = outputStream;
                _lifetimeCts = lifetimeCts;
                Columns = validatedColumns;
                Rows = validatedRows;
                IsRunning = true;
            }

            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;
            pseudoConsole = null;
            process = null;
            inputStream = null;
            outputStream = null;

            var processForWait = _process
                ?? throw new InvalidOperationException("The ConPTY process was not initialized.");
            var outputForRead = _outputStream
                ?? throw new InvalidOperationException("The ConPTY output stream was not initialized.");
            _readTask = Task.Factory.StartNew(
                () => ReadLoop(outputForRead, lifetimeCts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _waitTask = Task.Factory.StartNew(
                () => WaitLoop(processForWait),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _cancellationRegistration = cancellationToken.Register(
                static state => ((ConPtyConnection)state!).Cancel(),
                this);
            return Task.CompletedTask;
        }
        catch
        {
            lock (_stateLock)
            {
                _started = false;
            }

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

        var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
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
            if (_pseudoConsole is not null && !_pseudoConsole.IsInvalid && !_pseudoConsole.IsClosed)
            {
                var size = new Kernel32.Coord { X = (short)validatedColumns, Y = (short)validatedRows };
                var hr = Kernel32.ResizePseudoConsole(_pseudoConsole, size);
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
        SafePseudoConsoleHandle? pseudoConsole;
        SafeKernelObjectHandle? process;
        FileStream? inputStream;
        FileStream? outputStream;
        CancellationTokenSource? lifetimeCts;
        Task? readTask;
        Task? waitTask;
        CancellationTokenRegistration cancellationRegistration;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IsRunning = false;
            pseudoConsole = _pseudoConsole;
            process = _process;
            inputStream = _inputStream;
            outputStream = _outputStream;
            lifetimeCts = _lifetimeCts;
            readTask = _readTask;
            waitTask = _waitTask;
            cancellationRegistration = _cancellationRegistration;
            _pseudoConsole = null;
            _process = null;
            _inputStream = null;
            _outputStream = null;
            _lifetimeCts = null;
            _readTask = null;
            _waitTask = null;
            _cancellationRegistration = default;
        }

        cancellationRegistration.Dispose();
        if (lifetimeCts is not null)
        {
            await lifetimeCts.CancelAsync().ConfigureAwait(false);
        }

        if (process is not null && !process.IsInvalid && !process.IsClosed && waitTask?.IsCompleted != true)
        {
            _ = Kernel32.TerminateProcess(process, 0);
        }

        inputStream?.Dispose();
        await _writeLock.WaitAsync().ConfigureAwait(false);
        _writeLock.Release();
        outputStream?.Dispose();
        pseudoConsole?.Dispose();

        var taskErrors = new List<Exception>();
        try
        {
            await ObserveAsync(readTask, taskErrors).ConfigureAwait(false);
            await ObserveAsync(waitTask, taskErrors).ConfigureAwait(false);
        }
        finally
        {
            process?.Dispose();
            lifetimeCts?.Dispose();
            _writeLock.Dispose();
        }

        if (taskErrors.Count > 0)
        {
            throw new AggregateException("ConPTY background task failed.", taskErrors);
        }
    }

    private void Cancel()
    {
        SafeKernelObjectHandle? process;
        CancellationTokenSource? lifetimeCts;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            process = _process;
            lifetimeCts = _lifetimeCts;
        }

        lifetimeCts?.Cancel();
        if (process is not null && !process.IsInvalid && !process.IsClosed)
        {
            _ = Kernel32.TerminateProcess(process, 0);
        }
    }

    private FileStream GetWritableStream()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRunning || _inputStream is null)
            {
                throw new InvalidOperationException("The ConPTY connection is not running.");
            }

            return _inputStream;
        }
    }

    private void ReadLoop(FileStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = stream.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                OutputReceived?.Invoke(this, buffer.AsMemory(0, read).ToArray());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _disposed)
        {
        }
        catch (IOException ex) when (cancellationToken.IsCancellationRequested || _disposed)
        {
            _ = ex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    private void WaitLoop(SafeKernelObjectHandle process)
    {
        var waitResult = Kernel32.WaitForSingleObject(process, Kernel32.Infinite);
        if (waitResult == Kernel32.WaitFailed)
        {
            Faulted?.Invoke(this, new Win32Exception(Marshal.GetLastPInvokeError()));
            return;
        }

        if (!Kernel32.GetExitCodeProcess(process, out var code))
        {
            Faulted?.Invoke(this, new Win32Exception(Marshal.GetLastPInvokeError()));
            return;
        }

        lock (_stateLock)
        {
            IsRunning = false;
        }

        Exited?.Invoke(this, unchecked((int)code));
    }

    private static SafeKernelObjectHandle StartProcess(
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
            : options.WorkingDirectory;
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
            return new SafeKernelObjectHandle(info.hProcess);
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
}
