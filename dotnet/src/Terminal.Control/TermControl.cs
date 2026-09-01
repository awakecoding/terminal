using System.Globalization;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    private IRestartableTerminalConnection? _connection;
    private Typeface _typeface = new("Cascadia Mono, Consolas, Courier New");
    private double _fontSize = 12;
    private double _defaultFontSize = 12;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private bool _cursorOn = true;
    private bool _selecting;
    private int _selX1, _selY1, _selX2, _selY2;
    private bool _hasSelection;
    private bool _dirty = true;

    public TermControl()
    {
        Engine = new TerminalEngine();
        Focusable = true;
        ClipToBounds = true;

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
    public bool HasSelection => _hasSelection;
    public double FontSize => _fontSize;
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
        _defaultFontSize = profile.FontSize <= 0 ? 12 : profile.FontSize;
        _fontSize = _defaultFontSize;
        _typeface = new Typeface($"{profile.FontFace}, Cascadia Mono, Consolas, Courier New");
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
    }

    public async Task CopyAsync(bool singleLine = false)
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

        if (singleLine)
        {
            text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
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

    public void WriteInput(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _connection?.Write(input);
        Engine.Buffer.ScrollOffset = 0;
    }

    public void SelectAll()
    {
        _selX1 = 0;
        _selY1 = 0;
        _selX2 = Engine.Columns - 1;
        _selY2 = Engine.Rows - 1;
        _hasSelection = true;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _hasSelection = false;
        InvalidateVisual();
    }

    public void AdjustFontSize(double delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 1, 72);
        if (VisualRoot is not null)
        {
            MeasureGlyph();
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    public void ResetFontSize()
    {
        _fontSize = _defaultFontSize;
        if (VisualRoot is not null)
        {
            MeasureGlyph();
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    public void ScrollBy(int rows)
    {
        Engine.Buffer.ScrollOffset = Math.Clamp(
            Engine.Buffer.ScrollOffset + rows,
            0,
            Engine.Buffer.HistoryCount);
        InvalidateVisual();
    }

    public void ScrollPage(int direction) => ScrollBy(direction * Math.Max(1, Engine.Rows - 1));

    public void ScrollToTop()
    {
        Engine.Buffer.ScrollOffset = Engine.Buffer.HistoryCount;
        InvalidateVisual();
    }

    public void ScrollToBottom()
    {
        Engine.Buffer.ScrollOffset = 0;
        InvalidateVisual();
    }

    public bool Find(string query, bool previous = false)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        var snapshot = Engine.Buffer.CreateSnapshot();
        var indexes = previous
            ? Enumerable.Range(0, snapshot.Lines.Count).Reverse()
            : Enumerable.Range(0, snapshot.Lines.Count);
        foreach (var rowIndex in indexes)
        {
            var text = string.Concat(snapshot.Lines[rowIndex].Cells.Select(static cell =>
                cell.IsWideContinuation ? string.Empty : cell.Text));
            var column = previous
                ? text.LastIndexOf(query, StringComparison.CurrentCultureIgnoreCase)
                : text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (column < 0)
            {
                continue;
            }

            _selX1 = Math.Min(column, Engine.Columns - 1);
            _selY1 = rowIndex;
            _selX2 = Math.Min(column + query.Length - 1, Engine.Columns - 1);
            _selY2 = rowIndex;
            _hasSelection = true;
            InvalidateVisual();
            return true;
        }

        return false;
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
        var frame = TerminalRenderPlanner.Create(Engine.CreateSnapshot(), Engine.Scheme);
        var bg = ToBrush(frame.Background);
        context.FillRectangle(bg, new Rect(Bounds.Size));

        var padding = 8.0;
        foreach (var row in frame.RowsData)
        {
            DrawRow(context, row, frame.Background, padding);
        }

        if (_hasSelection)
        {
            DrawSelection(context, frame.SelectionColor, frame.Columns, padding);
        }

        if (frame.CursorVisible && _cursorOn && IsFocused)
        {
            var x = padding + (frame.CursorX * _cellWidth);
            var y = padding + (frame.CursorY * _cellHeight);
            context.FillRectangle(ToBrush(frame.CursorColor), new Rect(x, y, 2, _cellHeight));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
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
        if (_selecting)
        {
            var (x, y) = HitTest(e.GetPosition(this));
            _selX2 = x;
            _selY2 = y;
            InvalidateVisual();
        }

        base.OnPointerMoved(e);
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

    private void DrawRow(
        DrawingContext context,
        TerminalRenderRow row,
        uint defaultBackground,
        double padding)
    {
        foreach (var run in row.Runs)
        {
            var rect = new Rect(
                padding + (run.StartColumn * _cellWidth),
                padding + (row.RowIndex * _cellHeight),
                run.CellCount * _cellWidth,
                _cellHeight);
            if (run.Attributes.Background != defaultBackground)
            {
                context.FillRectangle(ToBrush(run.Attributes.Background), rect);
            }

            if ((run.Attributes.Flags & CellFlags.Invisible) != 0)
            {
                continue;
            }

            var text = run.Text.TrimEnd();
            if (text.Length == 0)
            {
                continue;
            }

            var typeface = new Typeface(
                _typeface.FontFamily,
                (run.Attributes.Flags & CellFlags.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal,
                (run.Attributes.Flags & CellFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal);

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                _fontSize,
                ToBrush(run.Attributes.Foreground));

            context.DrawText(formatted, new Point(rect.X, rect.Y));

            if ((run.Attributes.Flags & CellFlags.Underline) != 0 || run.Attributes.HyperlinkUri is not null)
            {
                var yPos = rect.Bottom - 2;
                context.DrawLine(
                    new Pen(ToBrush(run.Attributes.Foreground), 1),
                    new Point(rect.X, yPos),
                    new Point(rect.Right, yPos));
            }

            if ((run.Attributes.Flags & CellFlags.Strikethrough) != 0)
            {
                var yPos = rect.Y + (rect.Height / 2);
                context.DrawLine(
                    new Pen(ToBrush(run.Attributes.Foreground), 1),
                    new Point(rect.X, yPos),
                    new Point(rect.Right, yPos));
            }
        }
    }

    private void DrawSelection(DrawingContext context, uint selectionColor, int columns, double padding)
    {
        var x1 = _selX1;
        var y1 = _selY1;
        var x2 = _selX2;
        var y2 = _selY2;
        if (y1 > y2 || (y1 == y2 && x1 > x2))
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }

        var brush = ToBrush(selectionColor);
        for (var y = y1; y <= y2; y++)
        {
            var from = y == y1 ? x1 : 0;
            var to = y == y2 ? x2 : columns - 1;
            var rect = new Rect(
                padding + (from * _cellWidth),
                padding + (y * _cellHeight),
                Math.Max(1, to - from + 1) * _cellWidth,
                _cellHeight);
            context.FillRectangle(brush, rect);
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
        var formatted = new FormattedText(
            "M",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.White);
        _cellWidth = Math.Max(1, formatted.Width);
        _cellHeight = Math.Max(1, Math.Ceiling(formatted.Height * 1.2));
    }

    private static IBrush ToBrush(uint argb) =>
        new SolidColorBrush(Color.FromUInt32(argb));

    private static TerminalCloseOnExitPolicy ToConnectionPolicy(CloseOnExitMode mode) =>
        mode switch
        {
            CloseOnExitMode.Never => TerminalCloseOnExitPolicy.Never,
            CloseOnExitMode.Graceful => TerminalCloseOnExitPolicy.Graceful,
            CloseOnExitMode.Always => TerminalCloseOnExitPolicy.Always,
            _ => TerminalCloseOnExitPolicy.Automatic,
        };
}
