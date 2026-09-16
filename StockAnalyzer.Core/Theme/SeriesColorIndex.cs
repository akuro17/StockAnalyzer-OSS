using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Manages the mapping between series symbols and their assigned color indices.
/// Ensures that once a color index is assigned to a symbol, it stays fixed 
/// even if other symbols are added or removed (gap preservation).
/// </summary>
public sealed class SeriesColorIndex
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _map = new(capacity: 16, comparer: StringComparer.Ordinal);
    private int _nextIndex = 0;

    /// <summary>
    /// Gets the assigned index for a symbol, or assigns a new one if it doesn't exist.
    /// </summary>
    /// <param name="symbol">The symbol string.</param>
    /// <returns>The assigned color index.</returns>
    public int GetOrAdd(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return 0;

        lock (_lock)
        {
            if (_map.TryGetValue(symbol, out int index))
            {
                return index;
            }

            int newIndex = _nextIndex++;
            _map[symbol] = newIndex;
            return newIndex;
        }
    }

    /// <summary>
    /// Removes a symbol from the mapping.
    /// Note: This does NOT decrement the next available index to preserve gaps.
    /// </summary>
    /// <param name="symbol">The symbol to remove.</param>
    public void Remove(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        lock (_lock)
        {
            _map.Remove(symbol);
        }
    }

    /// <summary>
    /// Resets all mappings and the index counter.
    /// Should be called when the entire comparison context is cleared.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _nextIndex = 0;
        }
    }
}
