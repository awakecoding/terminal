using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Ghostty;

public sealed unsafe class GhosttyTerminalEngine : ITerminalEngine
{
    private const int TerminalOptionUserData = 0;
    private const int TerminalOptionWritePty = 1;
    private const int TerminalOptionBell = 2;
    private const int TerminalOptionTitleChanged = 5;
    private const int TerminalOptionSize = 6;
    private const int TerminalOptionPwdChanged = 25;
    private const int TerminalOptionClipboardWrite = 26;
    private const int TerminalOptionDesktopNotification = 29;

    private const int TerminalDataActiveScreen = 6;
    private const int TerminalDataTitle = 12;
    private const int TerminalDataPwd = 13;
    private const int TerminalDataMode = 37;

    private const int RenderDataCols = 1;
    private const int RenderDataRows = 2;
    private const int RenderDataRowIterator = 4;
    private const int RenderDataCursor = 18;
    private const int RenderRowDataRaw = 2;
    private const int RenderRowDataCells = 3;
    private const int RenderCellDataStyle = 2;
    private const int RenderCellDataRaw = 1;
    private const int RenderCellDataGraphemeLength = 3;
    private const int RenderCellDataGraphemeBuffer = 4;
    private const int RenderCellDataBackground = 5;
    private const int RenderCellDataForeground = 6;

    private readonly object _gate = new();
    private readonly SafeGhosttyTerminalHandle _terminal;
    private readonly SafeGhosttyRenderStateHandle _renderState;
    private readonly SafeGhosttyRowIteratorHandle _rowIterator;
    private readonly SafeGhosttyRowCellsHandle _rowCells;
    private readonly GCHandle _selfHandle;
    private ColorScheme _scheme = ColorScheme.Campbell;
    private bool _disposed;
    private bool _allowClipboardWrite;
    private bool _allowNotifications;
    private string _title = "Windows Terminal";
    private string? _workingDirectory;
    private GhosttyRenderCursor _cursor;
    private Exception? _callbackException;
    private SafeGhosttyTrackedGridRefHandle? _historyAnchor;
    private readonly int _historyLimit;
    private uint _cellWidthPixels = 1;
    private uint _cellHeightPixels = 1;

    public GhosttyTerminalEngine(int columns = 120, int rows = 30, int historySize = 9001)
    {
        GhosttyAbi.Validate();
        _historyLimit = Math.Max(0, historySize);
        Buffer = new TextBuffer(columns, rows, historySize, hasHistory: true);
        nint terminal = 0;
        nint renderState = 0;
        nint rowIterator = 0;
        nint rowCells = 0;
        try
        {
            ThrowIfFailed(
                GhosttyNative.TerminalNew(
                    0,
                    out terminal,
                    checked((ushort)columns),
                    checked((ushort)rows)),
                "ghostty_terminal_new");
            ThrowIfFailed(GhosttyNative.RenderStateNew(0, out renderState), "ghostty_render_state_new");
            ThrowIfFailed(GhosttyNative.RowIteratorNew(0, out rowIterator), "ghostty_render_state_row_iterator_new");
            ThrowIfFailed(GhosttyNative.RowCellsNew(0, out rowCells), "ghostty_render_state_row_cells_new");
        }
        catch
        {
            if (rowCells != 0) GhosttyNative.RowCellsFree(rowCells);
            if (rowIterator != 0) GhosttyNative.RowIteratorFree(rowIterator);
            if (renderState != 0) GhosttyNative.RenderStateFree(renderState);
            if (terminal != 0) GhosttyNative.TerminalFree(terminal);
            throw;
        }

        _terminal = new SafeGhosttyTerminalHandle(terminal);
        _renderState = new SafeGhosttyRenderStateHandle(renderState);
        _rowIterator = new SafeGhosttyRowIteratorHandle(rowIterator);
        _rowCells = new SafeGhosttyRowCellsHandle(rowCells);
        _selfHandle = GCHandle.Alloc(this);
        try
        {
            RegisterEffects();
            var scrollbackLines = (nuint)Math.Max(0, historySize);
            if (historySize == 0)
            {
                var scrollbackBytes = (nuint)0;
                ThrowIfFailed(
                    GhosttyNative.TerminalSet(
                        _terminal.DangerousGetHandle(),
                        27,
                        &scrollbackBytes),
                    "disable scrollback bytes");
            }
            else
            {
                ThrowIfFailed(
                    GhosttyNative.TerminalSet(
                        _terminal.DangerousGetHandle(),
                        27,
                        null),
                    "remove scrollback byte limit");
            }

            ThrowIfFailed(
                GhosttyNative.TerminalSet(
                    _terminal.DangerousGetHandle(),
                    28,
                    &scrollbackLines),
                "set scrollback line limit");
            ApplyScheme();
            SynchronizeProjection();
            ResetHistoryAnchor();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public TextBuffer Buffer { get; }

    public ColorScheme Scheme
    {
        get => _scheme;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _scheme = value;
                ApplyScheme();
                SynchronizeProjection();
            }

            Invalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Title => _title;
    public string? WorkingDirectory => _workingDirectory;
    public bool AlternateBufferActive => QueryInt(TerminalDataActiveScreen) == 1;
    public bool CursorVisible => _cursor.ViewportHasValue != 0 && _cursor.Visible != 0;
    public bool CursorBlinking => _cursor.Blinking != 0;
    public bool ApplicationCursorKeys => QueryMode(1);
    public bool BracketedPaste => QueryMode(2004);
    public bool MouseTracking => QueryMode(9) || QueryMode(1000) || QueryMode(1002) || QueryMode(1003);
    public TerminalMouseTrackingMode MouseTrackingMode =>
        QueryMode(1003) ? TerminalMouseTrackingMode.AllMotion :
        QueryMode(1002) ? TerminalMouseTrackingMode.ButtonEvent :
        QueryMode(1000) ? TerminalMouseTrackingMode.Button :
        QueryMode(9) ? TerminalMouseTrackingMode.Button :
        TerminalMouseTrackingMode.None;
    public bool SgrMouse => QueryMode(1006);
    public bool FocusTracking => QueryMode(1004);
    public bool AutoWrap => QueryMode(7);
    public bool InsertMode => QueryMode(4, ansi: true);
    public bool ReverseVideo => QueryMode(5);
    public int Columns => Buffer.Columns;
    public int Rows => Buffer.Rows;
    public int CursorX => Buffer.CursorX;
    public int CursorY => Buffer.CursorY;
    public uint PixelWidth => QueryUInt32(16);
    public uint PixelHeight => QueryUInt32(17);
    public int HistoryCount => ReadScrollbar().HistoryCount;
    public int ScrollOffset => ReadScrollbar().ScrollOffset;

    public event EventHandler? Invalidated;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string?>? WorkingDirectoryChanged;
    public event EventHandler? ShellIntegrationChanged;
    public event EventHandler<string>? ClipboardWriteRequested;
    public event EventHandler<TerminalNotification>? NotificationRequested;
    public event EventHandler? Bell;
    public event EventHandler<byte[]>? ResponseReady;

    public void Feed(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var trackedHistory = _historyAnchor is { IsInvalid: false, IsClosed: false };
            var historyBefore = ReadScrollbarCore().HistoryCount;
            fixed (byte* bytes = data)
            {
                _callbackException = null;
                GhosttyNative.TerminalWrite(_terminal.DangerousGetHandle(), bytes, (nuint)data.Length);
            }

            if (_callbackException is { } callbackException)
            {
                throw new InvalidOperationException(
                    "A libghostty-vt effect callback failed.",
                    callbackException);
            }

            SynchronizeProjection();
            var historyAfter = ReadScrollbarCore().HistoryCount;
            var initialEvictionPossible = historyBefore == 0 &&
                                          EstimateRows(data, Columns) >= historyAfter + Rows;
            var trackedHistoryEvicted = trackedHistory &&
                                        !GhosttyNative.TrackedGridRefHasValue(
                                            _historyAnchor!.DangerousGetHandle());
            if (trackedHistoryEvicted ||
                initialEvictionPossible ||
                !trackedHistory)
            {
                Buffer.AdvanceCoordinateVersion();
            }
            if (!trackedHistory || trackedHistoryEvicted)
            {
                ResetHistoryAnchor();
            }
        }

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Feed(Encoding.UTF8.GetBytes(text));
    }

    public void Resize(int columns, int rows, double cellWidth = 1, double cellHeight = 1)
    {
        columns = Math.Clamp(columns, 1, ushort.MaxValue);
        rows = Math.Clamp(rows, 1, ushort.MaxValue);
        _cellWidthPixels = checked((uint)Math.Max(1, Math.Round(cellWidth)));
        _cellHeightPixels = checked((uint)Math.Max(1, Math.Round(cellHeight)));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFailed(
                GhosttyNative.TerminalResize(
                    _terminal.DangerousGetHandle(),
                    (ushort)columns,
                    (ushort)rows,
                    _cellWidthPixels,
                    _cellHeightPixels),
                "ghostty_terminal_resize");
            Buffer.Resize(columns, rows);
            SynchronizeProjection();
        }

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            GhosttyNative.TerminalReset(_terminal.DangerousGetHandle());
            SynchronizeProjection();
            ResetHistoryAnchor();
        }

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void SetScrollOffset(int offset)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var scrollbar = ReadScrollbarCore();
            var normalized = Math.Clamp(offset, 0, scrollbar.HistoryCount);
            GhosttyNative.TerminalScrollViewport(
                _terminal.DangerousGetHandle(),
                new GhosttyScrollViewport
                {
                    Tag = 3,
                    Row = (nuint)(scrollbar.HistoryCount - normalized),
                });
            SynchronizeProjection();
        }

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void ConfigureOptionalFeatures(bool allowClipboardWrite, bool allowNotifications)
    {
        _allowClipboardWrite = allowClipboardWrite;
        _allowNotifications = allowNotifications;
    }

    public TerminalSnapshot CreateSnapshot(bool includeHistory = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var buffer = includeHistory ? CreateHistorySnapshot() : Buffer.CreateSnapshot();
            return new TerminalSnapshot(
                buffer,
                Title,
                WorkingDirectory,
                AlternateBufferActive,
                CursorVisible,
                ApplicationCursorKeys,
                BracketedPaste,
                MouseTracking,
                SgrMouse,
                AutoWrap,
                InsertMode,
                ReverseVideo);
        }
    }

    public string WrapPaste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return BracketedPaste
            ? "\u001b[200~" + text.Replace("\u001b", string.Empty, StringComparison.Ordinal) + "\u001b[201~"
            : text;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _rowCells.Dispose();
            _rowIterator.Dispose();
            _renderState.Dispose();
            _historyAnchor?.Dispose();
            _terminal.Dispose();
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }

        GC.SuppressFinalize(this);
    }

    private void RegisterEffects()
    {
        var userData = (void*)GCHandle.ToIntPtr(_selfHandle);
        var terminal = _terminal.DangerousGetHandle();
        ThrowIfFailed(GhosttyNative.TerminalSet(terminal, TerminalOptionUserData, userData), "set userdata");

        delegate* unmanaged[Cdecl]<nint, nint, byte*, nuint, void> writePty = &OnWritePty;
        ThrowIfFailed(GhosttyNative.TerminalSet(terminal, TerminalOptionWritePty, writePty), "set write PTY");

        delegate* unmanaged[Cdecl]<nint, nint, void> bell = &OnBell;
        ThrowIfFailed(GhosttyNative.TerminalSet(terminal, TerminalOptionBell, bell), "set bell");

        delegate* unmanaged[Cdecl]<nint, nint, void> stateChanged = &OnStateChanged;
        ThrowIfFailed(
            GhosttyNative.TerminalSet(terminal, TerminalOptionTitleChanged, stateChanged),
            "set title callback");
        ThrowIfFailed(
            GhosttyNative.TerminalSet(terminal, TerminalOptionPwdChanged, stateChanged),
            "set working-directory callback");

        delegate* unmanaged[Cdecl]<nint, nint, GhosttySizeReport*, byte> size = &OnSize;
        ThrowIfFailed(
            GhosttyNative.TerminalSet(terminal, TerminalOptionSize, size),
            "set size callback");

        delegate* unmanaged[Cdecl]<nint, nint, GhosttyClipboardWrite*, void> clipboardWrite = &OnClipboardWrite;
        ThrowIfFailed(
            GhosttyNative.TerminalSet(terminal, TerminalOptionClipboardWrite, clipboardWrite),
            "set clipboard callback");

        delegate* unmanaged[Cdecl]<nint, nint, GhosttyDesktopNotification*, void> notification = &OnNotification;
        ThrowIfFailed(
            GhosttyNative.TerminalSet(terminal, TerminalOptionDesktopNotification, notification),
            "set notification callback");
    }

    private void ApplyScheme()
    {
        var foreground = ToRgb(_scheme.Foreground);
        var background = ToRgb(_scheme.Background);
        var cursor = ToRgb(_scheme.Cursor);
        ThrowIfFailed(
            GhosttyNative.TerminalSet(_terminal.DangerousGetHandle(), 11, &foreground),
            "set foreground");
        ThrowIfFailed(
            GhosttyNative.TerminalSet(_terminal.DangerousGetHandle(), 12, &background),
            "set background");
        ThrowIfFailed(
            GhosttyNative.TerminalSet(_terminal.DangerousGetHandle(), 13, &cursor),
            "set cursor");

        var palette = stackalloc GhosttyColorRgb[256];
        for (var index = 0; index < 256; index++)
        {
            palette[index] = ToRgb(_scheme.Resolve(index));
        }

        ThrowIfFailed(
            GhosttyNative.TerminalSet(_terminal.DangerousGetHandle(), 14, palette),
            "set palette");
    }

    private void SynchronizeProjection()
    {
        ThrowIfFailed(
            GhosttyNative.RenderStateUpdate(
                _renderState.DangerousGetHandle(),
                _terminal.DangerousGetHandle()),
            "ghostty_render_state_update");

        var columns = GetRenderUInt16(RenderDataCols);
        var rows = GetRenderUInt16(RenderDataRows);
        if (Buffer.Columns != columns || Buffer.Rows != rows)
        {
            Buffer.Resize(columns, rows);
        }

        var cursor = new GhosttyRenderCursor { Size = (nuint)sizeof(GhosttyRenderCursor) };
        ThrowIfFailed(
            GhosttyNative.RenderStateGet(_renderState.DangerousGetHandle(), RenderDataCursor, &cursor),
            "get render cursor");
        _cursor = cursor;

        var iteratorValue = _rowIterator.DangerousGetHandle();
        ThrowIfFailed(
            GhosttyNative.RenderStateGet(
                _renderState.DangerousGetHandle(),
                RenderDataRowIterator,
                &iteratorValue),
            "get row iterator");

        var projectedRows = new List<TextBufferProjectionRow>(rows);
        var rowIndex = 0;
        while (GhosttyNative.RowIteratorNext(_rowIterator.DangerousGetHandle()))
        {
            ulong rawRow = 0;
            ThrowIfFailed(
                GhosttyNative.RowGet(
                    _rowIterator.DangerousGetHandle(),
                    RenderRowDataRaw,
                    &rawRow),
                "get raw row");
            var cellsValue = _rowCells.DangerousGetHandle();
            ThrowIfFailed(
                GhosttyNative.RowGet(
                    _rowIterator.DangerousGetHandle(),
                    RenderRowDataCells,
                    &cellsValue),
                "get row cells");
            byte wrapped = 0;
            ThrowIfFailed(GhosttyNative.RawRowGet(rawRow, 1, &wrapped), "get row wrap");
            var semanticPrompt = 0;
            ThrowIfFailed(
                GhosttyNative.RawRowGet(rawRow, 6, &semanticPrompt),
                "get row semantic prompt");
            var projected = ProjectRow(columns, rowIndex);
            projectedRows.Add(new TextBufferProjectionRow(
                projected.Cells,
                wrapped != 0,
                semanticPrompt == 1 && projected.PromptStart is { } promptStart
                    ? [new ShellMark(promptStart)]
                    : []));
            rowIndex++;
        }

        while (projectedRows.Count < rows)
        {
            projectedRows.Add(new TextBufferProjectionRow(
                Enumerable.Repeat(Cell.Blank, columns).ToArray(),
                false,
                []));
        }

        var cursorX = _cursor.ViewportHasValue != 0
            ? _cursor.ViewportX
            : QueryUInt16Core(3);
        var cursorY = _cursor.ViewportHasValue != 0
            ? _cursor.ViewportY
            : QueryUInt16Core(4);
        Buffer.ReplaceViewport(
            projectedRows,
            cursorX,
            cursorY);
        SynchronizeMetadata();
    }

    private (Cell[] Cells, int? PromptStart) ProjectRow(int columns, int rowIndex)
    {
        var row = Enumerable.Repeat(Cell.Blank, columns).ToArray();
        var column = 0;
        var previousWidth = 0;
        int? promptStart = null;
        var codepointBuffer = new uint[16];
        var hyperlinkBuffer = new byte[256];
        while (column < columns && GhosttyNative.RowCellsNext(_rowCells.DangerousGetHandle()))
        {
            ulong rawCell = 0;
            ThrowIfFailed(
                GhosttyNative.RowCellsGet(
                    _rowCells.DangerousGetHandle(),
                    RenderCellDataRaw,
                    &rawCell),
                "get raw cell");
            var wide = GetRawCellInt(rawCell, 3);
            var style = new GhosttyStyle { Size = (nuint)sizeof(GhosttyStyle) };
            ThrowIfFailed(
                GhosttyNative.RowCellsGet(
                    _rowCells.DangerousGetHandle(),
                    RenderCellDataStyle,
                    &style),
                "get cell style");
            var hasHyperlink = GetRawCellBool(rawCell, 7);
            var semantic = GetRawCellInt(rawCell, 9);
            var projected = Cell.Blank with
            {
                Attributes = new CellAttributes
                {
                    Foreground = GetCellColor(RenderCellDataForeground),
                    Background = GetCellColor(RenderCellDataBackground),
                    Flags = ToCellFlags(style),
                },
                IsProtected = GetRawCellBool(rawCell, 8),
                HyperlinkUri = hasHyperlink
                    ? GetHyperlinkUri(column, rowIndex, ref hyperlinkBuffer)
                    : null,
                ShellIntegration = ShellIntegrationKind.None,
            };
            uint graphemeLength = 0;
            ThrowIfFailed(
                GhosttyNative.RowCellsGet(
                    _rowCells.DangerousGetHandle(),
                    RenderCellDataGraphemeLength,
                    &graphemeLength),
                "get grapheme length");
            if (graphemeLength == 0)
            {
                if (wide == 2 || previousWidth == 2)
                {
                    projected.IsWideContinuation = true;
                    projected.StoredWidth = 0;
                }

                row[column] = projected;
                previousWidth = 0;
                column++;
                continue;
            }

            if (codepointBuffer.Length < graphemeLength)
            {
                codepointBuffer = new uint[graphemeLength];
            }

            fixed (uint* codepoints = codepointBuffer)
            {
                ThrowIfFailed(
                    GhosttyNative.RowCellsGet(
                        _rowCells.DangerousGetHandle(),
                        RenderCellDataGraphemeBuffer,
                        codepoints),
                    "get grapheme");
            }

            var rune = Rune.TryCreate(codepointBuffer[0], out var value) ? value : Rune.ReplacementChar;
            var combining = new StringBuilder();
            for (var index = 1; index < graphemeLength; index++)
            {
                if (Rune.TryCreate(codepointBuffer[index], out var extra))
                {
                    combining.Append(extra);
                }
            }

            var width = wide == 1 ? 2 : 1;
            projected.Rune = rune;
            projected.CombiningCharacters = combining.Length == 0 ? null : combining.ToString();
            projected.StoredWidth = (byte)width;
            projected.ShellIntegration = semantic switch
            {
                1 => ShellIntegrationKind.Command,
                2 => ShellIntegrationKind.Prompt,
                _ => ShellIntegrationKind.Output,
            };
            if (semantic == 2)
            {
                promptStart ??= column;
            }

            row[column] = projected;
            previousWidth = width;
            column++;
        }

        return (row, promptStart);
    }

    private static bool GetRawCellBool(ulong cell, int data)
    {
        byte value = 0;
        ThrowIfFailed(GhosttyNative.CellGet(cell, data, &value), "get raw cell flag");
        return value != 0;
    }

    private static int GetRawCellInt(ulong cell, int data)
    {
        var value = 0;
        ThrowIfFailed(GhosttyNative.CellGet(cell, data, &value), "get raw cell value");
        return value;
    }

    private string? GetHyperlinkUri(int column, int row, ref byte[] buffer)
    {
        var gridRef = new GhosttyGridRef { Size = (nuint)sizeof(GhosttyGridRef) };
        var result = GhosttyNative.TerminalGridRef(
            _terminal.DangerousGetHandle(),
            new GhosttyPoint
            {
                Tag = 1,
                X = checked((ushort)column),
                Y = checked((uint)row),
            },
            &gridRef);
        if (result != GhosttyResult.Success)
        {
            return null;
        }

        nuint written = 0;
        fixed (byte* bytes = buffer)
        {
            result = GhosttyNative.GridRefHyperlinkUri(
                &gridRef,
                bytes,
                (nuint)buffer.Length,
                &written);
        }

        if (result == GhosttyResult.OutOfSpace)
        {
            buffer = new byte[checked((int)written)];
            fixed (byte* bytes = buffer)
            {
                result = GhosttyNative.GridRefHyperlinkUri(
                    &gridRef,
                    bytes,
                    (nuint)buffer.Length,
                    &written);
            }
        }

        if (result != GhosttyResult.Success || written == 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer, 0, checked((int)written));
    }

    private TermColor GetCellColor(int data)
    {
        GhosttyColorRgb color;
        var result = GhosttyNative.RowCellsGet(_rowCells.DangerousGetHandle(), data, &color);
        return result == GhosttyResult.Success
            ? TermColor.FromRgb(color.R, color.G, color.B)
            : TermColor.Default;
    }

    private void SynchronizeMetadata()
    {
        var rawTitle = QueryString(TerminalDataTitle);
        var title = string.IsNullOrEmpty(rawTitle) ? "Windows Terminal" : rawTitle;
        if (!string.Equals(_title, title, StringComparison.Ordinal))
        {
            _title = title;
            TitleChanged?.Invoke(this, _title);
        }

        var rawWorkingDirectory = QueryString(TerminalDataPwd);
        var workingDirectory = string.IsNullOrEmpty(rawWorkingDirectory)
            ? null
            : rawWorkingDirectory;
        if (!string.Equals(_workingDirectory, workingDirectory, StringComparison.Ordinal))
        {
            _workingDirectory = workingDirectory;
            WorkingDirectoryChanged?.Invoke(this, _workingDirectory);
            ShellIntegrationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private ushort GetRenderUInt16(int data)
    {
        ushort value = 0;
        ThrowIfFailed(
            GhosttyNative.RenderStateGet(_renderState.DangerousGetHandle(), data, &value),
            "get render dimension");
        return value;
    }

    private int QueryInt(int data)
    {
        lock (_gate)
        {
            var value = 0;
            ThrowIfFailed(
                GhosttyNative.TerminalGet(_terminal.DangerousGetHandle(), data, &value),
                "get terminal value");
            return value;
        }
    }

    private ushort QueryUInt16Core(int data)
    {
        ushort value = 0;
        ThrowIfFailed(
            GhosttyNative.TerminalGet(_terminal.DangerousGetHandle(), data, &value),
            "get terminal coordinate");
        return value;
    }

    private uint QueryUInt32(int data)
    {
        lock (_gate)
        {
            uint value = 0;
            ThrowIfFailed(
                GhosttyNative.TerminalGet(_terminal.DangerousGetHandle(), data, &value),
                "get terminal pixel dimension");
            return value;
        }
    }

    private (int HistoryCount, int ScrollOffset) ReadScrollbar()
    {
        lock (_gate)
        {
            return ReadScrollbarCore();
        }
    }

    private (int HistoryCount, int ScrollOffset) ReadScrollbarCore()
    {
        var value = new GhosttyScrollbar();
        ThrowIfFailed(
            GhosttyNative.TerminalGet(_terminal.DangerousGetHandle(), 9, &value),
            "get scrollbar");
        var historyRows = value.Total > value.Length ? value.Total - value.Length : 0;
        var history = checked((int)Math.Min(historyRows, int.MaxValue));
        var top = checked((int)Math.Min(value.Offset, int.MaxValue));
        return (history, Math.Clamp(history - top, 0, history));
    }

    private void ResetHistoryAnchor()
    {
        _historyAnchor?.Dispose();
        _historyAnchor = null;
        var pointTag = ReadScrollbarCore().HistoryCount == 0 ? 0 : 3;
        var result = GhosttyNative.TerminalGridRefTrack(
            _terminal.DangerousGetHandle(),
            new GhosttyPoint { Tag = pointTag },
            out var tracked);
        if (result == GhosttyResult.Success)
        {
            _historyAnchor = new SafeGhosttyTrackedGridRefHandle(tracked);
            return;
        }

        ThrowIfFailed(result, "track history origin");
    }

    private TextBufferSnapshot CreateHistorySnapshot()
    {
        var scrollbar = ReadScrollbarCore();
        if (scrollbar.HistoryCount == 0)
        {
            return Buffer.CreateSnapshot();
        }

        var originalOffset = scrollbar.ScrollOffset;
        var lines = new List<TextBufferLineSnapshot>(scrollbar.HistoryCount + Rows);
        var nextAbsoluteRow = 0;
        for (var top = 0; top <= scrollbar.HistoryCount;)
        {
            ScrollToAbsoluteRow(top);
            var viewport = Buffer.CreateSnapshot();
            var firstNewRow = Math.Max(0, nextAbsoluteRow - top);
            for (var row = firstNewRow; row < viewport.Lines.Count; row++)
            {
                lines.Add(viewport.Lines[row]);
            }

            nextAbsoluteRow = top + viewport.Rows;
            if (top == scrollbar.HistoryCount)
            {
                break;
            }

            top = Math.Min(scrollbar.HistoryCount, top + viewport.Rows);
        }

        ScrollToAbsoluteRow(scrollbar.HistoryCount - originalOffset);
        return new TextBufferSnapshot(
            Columns,
            Rows,
            CursorX,
            CursorY,
            scrollbar.HistoryCount,
            originalOffset,
            lines);
    }

    private void ScrollToAbsoluteRow(int row)
    {
        GhosttyNative.TerminalScrollViewport(
            _terminal.DangerousGetHandle(),
            new GhosttyScrollViewport
            {
                Tag = 3,
                Row = (nuint)Math.Max(0, row),
            });
        SynchronizeProjection();
    }

    private bool QueryMode(ushort value, bool ansi = false)
    {
        lock (_gate)
        {
            var config = new GhosttyModeConfig
            {
                Mode = (ushort)(value | (ansi ? 0x8000 : 0)),
            };
            var result = GhosttyNative.TerminalGet(
                _terminal.DangerousGetHandle(),
                TerminalDataMode,
                &config);
            return result == GhosttyResult.Success && config.Value != 0;
        }
    }

    private string QueryString(int data)
    {
        var value = new GhosttyString();
        var result = GhosttyNative.TerminalGet(_terminal.DangerousGetHandle(), data, &value);
        if (result == GhosttyResult.NoValue || value.Pointer == 0 || value.Length == 0)
        {
            return string.Empty;
        }

        ThrowIfFailed(result, "get terminal string");
        return Encoding.UTF8.GetString(
            new ReadOnlySpan<byte>((void*)value.Pointer, checked((int)value.Length)));
    }

    private static CellFlags ToCellFlags(GhosttyStyle style)
    {
        var flags = CellFlags.None;
        if (style.Bold != 0) flags |= CellFlags.Bold;
        if (style.Faint != 0) flags |= CellFlags.Faint;
        if (style.Italic != 0) flags |= CellFlags.Italic;
        if (style.Underline != 0) flags |= CellFlags.Underline;
        if (style.Blink != 0) flags |= CellFlags.Blink;
        if (style.Inverse != 0) flags |= CellFlags.Inverse;
        if (style.Invisible != 0) flags |= CellFlags.Invisible;
        if (style.Strikethrough != 0) flags |= CellFlags.Strikethrough;
        return flags;
    }

    private static GhosttyColorRgb ToRgb(uint color) => new()
    {
        R = (byte)(color >> 16),
        G = (byte)(color >> 8),
        B = (byte)color,
    };

    private static void ThrowIfFailed(GhosttyResult result, string operation)
    {
        if (result != GhosttyResult.Success)
        {
            throw new GhosttyException(operation, result);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWritePty(nint terminal, nint userData, byte* data, nuint length)
    {
        if (TryGetInstance(userData) is { } instance)
        {
            try
            {
                instance.ResponseReady?.Invoke(
                    instance,
                    new ReadOnlySpan<byte>(data, checked((int)length)).ToArray());
            }
            catch (Exception exception)
            {
                instance._callbackException ??= exception;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBell(nint terminal, nint userData)
    {
        if (TryGetInstance(userData) is { } instance)
        {
            try
            {
                instance.Bell?.Invoke(instance, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                instance._callbackException ??= exception;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChanged(nint terminal, nint userData)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnSize(nint terminal, nint userData, GhosttySizeReport* size)
    {
        var instance = TryGetInstance(userData);
        if (instance is null || size is null)
        {
            return 0;
        }

        size->Rows = checked((ushort)instance.Rows);
        size->Columns = checked((ushort)instance.Columns);
        size->CellWidth = instance._cellWidthPixels;
        size->CellHeight = instance._cellHeightPixels;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClipboardWrite(
        nint terminal,
        nint userData,
        GhosttyClipboardWrite* write)
    {
        var instance = TryGetInstance(userData);
        var result = 1;
        if (instance?._allowClipboardWrite == true && write is not null)
        {
            string? text = write->ContentsLength == 0 ? string.Empty : null;
            for (nuint index = 0; index < write->ContentsLength; index++)
            {
                var content = write->Contents[index];
                var mime = ReadString(content.Mime);
                if (mime.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
                    mime.StartsWith("text/plain;", StringComparison.OrdinalIgnoreCase))
                {
                    text = ReadString(content.Data);
                    break;
                }
            }

            if (text is not null)
            {
                try
                {
                    instance.ClipboardWriteRequested?.Invoke(instance, text);
                    // Avalonia's clipboard API is asynchronous, while libghostty
                    // requires this callback to reply synchronously. OSC 52 ignores
                    // this status; acknowledged protocols are explicitly unsupported.
                    result = 2;
                }
                catch (Exception exception)
                {
                    instance._callbackException ??= exception;
                    result = 5;
                }
            }
            else
            {
                result = 2;
            }
        }

        if (write is not null && write->Reply != 0)
        {
            var reply = new GhosttyClipboardWriteReply
            {
                Size = (nuint)sizeof(GhosttyClipboardWriteReply),
                Result = result,
            };
            var callback =
                (delegate* unmanaged[Cdecl]<GhosttyClipboardWrite*, GhosttyClipboardWriteReply*, void>)write->Reply;
            callback(write, &reply);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNotification(
        nint terminal,
        nint userData,
        GhosttyDesktopNotification* notification)
    {
        var instance = TryGetInstance(userData);
        if (instance?._allowNotifications != true || notification is null)
        {
            return;
        }

        try
        {
            instance.NotificationRequested?.Invoke(
                instance,
                new TerminalNotification(
                    ReadString(notification->Title),
                    ReadString(notification->Body)));
        }
        catch (Exception exception)
        {
            instance._callbackException ??= exception;
        }
    }

    private static string ReadString(GhosttyString value) =>
        value.Pointer == 0 || value.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(
                new ReadOnlySpan<byte>((void*)value.Pointer, checked((int)value.Length)));

    private static GhosttyTerminalEngine? TryGetInstance(nint userData) =>
        userData == 0 ? null : GCHandle.FromIntPtr(userData).Target as GhosttyTerminalEngine;

    private static int EstimateRows(ReadOnlySpan<byte> data, int columns)
    {
        var rows = 0;
        var printable = 0;
        for (var index = 0; index < data.Length; index++)
        {
            var value = data[index];
            if (value is (byte)'\n' or 0x0B or 0x0C)
            {
                rows++;
            }
            else if (value == 0x1B &&
                     index + 1 < data.Length &&
                     data[index + 1] is (byte)'D' or (byte)'E' or (byte)'M')
            {
                rows++;
                index++;
            }
            else if (value >= 0x20 && value != 0x7F)
            {
                printable++;
            }
        }

        return rows + (printable / Math.Max(1, columns));
    }

}
