using System.Text;

namespace Microsoft.Terminal.Core;

public sealed record TextBufferLineSnapshot(
    IReadOnlyList<Cell> Cells,
    bool Wrapped,
    IReadOnlyList<ShellMark> Marks)
{
    public ShellMark? Mark => Marks.Count > 0 ? Marks[0] : null;
}

public sealed record TextBufferSnapshot(
    int Columns,
    int Rows,
    int CursorX,
    int CursorY,
    int HistoryCount,
    int ScrollOffset,
    IReadOnlyList<TextBufferLineSnapshot> Lines);

public sealed class TextBuffer
{
    private sealed class BufferLine
    {
        public BufferLine(Cell[] cells)
        {
            Cells = cells;
        }

        public Cell[] Cells { get; }
        public bool Wrapped { get; set; }
        public List<ShellMark> Marks { get; } = [];
    }

    private CircularBuffer<BufferLine> _lines;
    private readonly int _historySize;
    private bool[] _tabStops;
    private int _scrollOffset;
    private ShellIntegrationKind _shellIntegration;
    private ShellMark? _activeMark;
    private bool _activeMarkHasOutput;

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
    public CellAttributes CurrentAttributes { get; set; } = CellAttributes.Default;
    public string? CurrentHyperlinkUri { get; set; }
    public int SavedCursorX { get; set; }
    public int SavedCursorY { get; set; }
    public CellAttributes SavedAttributes { get; set; } = CellAttributes.Default;

    public void Resize(int columns, int rows)
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
        var source = _lines.ToList();
        var activeMark = _activeMark;

        Columns = columns;
        Rows = rows;
        _tabStops = ResizeTabStops(_tabStops, columns);
        ScrollTop = 0;
        ScrollBottom = rows - 1;
        WrapPending = false;
        ScrollOffset = 0;

        var reflowed = Reflow(source, oldColumns, columns, cursorAbsoluteLine, CursorX, out var cursorLine, out var cursorColumn);
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
                    .ToArray());
        }

        return new TextBufferSnapshot(Columns, Rows, CursorX, CursorY, HistoryCount, ScrollOffset, copies);
    }

    public void Print(Rune rune)
    {
        var width = WcWidth.Width(rune);
        if (width == 0)
        {
            AppendCombining(rune);
            return;
        }

        if (WrapPending)
        {
            GetLiveLine(CursorY).Wrapped = true;
            CarriageReturn();
            LineFeed();
        }

        if (CursorX + width > Columns)
        {
            GetLiveLine(CursorY).Wrapped = true;
            CarriageReturn();
            LineFeed();
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
            HyperlinkUri = CurrentHyperlinkUri,
            ShellIntegration = _shellIntegration,
        };

        if (width == 2 && CursorX + 1 < Columns)
        {
            row[CursorX + 1] = new Cell
            {
                Rune = new Rune(' '),
                Attributes = CurrentAttributes,
                IsWideContinuation = true,
                HyperlinkUri = CurrentHyperlinkUri,
                ShellIntegration = _shellIntegration,
            };
        }

        CursorX += width;
        if (CursorX >= Columns)
        {
            CursorX = Columns - 1;
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
        for (var n = 0; n < Math.Max(1, count); n++)
        {
            var next = Columns - 1;
            for (var x = CursorX + 1; x < Columns; x++)
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

    public void SetCursor(int row, int col, bool relativeToOrigin = true)
    {
        WrapPending = false;
        var top = OriginMode && relativeToOrigin ? ScrollTop : 0;
        var bottom = OriginMode && relativeToOrigin ? ScrollBottom : Rows - 1;
        CursorY = Math.Clamp(top + row, top, bottom);
        CursorX = Math.Clamp(col, 0, Columns - 1);
    }

    public void MoveCursor(int dx, int dy, bool respectMargins = false)
    {
        WrapPending = false;
        CursorX = Math.Clamp(CursorX + dx, 0, Columns - 1);
        var withinMargins = CursorY >= ScrollTop && CursorY <= ScrollBottom;
        var top = OriginMode || (respectMargins && withinMargins) ? ScrollTop : 0;
        var bottom = OriginMode || (respectMargins && withinMargins) ? ScrollBottom : Rows - 1;
        CursorY = Math.Clamp(CursorY + dy, top, bottom);
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
    }

    public void RestoreCursor()
    {
        CursorX = Math.Clamp(SavedCursorX, 0, Columns - 1);
        CursorY = Math.Clamp(SavedCursorY, 0, Rows - 1);
        CurrentAttributes = SavedAttributes;
        WrapPending = false;
    }

    public void Reset(bool keepHistory)
    {
        CurrentAttributes = CellAttributes.Default;
        CurrentHyperlinkUri = null;
        _shellIntegration = ShellIntegrationKind.None;
        _activeMark = null;
        _activeMarkHasOutput = false;
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

        return new BufferLine(row);
    }

    private static Cell BlankWith(CellAttributes attributes) => new()
    {
        Rune = new Rune(' '),
        Attributes = attributes,
    };

    private void EraseLineRange(int fromY, int toY)
    {
        for (var y = Math.Max(0, fromY); y <= Math.Min(toY, Rows - 1); y++)
        {
            EraseCells(GetLiveLine(y).Cells, 0, Columns);
            GetLiveLine(y).Wrapped = false;
        }
    }

    private void EraseCells(Cell[] row, int from, int to)
    {
        from = Math.Clamp(from, 0, row.Length);
        to = Math.Clamp(to, 0, row.Length);
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
        else if (WcWidth.Width(row[x].Rune) == 2)
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
                if (x == 0 || WcWidth.Width(row[x - 1].Rune) != 2)
                {
                    row[x] = Cell.Blank;
                }
            }
            else if (WcWidth.Width(row[x].Rune) == 2)
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
                        IsWideContinuation = true,
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

        for (var lineIndex = 0; lineIndex < source.Count; lineIndex++)
        {
            var line = source[lineIndex];
            var used = line.Wrapped ? oldColumns : LastContentColumn(line.Cells) + 1;
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

            if (!line.Wrapped || lineIndex == source.Count - 1)
            {
                var paragraphStart = result.Count;
                EmitParagraph(paragraphCells, newColumns, result, cursorOffset, out var relativeLine, out var relativeColumn);
                foreach (var (offset, mark) in paragraphMarks)
                {
                    var destination = paragraphStart + Math.Min(
                        offset / newColumns,
                        result.Count - paragraphStart - 1);
                    mark.StartColumn = offset % newColumns;
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
        List<BufferLine> output,
        int cursorOffset,
        out int linesAfterCursor,
        out int cursorColumn)
    {
        var line = NewBlankLine(CellAttributes.Default);
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
            var width = Math.Max(1, WcWidth.Width(cell.Rune));
            if (width > columns)
            {
                continue;
            }

            if (x + width > columns)
            {
                line.Wrapped = true;
                output.Add(line);
                line = NewBlankLine(CellAttributes.Default);
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
                    IsWideContinuation = true,
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
                return Math.Min(cells.Length - 1, x + Math.Max(1, WcWidth.Width(cells[x].Rune)) - 1);
            }
        }

        return -1;
    }

    private static int DisplayWidth(IReadOnlyList<Cell> cells)
    {
        var width = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            width += Math.Max(1, WcWidth.Width(cells[i].Rune));
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
