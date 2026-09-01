using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Terminal.Connection;
using Microsoft.Terminal.Core;
using Microsoft.Terminal.Settings;

namespace Microsoft.Terminal.Control;

public sealed class TermControl : Avalonia.Controls.Control
{
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly object _outputLock = new();
    private readonly List<byte> _pendingOutput = [];
    private ITerminalConnection? _connection;
    private Typeface _typeface = new("Cascadia Mono, Consolas, Courier New");
    private double _fontSize = 12;
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

    public event EventHandler<string>? TitleChanged;
    public event EventHandler<int>? ProcessExited;

    public async Task StartAsync(ProfileSettings profile, int columns, int rows)
    {
        Profile = profile;
        _fontSize = profile.FontSize <= 0 ? 12 : profile.FontSize;
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
        connection.Exited += (_, code) => Dispatcher.UIThread.Post(() => ProcessExited?.Invoke(this, code));
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
            }).ConfigureAwait(true);
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
        var scheme = Engine.Scheme;
        var bg = ToBrush(scheme.Background);
        context.FillRectangle(bg, new Rect(Bounds.Size));

        var buffer = Engine.Buffer;
        var padding = 8.0;
        for (var y = 0; y < buffer.Rows; y++)
        {
            var row = buffer.GetRow(y);
            DrawRow(context, row, y, padding);
        }

        if (_hasSelection)
        {
            DrawSelection(context, padding);
        }

        if (Engine.CursorVisible && _cursorOn && IsFocused)
        {
            var x = padding + (Engine.CursorX * _cellWidth);
            var y = padding + (Engine.CursorY * _cellHeight);
            context.FillRectangle(ToBrush(scheme.Cursor), new Rect(x, y, 2, _cellHeight));
        }
    }

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

    private void DrawRow(DrawingContext context, Cell[] row, int y, double padding)
    {
        var scheme = Engine.Scheme;
        var x = 0;
        while (x < row.Length)
        {
            if (row[x].IsWideContinuation)
            {
                x++;
                continue;
            }

            var start = x;
            var cell = row[x];
            x++;
            while (x < row.Length && !row[x].IsWideContinuation && SamePaint(row[x], cell))
            {
                x++;
            }

            var width = x - start;
            var (fg, bg) = ResolveColors(cell.Attributes, scheme);
            var rect = new Rect(
                padding + (start * _cellWidth),
                padding + (y * _cellHeight),
                width * _cellWidth,
                _cellHeight);
            if (bg != scheme.Background)
            {
                context.FillRectangle(ToBrush(bg), rect);
            }

            if ((cell.Attributes.Flags & CellFlags.Invisible) != 0)
            {
                continue;
            }

            var sb = new StringBuilder(width);
            for (var i = start; i < x; i++)
            {
                if (!row[i].IsWideContinuation)
                {
                    sb.Append(row[i].Rune.ToString());
                }
            }

            var text = sb.ToString().TrimEnd();
            if (text.Length == 0)
            {
                continue;
            }

            var typeface = new Typeface(
                _typeface.FontFamily,
                (cell.Attributes.Flags & CellFlags.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal,
                (cell.Attributes.Flags & CellFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal);

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                _fontSize,
                ToBrush(fg));

            context.DrawText(formatted, new Point(rect.X, rect.Y));

            if ((cell.Attributes.Flags & CellFlags.Underline) != 0)
            {
                var yPos = rect.Bottom - 2;
                context.DrawLine(new Pen(ToBrush(fg), 1), new Point(rect.X, yPos), new Point(rect.Right, yPos));
            }
        }
    }

    private void DrawSelection(DrawingContext context, double padding)
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

        var brush = ToBrush(Engine.Scheme.SelectionBackground);
        for (var y = y1; y <= y2; y++)
        {
            var from = y == y1 ? x1 : 0;
            var to = y == y2 ? x2 : Engine.Columns - 1;
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

    private static bool SamePaint(Cell left, Cell right) =>
        left.Attributes.Equals(right.Attributes);

    private static (uint Fg, uint Bg) ResolveColors(CellAttributes attributes, ColorScheme scheme)
    {
        var fg = attributes.Foreground.ToArgb(scheme, foreground: true);
        var bg = attributes.Background.ToArgb(scheme, foreground: false);
        if ((attributes.Flags & CellFlags.Inverse) != 0)
        {
            (fg, bg) = (bg, fg);
        }

        if ((attributes.Flags & CellFlags.Faint) != 0)
        {
            fg = Fade(fg);
        }

        return (fg, bg);
    }

    private static uint Fade(uint argb)
    {
        var r = (byte)((argb >> 16) & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var b = (byte)(argb & 0xFF);
        return 0xFF000000u | ((uint)(r / 2) << 16) | ((uint)(g / 2) << 8) | (byte)(b / 2);
    }

    private static IBrush ToBrush(uint argb) =>
        new SolidColorBrush(Color.FromUInt32(argb));
}
