using System.Globalization;
using System.Net;
using System.Text;
using Avalonia.Input;
using Microsoft.Terminal.Core;
using Microsoft.Terminal.Settings;

namespace Microsoft.Terminal.Control;

public static class TerminalInteractionModel
{
    public static TerminalSelection SelectAt(
        TextBufferSnapshot snapshot,
        TerminalSelectionPoint point,
        TerminalSelectionMode mode,
        string wordDelimiters)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        point = Clamp(snapshot, point);
        return mode switch
        {
            TerminalSelectionMode.Word => SelectWord(snapshot, point, wordDelimiters),
            TerminalSelectionMode.Line => SelectLine(snapshot, point),
            TerminalSelectionMode.Command => SelectShellRegion(snapshot, point, command: true),
            TerminalSelectionMode.Output => SelectShellRegion(snapshot, point, command: false),
            _ => new TerminalSelection(point, point, mode),
        };
    }

    public static TerminalSelection ExpandToWord(
        TextBufferSnapshot snapshot,
        TerminalSelection selection,
        string wordDelimiters)
    {
        var normalized = Normalize(snapshot, selection);
        var start = SelectWord(snapshot, normalized.Start, wordDelimiters).Anchor;
        var end = SelectWord(snapshot, normalized.End, wordDelimiters).Active;
        return new TerminalSelection(start, end, TerminalSelectionMode.Word, selection.ActiveEndpoint);
    }

    internal static NormalizedSelection Normalize(
        TextBufferSnapshot snapshot,
        TerminalSelection selection)
    {
        var anchor = Clamp(snapshot, selection.Anchor);
        var active = Clamp(snapshot, selection.Active);
        if (selection.Mode == TerminalSelectionMode.Block)
        {
            return new NormalizedSelection(
                new TerminalSelectionPoint(
                    Math.Min(anchor.Column, active.Column),
                    Math.Min(anchor.Line, active.Line)),
                new TerminalSelectionPoint(
                    Math.Max(anchor.Column, active.Column),
                    Math.Max(anchor.Line, active.Line)),
                selection.Mode);
        }

        return Compare(anchor, active) <= 0
            ? new NormalizedSelection(anchor, active, selection.Mode)
            : new NormalizedSelection(active, anchor, selection.Mode);
    }

    public static string GetSelectedText(
        TextBufferSnapshot snapshot,
        TerminalSelection selection,
        bool trimBlockSelection)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var range = Normalize(snapshot, selection);
        var output = new StringBuilder();
        for (var line = range.Start.Line; line <= range.End.Line; line++)
        {
            var cells = snapshot.Lines[line].Cells;
            var start = range.Mode == TerminalSelectionMode.Block || line == range.Start.Line
                ? range.Start.Column
                : 0;
            var end = range.Mode == TerminalSelectionMode.Block || line == range.End.Line
                ? range.End.Column
                : snapshot.Columns - 1;
            var text = CellText(cells, start, end);
            if (range.Mode == TerminalSelectionMode.Block && trimBlockSelection)
            {
                text = text.TrimEnd();
            }
            else if (range.Mode != TerminalSelectionMode.Block &&
                     line < range.End.Line &&
                     !snapshot.Lines[line].Wrapped)
            {
                text = text.TrimEnd();
            }

            output.Append(text);
            if (line < range.End.Line && (range.Mode == TerminalSelectionMode.Block || !snapshot.Lines[line].Wrapped))
            {
                output.Append('\n');
            }
        }

        return output.ToString();
    }

    public static TerminalClipboardPayload BuildClipboardPayload(
        string selectedText,
        TerminalCopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(selectedText);
        ArgumentNullException.ThrowIfNull(options);
        var text = ApplyControlSequencePolicy(selectedText, options.ControlSequencePolicy);
        if (options.SingleLine)
        {
            text = text.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        var html = (options.Formats & CopyFormat.Html) != 0 ? BuildHtml(text) : null;
        var rtf = (options.Formats & CopyFormat.Rtf) != 0 ? BuildRtf(text) : null;
        return new TerminalClipboardPayload(text, html, rtf);
    }

    public static TerminalPasteRequest PreparePaste(
        string? clipboardText,
        TerminalPasteOptions options,
        bool bracketedPaste = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = clipboardText ?? string.Empty;
        if (options.TrimWhitespace && !bracketedPaste)
        {
            text = text.Trim();
        }

        text = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (text.Length == 0)
        {
            return new TerminalPasteRequest(
                string.Empty,
                TerminalPasteWarning.None,
                0,
                0,
                bracketedPaste);
        }

        var lineCount = text.Count(static character => character == '\r') + 1;
        var warning = TerminalPasteWarning.None;
        if (options.WarnAboutLargePaste && text.Length >= Math.Max(1, options.LargePasteThreshold))
        {
            warning |= TerminalPasteWarning.Large;
        }

        var multilinePolicy = options.WarnAboutMultiLinePaste;
        if (lineCount > 1 &&
            !string.Equals(multilinePolicy, "never", StringComparison.OrdinalIgnoreCase) &&
            (!bracketedPaste ||
             string.Equals(multilinePolicy, "always", StringComparison.OrdinalIgnoreCase)))
        {
            warning |= TerminalPasteWarning.MultiLine;
        }

        return new TerminalPasteRequest(text, warning, text.Length, lineCount, bracketedPaste);
    }

    public static IReadOnlyList<TerminalScrollMark> GetScrollMarks(
        TextBufferSnapshot snapshot,
        IReadOnlyList<BufferRange> searchMatches,
        int currentSearchIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(searchMatches);
        var denominator = Math.Max(1, snapshot.Lines.Count - 1);
        var marks = new List<TerminalScrollMark>();
        for (var line = 0; line < snapshot.Lines.Count; line++)
        {
            foreach (var mark in snapshot.Lines[line].Marks)
            {
                marks.Add(new TerminalScrollMark(
                    line,
                    (double)line / denominator,
                    mark.ExitCode switch
                    {
                        null => TerminalScrollMarkKind.Prompt,
                        0 => TerminalScrollMarkKind.CommandSuccess,
                        _ => TerminalScrollMarkKind.CommandError,
                    },
                    mark.ExitCode));
            }
        }

        for (var index = 0; index < searchMatches.Count; index++)
        {
            var line = Math.Clamp(searchMatches[index].Start.Line, 0, snapshot.Lines.Count - 1);
            marks.Add(new TerminalScrollMark(
                line,
                (double)line / denominator,
                index == currentSearchIndex
                    ? TerminalScrollMarkKind.CurrentSearchMatch
                    : TerminalScrollMarkKind.SearchMatch));
        }

        return marks.OrderBy(static mark => mark.Line).ThenBy(static mark => mark.Kind).ToArray();
    }

    public static TerminalHyperlinkContext? HitTestHyperlink(
        TextBufferSnapshot snapshot,
        TerminalSelectionPoint point,
        IReadOnlySet<string> safeSchemes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(safeSchemes);
        point = Clamp(snapshot, point);
        var cells = snapshot.Lines[point.Line].Cells;
        var uri = cells[point.Column].HyperlinkUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var start = point.Column;
        var end = point.Column;
        while (start > 0 && string.Equals(cells[start - 1].HyperlinkUri, uri, StringComparison.Ordinal))
        {
            start--;
        }

        while (end + 1 < cells.Count &&
               string.Equals(cells[end + 1].HyperlinkUri, uri, StringComparison.Ordinal))
        {
            end++;
        }

        var canOpen = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                      safeSchemes.Contains(parsed.Scheme) &&
                      IsSafeTarget(parsed);
        return new TerminalHyperlinkContext(
            uri,
            CellText(cells, start, end).TrimEnd(),
            new TerminalSelectionPoint(start, point.Line),
            new TerminalSelectionPoint(end, point.Line),
            canOpen);
    }

    public static string BuildCursorRepositionSequence(
        int cursorColumn,
        int cursorRow,
        int targetColumn,
        int targetRow,
        bool applicationCursorKeys = false)
    {
        var output = new StringBuilder();
        var prefix = applicationCursorKeys ? "\u001bO" : "\u001b[";
        AppendMoves(output, targetRow - cursorRow, $"{prefix}B", $"{prefix}A");
        AppendMoves(output, targetColumn - cursorColumn, $"{prefix}C", $"{prefix}D");
        return output.ToString();
    }

    public static string BuildMouseSequence(
        int button,
        int column,
        int row,
        bool released,
        bool sgr,
        KeyModifiers modifiers)
    {
        var code = released && !sgr ? 3 : button;
        if ((modifiers & KeyModifiers.Shift) != 0) code |= 4;
        if ((modifiers & KeyModifiers.Alt) != 0) code |= 8;
        if ((modifiers & KeyModifiers.Control) != 0) code |= 16;
        column = Math.Max(0, column) + 1;
        row = Math.Max(0, row) + 1;
        if (sgr)
        {
            return $"\u001b[<{code};{column};{row}{(released ? 'm' : 'M')}";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"\u001b[M{(char)Math.Clamp(code + 32, 32, 255)}" +
            $"{(char)Math.Clamp(column + 32, 32, 255)}{(char)Math.Clamp(row + 32, 32, 255)}");
    }

    internal static TerminalSelectionPoint Clamp(TextBufferSnapshot snapshot, TerminalSelectionPoint point) =>
        SnapFromWideContinuation(
            snapshot,
            new TerminalSelectionPoint(
                Math.Clamp(point.Column, 0, Math.Max(0, snapshot.Columns - 1)),
                Math.Clamp(point.Line, 0, Math.Max(0, snapshot.Lines.Count - 1))));

    private static TerminalSelection SelectWord(
        TextBufferSnapshot snapshot,
        TerminalSelectionPoint point,
        string delimiters)
    {
        var cells = snapshot.Lines[point.Line].Cells;
        var start = point.Column;
        var end = point.Column;
        var delimiter = IsDelimiterAt(cells, point.Column, delimiters);
        while (start > 0 && IsDelimiterAt(cells, start - 1, delimiters) == delimiter)
        {
            start--;
        }

        while (end + 1 < cells.Count && IsDelimiterAt(cells, end + 1, delimiters) == delimiter)
        {
            end++;
        }

        return new TerminalSelection(
            SnapFromWideContinuation(snapshot, new TerminalSelectionPoint(start, point.Line)),
            SnapFromWideContinuation(snapshot, new TerminalSelectionPoint(end, point.Line)),
            TerminalSelectionMode.Word);
    }

    private static TerminalSelection SelectLine(TextBufferSnapshot snapshot, TerminalSelectionPoint point)
    {
        var startLine = point.Line;
        var endLine = point.Line;
        while (startLine > 0 && snapshot.Lines[startLine - 1].Wrapped)
        {
            startLine--;
        }

        while (endLine < snapshot.Lines.Count - 1 && snapshot.Lines[endLine].Wrapped)
        {
            endLine++;
        }

        return new TerminalSelection(
            new TerminalSelectionPoint(0, startLine),
            new TerminalSelectionPoint(snapshot.Columns - 1, endLine),
            TerminalSelectionMode.Line);
    }

    private static TerminalSelection SelectShellRegion(
        TextBufferSnapshot snapshot,
        TerminalSelectionPoint point,
        bool command)
    {
        var regions = TerminalBufferExport.GetShellCommandRanges(snapshot);
        var region = regions.FirstOrDefault(candidate =>
            Contains(candidate.Prompt, point) ||
            Contains(candidate.Command, point) ||
            Contains(candidate.Output, point));
        var selected = command ? region?.Command : region?.Output;
        if (selected is null)
        {
            return SelectLine(snapshot, point) with
            {
                Mode = command ? TerminalSelectionMode.Command : TerminalSelectionMode.Output,
            };
        }

        var end = selected.Value.End;
        return new TerminalSelection(
            new TerminalSelectionPoint(selected.Value.Start.Column, selected.Value.Start.Line),
            new TerminalSelectionPoint(Math.Max(0, end.Column - 1), end.Line),
            command ? TerminalSelectionMode.Command : TerminalSelectionMode.Output);
    }

    private static bool Contains(BufferRange? range, TerminalSelectionPoint point) =>
        range is { } value &&
        Compare(new TerminalSelectionPoint(value.Start.Column, value.Start.Line), point) <= 0 &&
        Compare(point, new TerminalSelectionPoint(value.End.Column, value.End.Line)) < 0;

    private static bool IsDelimiter(Cell cell, string delimiters) =>
        string.IsNullOrEmpty(cell.Text) ||
        char.IsWhiteSpace(cell.Text[0]) ||
        delimiters.Contains(cell.Text[0], StringComparison.Ordinal);

    private static bool IsDelimiterAt(IReadOnlyList<Cell> cells, int column, string delimiters)
    {
        while (column > 0 && cells[column].IsWideContinuation)
        {
            column--;
        }

        return IsDelimiter(cells[column], delimiters);
    }

    private static string CellText(IReadOnlyList<Cell> cells, int start, int end)
    {
        var output = new StringBuilder();
        for (var column = Math.Max(0, start); column <= Math.Min(end, cells.Count - 1); column++)
        {
            if (!cells[column].IsWideContinuation)
            {
                output.Append(cells[column].Text);
            }
        }

        return output.ToString();
    }

    private static int Compare(TerminalSelectionPoint left, TerminalSelectionPoint right) =>
        left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Column.CompareTo(right.Column);

    private static string ApplyControlSequencePolicy(
        string text,
        TerminalControlSequencePolicy policy)
    {
        if (policy == TerminalControlSequencePolicy.Preserve)
        {
            return text;
        }

        var output = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
            {
                output.Append(character);
            }
        }

        return output.ToString();
    }

    private static string BuildHtml(string text)
    {
        var fragment = $"<pre>{WebUtility.HtmlEncode(text)}</pre>";
        var body = $"<html><body><!--StartFragment-->{fragment}<!--EndFragment--></body></html>";
        const string headerTemplate =
            "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var emptyHeader = string.Format(CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(emptyHeader);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount("<html><body><!--StartFragment-->");
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(body);
        return string.Format(
            CultureInfo.InvariantCulture,
            headerTemplate,
            startHtml,
            endHtml,
            startFragment,
            endFragment) + body;
    }

    private static string BuildRtf(string text)
    {
        var output = new StringBuilder(@"{\rtf1\ansi\deff0 ");
        foreach (var character in text)
        {
            switch (character)
            {
                case '\\':
                case '{':
                case '}':
                    output.Append('\\').Append(character);
                    break;
                case '\r':
                    break;
                case '\n':
                    output.Append(@"\par ");
                    break;
                default:
                    if (character <= 0x7f)
                    {
                        output.Append(character);
                    }
                    else
                    {
                        output.Append(@"\u").Append((short)character).Append('?');
                    }

                    break;
            }
        }

        return output.Append('}').ToString();
    }

    private static void AppendMoves(StringBuilder output, int delta, string positive, string negative)
    {
        var sequence = delta >= 0 ? positive : negative;
        for (var index = 0; index < Math.Abs(delta); index++)
        {
            output.Append(sequence);
        }
    }

    private static TerminalSelectionPoint SnapFromWideContinuation(
        TextBufferSnapshot snapshot,
        TerminalSelectionPoint point)
    {
        var cells = snapshot.Lines[point.Line].Cells;
        var column = point.Column;
        while (column > 0 && cells[column].IsWideContinuation)
        {
            column--;
        }

        return point with { Column = column };
    }

    private static bool IsSafeTarget(Uri uri)
    {
        if (!uri.IsFile)
        {
            return true;
        }

        var extension = Path.GetExtension(uri.LocalPath);
        if (extension.Length == 0)
        {
            return true;
        }

        var executableExtensions = new HashSet<string>(
            [".exe", ".com", ".bat", ".cmd", ".msi", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh", ".scr"],
            StringComparer.OrdinalIgnoreCase);
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (!string.IsNullOrWhiteSpace(pathExt))
        {
            executableExtensions.UnionWith(pathExt.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return !executableExtensions.Contains(extension);
    }
}
