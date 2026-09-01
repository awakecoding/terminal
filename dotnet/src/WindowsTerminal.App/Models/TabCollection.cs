namespace WindowsTerminal.Models;

public sealed class TabCollection<TTab, TSnapshot>
    where TTab : class
    where TSnapshot : class
{
    private readonly List<TTab> _items = [];
    private readonly List<TTab> _mru = [];
    private readonly Stack<TSnapshot> _closed = [];

    public IReadOnlyList<TTab> Items => _items;
    public TTab? ActiveTab { get; private set; }
    public int Count => _items.Count;
    public int ClosedCount => _closed.Count;

    public void Add(TTab tab, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_items.Contains(tab))
        {
            throw new InvalidOperationException("The tab already belongs to this window.");
        }

        _items.Insert(index is { } value ? Math.Clamp(value, 0, _items.Count) : _items.Count, tab);
        Activate(tab);
    }

    public bool Activate(TTab tab)
    {
        if (!_items.Contains(tab))
        {
            return false;
        }

        ActiveTab = tab;
        _mru.Remove(tab);
        _mru.Insert(0, tab);
        return true;
    }

    public bool Move(TTab tab, int targetIndex)
    {
        var current = _items.IndexOf(tab);
        if (current < 0)
        {
            return false;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _items.Count - 1);
        if (current == targetIndex)
        {
            return false;
        }

        _items.RemoveAt(current);
        _items.Insert(targetIndex, tab);
        return true;
    }

    public bool MoveRelative(TTab tab, int delta)
    {
        var current = _items.IndexOf(tab);
        return current >= 0 && Move(tab, current + delta);
    }

    public bool SelectRelative(int delta, bool mostRecentlyUsed = false)
    {
        var candidates = mostRecentlyUsed ? _mru : _items;
        if (ActiveTab is null || candidates.Count <= 1)
        {
            return false;
        }

        var current = candidates.IndexOf(ActiveTab);
        var next = (current + delta) % candidates.Count;
        if (next < 0)
        {
            next += candidates.Count;
        }

        return Activate(candidates[next]);
    }

    public bool Close(TTab tab, Func<TTab, TSnapshot> snapshot, bool remember = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var index = _items.IndexOf(tab);
        if (index < 0)
        {
            return false;
        }

        if (remember)
        {
            _closed.Push(snapshot(tab));
        }

        _items.RemoveAt(index);
        _mru.Remove(tab);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = null;
            var replacement = _mru.FirstOrDefault(_items.Contains) ??
                              _items.ElementAtOrDefault(Math.Min(index, _items.Count - 1));
            if (replacement is not null)
            {
                Activate(replacement);
            }
        }

        return true;
    }

    public bool Remove(TTab tab) => Close(tab, static _ =>
        throw new InvalidOperationException("A snapshot is not required when removing a tab."), remember: false);

    public IReadOnlyList<TTab> CloseOthers(TTab keep, Func<TTab, TSnapshot> snapshot)
    {
        if (!_items.Contains(keep))
        {
            return [];
        }

        var removed = _items.Where(tab => !ReferenceEquals(tab, keep)).ToArray();
        foreach (var tab in removed)
        {
            Close(tab, snapshot);
        }

        Activate(keep);
        return removed;
    }

    public IReadOnlyList<TTab> CloseAfter(TTab keep, Func<TTab, TSnapshot> snapshot)
    {
        var index = _items.IndexOf(keep);
        if (index < 0)
        {
            return [];
        }

        var removed = _items.Skip(index + 1).ToArray();
        foreach (var tab in removed)
        {
            Close(tab, snapshot);
        }

        return removed;
    }

    public TTab Duplicate(TTab source, Func<TTab, TTab> duplicate)
    {
        ArgumentNullException.ThrowIfNull(duplicate);
        var index = _items.IndexOf(source);
        if (index < 0)
        {
            throw new InvalidOperationException("The source tab does not belong to this window.");
        }

        var result = duplicate(source);
        Add(result, index + 1);
        return result;
    }

    public bool TryRestore(Func<TSnapshot, TTab> restore, out TTab? tab)
    {
        ArgumentNullException.ThrowIfNull(restore);
        if (!_closed.TryPop(out var snapshot))
        {
            tab = null;
            return false;
        }

        tab = restore(snapshot);
        Add(tab);
        return true;
    }

    public bool TryTakeLastClosed(out TSnapshot? snapshot) => _closed.TryPop(out snapshot);

    public IReadOnlyList<TTab> Search(string? query, Func<TTab, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.IsNullOrWhiteSpace(query)
            ? _items.ToArray()
            : _items.Where(tab => text(tab).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }
}
