namespace Devolutions.Terminal.Core;

internal sealed class CircularBuffer<T>
{
    private T[] _items;
    private int _start;

    public CircularBuffer(int capacity)
    {
        _items = new T[Math.Max(1, capacity)];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            ValidateIndex(index);
            return _items[PhysicalIndex(index)];
        }
        set
        {
            ValidateIndex(index);
            _items[PhysicalIndex(index)] = value;
        }
    }

    public bool AddLast(T item)
    {
        if (Count < Capacity)
        {
            _items[PhysicalIndex(Count++)] = item;
            return false;
        }

        _items[_start] = item;
        _start = (_start + 1) % Capacity;
        return true;
    }

    public T RemoveFirst()
    {
        if (Count == 0)
        {
            throw new InvalidOperationException("The circular buffer is empty.");
        }

        var item = _items[_start];
        _items[_start] = default!;
        _start = (_start + 1) % Capacity;
        Count--;
        return item;
    }

    public T RemoveLast()
    {
        if (Count == 0)
        {
            throw new InvalidOperationException("The circular buffer is empty.");
        }

        var index = PhysicalIndex(Count - 1);
        var item = _items[index];
        _items[index] = default!;
        Count--;
        return item;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    public void ResetCapacity(int capacity, IReadOnlyList<T> items)
    {
        capacity = Math.Max(1, capacity);
        _items = new T[capacity];
        _start = 0;
        Count = 0;
        var first = Math.Max(0, items.Count - capacity);
        for (var i = first; i < items.Count; i++)
        {
            AddLast(items[i]);
        }
    }

    public List<T> ToList()
    {
        var result = new List<T>(Count);
        for (var i = 0; i < Count; i++)
        {
            result.Add(this[i]);
        }

        return result;
    }

    private int PhysicalIndex(int logicalIndex) => (_start + logicalIndex) % Capacity;

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
