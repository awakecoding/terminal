using System.Buffers;
using System.Globalization;
using System.Text;

namespace Devolutions.Terminal.Core;

public readonly record struct BufferPosition(int Line, int Column);

public readonly record struct BufferRange(BufferPosition Start, BufferPosition End);

public sealed record TextSearchOptions(
    bool CaseSensitive = false,
    bool WholeWord = false,
    bool Wrap = true);

public static class TextBufferSearch
{
    public static IReadOnlyList<BufferRange> FindAll(
        TextBufferSnapshot snapshot,
        string query,
        TextSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        options ??= new TextSearchOptions();

        var matches = new List<BufferRange>();
        foreach (var searchable in SearchableLine.CreateLogicalLines(snapshot.Lines))
        {
            var searchStart = 0;
            while (searchStart <= searchable.Text.Length - query.Length)
            {
                var index = searchable.Text.IndexOf(
                    query,
                    searchStart,
                    options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                var endIndex = index + query.Length;
                if (!options.WholeWord || IsWholeWord(searchable.Text, index, endIndex))
                {
                    matches.Add(new BufferRange(
                        searchable.PositionAt(index),
                        searchable.PositionAfter(endIndex - 1)));
                }

                searchStart = index + Math.Max(1, query.Length);
            }
        }

        return matches;
    }

    public static BufferRange? FindNext(
        TextBufferSnapshot snapshot,
        string query,
        BufferPosition start,
        bool reverse = false,
        TextSearchOptions? options = null)
    {
        options ??= new TextSearchOptions();
        var matches = FindAll(snapshot, query, options);
        if (matches.Count == 0)
        {
            return null;
        }

        if (reverse)
        {
            for (var index = matches.Count - 1; index >= 0; index--)
            {
                if (Compare(matches[index].Start, start) < 0)
                {
                    return matches[index];
                }
            }

            return options.Wrap ? matches[^1] : null;
        }

        foreach (var match in matches)
        {
            if (Compare(match.Start, start) > 0)
            {
                return match;
            }
        }

        return options.Wrap ? matches[0] : null;
    }

    private static bool IsWholeWord(string text, int start, int end)
    {
        var startsAtBoundary = start == 0 || !IsWordRune(RuneBefore(text, start));
        var endsAtBoundary = end == text.Length || !IsWordRune(RuneAt(text, end));
        return startsAtBoundary && endsAtBoundary;
    }

    private static Rune RuneBefore(string text, int index)
    {
        var status = Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var rune, out _);
        return status == OperationStatus.Done ? rune : Rune.ReplacementChar;
    }

    private static Rune RuneAt(string text, int index)
    {
        var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out _);
        return status == OperationStatus.Done ? rune : Rune.ReplacementChar;
    }

    private static bool IsWordRune(Rune value)
    {
        var category = Rune.GetUnicodeCategory(value);
        return category is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    private static int Compare(BufferPosition left, BufferPosition right)
    {
        var line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }

    private sealed class SearchableLine
    {
        private readonly BufferPosition[] _positions;
        private readonly BufferPosition[] _positionsAfter;

        private SearchableLine(
            string text,
            BufferPosition[] positions,
            BufferPosition[] positionsAfter)
        {
            Text = text;
            _positions = positions;
            _positionsAfter = positionsAfter;
        }

        public string Text { get; }

        public BufferPosition PositionAt(int textIndex) => _positions[textIndex];

        public BufferPosition PositionAfter(int textIndex) => _positionsAfter[textIndex];

        public static IReadOnlyList<SearchableLine> CreateLogicalLines(
            IReadOnlyList<TextBufferLineSnapshot> lines)
        {
            var result = new List<SearchableLine>();
            var builder = new LogicalLineBuilder();
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                builder.Append(lineIndex, lines[lineIndex].Cells);
                if (!lines[lineIndex].Wrapped)
                {
                    result.Add(builder.Build());
                    builder = new LogicalLineBuilder();
                }
            }

            if (builder.Length > 0)
            {
                result.Add(builder.Build());
            }

            return result;
        }

        private sealed class LogicalLineBuilder
        {
            private readonly StringBuilder _text = new();
            private readonly List<BufferPosition> _positions = [];
            private readonly List<BufferPosition> _positionsAfter = [];

            public int Length => _text.Length;

            public void Append(int lineIndex, IReadOnlyList<Cell> cells)
            {
                for (var column = 0; column < cells.Count; column++)
                {
                    var cell = cells[column];
                    if (cell.IsWideContinuation)
                    {
                        continue;
                    }

                    var cellText = cell.Text;
                    var width = column + 1 < cells.Count && cells[column + 1].IsWideContinuation ? 2 : 1;
                    _text.Append(cellText);
                    for (var index = 0; index < cellText.Length; index++)
                    {
                        _positions.Add(new BufferPosition(lineIndex, column));
                        _positionsAfter.Add(new BufferPosition(lineIndex, column + width));
                    }
                }
            }

            public SearchableLine Build() => new(
                _text.ToString(),
                [.. _positions],
                [.. _positionsAfter]);
        }
    }
}
