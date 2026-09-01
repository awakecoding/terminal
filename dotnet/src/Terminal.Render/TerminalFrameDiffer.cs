namespace Microsoft.Terminal.Render;

public static class TerminalFrameDiffer
{
    public static IReadOnlyList<int> GetDirtyRows(
        TerminalRenderFrame? previous,
        TerminalRenderFrame current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null ||
            previous.Columns != current.Columns ||
            previous.Rows != current.Rows ||
            previous.Background != current.Background)
        {
            return Enumerable.Range(0, current.Rows).ToArray();
        }

        var dirty = new List<int>();
        var rowCount = Math.Min(previous.RowsData.Count, current.RowsData.Count);
        for (var row = 0; row < rowCount; row++)
        {
            if (!RowsEqual(previous.RowsData[row], current.RowsData[row]))
            {
                dirty.Add(row);
            }
        }

        AddCursorRow(previous, current, previous.CursorY, dirty);
        AddCursorRow(previous, current, current.CursorY, dirty);
        dirty.Sort();
        return dirty;
    }

    private static void AddCursorRow(
        TerminalRenderFrame previous,
        TerminalRenderFrame current,
        int row,
        List<int> dirty)
    {
        if (row < 0 || row >= current.Rows || dirty.Contains(row))
        {
            return;
        }

        if (previous.CursorX != current.CursorX ||
            previous.CursorY != current.CursorY ||
            previous.CursorVisible != current.CursorVisible ||
            previous.CursorStyle != current.CursorStyle ||
            previous.CursorHeightPercentage != current.CursorHeightPercentage ||
            previous.CursorColor != current.CursorColor)
        {
            dirty.Add(row);
        }
    }

    private static bool RowsEqual(TerminalRenderRow left, TerminalRenderRow right)
    {
        if (left.Runs.Count != right.Runs.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Runs.Count; index++)
        {
            var a = left.Runs[index];
            var b = right.Runs[index];
            if (a.StartColumn != b.StartColumn ||
                a.CellCount != b.CellCount ||
                a.Attributes != b.Attributes ||
                !string.Equals(a.Text, b.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
