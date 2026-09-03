namespace Devolutions.Terminal.Render;

internal sealed class BoundedResourceCache<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : IDisposable
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _lru = [];
    private long _hits;
    private long _misses;
    private long _evictions;

    public BoundedResourceCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        _capacity = Math.Max(1, capacity);
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(_capacity, comparer);
    }

    public GlyphCacheStatistics Statistics =>
        new(_entries.Count, _capacity, _hits, _misses, _evictions);

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            _hits++;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Value;
        }

        _misses++;
        var value = factory(key);
        var added = _lru.AddFirst(new Entry(key, value));
        _entries.Add(key, added);
        if (_entries.Count > _capacity)
        {
            var victim = _lru.Last!;
            _lru.RemoveLast();
            _entries.Remove(victim.Value.Key);
            victim.Value.Value.Dispose();
            _evictions++;
        }

        return value;
    }

    public void Clear()
    {
        foreach (var entry in _lru)
        {
            entry.Value.Dispose();
        }

        _entries.Clear();
        _lru.Clear();
    }

    public void Dispose() => Clear();

    private sealed record Entry(TKey Key, TValue Value);
}
