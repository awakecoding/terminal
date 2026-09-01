using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Terminal.Connection;
using Microsoft.Terminal.Core;
using Microsoft.Terminal.Render;
using Microsoft.Terminal.Settings;

namespace Microsoft.Terminal.Control;

[Flags]
public enum TerminalControlCapabilities
{
    None = 0,
    ClearBuffer = 1 << 0,
    Reset = 1 << 1,
    ShowHide = 1 << 2,
    Restart = 1 << 3,
}

public sealed class TermControl : Avalonia.Controls.Control
{
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly object _outputLock = new();
    private readonly List<byte> _pendingOutput = [];
    private readonly SkiaTerminalRenderer _renderer = new();
    private IRestartableTerminalConnection? _connection;
    private IReadOnlyList<TerminalCellRange> _searchHighlights = [];
    private IReadOnlyList<TerminalCellRange> _hoveredHyperlink = [];
    private TerminalRenderFrame? _lastFrame;
    private IReadOnlyList<int> _lastDirtyRows = [];
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private bool _cursorOn = true;
    private bool _selecting;
    private int _selX1, _selY1, _selX2, _selY2;
    private bool _hasSelection;
    private bool _dirty = true;
    private bool _rendererDisposed;

    public TermControl()
    {
        Engine = new TerminalEngine();
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _renderTimer.Tick += (_, _) => DrainOutput();

        Engine.Invalidated += (_, _) =>
        {
            _dirty = true;
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        };
        Engine.TitleChanged += (_, title) => TitleChanged?.Invoke(this, title);
        Engine.ResponseReady += (_, data) => _connection?.Write(data);
    }

    public TerminalEngine Engine { get; }
    public ProfileSettings? Profile { get; private set; }
    public bool IsRunning => _connection?.IsRunning == true;
    public TerminalConnectionState ConnectionState =>
        _connection?.State ?? TerminalConnectionState.NotConnected;
    public TerminalProcessMetadata? ProcessMetadata => _connection?.ProcessMetadata;
    public TerminalControlCapabilities Capabilities { get; } =
        TerminalControlCapabilities.ClearBuffer |
        TerminalControlCapabilities.Reset |
        TerminalControlCapabilities.ShowHide |
        TerminalControlCapabilities.Restart;

    public event EventHandler<string>? TitleChanged;
    public event EventHandler<int>? ProcessExited;
    public event EventHandler<TerminalExitInfo>? SessionExited;
    public event EventHandler? CloseRequested;

    public async Task StartAsync(ProfileSettings profile, int columns, int rows)
    {
        Profile = profile;
        _renderer.Configure(new TerminalRendererSettings
        {
            FontFamily = profile.FontFace,
            FontSize = (float)(profile.FontSize <= 0 ? 12 : profile.FontSize),
            FontWeight = profile.FontWeight,
            FontSources =
            [
                new TerminalFontSource("Cascadia Mono", false, OpenCascadiaMono),
                new TerminalFontSource("Cascadia Mono", true, OpenCascadiaMonoItalic),
            ],
        });
        Engine.Scheme = profile.ResolveScheme();
        Engine.Resize(columns, rows);
        MeasureGlyph();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This terminal host requires Windows ConPTY.");
        }

        await StartConnectionAsync(profile, columns, rows).ConfigureAwait(true);
        _blinkTimer.Start();
        _renderTimer.Start();
        InvalidateVisual();
    }

    [SupportedOSPlatform("windows")]
    private async Task StartConnectionAsync(ProfileSettings profile, int columns, int rows)
    {
        var connection = new ConPtyConnection();
        connection.OutputReceived += OnOutput;
        connection.SessionExited += OnSessionExited;
        _connection = connection;
        await connection.StartAsync(
            new TerminalLaunchOptions
            {
                CommandLine = profile.ExpandCommandline(),
                WorkingDirectory = profile.ExpandStartingDirectory(),
                Columns = columns,
                Rows = rows,
                InheritEnvironment = profile.ReloadEnvironmentVariables,
                EnvironmentVariables = profile.Environment,
                CloseOnExit = ToConnectionPolicy(profile.CloseOnExit),
            }).ConfigureAwait(true);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The terminal connection has not been started.");
        _renderTimer.Stop();
        await connection.CloseAsync(cancellationToken).ConfigureAwait(true);
        ResetTerminal();
        await connection.RestartAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        _blinkTimer.Start();
        _renderTimer.Start();
    }

    public async Task CloseAsync()
    {
        _blinkTimer.Stop();
        _renderTimer.Stop();
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _renderer.Dispose();
        _rendererDisposed = true;
    }

    public async Task CopyAsync()
    {
        if (!_hasSelection)
        {
            return;
        }

        var text = Engine.CopySelection(_selX1, _selY1, _selX2, _selY2);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard is null ? null : await clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text) || _connection is null)
        {
            return;
        }

        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        _connection.Write(Engine.WrapPaste(text));
    }

    public void ClearBuffer()
    {
        Engine.Feed("\u001b[3J\u001b[2J\u001b[H");
        _hasSelection = false;
        InvalidateVisual();
    }

    public void ResetTerminal()
    {
        lock (_outputLock)
        {
            _pendingOutput.Clear();
        }

        Engine.Reset();
        _hasSelection = false;
        _cursorOn = true;
        InvalidateVisual();
    }

    public void ShowHide(bool show)
    {
        IsVisible = show;
        if (show)
        {
            Focus();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureGlyph();
        const double padding = 8;
        var cols = Math.Max(1, (int)((availableSize.Width - (padding * 2)) / _cellWidth));
        var rows = Math.Max(1, (int)((availableSize.Height - (padding * 2)) / _cellHeight));
        return new Size((cols * _cellWidth) + (padding * 2), (rows * _cellHeight) + (padding * 2));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        MeasureGlyph();
        const double padding = 8;
        var cols = Math.Max(1, (int)((finalSize.Width - (padding * 2)) / _cellWidth));
        var rows = Math.Max(1, (int)((finalSize.Height - (padding * 2)) / _cellHeight));
        if (cols != Engine.Columns || rows != Engine.Rows)
        {
            Engine.Resize(cols, rows);
            _connection?.Resize(cols, rows);
        }

        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (_rendererDisposed)
        {
            return;
        }

        var profile = Profile;
        var frame = TerminalRenderPlanner.Create(
            Engine.CreateSnapshot(),
            Engine.Scheme,
            new TerminalRenderOptions
            {
                CursorStyle = ParseCursorStyle(profile?.CursorShape),
                CursorHeightPercentage = profile?.CursorHeight ?? 25,
            });
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, scale));
        MeasureGlyph();

        var selection = _hasSelection
            ? TerminalOverlayPlanner.CreateSelection(
                _selX1,
                _selY1,
                _selX2,
                _selY2,
                frame.Columns,
                frame.Rows,
                frame.SelectionColor)
            : [];
        var overlays = new TerminalRenderOverlays(
            selection,
            _searchHighlights,
            _hoveredHyperlink);
        _lastDirtyRows = TerminalFrameDiffer.GetDirtyRows(_lastFrame, frame);
        _lastFrame = frame;
        context.Custom(new TerminalSkiaDrawOperation(
            new Rect(Bounds.Size),
            _renderer,
            frame,
            overlays,
            padding: 8,
            drawCursor: _cursorOn && IsFocused));
    }

    public void SetSearchHighlights(IReadOnlyList<TerminalCellRange> highlights)
    {
        ArgumentNullException.ThrowIfNull(highlights);
        _searchHighlights = highlights.ToArray();
        InvalidateVisual();
    }

    internal IReadOnlyList<int> LastDirtyRows => _lastDirtyRows;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.C)
            {
                _ = CopyAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V)
            {
                _ = PasteAsync();
                e.Handled = true;
                return;
            }
        }

        var vt = KeyMapper.ToVt(e.Key, e.KeyModifiers, e.PhysicalKey, e.KeySymbol, Engine.ApplicationCursorKeys);
        if (vt is not null)
        {
            _connection?.Write(vt);
            Engine.Buffer.ScrollOffset = 0;
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && e.Text != "\r" && e.Text != "\n" && e.Text != "\t")
        {
            _connection?.Write(e.Text);
            Engine.Buffer.ScrollOffset = 0;
            e.Handled = true;
        }

        base.OnTextInput(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            var (x, y) = HitTest(point.Position);
            _selecting = true;
            _hasSelection = true;
            _selX1 = _selX2 = x;
            _selY1 = _selY2 = y;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        UpdateHoveredHyperlink(e.GetPosition(this));
        if (_selecting)
        {
            var (x, y) = HitTest(e.GetPosition(this));
            _selX2 = x;
            _selY2 = y;
            InvalidateVisual();
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoveredHyperlink.Count != 0)
        {
            _hoveredHyperlink = [];
            InvalidateVisual();
        }

        base.OnPointerExited(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _selecting = false;
        e.Pointer.Capture(null);
        if (_selX1 == _selX2 && _selY1 == _selY2)
        {
            _hasSelection = false;
            InvalidateVisual();
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var delta = (int)Math.Round(e.Delta.Y * 3);
        var max = Engine.Buffer.HistoryCount;
        Engine.Buffer.ScrollOffset = Math.Clamp(Engine.Buffer.ScrollOffset + delta, 0, max);
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
    {
        lock (_outputLock)
        {
            _pendingOutput.AddRange(data.ToArray());
        }
    }

    private void OnSessionExited(object? sender, TerminalExitInfo exit)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SessionExited?.Invoke(this, exit);
            if (exit.ExitCode is int exitCode)
            {
                ProcessExited?.Invoke(this, exitCode);
            }

            if (exit.ShouldClose)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void DrainOutput()
    {
        byte[] chunk;
        lock (_outputLock)
        {
            if (_pendingOutput.Count == 0)
            {
                return;
            }

            chunk = [.. _pendingOutput];
            _pendingOutput.Clear();
        }

        Engine.Feed(chunk);
        if (_dirty)
        {
            InvalidateVisual();
            _dirty = false;
        }
    }

    private (int X, int Y) HitTest(Point point)
    {
        const double padding = 8;
        var x = (int)Math.Floor((point.X - padding) / _cellWidth);
        var y = (int)Math.Floor((point.Y - padding) / _cellHeight);
        return (Math.Clamp(x, 0, Engine.Columns - 1), Math.Clamp(y, 0, Engine.Rows - 1));
    }

    private void MeasureGlyph()
    {
        _cellWidth = _renderer.CellSize.Width;
        _cellHeight = _renderer.CellSize.Height;
    }

    private void UpdateHoveredHyperlink(Point position)
    {
        var (x, y) = HitTest(position);
        var row = Engine.Buffer.GetRow(y);
        var uri = row[x].HyperlinkUri;
        IReadOnlyList<TerminalCellRange> next = [];
        if (uri is not null)
        {
            var start = x;
            var end = x;
            while (start > 0 && string.Equals(row[start - 1].HyperlinkUri, uri, StringComparison.Ordinal))
            {
                start--;
            }

            while (end + 1 < row.Length &&
                   string.Equals(row[end + 1].HyperlinkUri, uri, StringComparison.Ordinal))
            {
                end++;
            }

            next = [new TerminalCellRange(y, start, end, 0x202080FF)];
        }

        if (!_hoveredHyperlink.SequenceEqual(next))
        {
            _hoveredHyperlink = next;
            InvalidateVisual();
        }
    }

    internal static TerminalCursorStyle ParseCursorStyle(string? value) =>
        value is not null && value.Equals("underscore", StringComparison.OrdinalIgnoreCase)
            ? TerminalCursorStyle.Underscore
            : value is not null && value.Equals("doubleUnderscore", StringComparison.OrdinalIgnoreCase)
                ? TerminalCursorStyle.DoubleUnderscore
                : value is not null && value.Equals("vintage", StringComparison.OrdinalIgnoreCase)
                    ? TerminalCursorStyle.Vintage
                    : value is not null && value.Equals("filledBox", StringComparison.OrdinalIgnoreCase)
                        ? TerminalCursorStyle.FilledBox
                        : value is not null && value.Equals("emptyBox", StringComparison.OrdinalIgnoreCase)
                            ? TerminalCursorStyle.EmptyBox
                            : TerminalCursorStyle.Bar;

    private static Stream OpenCascadiaMono() =>
        AssetLoader.Open(new Uri("avares://WindowsTerminal/Assets/Fonts/CascadiaMono.ttf"));

    private static Stream OpenCascadiaMonoItalic() =>
        AssetLoader.Open(new Uri("avares://WindowsTerminal/Assets/Fonts/CascadiaMonoItalic.ttf"));

    private static TerminalCloseOnExitPolicy ToConnectionPolicy(CloseOnExitMode mode) =>
        mode switch
        {
            CloseOnExitMode.Never => TerminalCloseOnExitPolicy.Never,
            CloseOnExitMode.Graceful => TerminalCloseOnExitPolicy.Graceful,
            CloseOnExitMode.Always => TerminalCloseOnExitPolicy.Always,
            _ => TerminalCloseOnExitPolicy.Automatic,
        };
}
