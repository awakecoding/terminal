namespace Devolutions.Terminal.Render;

public static class TerminalOverlayPlanner
{
    public static IReadOnlyList<TerminalCellRange> CreateSelection(
        int x1,
        int y1,
        int x2,
        int y2,
        int columns,
        int rows,
        uint color)
    {
        if (columns <= 0 || rows <= 0)
        {
            return [];
        }

        x1 = Math.Clamp(x1, 0, columns - 1);
        x2 = Math.Clamp(x2, 0, columns - 1);
        y1 = Math.Clamp(y1, 0, rows - 1);
        y2 = Math.Clamp(y2, 0, rows - 1);
        if (y1 > y2 || (y1 == y2 && x1 > x2))
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }

        var ranges = new TerminalCellRange[y2 - y1 + 1];
        for (var row = y1; row <= y2; row++)
        {
            ranges[row - y1] = new TerminalCellRange(
                row,
                row == y1 ? x1 : 0,
                row == y2 ? x2 : columns - 1,
                color);
        }

        return ranges;
    }
}
