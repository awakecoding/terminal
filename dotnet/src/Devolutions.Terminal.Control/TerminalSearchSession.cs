using Devolutions.Terminal.Core;

namespace Devolutions.Terminal;

public sealed class TerminalSearchSession : IDisposable
{
    private readonly ITerminalEngine _engine;
    private IReadOnlyList<BufferRange> _matches = [];
    private int _currentIndex = -1;
    private TextBufferSnapshot? _snapshot;
    private bool _stale;

    public TerminalSearchSession(ITerminalEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.Invalidated += OnEngineInvalidated;
    }

    public string Query { get; private set; } = string.Empty;

    public bool CaseSensitive { get; private set; }

    public bool WholeWord { get; private set; }

    public IReadOnlyList<BufferRange> Matches => _matches;

    public int CurrentIndex => _currentIndex;

    public BufferRange? Current =>
        _currentIndex >= 0 && _currentIndex < _matches.Count
            ? _matches[_currentIndex]
            : null;

    public event EventHandler? Changed;

    public void Update(string query, bool caseSensitive = false, bool wholeWord = false)
    {
        Query = query ?? string.Empty;
        CaseSensitive = caseSensitive;
        WholeWord = wholeWord;
        if (string.IsNullOrWhiteSpace(Query))
        {
            Clear();
            return;
        }

        var snapshot = _engine.CreateSnapshot(includeHistory: true).Buffer;
        Recompute(snapshot, preserveSelection: false);
        RevealCurrent(snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool MoveNext(bool reverse = false)
    {
        var hadCurrent = Current is not null;
        if (_matches.Count == 0)
        {
            if (_stale)
            {
                Recompute(_engine.CreateSnapshot(includeHistory: true).Buffer, preserveSelection: true);
            }

            if (_matches.Count == 0)
            {
                return false;
            }
        }
        else if (_stale)
        {
            Recompute(_engine.CreateSnapshot(includeHistory: true).Buffer, preserveSelection: true);
            if (_matches.Count == 0)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }
        }

        if (!hadCurrent && Current is not null)
        {
            RevealCurrent(_snapshot!);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (_matches.Count == 0)
        {
            return false;
        }

        _currentIndex = reverse
            ? (_currentIndex - 1 + _matches.Count) % _matches.Count
            : (_currentIndex + 1) % _matches.Count;
        RevealCurrent(_engine.CreateSnapshot(includeHistory: true).Buffer);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Refresh()
    {
        if (Query.Length == 0)
        {
            return;
        }

        var snapshot = _engine.CreateSnapshot(includeHistory: true).Buffer;
        Recompute(snapshot, preserveSelection: true);
        RevealCurrent(snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Query = string.Empty;
        _matches = [];
        _currentIndex = -1;
        _snapshot = null;
        _stale = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _engine.Invalidated -= OnEngineInvalidated;
        GC.SuppressFinalize(this);
    }

    private void Recompute(TextBufferSnapshot snapshot, bool preserveSelection)
    {
        var previousIndex = _currentIndex;
        var anchor = preserveSelection ? CaptureAnchor() : null;
        _matches = TextBufferSearch.FindAll(
            snapshot,
            Query,
            new TextSearchOptions(CaseSensitive, WholeWord));
        _snapshot = snapshot;
        _stale = false;
        _currentIndex = anchor is null
            ? (_matches.Count > 0 ? 0 : -1)
            : FindAnchor(anchor, previousIndex);
    }

    private SearchAnchor? CaptureAnchor()
    {
        if (Current is not { } current || _snapshot is null)
        {
            return null;
        }

        return new SearchAnchor(
            LineText(_snapshot, current.Start.Line),
            current.Start.Column,
            current.End.Column,
            current.Start.Line > 0 ? LineText(_snapshot, current.Start.Line - 1) : null,
            current.Start.Line + 1 < _snapshot.Lines.Count
                ? LineText(_snapshot, current.Start.Line + 1)
                : null);
    }

    private int FindAnchor(SearchAnchor anchor, int fallbackIndex)
    {
        if (_snapshot is null || _matches.Count == 0)
        {
            return -1;
        }

        var bestIndex = -1;
        var bestScore = int.MinValue;
        for (var index = 0; index < _matches.Count; index++)
        {
            var match = _matches[index];
            var score = 0;
            if (LineText(_snapshot, match.Start.Line) == anchor.LineText)
            {
                score += 8;
            }

            if (match.Start.Column == anchor.StartColumn && match.End.Column == anchor.EndColumn)
            {
                score += 4;
            }

            if (anchor.PreviousLine is not null &&
                match.Start.Line > 0 &&
                LineText(_snapshot, match.Start.Line - 1) == anchor.PreviousLine)
            {
                score += 2;
            }

            if (anchor.NextLine is not null &&
                match.Start.Line + 1 < _snapshot.Lines.Count &&
                LineText(_snapshot, match.Start.Line + 1) == anchor.NextLine)
            {
                score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestScore > 0 ? bestIndex : Math.Clamp(fallbackIndex, 0, _matches.Count - 1);
    }

    private static string LineText(TextBufferSnapshot snapshot, int line)
    {
        var text = new System.Text.StringBuilder();
        foreach (var cell in snapshot.Lines[line].Cells)
        {
            if (!cell.IsWideContinuation)
            {
                text.Append(cell.Text);
            }
        }

        return text.ToString().TrimEnd();
    }

    private void OnEngineInvalidated(object? sender, EventArgs e) => _stale = true;

    private void RevealCurrent(TextBufferSnapshot snapshot)
    {
        if (Current is not { } current)
        {
            return;
        }

        var liveViewportStart = snapshot.HistoryCount;
        var desiredTop = Math.Clamp(
            current.Start.Line - (snapshot.Rows / 2),
            0,
            liveViewportStart);
        _engine.SetScrollOffset(liveViewportStart - desiredTop);
    }

    private sealed record SearchAnchor(
        string LineText,
        int StartColumn,
        int EndColumn,
        string? PreviousLine,
        string? NextLine);
}
