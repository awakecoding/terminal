using System.Text;
using System.Globalization;

namespace Devolutions.Terminal.Core;

public enum LineRendition : byte
{
    SingleWidth,
    DoubleWidth,
    DoubleHeightTop,
    DoubleHeightBottom,
}

public sealed record TextBufferLineSnapshot(
    IReadOnlyList<Cell> Cells,
    bool Wrapped,
    IReadOnlyList<ShellMark> Marks)
{
    public ShellMark? Mark => Marks.Count > 0 ? Marks[0] : null;
    public LineRendition Rendition { get; init; }
    public long LogicalLineId { get; init; }
    public int LogicalOffset { get; init; }
}

public sealed record TextBufferSnapshot(
    int Columns,
    int Rows,
    int CursorX,
    int CursorY,
    int HistoryCount,
    int ScrollOffset,
    IReadOnlyList<TextBufferLineSnapshot> Lines);

public sealed record TextBufferProjectionRow(
    Cell[] Cells,
    bool Wrapped,
    IReadOnlyList<ShellMark> Marks)
{
    public LineRendition Rendition { get; init; }
}

public sealed class TextBuffer
{
    private sealed class BufferLine
    {
        public BufferLine(Cell[] cells, long logicalLineId)
        {
            Cells = cells;
            LogicalLineId = logicalLineId;
        }

        public Cell[] Cells { get; }
        public bool Wrapped { get; set; }
        public LineRendition Rendition { get; set; }
        public List<ShellMark> Marks { get; } = [];
        public long LogicalLineId { get; set; }
        public int LogicalOffset { get; set; }
    }

    private CircularBuffer<BufferLine> _lines;
    private readonly int _historySize;
    private bool[] _tabStops;
    private int _scrollOffset;
    private ShellIntegrationKind _shellIntegration;
    private ShellMark? _activeMark;
    private bool _activeMarkHasOutput;
    private string? _pendingPrepend;
    private long _nextLogicalLineId;

    public TextBuffer(int columns, int rows, int historySize, bool hasHistory)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _historySize = hasHistory ? Math.Max(0, historySize) : 0;
        HasHistory = hasHistory;
        _lines = new CircularBuffer<BufferLine>(Rows + _historySize);
        _tabStops = CreateDefaultTabStops(Columns);
        ScrollBottom = Rows - 1;
        EnsureViewportLines();
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public bool HasHistory { get; }
    public int CursorX { get; set; }
    public int CursorY { get; set; }
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }
    public int ViewportStart => Math.Max(0, _lines.Count - Rows);
    public int ScrollOffset
    {
        get => _scrollOffset;
        set => _scrollOffset = Math.Clamp(value, 0, HistoryCount);
    }

    public bool OriginMode { get; set; }
    public bool WrapPending { get; set; }
    public int TotalLines => _lines.Count;
    public int HistoryCount => ViewportStart;
    public long CoordinateVersion { get; private set; }
    public CellAttributes CurrentAttributes { get; set; } = CellAttributes.Default;
    public bool CurrentProtection { get; set; }
    public string? CurrentHyperlinkUri { get; set; }
    public int SavedCursorX { get; set; }
    public int SavedCursorY { get; set; }
    public CellAttributes SavedAttributes { get; set; } = CellAttributes.Default;
    public bool SavedProtection { get; set; }

    public void Resize(
        int columns,
        int rows,
        IReadOnlyList<TerminalImageAnchor>? retainedAnchors = null)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        var oldColumns = Columns;
        var oldViewport = ViewportStart;
        var cursorAbsoluteLine = oldViewport + CursorY;
        var wasWrapPending = WrapPending;
        var source = _lines.ToList();
        var activeMark = _activeMark;

        Columns = columns;
        Rows = rows;
        CoordinateVersion++;
        _tabStops = ResizeTabStops(_tabStops, columns);
        ScrollTop = 0;
        ScrollBottom = rows - 1;
        WrapPending = false;
        ScrollOffset = 0;

        var reflowed = Reflow(
            source,
            oldColumns,
            columns,
            retainedAnchors,
            cursorAbsoluteLine,
            CursorX + (wasWrapPending ? 1 : 0),
            out var cursorLine,
            out var cursorColumn);
        while (reflowed.Count < rows)
        {
            reflowed.Add(NewBlankLine(CellAttributes.Default));
        }

        _lines.ResetCapacity(rows + _historySize, reflowed);
        _activeMark = activeMark;
        var dropped = Math.Max(0, reflowed.Count - _lines.Count);
        cursorLine = Math.Max(0, cursorLine - dropped);

        EnsureViewportLines();
        var viewport = ViewportStart;
        if (cursorLine < viewport)
        {
            cursorLine = viewport;
        }
        else if (cursorLine >= viewport + rows)
        {
            cursorLine = viewport + rows - 1;
        }

        CursorY = Math.Clamp(cursorLine - viewport, 0, rows - 1);
        CursorX = Math.Clamp(cursorColumn, 0, columns - 1);
        WrapPending = wasWrapPending &&
                      CursorX == EffectiveColumns(GetLiveLine(CursorY)) - 1;

    }

    public Cell[] GetRow(int viewportY) => GetVisibleLine(viewportY).Cells;

    public Cell GetCell(int x, int y)
    {
        if ((uint)x >= (uint)Columns || (uint)y >= (uint)Rows)
        {
            return Cell.Blank;
        }

        return GetRow(y)[x];
    }

    public void ApplyColors(
        BufferPosition start,
        BufferPosition end,
        TermColor? foreground,
        TermColor? background)
    {
        if (start.Line > end.Line ||
            (start.Line == end.Line && start.Column > end.Column))
        {
            (start, end) = (end, start);
        }

        var firstLine = Math.Clamp(start.Line, 0, _lines.Count - 1);
        var lastLine = Math.Clamp(end.Line, 0, _lines.Count - 1);
        for (var lineIndex = firstLine; lineIndex <= lastLine; lineIndex++)
        {
            var line = _lines[lineIndex];
            var firstColumn = lineIndex == start.Line ? start.Column : 0;
            var lastColumn = lineIndex == end.Line ? end.Column : Columns - 1;
            for (var column = Math.Clamp(firstColumn, 0, Columns - 1);
                 column <= Math.Clamp(lastColumn, 0, Columns - 1);
                 column++)
            {
                if (foreground is { } foregroundColor)
                {
                    line.Cells[column].Attributes.Foreground = foregroundColor;
                }

                if (background is { } backgroundColor)
                {
                    line.Cells[column].Attributes.Background = backgroundColor;
                }
            }
        }
    }

    public TextBufferSnapshot CreateSnapshot(bool includeHistory = false)
    {
        var first = includeHistory ? 0 : Math.Max(0, ViewportStart - ScrollOffset);
        var count = includeHistory ? _lines.Count : Rows;
        var copies = new TextBufferLineSnapshot[count];
        for (var i = 0; i < count; i++)
        {
            var sourceIndex = Math.Min(first + i, _lines.Count - 1);
            var cells = Array.AsReadOnly((Cell[])_lines[sourceIndex].Cells.Clone());
            copies[i] = new TextBufferLineSnapshot(
                cells,
                _lines[sourceIndex].Wrapped,
                _lines[sourceIndex].Marks
                    .Select(static mark => new ShellMark(mark.StartColumn, mark.ExitCode))
                    .ToArray())
            {
                Rendition = _lines[sourceIndex].Rendition,
                LogicalLineId = _lines[sourceIndex].LogicalLineId,
                LogicalOffset = _lines[sourceIndex].LogicalOffset,
            };
        }

        return new TextBufferSnapshot(Columns, Rows, CursorX, CursorY, HistoryCount, ScrollOffset, copies);
    }

    public void ReplaceViewport(
        IReadOnlyList<TextBufferProjectionRow> rows,
        int cursorX,
        int cursorY)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var replacement = new List<BufferLine>(Rows);
        for (var y = 0; y < Rows; y++)
        {
            var projection = y < rows.Count
                ? rows[y]
                : new TextBufferProjectionRow([], false, []);
            var source = projection.Cells;
            var cells = Enumerable.Repeat(Cell.Blank, Columns).ToArray();
            Array.Copy(source, cells, Math.Min(source.Length, cells.Length));
            var line = new BufferLine(cells, ++_nextLogicalLineId)
            {
                Wrapped = projection.Wrapped,
                Rendition = projection.Rendition,
            };
            line.Marks.AddRange(projection.Marks.Select(static mark =>
                new ShellMark(mark.StartColumn, mark.ExitCode)));
            replacement.Add(line);
        }

        _lines.ResetCapacity(Rows + _historySize, replacement);
        CursorX = Math.Clamp(cursorX, 0, Columns - 1);
        CursorY = Math.Clamp(cursorY, 0, Rows - 1);
        ScrollOffset = 0;
        WrapPending = false;
    }

    public void AdvanceCoordinateVersion() => CoordinateVersion++;

    public TerminalImageAnchor CreateImageAnchor(int column, int viewportRow)
    {
        var line = GetLiveLine(viewportRow);
        return new TerminalImageAnchor(
            line.LogicalLineId,
            line.LogicalOffset + Math.Clamp(column, 0, EffectiveColumns(line) - 1));
    }

    public bool TryResolveImageAnchor(
        TerminalImageAnchor anchor,
        out int absoluteRow,
        out int column)
    {
        for (var row = 0; row < _lines.Count; row++)
        {
            var line = _lines[row];
            if (line.LogicalLineId != anchor.LogicalLineId)
            {
                continue;
            }

            var width = EffectiveColumns(line);
            if (row + 1 < _lines.Count &&
                _lines[row + 1].LogicalLineId == line.LogicalLineId)
            {
                var nextOffset = _lines[row + 1].LogicalOffset;
                if (nextOffset > line.LogicalOffset)
                {
                    width = Math.Min(width, nextOffset - line.LogicalOffset);
                }
            }
            if (anchor.LogicalOffset < line.LogicalOffset ||
                anchor.LogicalOffset >= line.LogicalOffset + width)
            {
                continue;
            }

            absoluteRow = row;
            column = anchor.LogicalOffset - line.LogicalOffset;
            return true;
        }

        absoluteRow = -1;
        column = -1;
        return false;
    }

    public LineRendition CurrentLineRendition => GetLiveLine(CursorY).Rendition;

    public void SetCurrentLineRendition(LineRendition rendition)
    {
        var line = GetLiveLine(CursorY);
        line.Rendition = rendition;
        line.Wrapped = false;
        var width = EffectiveColumns(line);
        if (width < Columns)
        {
            EraseCells(line.Cells, width, Columns);
        }

        CursorX = Math.Clamp(CursorX, 0, width - 1);
        WrapPending = false;
    }

    public int GetPrintAdvance(Rune rune)
    {
        var target = GetJoinTarget(rune);
        if (target is null)
        {
            return WcWidth.Width(rune);
        }

        var (row, column, desiredWidth, lineColumns) = target.Value;
        return column + desiredWidth <= lineColumns
            ? Math.Max(0, desiredWidth - row[column].DisplayWidth)
            : 0;
    }

    public void Print(Rune rune)
    {
        if (IsPrepend(rune))
        {
            _pendingPrepend = (_pendingPrepend ?? string.Empty) + rune;
            return;
        }

        var prepend = _pendingPrepend;
        _pendingPrepend = null;
        if (prepend is null && TryAppendJoinedRune(rune))
        {
            return;
        }

        var width = WcWidth.Width(rune);
        if (width == 0)
        {
            AppendCombining(rune);
            return;
        }

        if (WrapPending)
        {
            var source = GetLiveLine(CursorY);
            source.Wrapped = true;
            CarriageReturn();
            LineFeed();
            ContinueLogicalLine(source);
        }

        var effectiveColumns = EffectiveColumns(GetLiveLine(CursorY));
        if (CursorX + width > effectiveColumns)
        {
            var source = GetLiveLine(CursorY);
            source.Wrapped = true;
            CarriageReturn();
            LineFeed();
            ContinueLogicalLine(source);
            effectiveColumns = EffectiveColumns(GetLiveLine(CursorY));
        }

        var row = GetLiveLine(CursorY).Cells;
        ClearGlyphAt(row, CursorX);
        if (width == 2 && CursorX + 1 < Columns)
        {
            ClearGlyphAt(row, CursorX + 1);
        }

        row[CursorX] = new Cell
        {
            Rune = rune,
            Attributes = CurrentAttributes,
            IsProtected = CurrentProtection,
            StoredWidth = (byte)width,
            HyperlinkUri = CurrentHyperlinkUri,
            ShellIntegration = _shellIntegration,
        };
        if (prepend is not null)
        {
            var runes = prepend.EnumerateRunes().ToArray();
            row[CursorX].Rune = runes[0];
            row[CursorX].CombiningCharacters =
                string.Concat(runes.Skip(1).Select(static value => value.ToString())) + rune;
        }

        if (width == 2 && CursorX + 1 < Columns)
        {
            row[CursorX + 1] = new Cell
            {
                Rune = new Rune(' '),
                Attributes = CurrentAttributes,
                IsProtected = CurrentProtection,
                IsWideContinuation = true,
                StoredWidth = 0,
                HyperlinkUri = CurrentHyperlinkUri,
                ShellIntegration = _shellIntegration,
            };
        }

        CursorX += width;
        if (CursorX >= effectiveColumns)
        {
            CursorX = effectiveColumns - 1;
            WrapPending = true;
        }
    }

    public void CarriageReturn()
    {
        CursorX = 0;
        WrapPending = false;
    }

    public void LineFeed(bool alsoCarriageReturn = false)
    {
        WrapPending = false;
        if (CursorY == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }

        if (alsoCarriageReturn)
        {
            CursorX = 0;
        }
        else
        {
            CursorX = Math.Min(CursorX, EffectiveColumns(GetLiveLine(CursorY)) - 1);
        }
    }

    public void ReverseIndex()
    {
        WrapPending = false;
        if (CursorY == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }

        CursorX = Math.Min(CursorX, EffectiveColumns(GetLiveLine(CursorY)) - 1);
    }

    public void Backspace()
    {
        WrapPending = false;
        if (CursorX > 0)
        {
            CursorX--;
            if (GetLiveLine(CursorY).Cells[CursorX].IsWideContinuation && CursorX > 0)
            {
                CursorX--;
            }
        }
    }

    public void Tab(int count = 1)
    {
        WrapPending = false;
        var columns = EffectiveColumns(GetLiveLine(CursorY));
        for (var n = 0; n < Math.Max(1, count); n++)
        {
            var next = columns - 1;
            for (var x = CursorX + 1; x < columns; x++)
            {
                if (_tabStops[x])
                {
                    next = x;
                    break;
                }
            }

            CursorX = next;
        }
    }

    public void BackTab(int count = 1)
    {
        WrapPending = false;
        for (var n = 0; n < Math.Max(1, count); n++)
        {
            var previous = 0;
            for (var x = CursorX - 1; x > 0; x--)
            {
                if (_tabStops[x])
                {
                    previous = x;
                    break;
                }
            }

            CursorX = previous;
        }
    }

    public void SetTabStop() => _tabStops[CursorX] = true;

    public void ClearTabStop(bool all)
    {
        if (all)
        {
            Array.Clear(_tabStops);
        }
        else
        {
            _tabStops[CursorX] = false;
        }
    }

    public int[] GetTabStops() =>
        _tabStops
            .Select((isSet, index) => (isSet, index))
            .Where(static item => item.isSet)
            .Select(static item => item.index)
            .ToArray();

    public void ReplaceTabStops(ReadOnlySpan<int> columns)
    {
        Array.Clear(_tabStops);
        foreach (var column in columns)
        {
            if ((uint)column < (uint)_tabStops.Length)
            {
                _tabStops[column] = true;
            }
        }
    }

    public void SetCursor(int row, int col, bool relativeToOrigin = true)
    {
        WrapPending = false;
        var top = OriginMode && relativeToOrigin ? ScrollTop : 0;
        var bottom = OriginMode && relativeToOrigin ? ScrollBottom : Rows - 1;
        CursorY = Math.Clamp(top + row, top, bottom);
        CursorX = Math.Clamp(col, 0, EffectiveColumns(GetLiveLine(CursorY)) - 1);
    }

    public void MoveCursor(int dx, int dy, bool respectMargins = false)
    {
        WrapPending = false;
        var withinMargins = CursorY >= ScrollTop && CursorY <= ScrollBottom;
        var top = OriginMode || (respectMargins && withinMargins) ? ScrollTop : 0;
        var bottom = OriginMode || (respectMargins && withinMargins) ? ScrollBottom : Rows - 1;
        CursorY = Math.Clamp(CursorY + dy, top, bottom);
        CursorX = Math.Clamp(CursorX + dx, 0, EffectiveColumns(GetLiveLine(CursorY)) - 1);
    }

    public void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        if (bottom <= top)
        {
            return;
        }

        ScrollTop = top;
        ScrollBottom = bottom;
        SetCursor(0, 0);
    }

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 1:
                EraseLineRange(0, CursorY - 1);
                EraseInLine(1);
                break;
            case 2:
                EraseLineRange(0, Rows - 1);
                break;
            case 3:
                TrimHistory();
                break;
            default:
                EraseInLine(0);
                EraseLineRange(CursorY + 1, Rows - 1);
                break;
        }
    }

    public void SelectiveEraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 1:
                EraseLineRange(0, CursorY - 1, selective: true);
                SelectiveEraseInLine(1);
                break;
            case 2:
                EraseLineRange(0, Rows - 1, selective: true);
                break;
            default:
                SelectiveEraseInLine(0);
                EraseLineRange(CursorY + 1, Rows - 1, selective: true);
                break;
        }
    }

    public void EraseInLine(int mode)
    {
        var row = GetLiveLine(CursorY).Cells;
        switch (mode)
        {
            case 1:
                EraseCells(row, 0, CursorX + 1);
                break;
            case 2:
                EraseCells(row, 0, Columns);
                break;
            default:
                EraseCells(row, CursorX, Columns);
                break;
        }
    }

    public void SelectiveEraseInLine(int mode)
    {
        var row = GetLiveLine(CursorY).Cells;
        switch (mode)
        {
            case 1:
                EraseCells(row, 0, CursorX + 1, selective: true);
                break;
            case 2:
                EraseCells(row, 0, Columns, selective: true);
                break;
            default:
                EraseCells(row, CursorX, Columns, selective: true);
                break;
        }
    }

    public void FillRectangle(Rune rune, int top, int left, int bottom, int right)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return;
        }

        for (var y = top; y <= bottom; y++)
        {
            var row = GetLiveLine(y).Cells;
            ClearGlyphAt(row, left);
            ClearGlyphAt(row, right);
            for (var x = left; x <= right; x++)
            {
                row[x] = new Cell
                {
                    Rune = rune,
                    Attributes = CurrentAttributes,
                    IsProtected = CurrentProtection,
                    StoredWidth = 1,
                };
            }
        }
    }

    public void EraseRectangle(int top, int left, int bottom, int right, bool selective)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return;
        }

        for (var y = top; y <= bottom; y++)
        {
            EraseCells(GetLiveLine(y).Cells, left, right + 1, selective);
        }
    }

    public void CopyRectangle(
        int top,
        int left,
        int bottom,
        int right,
        int destinationTop,
        int destinationLeft)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right) ||
            destinationTop >= Rows ||
            destinationLeft >= Columns)
        {
            return;
        }

        destinationTop = Math.Max(0, destinationTop);
        destinationLeft = Math.Max(0, destinationLeft);
        var height = Math.Min(bottom - top + 1, Rows - destinationTop);
        var width = Math.Min(right - left + 1, Columns - destinationLeft);
        var copy = new Cell[height, width];
        for (var y = 0; y < height; y++)
        {
            var source = GetLiveLine(top + y).Cells;
            for (var x = 0; x < width; x++)
            {
                copy[y, x] = source[left + x];
            }

            if (copy[y, 0].IsWideContinuation)
            {
                copy[y, 0] = BlankPreservingAttributes(copy[y, 0]);
            }

            if (copy[y, width - 1].DisplayWidth == 2)
            {
                copy[y, width - 1] = BlankPreservingAttributes(copy[y, width - 1]);
            }
        }

        for (var y = 0; y < height; y++)
        {
            var destination = GetLiveLine(destinationTop + y).Cells;
            ClearGlyphAt(destination, destinationLeft);
            ClearGlyphAt(destination, destinationLeft + width - 1);
            for (var x = 0; x < width; x++)
            {
                destination[destinationLeft + x] = copy[y, x];
            }

            RepairWideCells(destination);
        }
    }

    public void ChangeAttributesRectangle(
        int top,
        int left,
        int bottom,
        int right,
        ReadOnlySpan<int> attributes,
        bool reverse,
        bool rectangular)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return;
        }

        if (!rectangular && top != bottom)
        {
            if (right <= left)
            {
                return;
            }

            ChangeAttributesArea(top, left, top, Columns - 1, attributes, reverse);
            if (bottom - top > 1)
            {
                ChangeAttributesArea(top + 1, 0, bottom - 1, Columns - 1, attributes, reverse);
            }

            ChangeAttributesArea(bottom, 0, bottom, right, attributes, reverse);
            return;
        }

        ChangeAttributesArea(top, left, bottom, right, attributes, reverse);
    }

    public ushort ChecksumRectangle(int top, int left, int bottom, int right)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return 0;
        }

        ushort checksum = 0;
        for (var y = top; y <= bottom; y++)
        {
            var row = GetLiveLine(y).Cells;
            for (var x = left; x <= right; x++)
            {
                ref readonly var cell = ref row[x];
                if (!cell.IsWideContinuation)
                {
                    checksum = SubtractChecksum(checksum, cell.Rune.Value == 0x2426 ? 0x1B : cell.Rune.Value);
                    if (cell.CombiningCharacters is { } combining)
                    {
                        foreach (var rune in combining.EnumerateRunes())
                        {
                            checksum = SubtractChecksum(checksum, rune.Value == 0x2426 ? 0x1B : rune.Value);
                        }
                    }
                }

                checksum = SubtractChecksum(checksum, cell.IsProtected ? 0x04 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Invisible) != 0 ? 0x08 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Underline) != 0 ? 0x10 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Inverse) != 0 ? 0x20 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Blink) != 0 ? 0x40 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Bold) != 0 ? 0x80 : 0);
                checksum = SubtractChecksum(checksum, LegacyColorIndex(cell.Attributes.Foreground, 7) << 4);
                checksum = SubtractChecksum(checksum, LegacyColorIndex(cell.Attributes.Background, 0));
            }
        }

        return checksum;
    }

    private static ushort SubtractChecksum(ushort checksum, int value) =>
        unchecked((ushort)(checksum - value));

    private static int LegacyColorIndex(TermColor color, int defaultIndex) =>
        color.Kind == ColorKind.Indexed && color.Index < 16 ? color.Index : defaultIndex;

    public void InsertLines(int count)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }

        count = Math.Min(Math.Max(1, count), ScrollBottom - CursorY + 1);
        var start = ViewportStart + CursorY;
        var bottom = ViewportStart + ScrollBottom;
        for (var y = bottom; y >= start + count; y--)
        {
            _lines[y] = _lines[y - count];
        }

        for (var y = start; y < start + count; y++)
        {
            _lines[y] = NewBlankLine(CurrentAttributes);
        }
    }

    public void DeleteLines(int count)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }

        count = Math.Min(Math.Max(1, count), ScrollBottom - CursorY + 1);
        var start = ViewportStart + CursorY;
        var bottom = ViewportStart + ScrollBottom;
        for (var y = start; y <= bottom - count; y++)
        {
            _lines[y] = _lines[y + count];
        }

        for (var y = bottom - count + 1; y <= bottom; y++)
        {
            _lines[y] = NewBlankLine(CurrentAttributes);
        }
    }

    public void InsertCharacters(int count)
    {
        var row = GetLiveLine(CursorY).Cells;
        count = Math.Min(Math.Max(1, count), Columns - CursorX);
        Array.Copy(row, CursorX, row, CursorX + count, Columns - CursorX - count);
        for (var x = CursorX; x < CursorX + count; x++)
        {
            row[x] = BlankWith(CurrentAttributes);
        }

        RepairWideCells(row);
    }

    public void DeleteCharacters(int count)
    {
        var row = GetLiveLine(CursorY).Cells;
        count = Math.Min(Math.Max(1, count), Columns - CursorX);
        Array.Copy(row, CursorX + count, row, CursorX, Columns - CursorX - count);
        for (var x = Columns - count; x < Columns; x++)
        {
            row[x] = BlankWith(CurrentAttributes);
        }

        RepairWideCells(row);
    }

    public void EraseCharacters(int count)
    {
        var row = GetLiveLine(CursorY).Cells;
        EraseCells(row, CursorX, Math.Min(Columns, CursorX + Math.Max(1, count)));
    }

    public void ScrollUp(int count)
    {
        count = Math.Min(Math.Max(1, count), ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            if (ScrollTop == 0 && ScrollBottom == Rows - 1 && HasHistory)
            {
                if (_lines.Count == Rows + _historySize)
                {
                    CoordinateVersion++;
                }

                _lines.AddLast(NewBlankLine(CurrentAttributes));
                ScrollOffset = 0;
            }
            else
            {
                var top = ViewportStart + ScrollTop;
                var bottom = ViewportStart + ScrollBottom;
                for (var y = top; y < bottom; y++)
                {
                    _lines[y] = _lines[y + 1];
                }

                _lines[bottom] = NewBlankLine(CurrentAttributes);
            }
        }
    }

    public void ScrollDown(int count)
    {
        count = Math.Min(Math.Max(1, count), ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            var top = ViewportStart + ScrollTop;
            var bottom = ViewportStart + ScrollBottom;
            for (var y = bottom; y > top; y--)
            {
                _lines[y] = _lines[y - 1];
            }

            _lines[top] = NewBlankLine(CurrentAttributes);
        }
    }

    public void SaveCursor()
    {
        SavedCursorX = CursorX;
        SavedCursorY = CursorY;
        SavedAttributes = CurrentAttributes;
        SavedProtection = CurrentProtection;
    }

    public void RestoreCursor()
    {
        CursorY = Math.Clamp(SavedCursorY, 0, Rows - 1);
        CursorX = Math.Clamp(SavedCursorX, 0, EffectiveColumns(GetLiveLine(CursorY)) - 1);
        CurrentAttributes = SavedAttributes;
        CurrentProtection = SavedProtection;
        WrapPending = false;
    }

    public void Reset(bool keepHistory)
    {
        CurrentAttributes = CellAttributes.Default;
        CurrentProtection = false;
        SavedAttributes = CellAttributes.Default;
        SavedProtection = false;
        CurrentHyperlinkUri = null;
        _shellIntegration = ShellIntegrationKind.None;
        _activeMark = null;
        _activeMarkHasOutput = false;
        _pendingPrepend = null;
        CursorX = 0;
        CursorY = 0;
        WrapPending = false;
        OriginMode = false;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        ScrollOffset = 0;
        _tabStops = CreateDefaultTabStops(Columns);
        if (!keepHistory)
        {
            _lines.Clear();
        }

        EnsureViewportLines();
        EraseLineRange(0, Rows - 1);
        for (var y = 0; y < Rows; y++)
        {
            GetLiveLine(y).Rendition = LineRendition.SingleWidth;
        }
    }

    public string GetText(int startX, int startY, int endX, int endY)
    {
        Normalize(ref startX, ref startY, ref endX, ref endY);
        var sb = new StringBuilder();
        for (var y = startY; y <= endY; y++)
        {
            var row = GetRow(y);
            var from = y == startY ? startX : 0;
            var to = y == endY ? endX : Columns - 1;
            var last = from - 1;
            for (var x = from; x <= to && x < Columns; x++)
            {
                if (!row[x].IsWideContinuation && !row[x].IsBlank)
                {
                    last = x;
                }
            }

            for (var x = from; x <= last && x < Columns; x++)
            {
                if (!row[x].IsWideContinuation)
                {
                    sb.Append(row[x].Text);
                }
            }

            if (y != endY && !GetVisibleLine(y).Wrapped)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    public string GetVisibleText() => GetText(0, 0, Columns - 1, Rows - 1);

    public void StartPrompt()
    {
        if (_activeMark is not null)
        {
            _shellIntegration = ShellIntegrationKind.Prompt;
            return;
        }

        var line = GetLiveLine(CursorY);
        _activeMark = new ShellMark(CursorX);
        line.Marks.Add(_activeMark);
        _activeMarkHasOutput = false;
        _shellIntegration = ShellIntegrationKind.Prompt;
    }

    public void StartCommand()
    {
        EnsureShellMark();
        _shellIntegration = ShellIntegrationKind.Command;
    }

    public void StartOutput()
    {
        EnsureShellMark();
        _activeMarkHasOutput = true;
        _shellIntegration = ShellIntegrationKind.Output;
    }

    public void EndCommand(uint? exitCode)
    {
        _shellIntegration = ShellIntegrationKind.None;
        if (_activeMark is { } mark)
        {
            mark.ExitCode = exitCode;
        }

        _activeMark = null;
        _activeMarkHasOutput = false;
    }

    private void EnsureShellMark()
    {
        if (_activeMark is not null && !_activeMarkHasOutput)
        {
            return;
        }

        var line = GetLiveLine(CursorY);
        _activeMark = new ShellMark(CursorX);
        line.Marks.Add(_activeMark);
        _activeMarkHasOutput = false;
    }

    private BufferLine GetVisibleLine(int viewportY)
    {
        EnsureViewportLines();
        viewportY = Math.Clamp(viewportY, 0, Rows - 1);
        var index = Math.Clamp(ViewportStart - ScrollOffset + viewportY, 0, _lines.Count - 1);
        return _lines[index];
    }

    private BufferLine GetLiveLine(int viewportY)
    {
        EnsureViewportLines();
        viewportY = Math.Clamp(viewportY, 0, Rows - 1);
        return _lines[ViewportStart + viewportY];
    }

    private void EnsureViewportLines()
    {
        while (_lines.Count < Rows)
        {
            _lines.AddLast(NewBlankLine(CellAttributes.Default));
        }
    }

    private void TrimHistory()
    {
        if (_lines.Count > Rows)
        {
            CoordinateVersion++;
        }

        while (_lines.Count > Rows)
        {
            _lines.RemoveFirst();
        }

        ScrollOffset = 0;
    }

    private BufferLine NewBlankLine(CellAttributes attributes)
    {
        var row = new Cell[Columns];
        for (var x = 0; x < row.Length; x++)
        {
            row[x] = BlankWith(attributes);
        }

        return new BufferLine(row, ++_nextLogicalLineId);
    }

    private void ContinueLogicalLine(BufferLine source)
    {
        var destination = GetLiveLine(CursorY);
        destination.LogicalLineId = source.LogicalLineId;
        destination.LogicalOffset = source.LogicalOffset + EffectiveColumns(source);
    }

    private int EffectiveColumns(BufferLine line) =>
        line.Rendition == LineRendition.SingleWidth ? Columns : Math.Max(1, Columns / 2);

    private static Cell BlankWith(CellAttributes attributes) => new()
    {
        Rune = new Rune(' '),
        Attributes = attributes,
        StoredWidth = 1,
    };

    private static Cell BlankPreservingAttributes(Cell cell)
    {
        cell.Rune = new Rune(' ');
        cell.IsWideContinuation = false;
        cell.StoredWidth = 1;
        cell.CombiningCharacters = null;
        cell.HyperlinkUri = null;
        cell.ShellIntegration = ShellIntegrationKind.None;
        return cell;
    }

    private void EraseLineRange(int fromY, int toY, bool selective = false)
    {
        for (var y = Math.Max(0, fromY); y <= Math.Min(toY, Rows - 1); y++)
        {
            EraseCells(GetLiveLine(y).Cells, 0, Columns, selective);
            GetLiveLine(y).Wrapped = false;
        }
    }

    private void EraseCells(Cell[] row, int from, int to, bool selective = false)
    {
        from = Math.Clamp(from, 0, row.Length);
        to = Math.Clamp(to, 0, row.Length);
        if (selective)
        {
            for (var x = from; x < to; x++)
            {
                SelectivelyEraseGlyphAt(row, x);
            }

            return;
        }

        if (from < to)
        {
            ClearGlyphAt(row, from);
            ClearGlyphAt(row, to - 1);
        }

        for (var x = from; x < to; x++)
        {
            row[x] = BlankWith(CurrentAttributes);
        }
    }

    private static void SelectivelyEraseGlyphAt(Cell[] row, int x)
    {
        var first = row[x].IsWideContinuation ? x - 1 : x;
        var last = row[x].DisplayWidth == 2 ? x + 1 : x;
        if (first < 0 || last >= row.Length)
        {
            first = last = x;
        }

        if (row[first].IsProtected || row[last].IsProtected)
        {
            return;
        }

        for (var column = first; column <= last; column++)
        {
            var cell = row[column];
            cell.Rune = new Rune(' ');
            cell.IsProtected = false;
            cell.IsWideContinuation = false;
            cell.StoredWidth = 1;
            cell.CombiningCharacters = null;
            cell.HyperlinkUri = null;
            cell.ShellIntegration = ShellIntegrationKind.None;
            row[column] = cell;
        }
    }

    private bool TryNormalizeRectangle(ref int top, ref int left, ref int bottom, ref int right)
    {
        if (bottom < top || right < left || bottom < 0 || right < 0 || top >= Rows || left >= Columns)
        {
            return false;
        }

        top = Math.Clamp(top, 0, Rows - 1);
        left = Math.Clamp(left, 0, Columns - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        right = Math.Clamp(right, 0, Columns - 1);
        return true;
    }

    private void ChangeAttributesArea(
        int top,
        int left,
        int bottom,
        int right,
        ReadOnlySpan<int> attributes,
        bool reverse)
    {
        for (var y = top; y <= bottom; y++)
        {
            var row = GetLiveLine(y).Cells;
            for (var x = left; x <= right; x++)
            {
                ApplyRectangularAttributes(ref row[x].Attributes, attributes, reverse);
            }
        }
    }

    private static void ApplyRectangularAttributes(
        ref CellAttributes attributes,
        ReadOnlySpan<int> parameters,
        bool reverse)
    {
        if (reverse)
        {
            var mask = CellFlags.None;
            for (var index = 0; index < parameters.Length;)
            {
                var parameter = Parameter(parameters, index);
                if (parameter == 0)
                {
                    mask ^= CellFlags.Bold | CellFlags.Faint | CellFlags.Italic |
                        CellFlags.Underline | CellFlags.Blink | CellFlags.Inverse |
                        CellFlags.Invisible | CellFlags.Strikethrough;
                    index++;
                    continue;
                }

                var sample = CellAttributes.Default;
                index += ApplyRectangularSgr(ref sample, parameters, index);
                mask ^= sample.Flags;
            }

            attributes.Flags ^= mask;
            return;
        }

        for (var index = 0; index < parameters.Length;)
        {
            index += ApplyRectangularSgr(ref attributes, parameters, index);
        }
    }

    private static int ApplyRectangularSgr(
        ref CellAttributes attributes,
        ReadOnlySpan<int> parameters,
        int index)
    {
        var value = Parameter(parameters, index);
        switch (value)
        {
            case 0:
                attributes = CellAttributes.Default;
                break;
            case 1:
                attributes.Flags = (attributes.Flags | CellFlags.Bold) & ~CellFlags.Faint;
                break;
            case 2:
                attributes.Flags |= CellFlags.Faint;
                break;
            case 3:
                attributes.Flags |= CellFlags.Italic;
                break;
            case 4:
            case 21:
                attributes.Flags |= CellFlags.Underline;
                break;
            case 5:
            case 6:
                attributes.Flags |= CellFlags.Blink;
                break;
            case 7:
                attributes.Flags |= CellFlags.Inverse;
                break;
            case 8:
                attributes.Flags |= CellFlags.Invisible;
                break;
            case 9:
                attributes.Flags |= CellFlags.Strikethrough;
                break;
            case 22:
                attributes.Flags &= ~(CellFlags.Bold | CellFlags.Faint);
                break;
            case 23:
                attributes.Flags &= ~CellFlags.Italic;
                break;
            case 24:
                attributes.Flags &= ~CellFlags.Underline;
                break;
            case 25:
                attributes.Flags &= ~CellFlags.Blink;
                break;
            case 27:
                attributes.Flags &= ~CellFlags.Inverse;
                break;
            case 28:
                attributes.Flags &= ~CellFlags.Invisible;
                break;
            case 29:
                attributes.Flags &= ~CellFlags.Strikethrough;
                break;
            case 38:
            case 48:
                return ApplyRectangularExtendedColor(ref attributes, parameters, index, value == 38);
            case 39:
                attributes.Foreground = TermColor.Default;
                break;
            case 49:
                attributes.Background = TermColor.Default;
                break;
            case >= 30 and <= 37:
                attributes.Foreground = TermColor.FromIndex(value - 30);
                break;
            case >= 40 and <= 47:
                attributes.Background = TermColor.FromIndex(value - 40);
                break;
            case >= 90 and <= 97:
                attributes.Foreground = TermColor.FromIndex(value - 90 + 8);
                break;
            case >= 100 and <= 107:
                attributes.Background = TermColor.FromIndex(value - 100 + 8);
                break;
        }

        return 1;
    }

    private static int ApplyRectangularExtendedColor(
        ref CellAttributes attributes,
        ReadOnlySpan<int> parameters,
        int index,
        bool foreground)
    {
        var mode = Parameter(parameters, index + 1);
        if (mode == 5 && index + 2 < parameters.Length)
        {
            SetRectangularColor(
                ref attributes,
                TermColor.FromIndex(Parameter(parameters, index + 2)),
                foreground);
            return 3;
        }

        if (mode == 2 && index + 4 < parameters.Length)
        {
            SetRectangularColor(
                ref attributes,
                TermColor.FromRgb(
                    (byte)Math.Clamp(Parameter(parameters, index + 2), 0, 255),
                    (byte)Math.Clamp(Parameter(parameters, index + 3), 0, 255),
                    (byte)Math.Clamp(Parameter(parameters, index + 4), 0, 255)),
                foreground);
            return 5;
        }

        return 1;
    }

    private static void SetRectangularColor(
        ref CellAttributes attributes,
        TermColor color,
        bool foreground)
    {
        if (foreground)
        {
            attributes.Foreground = color;
        }
        else
        {
            attributes.Background = color;
        }
    }

    private static int Parameter(ReadOnlySpan<int> parameters, int index) =>
        (uint)index < (uint)parameters.Length && parameters[index] >= 0 ? parameters[index] : 0;

    private void AppendCombining(Rune rune)
    {
        var y = CursorY;
        var x = CursorX - 1;
        if (WrapPending)
        {
            x = CursorX;
        }
        else if (x < 0 && y > 0 && GetLiveLine(y - 1).Wrapped)
        {
            y--;
            x = Columns - 1;
        }

        if (x < 0)
        {
            return;
        }

        var row = GetLiveLine(y).Cells;
        if (row[x].IsWideContinuation && x > 0)
        {
            x--;
        }

        if (row[x].IsBlank)
        {
            return;
        }

        row[x].CombiningCharacters = (row[x].CombiningCharacters ?? string.Empty) + rune;
    }

    private bool TryAppendJoinedRune(Rune rune)
    {
        var target = GetJoinTarget(rune);
        if (target is null)
        {
            return false;
        }

        var (row, x, desiredWidth, lineColumns) = target.Value;
        ref var previous = ref row[x];
        var previousWidth = previous.DisplayWidth;
        previous.CombiningCharacters = (previous.CombiningCharacters ?? string.Empty) + rune;
        if (desiredWidth > previousWidth && x + desiredWidth <= lineColumns)
        {
            ClearGlyphAt(row, x + 1);
            previous.StoredWidth = (byte)desiredWidth;
            row[x + 1] = new Cell
            {
                Rune = new Rune(' '),
                Attributes = previous.Attributes,
                IsWideContinuation = true,
                HyperlinkUri = previous.HyperlinkUri,
                ShellIntegration = previous.ShellIntegration,
            };

            var newCursor = CursorX + desiredWidth - previousWidth;
            if (newCursor >= lineColumns)
            {
                CursorX = lineColumns - 1;
                WrapPending = true;
            }
            else
            {
                CursorX = newCursor;
            }
        }

        return true;
    }

    private (Cell[] Row, int Column, int DesiredWidth, int LineColumns)? GetJoinTarget(Rune rune)
    {
        var y = CursorY;
        var x = WrapPending ? CursorX : CursorX - 1;
        if (!WrapPending && x < 0 && y > 0 && GetLiveLine(y - 1).Wrapped)
        {
            y--;
            x = Columns - 1;
        }

        if (x < 0)
        {
            return null;
        }

        var row = GetLiveLine(y).Cells;
        if (row[x].IsWideContinuation && x > 0)
        {
            x--;
        }

        ref var previous = ref row[x];
        if (previous.IsBlank)
        {
            return null;
        }

        var joinedByZwj =
            previous.CombiningCharacters?.EndsWith('\u200D') == true &&
            IsExtendedPictographic(rune) &&
            (IsExtendedPictographic(previous.Rune) ||
             previous.CombiningCharacters.EnumerateRunes().Any(IsExtendedPictographic));
        var emojiPresentationSelector =
            rune.Value == 0xFE0F &&
            IsExtendedPictographic(previous.Rune);
        var regionalPair =
            IsRegionalIndicator(previous.Rune) &&
            IsRegionalIndicator(rune) &&
            (previous.CombiningCharacters is null ||
             !previous.CombiningCharacters.EnumerateRunes().Any(IsRegionalIndicator));
        var hangulSyllable = IsHangulContinuation(previous, rune);
        var spacingMark = Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpacingCombiningMark;
        var prepend = IsPrepend(previous.Rune);
        var indicConjunct = IsIndicConsonant(rune) && EndsWithIndicLinker(previous);
        if (!joinedByZwj &&
            !emojiPresentationSelector &&
            !regionalPair &&
            !hangulSyllable &&
            !spacingMark &&
            !prepend &&
            !indicConjunct)
        {
            return null;
        }

        var desiredWidth = joinedByZwj || emojiPresentationSelector || regionalPair
            ? 2
            : Math.Max(previous.DisplayWidth, Math.Max(1, WcWidth.Width(rune)));
        var lineColumns = EffectiveColumns(GetLiveLine(y));
        return x + desiredWidth <= lineColumns ? (row, x, desiredWidth, lineColumns) : null;
    }

    private static bool IsRegionalIndicator(Rune rune) =>
        rune.Value is >= 0x1F1E6 and <= 0x1F1FF;

    private static bool IsExtendedPictographic(Rune rune) =>
        rune.Value is >= 0x1F000 and <= 0x1FAFF or
            >= 0x2300 and <= 0x23FF or
            >= 0x2600 and <= 0x27BF;

    private enum HangulClass : byte
    {
        Other,
        L,
        V,
        T,
        Lv,
        Lvt,
    }

    private static bool IsHangulContinuation(Cell previous, Rune current)
    {
        var prior = Hangul(previous.CombiningCharacters is { Length: > 0 }
            ? previous.CombiningCharacters.EnumerateRunes().Last()
            : previous.Rune);
        var next = Hangul(current);
        return prior == HangulClass.L && next is HangulClass.L or HangulClass.V or HangulClass.Lv or HangulClass.Lvt ||
               prior is HangulClass.Lv or HangulClass.V && next is HangulClass.V or HangulClass.T ||
               prior is HangulClass.Lvt or HangulClass.T && next == HangulClass.T;
    }

    private static HangulClass Hangul(Rune rune)
    {
        var value = rune.Value;
        if (value is >= 0x1100 and <= 0x115F or >= 0xA960 and <= 0xA97C)
        {
            return HangulClass.L;
        }

        if (value is >= 0x1160 and <= 0x11A7 or >= 0xD7B0 and <= 0xD7C6)
        {
            return HangulClass.V;
        }

        if (value is >= 0x11A8 and <= 0x11FF or >= 0xD7CB and <= 0xD7FB)
        {
            return HangulClass.T;
        }

        if (value is >= 0xAC00 and <= 0xD7A3)
        {
            return (value - 0xAC00) % 28 == 0 ? HangulClass.Lv : HangulClass.Lvt;
        }

        return HangulClass.Other;
    }

    private static bool IsPrepend(Rune rune) =>
        rune.Value is >= 0x0600 and <= 0x0605 or
            0x06DD or
            0x070F or
            0x0890 or 0x0891 or
            0x08E2 or
            0x0D4E or
            0x110BD or 0x110CD or
            >= 0x111C2 and <= 0x111C3 or
            0x1193F or 0x11941 or
            0x11A3A or
            >= 0x11A84 and <= 0x11A89 or
            0x11D46;

    private static bool IsIndicConsonant(Rune rune)
    {
        var value = rune.Value;
        if (value is not (>= 0x0900 and <= 0x0DFF or
                          >= 0x1000 and <= 0x109F or
                          >= 0x1780 and <= 0x17FF or
                          >= 0x1A20 and <= 0x1CFF or
                          >= 0xA800 and <= 0xABFF or
                          >= 0x11000 and <= 0x11FFF))
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherLetter;
    }

    private static bool EndsWithIndicLinker(Cell cell)
    {
        if (cell.CombiningCharacters is not { Length: > 0 } combining)
        {
            return false;
        }

        foreach (var rune in combining.EnumerateRunes().Reverse())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (IsIndicLinker(rune))
            {
                return true;
            }

            if (category is not (UnicodeCategory.NonSpacingMark or
                                 UnicodeCategory.SpacingCombiningMark or
                                 UnicodeCategory.Format))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsIndicLinker(Rune rune) =>
        rune.Value is 0x094D or 0x09CD or 0x0A4D or 0x0ACD or 0x0B4D or 0x0BCD or
            0x0C4D or 0x0CCD or 0x0D3B or 0x0D3C or 0x0D4D or 0x0DCA or 0x1039 or
            0x103A or 0x1714 or 0x1734 or 0x17D2 or 0x1A60 or 0x1B44 or 0x1BAA or
            0x1BAB or 0xA806 or 0xA8C4 or 0xA953 or 0xAAF6 or 0xABED or 0x10A3F or
            0x11046 or 0x11070 or 0x11133 or 0x111C0 or 0x11235 or 0x112EA or
            0x1134D or 0x11442 or 0x114C2 or 0x115BF or 0x1163F or 0x116B6 or
            0x1172B or 0x11839 or 0x1193D or 0x119E0 or 0x11A34 or 0x11A47 or
            0x11A99 or 0x11C3F or 0x11D44 or 0x11D45 or 0x11D97;

    private static void ClearGlyphAt(Cell[] row, int x)
    {
        if ((uint)x >= (uint)row.Length)
        {
            return;
        }

        if (row[x].IsWideContinuation)
        {
            row[x] = Cell.Blank;
            if (x > 0)
            {
                row[x - 1] = Cell.Blank;
            }
        }
        else if (row[x].DisplayWidth == 2)
        {
            row[x] = Cell.Blank;
            if (x + 1 < row.Length && row[x + 1].IsWideContinuation)
            {
                row[x + 1] = Cell.Blank;
            }
        }
    }

    private static void RepairWideCells(Cell[] row)
    {
        for (var x = 0; x < row.Length; x++)
        {
            if (row[x].IsWideContinuation)
            {
                if (x == 0 || row[x - 1].DisplayWidth != 2)
                {
                    row[x] = Cell.Blank;
                }
            }
            else if (row[x].DisplayWidth == 2)
            {
                if (x + 1 >= row.Length)
                {
                    row[x] = Cell.Blank;
                }
                else
                {
                    row[x + 1] = new Cell
                    {
                        Rune = new Rune(' '),
                        Attributes = row[x].Attributes,
                        IsProtected = row[x].IsProtected,
                        IsWideContinuation = true,
                        StoredWidth = 0,
                        HyperlinkUri = row[x].HyperlinkUri,
                        ShellIntegration = row[x].ShellIntegration,
                    };
                    x++;
                }
            }
        }
    }

    private List<BufferLine> Reflow(
        IReadOnlyList<BufferLine> source,
        int oldColumns,
        int newColumns,
        IReadOnlyList<TerminalImageAnchor>? retainedAnchors,
        int cursorLine,
        int cursorX,
        out int newCursorLine,
        out int newCursorX)
    {
        var result = new List<BufferLine>();
        newCursorLine = 0;
        newCursorX = 0;
        var paragraphCells = new List<Cell>();
        var paragraphMarks = new List<(int Offset, ShellMark Mark)>();
        var cursorOffset = -1;
        var paragraphRendition = LineRendition.SingleWidth;
        var paragraphLogicalLineId = 0L;
        var paragraphLogicalOffset = 0;

        for (var lineIndex = 0; lineIndex < source.Count; lineIndex++)
        {
            var line = source[lineIndex];
            if (paragraphCells.Count == 0)
            {
                paragraphRendition = line.Rendition;
                paragraphLogicalLineId = line.LogicalLineId;
                paragraphLogicalOffset = line.LogicalOffset;
            }

            var sourceColumns = line.Rendition == LineRendition.SingleWidth
                ? oldColumns
                : Math.Max(1, oldColumns / 2);
            var used = line.Wrapped ? sourceColumns : Math.Min(sourceColumns, LastContentColumn(line.Cells) + 1);
            if (retainedAnchors is not null)
            {
                for (var anchorIndex = 0; anchorIndex < retainedAnchors.Count; anchorIndex++)
                {
                    var anchor = retainedAnchors[anchorIndex];
                    if (anchor.LogicalLineId == line.LogicalLineId &&
                        anchor.LogicalOffset >= line.LogicalOffset &&
                        anchor.LogicalOffset < line.LogicalOffset + sourceColumns)
                    {
                        used = Math.Max(used, anchor.LogicalOffset - line.LogicalOffset + 1);
                    }
                }
            }
            foreach (var mark in line.Marks)
            {
                paragraphMarks.Add((DisplayWidth(paragraphCells) + mark.StartColumn, mark));
            }

            if (lineIndex == cursorLine)
            {
                used = Math.Max(used, cursorX);
                cursorOffset = DisplayWidth(paragraphCells) + Math.Min(cursorX, used);
            }

            for (var x = 0; x < used && x < line.Cells.Length; x++)
            {
                if (!line.Cells[x].IsWideContinuation)
                {
                    paragraphCells.Add(line.Cells[x]);
                }
            }

            if (!line.Wrapped ||
                lineIndex == source.Count - 1 ||
                source[lineIndex + 1].Rendition != line.Rendition)
            {
                var paragraphStart = result.Count;
                var destinationColumns = paragraphRendition == LineRendition.SingleWidth
                    ? newColumns
                    : Math.Max(1, newColumns / 2);
                EmitParagraph(
                    paragraphCells,
                    destinationColumns,
                    paragraphRendition,
                    result,
                    paragraphLogicalLineId,
                    paragraphLogicalOffset,
                    cursorOffset,
                    out var relativeLine,
                    out var relativeColumn);
                foreach (var (offset, mark) in paragraphMarks)
                {
                    var destination = paragraphStart + Math.Min(
                        offset / destinationColumns,
                        result.Count - paragraphStart - 1);
                    mark.StartColumn = offset % destinationColumns;
                    result[destination].Marks.Add(mark);
                }

                if (cursorOffset >= 0)
                {
                    newCursorLine = result.Count - relativeLine;
                    newCursorX = relativeColumn;
                    cursorOffset = -1;
                }

                paragraphCells.Clear();
                paragraphMarks.Clear();
            }
        }

        return result;
    }

    private void EmitParagraph(
        IReadOnlyList<Cell> cells,
        int columns,
        LineRendition rendition,
        List<BufferLine> output,
        long logicalLineId,
        int logicalOffset,
        int cursorOffset,
        out int linesAfterCursor,
        out int cursorColumn)
    {
        var line = NewBlankLine(CellAttributes.Default);
        line.Rendition = rendition;
        line.LogicalLineId = logicalLineId;
        line.LogicalOffset = logicalOffset;
        var x = 0;
        var cursorLineIndex = -1;
        cursorColumn = 0;
        var consumedWidth = 0;

        if (cursorOffset == 0)
        {
            cursorLineIndex = output.Count;
        }

        foreach (var cell in cells)
        {
            var width = cell.DisplayWidth;
            if (width > columns)
            {
                continue;
            }

            if (x + width > columns)
            {
                line.Wrapped = true;
                output.Add(line);
                line = NewBlankLine(CellAttributes.Default);
                line.Rendition = rendition;
                line.LogicalLineId = logicalLineId;
                line.LogicalOffset = logicalOffset + consumedWidth;
                x = 0;
            }

            if (cursorLineIndex < 0 && cursorOffset >= 0 && consumedWidth >= cursorOffset)
            {
                cursorLineIndex = output.Count;
                cursorColumn = x;
            }

            line.Cells[x] = cell;
            line.Cells[x].IsWideContinuation = false;
            if (width == 2)
            {
                line.Cells[x + 1] = new Cell
                {
                    Rune = new Rune(' '),
                    Attributes = cell.Attributes,
                    IsProtected = cell.IsProtected,
                    IsWideContinuation = true,
                    StoredWidth = 0,
                    HyperlinkUri = cell.HyperlinkUri,
                    ShellIntegration = cell.ShellIntegration,
                };
            }

            x += width;
            consumedWidth += width;
        }

        if (cursorLineIndex < 0 && cursorOffset >= 0)
        {
            cursorLineIndex = output.Count;
            cursorColumn = Math.Min(x, columns - 1);
        }

        output.Add(line);
        linesAfterCursor = cursorLineIndex < 0 ? 0 : output.Count - cursorLineIndex;
    }

    private static int LastContentColumn(Cell[] cells)
    {
        for (var x = cells.Length - 1; x >= 0; x--)
        {
            if (!cells[x].IsBlank && !cells[x].IsWideContinuation)
            {
                return Math.Min(cells.Length - 1, x + cells[x].DisplayWidth - 1);
            }
        }

        return -1;
    }

    private static int DisplayWidth(IReadOnlyList<Cell> cells)
    {
        var width = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            width += cells[i].DisplayWidth;
        }

        return width;
    }

    private static bool[] CreateDefaultTabStops(int columns)
    {
        var stops = new bool[columns];
        for (var x = 8; x < columns; x += 8)
        {
            stops[x] = true;
        }

        return stops;
    }

    private static bool[] ResizeTabStops(bool[] source, int columns)
    {
        var stops = CreateDefaultTabStops(columns);
        Array.Copy(source, stops, Math.Min(source.Length, stops.Length));
        return stops;
    }

    private static void Normalize(ref int x1, ref int y1, ref int x2, ref int y2)
    {
        if (y1 > y2 || (y1 == y2 && x1 > x2))
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }
    }
}
