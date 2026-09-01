using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Terminal.Connection.Native;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Terminal.Connection;

[SupportedOSPlatform("windows")]
public sealed class ConPtyConnection : ITerminalConnection
{
    private readonly object _writeLock = new();
    private nint _pseudoConsole;
    private nint _process;
    private nint _thread;
    private nint _attributeList;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _inputStream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _waitTask;

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;

    public bool IsRunning { get; private set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public async Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ConPTY requires Windows.");
        }

        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);

        if (!Kernel32.CreatePipe(out var inputRead, out var inputWrite, 0, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (!Kernel32.CreatePipe(out var outputRead, out var outputWrite, 0, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        var size = new Kernel32.Coord { X = (short)Columns, Y = (short)Rows };
        var hr = Kernel32.CreatePseudoConsole(size, inputRead, outputWrite, 0, out _pseudoConsole);
        if (hr != 0)
        {
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
        }

        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _inputStream = new FileStream(_inputWrite, FileAccess.Write, 4096, isAsync: false);

        StartProcess(commandLine, workingDirectory);
        inputRead.Dispose();
        outputWrite.Dispose();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsRunning = true;
        _readTask = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);
        _waitTask = Task.Run(WaitLoop, CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_inputStream is null || !IsRunning)
        {
            return;
        }

        lock (_writeLock)
        {
            _inputStream.Write(data);
            _inputStream.Flush();
        }
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Write(Encoding.UTF8.GetBytes(text));
    }

    public void Resize(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        if (_pseudoConsole == 0)
        {
            return;
        }

        var size = new Kernel32.Coord { X = (short)Columns, Y = (short)Rows };
        _ = Kernel32.ResizePseudoConsole(_pseudoConsole, size);
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_process != 0)
        {
            Kernel32.TerminateProcess(_process, 0);
        }

        if (_pseudoConsole != 0)
        {
            Kernel32.ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = 0;
        }

        if (_readTask is not null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        CleanupHandles();
        _cts?.Dispose();
    }

    private void StartProcess(string commandLine, string? workingDirectory)
    {
        nint size = 0;
        Kernel32.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!Kernel32.InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (!Kernel32.UpdateProcThreadAttribute(
                _attributeList,
                0,
                Kernel32.ProcThreadAttributePseudoConsole,
                _pseudoConsole,
                nint.Size,
                0,
                0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        var startup = new Kernel32.StartupInfoExW
        {
            StartupInfo = new Kernel32.StartupInfoW
            {
                cb = Marshal.SizeOf<Kernel32.StartupInfoExW>(),
            },
            lpAttributeList = _attributeList,
        };

        var pSec = new Kernel32.SecurityAttributes { nLength = Marshal.SizeOf<Kernel32.SecurityAttributes>() };
        var tSec = new Kernel32.SecurityAttributes { nLength = Marshal.SizeOf<Kernel32.SecurityAttributes>() };

        var buffer = (commandLine + '\0').ToCharArray();
        var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        if (!Kernel32.CreateProcessW(
                null,
                ref buffer[0],
                ref pSec,
                ref tSec,
                false,
                Kernel32.ExtendedStartupinfoPresent,
                0,
                cwd,
                ref startup,
                out var info))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        _process = info.hProcess;
        _thread = info.hThread;
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        if (_outputRead is null)
        {
            return;
        }

        using var stream = new FileStream(_outputRead, FileAccess.Read, 4096, isAsync: false);
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                OutputReceived?.Invoke(this, buffer.AsMemory(0, read).ToArray());
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void WaitLoop()
    {
        if (_process == 0)
        {
            return;
        }

        Kernel32.WaitForSingleObject(_process, Kernel32.Infinite);
        Kernel32.GetExitCodeProcess(_process, out var code);
        IsRunning = false;
        Exited?.Invoke(this, (int)code);
    }

    private void CleanupHandles()
    {
        lock (_writeLock)
        {
            _inputStream?.Dispose();
            _inputStream = null;
            _inputWrite = null;
        }

        _outputRead?.Dispose();
        _outputRead = null;

        if (_thread != 0)
        {
            Kernel32.CloseHandle(_thread);
            _thread = 0;
        }

        if (_process != 0)
        {
            Kernel32.CloseHandle(_process);
            _process = 0;
        }

        if (_attributeList != 0)
        {
            Kernel32.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = 0;
        }
    }
}
