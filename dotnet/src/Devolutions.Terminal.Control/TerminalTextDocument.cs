using System.Text;
using Devolutions.Terminal.Core;

namespace Devolutions.Terminal;

internal sealed class TerminalTextDocument
{
    private readonly int[,] _offsets;

    private TerminalTextDocument(string text, int[,] offsets, int lineCount)
    {
        Text = text;
        _offsets = offsets;
        LineCount = lineCount;
    }

    public string Text { get; }
    public int LineCount { get; }
    public TerminalTextRange DocumentRange => new(0, Text.Length);

    public static TerminalTextDocument Create(TextBufferSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var output = new StringBuilder();
        var offsets = new int[snapshot.Lines.Count, snapshot.Columns + 1];
        for (var line = 0; line < snapshot.Lines.Count; line++)
        {
            var cells = snapshot.Lines[line].Cells;
            for (var column = 0; column < snapshot.Columns; column++)
            {
                offsets[line, column] = output.Length;
                if (!cells[column].IsWideContinuation)
                {
                    output.Append(cells[column].Text);
                }
            }

            offsets[line, snapshot.Columns] = output.Length;
            if (line + 1 < snapshot.Lines.Count && !snapshot.Lines[line].Wrapped)
            {
                output.Append('\n');
            }
        }

        return new TerminalTextDocument(output.ToString(), offsets, snapshot.Lines.Count);
    }

    public static string CreateText(TextBufferSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var output = new StringBuilder();
        for (var line = 0; line < snapshot.Lines.Count; line++)
        {
            foreach (var cell in snapshot.Lines[line].Cells)
            {
                if (!cell.IsWideContinuation)
                {
                    output.Append(cell.Text);
                }
            }

            if (line + 1 < snapshot.Lines.Count && !snapshot.Lines[line].Wrapped)
            {
                output.Append('\n');
            }
        }

        return output.ToString();
    }

    public int Offset(TerminalSelectionPoint point, bool afterCell = false)
    {
        if (LineCount == 0)
        {
            return 0;
        }

        var line = Math.Clamp(point.Line, 0, LineCount - 1);
        var column = Math.Clamp(point.Column + (afterCell ? 1 : 0), 0, _offsets.GetLength(1) - 1);
        return _offsets[line, column];
    }

    public TerminalTextRange Range(TerminalSelection? selection)
    {
        if (selection is null)
        {
            return CaretRange(new TerminalSelectionPoint(0, 0));
        }

        var anchor = Offset(selection.Anchor);
        var active = Offset(selection.Active);
        return anchor <= active
            ? new TerminalTextRange(anchor, Offset(selection.Active, afterCell: true))
            : new TerminalTextRange(active, Offset(selection.Anchor, afterCell: true));
    }

    public TerminalTextRange CaretRange(TerminalSelectionPoint caret)
    {
        var offset = Offset(caret);
        return new TerminalTextRange(offset, offset);
    }
}
