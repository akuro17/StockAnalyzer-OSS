using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// A thread-safe least-recently-used cache with a fixed capacity: reading an entry marks it most recent, and adding a new entry
/// beyond the capacity evicts the least recently used one. A single lock guards the index and the recency list (every operation
/// is O(1) and never runs caller code under the lock).
/// </summary>
internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _index;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _recency = new(); // first = most recently used

    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _index = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
    }

    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    /// <summary>Returns the cached value and marks it most recently used.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var node))
            {
                value = default!;
                return false;
            }

            Touch(node);
            value = node.Value.Value;
            return true;
        }
    }

    /// <summary>
    /// Adds <paramref name="value"/> unless the key is already cached (the first value wins, so every caller keeps seeing one
    /// instance) and evicts the least recently used entry when the capacity is exceeded.
    /// </summary>
    public void TryAdd(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                Touch(existing);
                return;
            }

            _index[key] = _recency.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
            if (_index.Count <= _capacity) return;
            var oldest = _recency.Last!;
            _recency.RemoveLast();
            _index.Remove(oldest.Value.Key);
        }
    }

    /// <summary>Removes all entries from the cache.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _recency.Clear();
        }
    }

    private void Touch(LinkedListNode<KeyValuePair<TKey, TValue>> node)
    {
        if (ReferenceEquals(_recency.First, node)) return;
        _recency.Remove(node);
        _recency.AddFirst(node);
    }
}
