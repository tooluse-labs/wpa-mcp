namespace WpaMcp.Core;

public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _index = new();
    private readonly object _lock = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return existing.Value.Value;
            }
        }

        var value = factory(key); // build outside lock; cost is high (TraceLog ctor)

        lock (_lock)
        {
            if (_index.TryGetValue(key, out var raced))
            {
                _order.Remove(raced);
                _order.AddFirst(raced);
                return raced.Value.Value;
            }

            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, value));
            _order.AddFirst(node);
            _index[key] = node;

            if (_order.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _index.Remove(lru.Value.Key);
            }
            return value;
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _index.Remove(key);
                return true;
            }
            return false;
        }
    }
}
