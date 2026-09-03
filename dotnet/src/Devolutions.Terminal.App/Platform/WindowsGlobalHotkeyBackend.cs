using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Platform;

internal sealed unsafe partial class WindowsGlobalHotkeyBackend : IGlobalHotkeyBackend
{
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmHotkey = 0x0312;
    private const uint WmExecuteCommand = 0x8001;
    private const int GwlpWndProc = -4;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly ConcurrentDictionary<nint, WindowsGlobalHotkeyBackend> Hosts = [];

    private readonly object _gate = new();
    private readonly Dictionary<int, Action> _callbacks = [];
    private readonly Dictionary<KeyChord, IGlobalHotkeyRegistration> _pending = [];
    private readonly ConcurrentQueue<MessageLoopCommand> _commands = new();
    private readonly CancellationTokenSource _stopped = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private nint _window;
    private nint _previousWindowProc;
    private Exception? _startupError;
    private int _nextId = 0x4400;
    private bool _disposed;

    public WindowsGlobalHotkeyBackend()
    {
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "Devolutions Terminal global hotkeys",
        };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _startupError = new InvalidOperationException(
                "The Windows global-hotkey message window did not initialize.");
        }
    }

    public GlobalHotkeyRegistrationResult Register(KeyChord chord, Action activated)
    {
        ArgumentNullException.ThrowIfNull(activated);
        if (!TryTranslate(chord, out var modifiers, out var virtualKey, out var diagnostic))
        {
            return new(chord, GlobalHotkeyRegistrationStatus.Invalid, diagnostic);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startupError is not null || _window == 0)
            {
                return new(
                    chord,
                    GlobalHotkeyRegistrationStatus.Unsupported,
                    $"Windows global hotkeys are unavailable: {_startupError?.Message ?? "no message window was created."}");
            }
        }

        return InvokeOnMessageLoopThread<GlobalHotkeyRegistrationResult>(() =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var id = _nextId++;
                if (!RegisterHotKey(_window, id, modifiers | ModNoRepeat, virtualKey))
                {
                    var error = Marshal.GetLastPInvokeError();
                    return new(
                        chord,
                        error == ErrorHotkeyAlreadyRegistered
                            ? GlobalHotkeyRegistrationStatus.Collision
                            : GlobalHotkeyRegistrationStatus.Unsupported,
                        error == ErrorHotkeyAlreadyRegistered
                            ? $"Global hotkey '{chord}' is already registered by another application."
                            : $"RegisterHotKey rejected '{chord}' with Windows error {error}.");
                }

                _callbacks.Add(id, activated);
                _pending[chord] = new Registration(this, chord, id);
                return new(
                    chord,
                    GlobalHotkeyRegistrationStatus.Registered,
                    $"Global hotkey '{chord}' was registered with Windows.");
            }
        });
    }

    public IGlobalHotkeyRegistration? TakeRegistration(KeyChord chord)
    {
        lock (_gate)
        {
            return _pending.Remove(chord, out var registration) ? registration : null;
        }
    }

    private void Unregister(KeyChord chord, int id)
    {
        lock (_gate)
        {
            _pending.Remove(chord);
            if (_disposed || _window == 0)
            {
                return;
            }
        }

        try
        {
            _ = InvokeOnMessageLoopThread(() =>
            {
                if (_callbacks.Remove(id))
                {
                    _ = UnregisterHotKey(_window, id);
                }
                return true;
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MessageLoop()
    {
        try
        {
            _window = CreateWindowEx(
                0,
                "STATIC",
                "Devolutions.Terminal.GlobalHotkeys",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
            if (_window == 0)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx failed with Windows error {Marshal.GetLastPInvokeError()}.");
            }

            Hosts[_window] = this;
            _previousWindowProc = SetWindowLongPtr(
                _window,
                GwlpWndProc,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc);
            if (_previousWindowProc == 0)
            {
                throw new InvalidOperationException(
                    $"SetWindowLongPtr failed with Windows error {Marshal.GetLastPInvokeError()}.");
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
        finally
        {
            _ready.Set();
        }

        if (_startupError is not null)
        {
            if (_window != 0)
            {
                _ = DestroyWindow(_window);
            }
            CleanupWindow();
            CancelPendingCommands();
            return;
        }

        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
        }
        finally
        {
            CleanupWindow();
            CancelPendingCommands();
        }
    }

    private void CleanupWindow()
    {
        if (_window != 0)
        {
            Hosts.TryRemove(_window, out _);
            _window = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (!Hosts.TryGetValue(window, out var host))
        {
            return DefWindowProc(window, message, wParam, lParam);
        }

        if (message == WmHotkey)
        {
            host._callbacks.TryGetValue((int)wParam, out var callback);
            callback?.Invoke();
            return 0;
        }
        if (message == WmExecuteCommand)
        {
            host.DrainCommands();
            return 0;
        }
        if (message == WmClose)
        {
            _ = DestroyWindow(window);
            return 0;
        }
        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }
        return host._previousWindowProc == 0
            ? DefWindowProc(window, message, wParam, lParam)
            : CallWindowProc(host._previousWindowProc, window, message, wParam, lParam);
    }

    private static bool TryTranslate(
        KeyChord chord,
        out uint modifiers,
        out uint virtualKey,
        out string diagnostic)
    {
        modifiers = 0;
        virtualKey = 0;
        diagnostic = string.Empty;
        var parts = chord.ToString().Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            diagnostic = "The global hotkey is empty.";
            return false;
        }
        foreach (var modifier in parts[..^1])
        {
            modifiers |= modifier switch
            {
                "win" => ModWin,
                "ctrl" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                _ => 0,
            };
        }

        var key = parts[^1];
        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
        {
            virtualKey = char.ToUpperInvariant(key[0]);
            return true;
        }
        if (key.Length is >= 2 and <= 3 &&
            key[0] == 'f' &&
            int.TryParse(key.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x6F + functionKey);
            return true;
        }

        virtualKey = key switch
        {
            "backspace" => 0x08,
            "tab" => 0x09,
            "enter" => 0x0D,
            "esc" => 0x1B,
            "space" => 0x20,
            "pgup" => 0x21,
            "pgdn" => 0x22,
            "end" => 0x23,
            "home" => 0x24,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "numpad_multiply" => 0x6A,
            "numpad_plus" => 0x6B,
            "numpad_minus" => 0x6D,
            "numpad_period" => 0x6E,
            "numpad_divide" => 0x6F,
            "semicolon" => 0xBA,
            "plus" => 0xBB,
            "comma" => 0xBC,
            "minus" => 0xBD,
            "period" => 0xBE,
            "slash" => 0xBF,
            "backtick" => 0xC0,
            "open_bracket" => 0xDB,
            "backslash" => 0xDC,
            "close_bracket" => 0xDD,
            "quote" => 0xDE,
            _ when key.StartsWith("numpad", StringComparison.Ordinal) &&
                   int.TryParse(key.AsSpan(6), out var number) &&
                   number is >= 0 and <= 9 => (uint)(0x60 + number),
            _ => 0,
        };
        if (virtualKey != 0)
        {
            return true;
        }

        diagnostic = $"Global hotkey '{chord}' uses a key that RegisterHotKey cannot map safely.";
        return false;
    }

    public void Dispose()
    {
        MessageLoopCommand? shutdown = null;
        var disposeOnMessageLoop = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pending.Clear();
            if (_window != 0)
            {
                shutdown = new MessageLoopCommand(() =>
                {
                    foreach (var id in _callbacks.Keys)
                    {
                        _ = UnregisterHotKey(_window, id);
                    }
                    _callbacks.Clear();
                    _ = DestroyWindow(_window);
                    return null;
                });
                disposeOnMessageLoop =
                    Environment.CurrentManagedThreadId == _thread.ManagedThreadId;
                if (!disposeOnMessageLoop)
                {
                    _commands.Enqueue(shutdown);
                    if (!PostMessage(_window, WmExecuteCommand, 0, 0))
                    {
                        shutdown.Fail(new Win32Exception(Marshal.GetLastPInvokeError()));
                    }
                }
            }
        }

        try
        {
            if (disposeOnMessageLoop)
            {
                shutdown!.Execute();
            }
            shutdown?.Wait();
        }
        finally
        {
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join();
            }
            _ready.Dispose();
        }
    }

    internal int MessageLoopThreadId => _thread.ManagedThreadId;

    internal int InvokeOnMessageLoopThreadForTesting() =>
        InvokeOnMessageLoopThread(static () => Environment.CurrentManagedThreadId);

    internal void DisposeOnMessageLoopThreadForTesting() =>
        InvokeOnMessageLoopThread(() =>
        {
            Dispose();
            return true;
        });

    private T InvokeOnMessageLoopThread<T>(Func<T> callback)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            return callback();
        }

        var command = new MessageLoopCommand(() => callback());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _commands.Enqueue(command);
            if (!PostMessage(_window, WmExecuteCommand, 0, 0))
            {
                command.Fail(new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        return (T)command.Wait()!;
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            command.Execute();
        }
    }

    private void CancelPendingCommands()
    {
        _stopped.Cancel();
        while (_commands.TryDequeue(out var command))
        {
            command.Cancel(_stopped.Token);
        }
    }

    private sealed class MessageLoopCommand(Func<object?> callback)
    {
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Execute()
        {
            if (_completion.Task.IsCompleted)
            {
                return;
            }
            try
            {
                _completion.TrySetResult(callback());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);

        public void Cancel(CancellationToken cancellationToken) =>
            _completion.TrySetCanceled(cancellationToken);

        public object? Wait() =>
            _completion.Task.GetAwaiter().GetResult();
    }

    private sealed class Registration(
        WindowsGlobalHotkeyBackend owner,
        KeyChord chord,
        int id) : IGlobalHotkeyRegistration
    {
        private WindowsGlobalHotkeyBackend? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unregister(chord, id);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProc(
        nint previous,
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static partial void PostQuitMessage(int exitCode);
}
