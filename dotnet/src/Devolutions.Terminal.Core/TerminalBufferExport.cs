using System.Text;

namespace Devolutions.Terminal.Core;

public sealed record ShellCommandRange(
    ShellMark Mark,
    BufferRange? Prompt,
    BufferRange? Command,
    BufferRange? Output);

public static class TerminalBufferExport
{
    public static string ToPlainText(
        TextBufferSnapshot snapshot,
        bool trimTrailingWhitespace = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var output = new StringBuilder();
        var logicalLine = new StringBuilder();
        for (var lineIndex = 0; lineIndex < snapshot.Lines.Count; lineIndex++)
        {
            var line = snapshot.Lines[lineIndex];
            logicalLine.Append(LineText(line.Cells));
            if (!line.Wrapped && lineIndex < snapshot.Lines.Count - 1)
            {
                var text = logicalLine.ToString();
                output.Append(trimTrailingWhitespace ? text.TrimEnd() : text);
                output.AppendLine();
                logicalLine.Clear();
            }
        }

        if (logicalLine.Length > 0)
        {
            var text = logicalLine.ToString();
            output.Append(trimTrailingWhitespace ? text.TrimEnd() : text);
        }

        return output.ToString();
    }

    public static IReadOnlyList<ShellCommandRange> GetShellCommandRanges(
        TextBufferSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var marks = snapshot.Lines
            .SelectMany((line, lineIndex) => line.Marks.Select(mark => new
            {
                Mark = mark,
                Position = new BufferPosition(lineIndex, mark.StartColumn),
            }))
            .OrderBy(static item => item.Position.Line)
            .ThenBy(static item => item.Position.Column)
            .ToArray();
        var ranges = new ShellCommandRange[marks.Length];
        for (var markIndex = 0; markIndex < marks.Length; markIndex++)
        {
            var start = marks[markIndex].Position;
            var end = markIndex + 1 < marks.Length
                ? marks[markIndex + 1].Position
                : new BufferPosition(snapshot.Lines.Count - 1, snapshot.Columns);
            ranges[markIndex] = new ShellCommandRange(
                marks[markIndex].Mark,
                FindRegion(snapshot, start, end, ShellIntegrationKind.Prompt),
                FindRegion(snapshot, start, end, ShellIntegrationKind.Command),
                FindRegion(snapshot, start, end, ShellIntegrationKind.Output));
        }

        return ranges;
    }

    public static string GetRangeText(TextBufferSnapshot snapshot, BufferRange range)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var startLine = Math.Clamp(range.Start.Line, 0, snapshot.Lines.Count - 1);
        var endLine = Math.Clamp(range.End.Line, startLine, snapshot.Lines.Count - 1);
        var output = new StringBuilder();
        for (var line = startLine; line <= endLine; line++)
        {
            var cells = snapshot.Lines[line].Cells;
            var startColumn = line == startLine ? Math.Clamp(range.Start.Column, 0, cells.Count) : 0;
            var endColumn = line == endLine ? Math.Clamp(range.End.Column, startColumn, cells.Count) : cells.Count;
            var lineText = new StringBuilder();
            for (var column = startColumn; column < endColumn; column++)
            {
                if (!cells[column].IsWideContinuation)
                {
                    lineText.Append(cells[column].Text);
                }
            }

            if (line < endLine && !snapshot.Lines[line].Wrapped)
            {
                output.Append(lineText.ToString().TrimEnd());
                output.AppendLine();
            }
            else
            {
                output.Append(lineText);
            }
        }

        return output.ToString().TrimEnd();
    }

    public static IReadOnlyList<string> GetCommandHistory(TextBufferSnapshot snapshot) =>
        GetShellCommandRanges(snapshot)
            .Where(static range => range.Command is not null)
            .Select(range => GetRangeText(snapshot, range.Command!.Value))
            .Where(static command => command.Length > 0)
            .ToArray();

    private static BufferRange? FindRegion(
        TextBufferSnapshot snapshot,
        BufferPosition rangeStart,
        BufferPosition rangeEnd,
        ShellIntegrationKind kind)
    {
        BufferPosition? start = null;
        BufferPosition? end = null;
        for (var lineIndex = rangeStart.Line; lineIndex <= rangeEnd.Line; lineIndex++)
        {
            var cells = snapshot.Lines[lineIndex].Cells;
            var firstColumn = lineIndex == rangeStart.Line ? rangeStart.Column : 0;
            var lastColumn = lineIndex == rangeEnd.Line ? rangeEnd.Column : cells.Count;
            for (var column = firstColumn; column < lastColumn; column++)
            {
                var cell = cells[column];
                if (cell.ShellIntegration != kind || cell.IsWideContinuation)
                {
                    continue;
                }

                start ??= new BufferPosition(lineIndex, column);
                var width = column + 1 < cells.Count && cells[column + 1].IsWideContinuation ? 2 : 1;
                end = new BufferPosition(lineIndex, column + width);
            }
        }

        return start is null || end is null ? null : new BufferRange(start.Value, end.Value);
    }

    private static string LineText(IReadOnlyList<Cell> cells)
    {
        var output = new StringBuilder();
        foreach (var cell in cells)
        {
            if (!cell.IsWideContinuation)
            {
                output.Append(cell.Text);
            }
        }

        return output.ToString();
    }
}
