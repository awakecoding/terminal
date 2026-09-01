using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
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
    private static readonly DataFormat<byte[]> HtmlClipboardFormat =
        DataFormat.CreateBytesPlatformFormat("HTML Format");
    private static readonly DataFormat<byte[]> RtfClipboardFormat =
        DataFormat.CreateBytesPlatformFormat("Rich Text Format");
    private readonly DispatcherTimer _blinkTimer;
    private readonly object _outputLock = new();
    private readonly MemoryStream _pendingOutput = new();
    private bool _outputDrainScheduled;
    private bool _acceptOutput;
    private readonly SkiaTerminalRenderer _renderer = new();
    private readonly TerminalSearchSession _search;
    private readonly Guid _terminalSessionId = Guid.NewGuid();
    private IRestartableTerminalConnection? _connection;
    private double _fontSize = 12;
    private double _defaultFontSize = 12;
    private IReadOnlyList<TerminalCellRange> _searchHighlights = [];
    private IReadOnlyList<TerminalCellRange> _hoveredHyperlink = [];
    private TerminalRenderFrame? _lastFrame;
    private IReadOnlyList<int> _lastDirtyRows = [];
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private bool _cursorOn = true;
    private bool _selecting;
    private TerminalSelection? _selection;
    private TerminalSelectionPoint _markCaret;
    private bool _isMarkMode;
    private TerminalCompositionOverlay? _composition;
    private readonly TerminalTextInputMethodClient _textInputMethodClient;
    private Point? _touchPoint;
    private string _accessibleName = "Terminal";
    private int _pressedMouseButton = -1;
    private long _selectionCoordinateVersion;
    private bool _selectionAlternateBuffer;
    private bool _rendererDisposed;

    public TermControl()
    {
        Engine = new TerminalEngine();
        _textInputMethodClient = new TerminalTextInputMethodClient(this);
        _search = new TerminalSearchSession(Engine);
        _search.Changed += (_, _) =>
        {
            UpdateSearchHighlights();
            ScrollMarksChanged?.Invoke(this, EventArgs.Empty);
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            AccessibilityChanged?.Invoke(this, EventArgs.Empty);
        };
        Focusable = true;
        ClipToBounds = true;
        TextInputMethodClientRequested += OnTextInputMethodClientRequested;
        GotFocus += (_, _) => SendFocusChanged(focused: true);
        LostFocus += (_, _) => SendFocusChanged(focused: false);

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };

        Engine.Invalidated += (_, _) =>
        {
            if (_selection is not null &&
                (_selectionCoordinateVersion != Engine.Buffer.CoordinateVersion ||
                 _selectionAlternateBuffer != Engine.AlternateBufferActive))
            {
                SetSelection(null);
            }

            _textInputMethodClient.NotifyCursorChanged();
            AccessibilityTextChanged?.Invoke(this, EventArgs.Empty);
            ScrollMarksChanged?.Invoke(this, EventArgs.Empty);
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        };
        Engine.TitleChanged += (_, title) => TitleChanged?.Invoke(this, title);
        Engine.ResponseReady += (_, data) => _connection?.Write(data);
        Engine.ClipboardWriteRequested += (_, text) =>
            Dispatcher.UIThread.Post(() => SetClipboardFromTerminalObservedAsync(text));
        Engine.NotificationRequested += (_, notification) =>
            Dispatcher.UIThread.Post(() => NotificationRequested?.Invoke(this, notification));
    }

    public TerminalEngine Engine { get; }
    public TerminalSearchSession Search => _search;
    public CellSize CellSize => _renderer.CellSize;
    public Func<ProfileSettings, IRestartableTerminalConnection>? ConnectionFactory { get; set; }
    public ProfileSettings? Profile { get; private set; }
    public bool IsRunning => _connection?.IsRunning == true;
    public bool HasSelection => _selection is not null;
    public TerminalSelection? Selection => _selection;
    public bool IsMarkMode => _isMarkMode;
    public string AccessibleName
    {
        get => _accessibleName;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (string.Equals(_accessibleName, value, StringComparison.Ordinal))
            {
                return;
            }

            _accessibleName = value;
            AccessibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public TerminalInteractionOptions InteractionOptions { get; set; } = new();
    public double FontSize => _fontSize;
    public TerminalConnectionState ConnectionState =>
        _connection?.State ?? TerminalConnectionState.NotConnected;
    public TerminalProcessMetadata? ProcessMetadata => _connection?.ProcessMetadata;
    public TerminalControlCapabilities Capabilities { get; } =
        TerminalControlCapabilities.ClearBuffer |
        TerminalControlCapabilities.Reset |
        TerminalControlCapabilities.ShowHide |
        TerminalControlCapabilities.Restart;

    public static CellSize MeasureCell(ProfileSettings profile, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var renderer = new SkiaTerminalRenderer(CreateRendererSettings(
            profile,
            profile.FontSize <= 0 ? 12 : profile.FontSize));
        renderer.Resize(new RenderViewport(1, 1, scale));
        return renderer.CellSize;
    }

    public event EventHandler<string>? TitleChanged;
    public event EventHandler<int>? ProcessExited;
    public event EventHandler<TerminalExitInfo>? SessionExited;
    public event EventHandler? CloseRequested;
    public event EventHandler<TerminalNotification>? NotificationRequested;
    public event EventHandler? SelectionChanged;
    public event EventHandler? AccessibilityChanged;
    internal event EventHandler? AccessibilityTextChanged;
    public event EventHandler? ScrollMarksChanged;
    public event EventHandler? ViewportChanged;
    public event EventHandler<TerminalPasteWarningEventArgs>? PasteWarning;
    public event EventHandler<TerminalHyperlinkEventArgs>? HyperlinkOpenRequested;
    public event EventHandler<TerminalHyperlinkEventArgs>? HyperlinkContextRequested;
    public event EventHandler<TerminalInteractionErrorEventArgs>? InteractionError;

    public async Task StartAsync(ProfileSettings profile, int columns, int rows)
    {
        Profile = profile;
        _defaultFontSize = profile.FontSize <= 0 ? 12 : profile.FontSize;
        _fontSize = _defaultFontSize;
        ConfigureRenderer(profile);
        Engine.Scheme = profile.ResolveScheme();
        Engine.ConfigureOptionalFeatures(
            profile.AllowVtClipboardWrite,
            profile.AllowOscNotifications);
        Engine.Resize(columns, rows);
        MeasureGlyph();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This terminal host requires Windows ConPTY.");
        }

        await StartConnectionAsync(profile, columns, rows).ConfigureAwait(true);
        _blinkTimer.Start();
        InvalidateVisual();
    }

    [SupportedOSPlatform("windows")]
    private async Task StartConnectionAsync(ProfileSettings profile, int columns, int rows)
    {
        var connection = ConnectionFactory?.Invoke(profile) ?? new ConPtyConnection();
        connection.OutputReceived += OnOutput;
        connection.SessionExited += OnSessionExited;
        _connection = connection;
        lock (_outputLock)
        {
            _acceptOutput = true;
        }
        try
        {
            await connection.StartAsync(
                new TerminalLaunchOptions
                {
                    CommandLine = profile.ExpandCommandline(),
                    WorkingDirectory = profile.ExpandStartingDirectory(),
                    Columns = columns,
                    Rows = rows,
                    InheritEnvironment = profile.ReloadEnvironmentVariables,
                    EnvironmentVariables = BuildTerminalEnvironment(profile),
                    CloseOnExit = ToConnectionPolicy(profile.CloseOnExit),
                }).ConfigureAwait(true);
        }
        catch
        {
            connection.OutputReceived -= OnOutput;
            connection.SessionExited -= OnSessionExited;
            _connection = null;
            lock (_outputLock)
            {
                _acceptOutput = false;
                _pendingOutput.SetLength(0);
                _outputDrainScheduled = false;
            }
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The terminal connection has not been started.");
        await connection.CloseAsync(cancellationToken).ConfigureAwait(true);
        ResetTerminal();
        await connection.RestartAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        _blinkTimer.Start();
    }

    public async Task CloseAsync()
    {
        _blinkTimer.Stop();
        if (_connection is not null)
        {
            var connection = _connection;
            _connection = null;
            connection.OutputReceived -= OnOutput;
            connection.SessionExited -= OnSessionExited;
            lock (_outputLock)
            {
                _acceptOutput = false;
                _pendingOutput.SetLength(0);
                _outputDrainScheduled = false;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _renderer.Dispose();
        _search.Dispose();
        _rendererDisposed = true;
    }

    public async Task CopyAsync(bool singleLine = false)
    {
        var options = InteractionOptions.Copy with { SingleLine = singleLine };
        await CopyAsync(options).ConfigureAwait(true);
    }

    public async Task<TerminalClipboardPayload?> CopyAsync(TerminalCopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = BuildCopyPayload(options);
        if (payload is null)
        {
            return null;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return payload;
        }

        var data = CreateClipboardDataTransfer(payload);
        await clipboard.SetDataAsync(data).ConfigureAwait(true);
        await clipboard.FlushAsync().ConfigureAwait(true);
        return payload;
    }

    public TerminalClipboardPayload? BuildCopyPayload(TerminalCopyOptions? options = null)
    {
        if (_selection is null)
        {
            return null;
        }

        options ??= InteractionOptions.Copy;
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var selected = TerminalInteractionModel.GetSelectedText(
            snapshot,
            _selection,
            options.TrimBlockSelection);
        return string.IsNullOrEmpty(selected)
            ? null
            : TerminalInteractionModel.BuildClipboardPayload(selected, options);
    }

    internal static DataTransfer CreateClipboardDataTransfer(TerminalClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var item = DataTransferItem.CreateText(payload.Text);
        if (payload.Html is not null)
        {
            item.Set(HtmlClipboardFormat, Encoding.UTF8.GetBytes(payload.Html));
        }

        if (payload.Rtf is not null)
        {
            item.Set(RtfClipboardFormat, Encoding.ASCII.GetBytes(payload.Rtf));
        }

        var data = new DataTransfer();
        data.Add(item);
        return data;
    }

    public async Task<TerminalPasteResult> PasteAsync()
    {
        return await PasteAsync(InteractionOptions.Paste).ConfigureAwait(true);
    }

    public async Task<TerminalPasteResult> PasteAsync(TerminalPasteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard is null ? null : await clipboard.TryGetTextAsync().ConfigureAwait(true);
        return PasteText(text, options);
    }

    public TerminalPasteResult PasteText(string? text, TerminalPasteOptions? options = null)
    {
        var request = TerminalInteractionModel.PreparePaste(
            text,
            options ?? InteractionOptions.Paste,
            Engine.BracketedPaste);
        if (request.Text.Length == 0 && !request.BracketedPaste)
        {
            return TerminalPasteResult.Empty;
        }

        if (request.RequiresConfirmation)
        {
            var args = new TerminalPasteWarningEventArgs(request);
            if (PasteWarning is not null)
            {
                PasteWarning.Invoke(this, args);
                if (!args.Allow)
                {
                    return TerminalPasteResult.Cancelled;
                }
            }
        }

        if (_connection is null)
        {
            return TerminalPasteResult.NoConnection;
        }

        _connection.Write(Engine.WrapPaste(request.Text));
        SetScrollOffset(0);
        return TerminalPasteResult.Written;
    }

    public void ClearBuffer()
    {
        Engine.Feed("\u001b[3J\u001b[2J\u001b[H");
        SetSelection(null);
        InvalidateVisual();
    }

    public void WriteInput(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _connection?.Write(input);
        SetScrollOffset(0);
    }

    public void SelectAll()
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        SetSelection(new TerminalSelection(
            new TerminalSelectionPoint(0, 0),
            new TerminalSelectionPoint(snapshot.Columns - 1, snapshot.Lines.Count - 1)));
    }

    public void ClearSelection() => SetSelection(null);

    public void BeginSelection(
        int viewportColumn,
        int viewportRow,
        TerminalSelectionMode mode = TerminalSelectionMode.Linear)
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var point = ViewportToBuffer(snapshot, viewportColumn, viewportRow);
        SetSelection(TerminalInteractionModel.SelectAt(
            snapshot,
            point,
            mode,
            InteractionOptions.WordDelimiters));
        _selecting = true;
    }

    public void UpdateSelection(int viewportColumn, int viewportRow)
    {
        if (_selection is null)
        {
            return;
        }

        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var point = ViewportToBuffer(snapshot, viewportColumn, viewportRow);
        SetSelection(_selection.ActiveEndpoint == TerminalSelectionEndpoint.Active
            ? _selection with { Active = point }
            : _selection with { Anchor = point });
    }

    public void EndSelection()
    {
        _selecting = false;
        if (!_isMarkMode &&
            _selection is { } selection &&
            selection.Anchor == selection.Active)
        {
            SetSelection(null);
            return;
        }

        if (InteractionOptions.CopyOnSelect && _selection is not null)
        {
            ObserveInteractionAsync("copy on select", CopyAsync(InteractionOptions.Copy));
        }
    }

    public void SelectWordAt(int viewportColumn, int viewportRow) =>
        BeginAndEndSelection(viewportColumn, viewportRow, TerminalSelectionMode.Word);

    public void SelectLineAt(int viewportColumn, int viewportRow) =>
        BeginAndEndSelection(viewportColumn, viewportRow, TerminalSelectionMode.Line);

    public void SelectCommandAt(int viewportColumn, int viewportRow) =>
        BeginAndEndSelection(viewportColumn, viewportRow, TerminalSelectionMode.Command);

    public void SelectOutputAt(int viewportColumn, int viewportRow) =>
        BeginAndEndSelection(viewportColumn, viewportRow, TerminalSelectionMode.Output);

    public bool SelectCommand(TerminalShellSelectionDirection direction) =>
        SelectShellRegion(direction, selectOutput: false);

    public bool SelectOutput(TerminalShellSelectionDirection direction) =>
        SelectShellRegion(direction, selectOutput: true);

    public void ExpandSelectionToWord()
    {
        if (_selection is null)
        {
            return;
        }

        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        SetSelection(TerminalInteractionModel.ExpandToWord(
            snapshot,
            _selection,
            InteractionOptions.WordDelimiters));
    }

    public void ToggleBlockSelection()
    {
        if (_selection is null)
        {
            return;
        }

        SetSelection(_selection with
        {
            Mode = _selection.Mode == TerminalSelectionMode.Block
                ? TerminalSelectionMode.Linear
                : TerminalSelectionMode.Block,
        });
    }

    private bool SelectShellRegion(
        TerminalShellSelectionDirection direction,
        bool selectOutput)
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var ranges = TerminalBufferExport.GetShellCommandRanges(snapshot)
            .Select(range => selectOutput ? range.Output : range.Command)
            .Where(static range => range is not null)
            .Select(static range => range!.Value)
            .ToArray();
        if (ranges.Length == 0)
        {
            return false;
        }

        var current = _selection is null
            ? new TerminalSelectionPoint(
                snapshot.CursorX,
                snapshot.HistoryCount + snapshot.CursorY)
            : direction == TerminalShellSelectionDirection.Previous
                ? Min(_selection.Anchor, _selection.Active)
                : Max(_selection.Anchor, _selection.Active);
        var candidates = ranges
            .Where(range => direction == TerminalShellSelectionDirection.Previous
                ? Compare(range.Start, current) < 0
                : Compare(range.Start, current) > 0)
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        var range = direction == TerminalShellSelectionDirection.Previous
            ? candidates[^1]
            : candidates[0];
        SetSelection(new TerminalSelection(
            new TerminalSelectionPoint(range.Start.Column, range.Start.Line),
            new TerminalSelectionPoint(
                Math.Max(0, range.End.Column - 1),
                range.End.Line),
            selectOutput ? TerminalSelectionMode.Output : TerminalSelectionMode.Command));
        return true;
    }

    private static TerminalSelectionPoint Min(
        TerminalSelectionPoint left,
        TerminalSelectionPoint right) =>
        Compare(left, right) <= 0 ? left : right;

    private static TerminalSelectionPoint Max(
        TerminalSelectionPoint left,
        TerminalSelectionPoint right) =>
        Compare(left, right) >= 0 ? left : right;

    private static int Compare(BufferPosition left, TerminalSelectionPoint right)
    {
        var line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }

    private static int Compare(
        TerminalSelectionPoint left,
        TerminalSelectionPoint right)
    {
        var line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }

    public void EnterMarkMode()
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        _markCaret = new TerminalSelectionPoint(
            snapshot.CursorX,
            snapshot.HistoryCount + snapshot.CursorY);
        _isMarkMode = true;
        SetSelection(new TerminalSelection(_markCaret, _markCaret));
    }

    public void ExitMarkMode(bool clearSelection = false)
    {
        _isMarkMode = false;
        if (clearSelection)
        {
            SetSelection(null);
        }
        else
        {
            AccessibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SwitchSelectionEndpoint()
    {
        if (_selection is null)
        {
            return;
        }

        var endpoint = _selection.ActiveEndpoint == TerminalSelectionEndpoint.Active
            ? TerminalSelectionEndpoint.Anchor
            : TerminalSelectionEndpoint.Active;
        _markCaret = endpoint == TerminalSelectionEndpoint.Active
            ? _selection.Active
            : _selection.Anchor;
        SetSelection(_selection with { ActiveEndpoint = endpoint });
    }

    public void MoveMarkCaret(int columns, int rows, bool extend = true)
    {
        if (!_isMarkMode)
        {
            EnterMarkMode();
        }

        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        _markCaret = TerminalInteractionModel.Clamp(
            snapshot,
            new TerminalSelectionPoint(_markCaret.Column + columns, _markCaret.Line + rows));
        if (!extend || _selection is null)
        {
            SetSelection(new TerminalSelection(_markCaret, _markCaret));
            return;
        }

        SetSelection(_selection.ActiveEndpoint == TerminalSelectionEndpoint.Active
            ? _selection with { Active = _markCaret }
            : _selection with { Anchor = _markCaret });
    }

    public void AdjustFontSize(double delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 1, 72);
        if (Profile is not null)
        {
            ConfigureRenderer(Profile);
        }

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
        if (Profile is not null)
        {
            ConfigureRenderer(Profile);
        }

        if (VisualRoot is not null)
        {
            MeasureGlyph();
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    public void ScrollBy(int rows)
    {
        SetScrollOffset(Engine.Buffer.ScrollOffset + rows);
    }

    public void ScrollPage(int direction) => ScrollBy(direction * Math.Max(1, Engine.Rows - 1));

    public void ScrollToTop()
    {
        SetScrollOffset(Engine.Buffer.HistoryCount);
    }

    public void ScrollToBottom()
    {
        SetScrollOffset(0);
    }

    public void SetScrollOffset(int offset)
    {
        var normalized = Math.Clamp(offset, 0, Engine.Buffer.HistoryCount);
        if (Engine.Buffer.ScrollOffset == normalized)
        {
            return;
        }

        Engine.Buffer.ScrollOffset = normalized;
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public bool Find(string query, bool previous = false)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _search.Clear();
            _searchHighlights = [];
            return false;
        }

        if (!string.Equals(_search.Query, query, StringComparison.Ordinal))
        {
            _search.Update(query);
        }
        else if (!_search.MoveNext(reverse: previous))
        {
            return false;
        }

        if (_search.Current is not { } current)
        {
            return false;
        }

        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        SetSelection(new TerminalSelection(
            new TerminalSelectionPoint(current.Start.Column, current.Start.Line),
            new TerminalSelectionPoint(
                Math.Max(current.Start.Column, current.End.Column - 1),
                current.End.Line)));
        UpdateSearchHighlights();
        return true;
    }

    public void ResetTerminal()
    {
        lock (_outputLock)
        {
            _pendingOutput.SetLength(0);
            _outputDrainScheduled = false;
        }

        Engine.Reset();
        SetSelection(null);
        _isMarkMode = false;
        _composition = null;
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

        var selection = CreateSelectionOverlays(frame);
        var overlays = new TerminalRenderOverlays(
            selection,
            _searchHighlights,
            _hoveredHyperlink)
        {
            Composition = Engine.Buffer.ScrollOffset == 0 ? _composition : null,
        };
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

    protected override AutomationPeer OnCreateAutomationPeer() => new TermControlAutomationPeer(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleMarkModeKey(e))
        {
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        var vt = KeyMapper.ToVt(e.Key, e.KeyModifiers, e.PhysicalKey, e.KeySymbol, Engine.ApplicationCursorKeys);
        if (vt is not null)
        {
            _connection?.Write(vt);
            SetScrollOffset(0);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && e.Text != "\r" && e.Text != "\n" && e.Text != "\t")
        {
            _connection?.Write(e.Text);
            SetScrollOffset(0);
            e.Handled = true;
        }

        base.OnTextInput(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(this);
        var (x, y) = HitTest(point.Position);
        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchPoint = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        if (Engine.MouseTracking)
        {
            _pressedMouseButton = PointerButton(point);
            WriteMouseInput(button: _pressedMouseButton, x, y, released: false, e.KeyModifiers);
            e.Pointer.Capture(this);
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        var hyperlink = HitTestHyperlink(x, y);
        if (point.Properties.IsRightButtonPressed && hyperlink is not null)
        {
            HyperlinkContextRequested?.Invoke(this, new TerminalHyperlinkEventArgs(hyperlink));
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        if (point.Properties.IsLeftButtonPressed &&
            (e.KeyModifiers & KeyModifiers.Control) != 0 &&
            hyperlink is not null)
        {
            ObserveInteractionAsync("open hyperlink", OpenHyperlinkAsync(hyperlink));
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        if (point.Properties.IsLeftButtonPressed &&
            (e.KeyModifiers & KeyModifiers.Alt) != 0 &&
            Profile?.RepositionCursorWithMouse == true)
        {
            var sequence = TerminalInteractionModel.BuildCursorRepositionSequence(
                Engine.CursorX,
                Engine.CursorY,
                x,
                y,
                Engine.ApplicationCursorKeys);
            _connection?.Write(sequence);
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            var mode = e.ClickCount switch
            {
                >= 3 => TerminalSelectionMode.Line,
                2 => TerminalSelectionMode.Word,
                _ when (e.KeyModifiers & KeyModifiers.Alt) != 0 => TerminalSelectionMode.Block,
                _ => TerminalSelectionMode.Linear,
            };
            BeginSelection(x, y, mode);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        UpdateHoveredHyperlink(e.GetPosition(this));
        if (e.Pointer.Type != PointerType.Touch &&
            Engine.MouseTracking &&
            (Engine.MouseTrackingMode == TerminalMouseTrackingMode.AllMotion ||
             (Engine.MouseTrackingMode == TerminalMouseTrackingMode.ButtonEvent &&
              _pressedMouseButton >= 0)))
        {
            var (mouseX, mouseY) = HitTest(e.GetPosition(this));
            var button = (_pressedMouseButton >= 0 ? _pressedMouseButton : 3) | 32;
            WriteMouseInput(button, mouseX, mouseY, released: false, e.KeyModifiers);
            e.Handled = true;
            base.OnPointerMoved(e);
            return;
        }

        if (_touchPoint is { } previous)
        {
            var current = e.GetPosition(this);
            var rows = (int)Math.Truncate((previous.Y - current.Y) / Math.Max(1, _cellHeight));
            if (rows != 0)
            {
                ScrollBy(rows);
                _touchPoint = current;
            }

            e.Handled = true;
            base.OnPointerMoved(e);
            return;
        }

        if (_selecting)
        {
            var (x, y) = HitTest(e.GetPosition(this));
            UpdateSelection(x, y);
            e.Handled = true;
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
        if (Engine.MouseTracking && _pressedMouseButton >= 0)
        {
            var point = e.GetCurrentPoint(this);
            var (x, y) = HitTest(point.Position);
            WriteMouseInput(
                button: PointerButton(e.InitialPressMouseButton),
                x,
                y,
                released: true,
                e.KeyModifiers);
            _pressedMouseButton = -1;
        }

        _touchPoint = null;
        EndSelection();
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && InteractionOptions.ScrollToZoom)
        {
            AdjustFontSize(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            base.OnPointerWheelChanged(e);
            return;
        }

        if (Engine.MouseTracking)
        {
            var (x, y) = HitTest(e.GetPosition(this));
            var button = e.Delta.Y > 0 ? 64 : 65;
            WriteMouseInput(button, x, y, released: false, e.KeyModifiers);
            e.Handled = true;
            base.OnPointerWheelChanged(e);
            return;
        }

        var delta = (int)Math.Round(e.Delta.Y * 3);
        SetScrollOffset(Engine.Buffer.ScrollOffset + delta);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
    {
        var scheduleDrain = false;
        lock (_outputLock)
        {
            if (!_acceptOutput)
            {
                return;
            }

            _pendingOutput.Write(data.Span);
            if (!_outputDrainScheduled)
            {
                _outputDrainScheduled = true;
                scheduleDrain = true;
            }
        }

        if (scheduleDrain)
        {
            Dispatcher.UIThread.Post(DrainOutput, DispatcherPriority.Render);
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
            if (_pendingOutput.Length == 0)
            {
                _outputDrainScheduled = false;
                return;
            }

            chunk = _pendingOutput.ToArray();
            _pendingOutput.SetLength(0);
            _outputDrainScheduled = false;
        }

        Engine.Feed(chunk);
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
        var width = _renderer.CellSize.Width;
        var height = _renderer.CellSize.Height;
        if (Math.Abs(_cellWidth - width) < 0.001 &&
            Math.Abs(_cellHeight - height) < 0.001)
        {
            return;
        }

        _cellWidth = width;
        _cellHeight = height;
        InvalidateMeasure();
    }

    private void UpdateSearchHighlights()
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var viewportTop = snapshot.HistoryCount - Engine.Buffer.ScrollOffset;
        var ranges = new List<TerminalCellRange>();
        foreach (var match in _search.Matches)
        {
            for (var line = match.Start.Line; line <= match.End.Line; line++)
            {
                var visibleRow = line - viewportTop;
                if (visibleRow < 0 || visibleRow >= snapshot.Rows)
                {
                    continue;
                }

                var start = line == match.Start.Line ? match.Start.Column : 0;
                var endExclusive = line == match.End.Line ? match.End.Column : snapshot.Columns;
                if (endExclusive > start)
                {
                    ranges.Add(new TerminalCellRange(
                        visibleRow,
                        start,
                        endExclusive - 1,
                        0x604080FF));
                }
            }
        }

        _searchHighlights = ranges;
        InvalidateVisual();
    }

    public IReadOnlyList<TerminalScrollMark> GetScrollMarks()
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        return TerminalInteractionModel.GetScrollMarks(
            snapshot,
            _search.Matches,
            _search.CurrentIndex);
    }

    public TerminalHyperlinkContext? HitTestHyperlink(int viewportColumn, int viewportRow)
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        return TerminalInteractionModel.HitTestHyperlink(
            snapshot,
            ViewportToBuffer(snapshot, viewportColumn, viewportRow),
            InteractionOptions.SafeUriSchemes);
    }

    public async Task<bool> OpenHyperlinkAsync(TerminalHyperlinkContext hyperlink)
    {
        ArgumentNullException.ThrowIfNull(hyperlink);
        var args = new TerminalHyperlinkEventArgs(hyperlink);
        HyperlinkOpenRequested?.Invoke(this, args);
        if (args.Handled)
        {
            return true;
        }

        if (!hyperlink.CanOpen)
        {
            throw new InvalidOperationException($"The hyperlink scheme is not allowed: {hyperlink.Uri}");
        }

        var startInfo = new ProcessStartInfo(hyperlink.Uri)
        {
            UseShellExecute = true,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to open hyperlink: {hyperlink.Uri}");
        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    public async Task CopyHyperlinkAsync(TerminalHyperlinkContext hyperlink)
    {
        ArgumentNullException.ThrowIfNull(hyperlink);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
            ?? throw new InvalidOperationException("No clipboard is available for this control.");
        await clipboard.SetTextAsync(hyperlink.Uri).ConfigureAwait(true);
        await clipboard.FlushAsync().ConfigureAwait(true);
    }

    internal ImeContext GetImeContext()
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var lineIndex = Math.Clamp(snapshot.HistoryCount + snapshot.CursorY, 0, snapshot.Lines.Count - 1);
        var cells = snapshot.Lines[lineIndex].Cells;
        var output = new StringBuilder();
        var cursorTextOffset = 0;
        for (var column = 0; column < cells.Count; column++)
        {
            if (column == snapshot.CursorX)
            {
                cursorTextOffset = output.Length;
            }

            if (!cells[column].IsWideContinuation)
            {
                output.Append(cells[column].Text);
            }
        }

        if (snapshot.CursorX >= cells.Count)
        {
            cursorTextOffset = output.Length;
        }

        var text = output.ToString();
        var retainedLength = Math.Max(text.TrimEnd().Length, cursorTextOffset);
        return new ImeContext(text[..retainedLength], Math.Min(cursorTextOffset, retainedLength));
    }

    internal Rect GetImeCursorRectangle()
    {
        const double padding = 8;
        return new Rect(
            padding + (Engine.CursorX * _cellWidth),
            padding + (Engine.CursorY * _cellHeight),
            _cellWidth,
            _cellHeight);
    }

    internal void SetImeSelectionOffset(int offset)
    {
        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var lineIndex = Math.Clamp(snapshot.HistoryCount + snapshot.CursorY, 0, snapshot.Lines.Count - 1);
        var cells = snapshot.Lines[lineIndex].Cells;
        var textOffset = 0;
        var targetColumn = cells.Count - 1;
        for (var column = 0; column < cells.Count; column++)
        {
            if (cells[column].IsWideContinuation)
            {
                continue;
            }

            if (textOffset >= offset)
            {
                targetColumn = column;
                break;
            }

            textOffset += cells[column].Text.Length;
            targetColumn = column + 1 < cells.Count && cells[column + 1].IsWideContinuation
                ? column + 2
                : column + 1;
        }

        targetColumn = Math.Clamp(targetColumn, 0, cells.Count - 1);
        var delta = targetColumn - Engine.CursorX;
        if (delta == 0)
        {
            return;
        }

        _connection?.Write(TerminalInteractionModel.BuildCursorRepositionSequence(
            Engine.CursorX,
            Engine.CursorY,
            Engine.CursorX + delta,
            Engine.CursorY,
            Engine.ApplicationCursorKeys));
    }

    internal void SetImeComposition(string text, int? cursorOffset)
    {
        _composition = string.IsNullOrEmpty(text)
            ? null
            : new TerminalCompositionOverlay(
                Engine.CursorY,
                Engine.CursorX,
                text,
                cursorOffset);
        InvalidateVisual();
        AccessibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void SetClipboardFromTerminalObservedAsync(string text)
    {
        try
        {
            await SetClipboardFromTerminalAsync(text).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            InteractionError?.Invoke(
                this,
                new TerminalInteractionErrorEventArgs("OSC 52 clipboard write", exception));
        }
    }

    private async Task SetClipboardFromTerminalAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            throw new InvalidOperationException("OSC 52 requested a clipboard write, but no clipboard is available.");
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(true);
        await clipboard.FlushAsync().ConfigureAwait(true);
    }

    private void ConfigureRenderer(ProfileSettings profile)
    {
        _renderer.Configure(CreateRendererSettings(profile, _fontSize));
    }

    private static TerminalRendererSettings CreateRendererSettings(
        ProfileSettings profile,
        double fontSize) =>
        new()
        {
            FontFamily = profile.FontFace,
            FontSize = (float)fontSize,
            FontWeight = profile.FontWeight,
            FontSources =
            [
                new TerminalFontSource("Cascadia Mono", false, OpenCascadiaMono),
                new TerminalFontSource("Cascadia Mono", true, OpenCascadiaMonoItalic),
            ],
        };

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

    private IReadOnlyList<TerminalCellRange> CreateSelectionOverlays(TerminalRenderFrame frame)
    {
        if (_selection is null)
        {
            return [];
        }

        var snapshot = Engine.CreateSnapshot(includeHistory: true).Buffer;
        var range = TerminalInteractionModel.Normalize(snapshot, _selection);
        var viewportTop = snapshot.HistoryCount - snapshot.ScrollOffset;
        var startRow = range.Start.Line - viewportTop;
        var endRow = range.End.Line - viewportTop;
        if (range.Mode != TerminalSelectionMode.Block)
        {
            if (endRow < 0 || startRow >= frame.Rows)
            {
                return [];
            }

            var startColumn = startRow < 0 ? 0 : range.Start.Column;
            var endColumn = endRow >= frame.Rows ? frame.Columns - 1 : range.End.Column;
            return TerminalOverlayPlanner.CreateSelection(
                startColumn,
                Math.Max(0, startRow),
                endColumn,
                Math.Min(frame.Rows - 1, endRow),
                frame.Columns,
                frame.Rows,
                frame.SelectionColor);
        }

        var visibleStart = Math.Max(0, startRow);
        var visibleEnd = Math.Min(frame.Rows - 1, endRow);
        if (visibleStart > visibleEnd)
        {
            return [];
        }

        return Enumerable.Range(visibleStart, visibleEnd - visibleStart + 1)
            .Select(row => new TerminalCellRange(
                row,
                range.Start.Column,
                range.End.Column,
                frame.SelectionColor))
            .ToArray();
    }

    private void SetSelection(TerminalSelection? selection)
    {
        if (_selection == selection)
        {
            return;
        }

        _selection = selection;
        _selectionCoordinateVersion = Engine.Buffer.CoordinateVersion;
        _selectionAlternateBuffer = Engine.AlternateBufferActive;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        AccessibilityChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void BeginAndEndSelection(
        int viewportColumn,
        int viewportRow,
        TerminalSelectionMode mode)
    {
        BeginSelection(viewportColumn, viewportRow, mode);
        EndSelection();
    }

    private static TerminalSelectionPoint ViewportToBuffer(
        TextBufferSnapshot snapshot,
        int viewportColumn,
        int viewportRow)
    {
        var top = snapshot.HistoryCount - snapshot.ScrollOffset;
        return TerminalInteractionModel.Clamp(
            snapshot,
            new TerminalSelectionPoint(viewportColumn, top + viewportRow));
    }

    private bool HandleMarkModeKey(KeyEventArgs e)
    {
        if (!_isMarkMode)
        {
            return false;
        }

        var extend = (e.KeyModifiers & KeyModifiers.Shift) != 0 || _selection is not null;
        switch (e.Key)
        {
            case Key.Left:
                MoveMarkCaret(-1, 0, extend);
                return true;
            case Key.Right:
                MoveMarkCaret(1, 0, extend);
                return true;
            case Key.Up:
                MoveMarkCaret(0, -1, extend);
                return true;
            case Key.Down:
                MoveMarkCaret(0, 1, extend);
                return true;
            case Key.Home:
                MoveMarkCaret(-Engine.Columns, 0, extend);
                return true;
            case Key.End:
                MoveMarkCaret(Engine.Columns, 0, extend);
                return true;
            case Key.PageUp:
                MoveMarkCaret(0, -Engine.Rows, extend);
                return true;
            case Key.PageDown:
                MoveMarkCaret(0, Engine.Rows, extend);
                return true;
            case Key.Space:
                SwitchSelectionEndpoint();
                return true;
            case Key.A when (e.KeyModifiers & KeyModifiers.Control) != 0:
                SelectAll();
                return true;
            case Key.W when (e.KeyModifiers & KeyModifiers.Control) != 0:
                ExpandSelectionToWord();
                return true;
            case Key.Enter:
                ExitMarkMode();
                return true;
            case Key.Escape:
                ExitMarkMode(clearSelection: true);
                return true;
            default:
                return false;
        }
    }

    private void SendFocusChanged(bool focused)
    {
        if (Engine.FocusTracking)
        {
            _connection?.Write(focused ? "\u001b[I" : "\u001b[O");
        }
    }

    private void WriteMouseInput(
        int button,
        int x,
        int y,
        bool released,
        KeyModifiers modifiers)
    {
        _connection?.Write(TerminalInteractionModel.BuildMouseSequence(
            button,
            x,
            y,
            released,
            Engine.SgrMouse,
            modifiers));
    }

    private static int PointerButton(PointerPoint point)
    {
        if (point.Properties.IsRightButtonPressed)
        {
            return 2;
        }

        if (point.Properties.IsMiddleButtonPressed)
        {
            return 1;
        }

        return 0;
    }

    private static int PointerButton(MouseButton button) =>
        button switch
        {
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            _ => 0,
        };

    private void OnTextInputMethodClientRequested(
        object? sender,
        TextInputMethodClientRequestedEventArgs e)
    {
        e.Client = _textInputMethodClient;
    }

    private async void ObserveInteractionAsync(string operation, Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            InteractionError?.Invoke(this, new TerminalInteractionErrorEventArgs(operation, exception));
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
        AssetLoader.Open(new Uri("avares://Terminal.Control/Assets/Fonts/CascadiaMono.ttf"));

    private static Stream OpenCascadiaMonoItalic() =>
        AssetLoader.Open(new Uri("avares://Terminal.Control/Assets/Fonts/CascadiaMonoItalic.ttf"));

    private static TerminalCloseOnExitPolicy ToConnectionPolicy(CloseOnExitMode mode) =>
        mode switch
        {
            CloseOnExitMode.Never => TerminalCloseOnExitPolicy.Never,
            CloseOnExitMode.Graceful => TerminalCloseOnExitPolicy.Graceful,
            CloseOnExitMode.Always => TerminalCloseOnExitPolicy.Always,
            _ => TerminalCloseOnExitPolicy.Automatic,
        };

    private IReadOnlyDictionary<string, string?> BuildTerminalEnvironment(ProfileSettings profile)
    {
        var environment = new Dictionary<string, string?>(
            profile.Environment,
            StringComparer.OrdinalIgnoreCase)
        {
            ["WT_SESSION"] = _terminalSessionId.ToString("D"),
            ["WT_PROFILE_ID"] = Guid.TryParse(profile.Guid, out var profileId)
                ? profileId.ToString("B")
                : profile.Guid,
        };
        var inheritedWslEnvironment = profile.ReloadEnvironmentVariables
            ? Environment.GetEnvironmentVariable("WSLENV") ?? string.Empty
            : string.Empty;
        var wslEnvironment = profile.Environment.TryGetValue("WSLENV", out var configuredWslEnvironment)
            ? configuredWslEnvironment ?? string.Empty
            : inheritedWslEnvironment;
        var wslVariables = new HashSet<string>(
            wslEnvironment
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => value.Split('/')[0]),
            StringComparer.OrdinalIgnoreCase);
        var additionalWslVariables = new List<string> { "WT_SESSION", "WT_PROFILE_ID" };
        additionalWslVariables.AddRange(profile.Environment
            .Where(static pair =>
                pair.Value is not null &&
                !pair.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals("WSLENV", StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Key));
        var newWslVariables = new List<string>();
        foreach (var variable in additionalWslVariables)
        {
            if (wslVariables.Add(variable))
            {
                newWslVariables.Add(variable);
            }
        }

        if (newWslVariables.Count > 0)
        {
            var additions = string.Join(':', newWslVariables);
            wslEnvironment = string.IsNullOrEmpty(wslEnvironment)
                ? additions
                : $"{additions}:{wslEnvironment}";
        }

        environment["WSLENV"] = wslEnvironment;
        return environment;
    }

    internal readonly record struct ImeContext(string Text, int CursorTextOffset);
}
