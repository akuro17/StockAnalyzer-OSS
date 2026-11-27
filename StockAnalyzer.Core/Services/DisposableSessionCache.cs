using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Bounded thread-safe LRU cache for <see cref="IDisposable"/> native resources (such as ONNX <c>InferenceSession</c>)
/// using in-flight reference counting so an evicted or shutdown entry is disposed only once its final active user exits.
/// </summary>
internal sealed class DisposableSessionCache<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : class, IDisposable
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _map;
    private readonly LinkedList<Entry> _lru = new();
    private bool _disposed;

    public DisposableSessionCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _map = new Dictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    public Lease AcquireOrCreate(TKey key, Func<TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_map.TryGetValue(key, out var hit))
            {
                Promote(hit);
                hit.Users++;
                return new Lease(this, hit);
            }
        }

        TValue created = factory();
        Entry? retiredToDispose = null;
        bool disposeCreated = false;
        Lease resultLease = default;
        bool hasResult = false;
        bool wasDisposed = false;

        lock (_gate)
        {
            if (_disposed)
            {
                disposeCreated = true;
                wasDisposed = true;
            }
            else if (_map.TryGetValue(key, out var winner))
            {
                disposeCreated = true;
                Promote(winner);
                winner.Users++;
                resultLease = new Lease(this, winner);
                hasResult = true;
            }
            else
            {
                var entry = new Entry(key, created) { Users = 1 };
                entry.Node = _lru.AddFirst(entry);
                _map.Add(key, entry);

                if (_map.Count > _capacity && _lru.Last is { Value: var victim })
                {
                    _lru.RemoveLast();
                    _map.Remove(victim.Key);
                    victim.Retired = true;
                    if (victim.Users == 0)
                    {
                        retiredToDispose = victim;
                    }
                }

                resultLease = new Lease(this, entry);
                hasResult = true;
            }
        }

        if (disposeCreated)
        {
            SafeDispose(created);
        }

        if (retiredToDispose is not null)
        {
            SafeDispose(retiredToDispose.Value);
        }

        if (wasDisposed)
        {
            throw new ObjectDisposedException(nameof(DisposableSessionCache<TKey, TValue>));
        }

        if (hasResult)
        {
            return resultLease;
        }

        throw new InvalidOperationException("Session cache insertion exited unexpectedly.");
    }

    private void Release(Entry entry)
    {
        bool shouldDispose;
        lock (_gate)
        {
            entry.Users--;
            shouldDispose = entry.Retired && entry.Users == 0;
        }

        if (shouldDispose)
        {
            SafeDispose(entry.Value);
        }
    }

    private void Promote(Entry entry)
    {
        if (entry.Node is not null && !ReferenceEquals(_lru.First, entry.Node))
        {
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
        }
    }

    public void Dispose()
    {
        List<TValue>? toDispose = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _map.Values)
            {
                entry.Retired = true;
                if (entry.Users == 0)
                {
                    toDispose ??= new List<TValue>();
                    toDispose.Add(entry.Value);
                }
            }

            _map.Clear();
            _lru.Clear();
        }

        if (toDispose is not null)
        {
            foreach (var value in toDispose)
            {
                SafeDispose(value);
            }
        }
    }

    private static void SafeDispose(TValue value)
    {
        try
        {
            value.Dispose();
        }
        catch (Exception)
        {
            // Best-effort cleanup of retired native handles.
        }
    }

    internal sealed class Entry
    {
        public Entry(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public TKey Key { get; }
        public TValue Value { get; }
        public LinkedListNode<Entry>? Node { get; set; }
        public int Users { get; set; }
        public bool Retired { get; set; }
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly DisposableSessionCache<TKey, TValue>? _owner;
        private readonly Entry? _entry;

        public Lease(DisposableSessionCache<TKey, TValue> owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public TValue Value => _entry!.Value;

        public void Dispose()
        {
            if (_owner is not null && _entry is not null)
            {
                _owner.Release(_entry);
            }
        }
    }
}
