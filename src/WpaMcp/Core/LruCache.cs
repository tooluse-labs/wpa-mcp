namespace WpaMcp.Core;

public sealed class LruCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly int _capacity;
    private readonly Action<TValue>? _onRemoved;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _index;
    private readonly List<TValue> _pendingRemovalCallbacks = [];
    private readonly object _lock = new();
    private bool _disposed;

    public LruCache(
        int capacity,
        Action<TValue>? onRemoved = null,
        IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _onRemoved = onRemoved;
        _index = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory) =>
        GetOrAdd(key, factory, out _);

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, out bool added)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_index.TryGetValue(key, out var existing))
            {
                Promote(existing);
                added = false;
                return existing.Value.Value;
            }
        }

        // Factories run outside the cache lock. Owned values that lose this race are
        // reported through _onRemoved so callers can retire them without leaking.
        var candidate = factory(key);
        var rejectedBecauseDisposed = false;
        var hasRemovedValue = false;
        var removedValue = default(TValue)!;
        TValue result;

        lock (_lock)
        {
            if (_disposed)
            {
                rejectedBecauseDisposed = true;
                hasRemovedValue = true;
                removedValue = candidate;
                result = default!;
                added = false;
            }
            else if (_index.TryGetValue(key, out var raced))
            {
                Promote(raced);
                hasRemovedValue = true;
                removedValue = candidate;
                result = raced.Value.Value;
                added = false;
            }
            else
            {
                var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, candidate));
                _order.AddFirst(node);
                _index[key] = node;
                result = candidate;
                added = true;

                if (_order.Count > _capacity)
                {
                    var lru = _order.Last!;
                    _order.RemoveLast();
                    _index.Remove(lru.Value.Key);
                    hasRemovedValue = true;
                    removedValue = lru.Value.Value;
                }
            }
        }

        if (hasRemovedValue)
            InvokeRemovalCallback(removedValue);
        if (rejectedBecauseDisposed)
            throw new ObjectDisposedException(GetType().Name);
        return result;
    }

    public bool TryGet(TKey key, out TValue value) =>
        TryGetAndPin(key, static _ => true, out value);

    /// <summary>
    /// Looks up and promotes a value only when <paramref name="tryPin"/> succeeds.
    /// The callback runs while the LRU lock is held, making a successful pin atomic
    /// with eviction, removal, and disposal. It must be short and non-blocking.
    /// </summary>
    public bool TryGetAndPin(TKey key, Func<TValue, bool> tryPin, out TValue value)
    {
        ArgumentNullException.ThrowIfNull(tryPin);
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_index.TryGetValue(key, out var node) && tryPin(node.Value.Value))
            {
                Promote(node);
                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    public bool Remove(TKey key) => Remove(key, static _ => true);

    public bool Remove(TKey key, Predicate<TValue> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var removedValue = default(TValue)!;
        var removed = false;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_index.TryGetValue(key, out var node) && match(node.Value.Value))
            {
                _order.Remove(node);
                _index.Remove(key);
                removedValue = node.Value.Value;
                removed = true;
            }
        }

        if (removed)
            InvokeRemovalCallback(removedValue);
        return removed;
    }

    public void Dispose()
    {
        List<TValue>? removed = null;
        lock (_lock)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_order.Count > 0)
                    removed = _order.Select(pair => pair.Value).ToList();
                _order.Clear();
                _index.Clear();
            }

            // A failed callback is retained so a later Dispose call can retry it.
            // This matters for TraceLog disposal: clearing the cache must not turn a
            // transient native/mmap close failure into a permanently unreachable entry.
            if (_pendingRemovalCallbacks.Count > 0)
            {
                removed ??= [];
                removed.AddRange(_pendingRemovalCallbacks);
                _pendingRemovalCallbacks.Clear();
            }
        }

        if (removed is null || _onRemoved is null)
            return;
        List<Exception>? failures = null;
        List<TValue>? failedValues = null;
        foreach (var value in removed)
        {
            try
            {
                _onRemoved(value);
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(ex);
                failedValues ??= [];
                failedValues.Add(value);
            }
        }

        if (failures is not null)
        {
            lock (_lock)
                _pendingRemovalCallbacks.AddRange(failedValues!);
            throw new AggregateException("One or more LRU removal callbacks failed.", failures);
        }
    }

    private void InvokeRemovalCallback(TValue value)
    {
        if (_onRemoved is null)
            return;

        try
        {
            _onRemoved(value);
        }
        catch
        {
            lock (_lock)
                _pendingRemovalCallbacks.Add(value);
            throw;
        }
    }

    private void Promote(LinkedListNode<KeyValuePair<TKey, TValue>> node)
    {
        _order.Remove(node);
        _order.AddFirst(node);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }
}
