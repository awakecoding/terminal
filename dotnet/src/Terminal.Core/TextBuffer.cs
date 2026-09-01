using System.Text;

namespace Microsoft.Terminal.Core;

public sealed class TextBuffer
{
    private readonly List<Cell[]> _lines = [];
    private readonly int _historySize;

    public TextBuffer(int columns, int rows, int historySize, bool hasHistory)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _historySize = hasHistory ? Math.Max(0, historySize) : 0;
        HasHistory = hasHistory;
        ScrollTop = 0;
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
    public int ViewportStart { get; private set; }
    public int ScrollOffset { get; set; }
    public bool OriginMode { get; set; }
    public bool WrapPending { get; set; }

    public int TotalLines => _lines.Count;
    public int HistoryCount => Math.Max(0, ViewportStart);

    public CellAttributes CurrentAttributes { get; set; } = CellAttributes.Default;

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

        var oldCols = Columns;
        Columns = columns;
        Rows = rows;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;

        for (var i = 0; i < _lines.Count; i++)
        {
            _lines[i] = ResizeRow(_lines[i], oldCols, columns);
        }

        EnsureViewportLines();
        CursorX = Math.Clamp(CursorX, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY, 0, Rows - 1);
        WrapPending = false;
        ScrollOffset = 0;
    }

    public Cell[] GetRow(int viewportY)
    {
        var index = VisibleIndex(viewportY);
        return _lines[index];
    }

    public Cell GetCell(int x, int y)
    {
        if ((uint)x >= (uint)Columns)
        {
            return Cell.Blank;
        }

        return GetRow(y)[x];
    }

    public void Print(Rune rune)
    {
        var width = WcWidth.Width(rune);
        if (width <= 0)
        {
            return;
        }

        if (WrapPending)
        {
            CarriageReturn();
            LineFeed();
            WrapPending = false;
        }

        if (CursorX + width > Columns)
        {
            CarriageReturn();
            LineFeed();
        }

        var row = GetRow(CursorY);
        row[CursorX] = new Cell
        {
            Rune = rune,
            Attributes = CurrentAttributes,
        };

        if (width == 2 && CursorX + 1 < Columns)
        {
            row[CursorX + 1] = new Cell
            {
                Rune = new Rune(' '),
                Attributes = CurrentAttributes,
                IsWideContinuation = true,
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
        }
    }

    public void Tab()
    {
        WrapPending = false;
        CursorX = Math.Min(Columns - 1, (CursorX + 8) & ~7);
    }

    public void SetCursor(int row, int col, bool relativeToOrigin = true)
    {
        WrapPending = false;
        var top = OriginMode && relativeToOrigin ? ScrollTop : 0;
        var bottom = OriginMode && relativeToOrigin ? ScrollBottom : Rows - 1;
        CursorY = Math.Clamp(top + row, top, bottom);
        CursorX = Math.Clamp(col, 0, Columns - 1);
    }

    public void MoveCursor(int dx, int dy)
    {
        WrapPending = false;
        CursorX = Math.Clamp(CursorX + dx, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY + dy, 0, Rows - 1);
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
        CursorX = 0;
        CursorY = OriginMode ? ScrollTop : 0;
        WrapPending = false;
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
        var row = GetRow(CursorY);
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
        count = Math.Max(1, count);
        var start = ViewportStart + CursorY;
        var end = ViewportStart + ScrollBottom;
        for (var i = 0; i < count; i++)
        {
            if (end >= start)
            {
                _lines.RemoveAt(end);
                _lines.Insert(start, BlankRow());
            }
        }
    }

    public void DeleteLines(int count)
    {
        count = Math.Max(1, count);
        var start = ViewportStart + CursorY;
        var end = ViewportStart + ScrollBottom;
        for (var i = 0; i < count; i++)
        {
            if (end >= start)
            {
                _lines.RemoveAt(start);
                _lines.Insert(end, BlankRow());
            }
        }
    }

    public void InsertCharacters(int count)
    {
        count = Math.Max(1, count);
        var row = GetRow(CursorY);
        for (var i = 0; i < count; i++)
        {
            Array.Copy(row, CursorX, row, CursorX + 1, Columns - CursorX - 1);
            row[CursorX] = BlankWithCurrent();
        }
    }

    public void DeleteCharacters(int count)
    {
        count = Math.Max(1, count);
        var row = GetRow(CursorY);
        var remaining = Math.Max(0, Columns - CursorX - count);
        if (remaining > 0)
        {
            Array.Copy(row, CursorX + count, row, CursorX, remaining);
        }

        for (var x = Columns - count; x < Columns; x++)
        {
            if (x >= CursorX)
            {
                row[Math.Max(x, CursorX)] = BlankWithCurrent();
            }
        }

        for (var x = Math.Max(CursorX, Columns - count); x < Columns; x++)
        {
            row[x] = BlankWithCurrent();
        }
    }

    public void EraseCharacters(int count)
    {
        count = Math.Max(1, count);
        var row = GetRow(CursorY);
        var end = Math.Min(Columns, CursorX + count);
        EraseCells(row, CursorX, end);
    }

    public void ScrollUp(int count)
    {
        count = Math.Max(1, count);
        for (var i = 0; i < count; i++)
        {
            if (ScrollTop == 0 && ScrollBottom == Rows - 1 && HasHistory)
            {
                _lines.Add(BlankRow());
                ViewportStart++;
                TrimHistoryOverflow();
            }
            else
            {
                var top = ViewportStart + ScrollTop;
                var bottom = ViewportStart + ScrollBottom;
                _lines.RemoveAt(top);
                _lines.Insert(bottom, BlankRow());
            }
        }
    }

    public void ScrollDown(int count)
    {
        count = Math.Max(1, count);
        for (var i = 0; i < count; i++)
        {
            var top = ViewportStart + ScrollTop;
            var bottom = ViewportStart + ScrollBottom;
            _lines.RemoveAt(bottom);
            _lines.Insert(top, BlankRow());
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
        CursorX = 0;
        CursorY = 0;
        WrapPending = false;
        OriginMode = false;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        ScrollOffset = 0;
        if (!keepHistory)
        {
            _lines.Clear();
            ViewportStart = 0;
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
                if (row[x].IsWideContinuation)
                {
                    continue;
                }

                if (!row[x].IsBlank)
                {
                    last = x;
                }
            }

            for (var x = from; x <= last && x < Columns; x++)
            {
                if (!row[x].IsWideContinuation)
                {
                    sb.Append(row[x].Rune.ToString());
                }
            }

            if (y != endY)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    public string GetVisibleText() => GetText(0, 0, Columns - 1, Rows - 1);

    private void EnsureViewportLines()
    {
        var needed = ViewportStart + Rows;
        while (_lines.Count < needed)
        {
            _lines.Add(BlankRow());
        }
    }

    private void TrimHistoryOverflow()
    {
        if (!HasHistory || _historySize <= 0)
        {
            return;
        }

        while (ViewportStart > _historySize)
        {
            _lines.RemoveAt(0);
            ViewportStart--;
        }
    }

    private void TrimHistory()
    {
        if (ViewportStart <= 0)
        {
            return;
        }

        _lines.RemoveRange(0, ViewportStart);
        ViewportStart = 0;
    }

    private int VisibleIndex(int viewportY)
    {
        viewportY = Math.Clamp(viewportY, 0, Rows - 1);
        var index = ViewportStart - ScrollOffset + viewportY;
        if (index < 0)
        {
            index = 0;
        }

        if (index >= _lines.Count)
        {
            EnsureViewportLines();
            index = Math.Min(index, _lines.Count - 1);
        }

        return index;
    }

    private Cell[] BlankRow()
    {
        var row = new Cell[Columns];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = BlankWithCurrent();
        }

        return row;
    }

    private Cell BlankWithCurrent() => new()
    {
        Rune = new Rune(' '),
        Attributes = CurrentAttributes,
    };

    private void EraseLineRange(int fromY, int toY)
    {
        for (var y = fromY; y <= toY; y++)
        {
            if (y >= 0 && y < Rows)
            {
                EraseCells(GetRow(y), 0, Columns);
            }
        }
    }

    private void EraseCells(Cell[] row, int from, int to)
    {
        from = Math.Clamp(from, 0, row.Length);
        to = Math.Clamp(to, 0, row.Length);
        for (var x = from; x < to; x++)
        {
            row[x] = BlankWithCurrent();
        }
    }

    private static Cell[] ResizeRow(Cell[] source, int oldCols, int newCols)
    {
        var row = new Cell[newCols];
        var copy = Math.Min(oldCols, newCols);
        for (var i = 0; i < copy && i < source.Length; i++)
        {
            row[i] = source[i];
        }

        for (var i = copy; i < newCols; i++)
        {
            row[i] = Cell.Blank;
        }

        return row;
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
