using System.Text;
using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Render;

public static class TerminalRenderPlanner
{
    public static TerminalRenderFrame Create(
        TerminalSnapshot snapshot,
        ColorScheme scheme,
        TerminalRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scheme);
        options ??= new TerminalRenderOptions();

        var rows = new TerminalRenderRow[snapshot.Buffer.Lines.Count];
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            rows[rowIndex] = PlanRow(
                rowIndex,
                snapshot.Buffer.Lines[rowIndex].Cells,
                scheme,
                snapshot.ReverseVideo);
        }

        var cursorY = snapshot.Buffer.CursorY + snapshot.Buffer.ScrollOffset;
        return new TerminalRenderFrame(
            snapshot.Buffer.Columns,
            snapshot.Buffer.Rows,
            snapshot.Buffer.CursorX,
            cursorY,
            snapshot.CursorVisible && cursorY < snapshot.Buffer.Rows,
            snapshot.ReverseVideo ? scheme.Foreground : scheme.Background,
            scheme.Cursor,
            scheme.SelectionBackground,
            rows)
        {
            CursorStyle = options.CursorStyle,
            CursorHeightPercentage = Math.Clamp(options.CursorHeightPercentage, 1, 100),
        };
    }

    public static ResolvedCellAttributes Resolve(
        CellAttributes attributes,
        string? hyperlinkUri,
        ColorScheme scheme,
        bool reverseScreen)
    {
        var foreground = attributes.Foreground.ToArgb(scheme, foreground: true);
        var background = attributes.Background.ToArgb(scheme, foreground: false);
        if ((attributes.Flags & CellFlags.Faint) != 0)
        {
            foreground = Fade(foreground);
        }

        if ((attributes.Flags & CellFlags.Inverse) != 0)
        {
            (foreground, background) = (background, foreground);
        }

        if (reverseScreen)
        {
            (foreground, background) = (background, foreground);
        }

        return new ResolvedCellAttributes(
            foreground,
            background,
            attributes.Flags,
            hyperlinkUri);
    }

    private static TerminalRenderRow PlanRow(
        int rowIndex,
        IReadOnlyList<Cell> cells,
        ColorScheme scheme,
        bool reverseScreen)
    {
        var runs = new List<TerminalRenderRun>();
        var column = 0;
        while (column < cells.Count)
        {
            var start = column;
            var first = cells[column];
            var resolved = Resolve(first.Attributes, first.HyperlinkUri, scheme, reverseScreen);
            var text = new StringBuilder();
            var clusters = new List<TerminalTextCluster>();
            do
            {
                var cell = cells[column];
                if (!cell.IsWideContinuation)
                {
                    var offset = text.Length;
                    text.Append(cell.Rune);
                    text.Append(cell.CombiningCharacters);
                    var cellWidth = Math.Max(1, WcWidth.Width(cell.Rune));
                    if (clusters.Count > 0 && EndsWithJoiner(text, offset))
                    {
                        var previous = clusters[^1];
                        clusters[^1] = previous with
                        {
                            TextLength = text.Length - previous.TextOffset,
                            CellCount = (column + cellWidth) - previous.StartColumn,
                        };
                    }
                    else
                    {
                        clusters.Add(new TerminalTextCluster(
                            offset,
                            text.Length - offset,
                            column,
                            cellWidth));
                    }
                }

                column++;
            }
            while (column < cells.Count &&
                   Resolve(cells[column].Attributes, cells[column].HyperlinkUri, scheme, reverseScreen) == resolved);

            runs.Add(new TerminalRenderRun(
                start,
                column - start,
                text.ToString(),
                resolved,
                clusters.ToArray()));
        }

        return new TerminalRenderRow(rowIndex, runs);
    }

    private static bool EndsWithJoiner(StringBuilder text, int currentOffset) =>
        currentOffset > 0 && text[currentOffset - 1] == '\u200D';

    private static uint Fade(uint argb)
    {
        var alpha = (byte)(argb >> 24);
        var red = (byte)((argb >> 16) & 0xFF);
        var green = (byte)((argb >> 8) & 0xFF);
        var blue = (byte)(argb & 0xFF);
        return ((uint)alpha << 24) |
               ((uint)(red / 2) << 16) |
               ((uint)(green / 2) << 8) |
               (byte)(blue / 2);
    }
}
