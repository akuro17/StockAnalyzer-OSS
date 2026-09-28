using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Manages multi-timeframe candle data with an LRU-like cache.
/// Supports lazy loading, local aggregation (daily → weekly/monthly),
/// and CancellationToken-based task cancellation for rapid timeframe switching.
/// </summary>
public class TimeFrameManager : ITimeFrameManager
{
    private readonly IDataService _dataService;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxCacheEntries;

    /// <summary>
    /// Initializes a new instance of TimeFrameManager.
    /// </summary>
    /// <param name="dataService">Underlying data service for loading candle data.</param>
    /// <param name="maxCacheEntries">Maximum number of cache entries before eviction.</param>
    public TimeFrameManager(IDataService dataService, int maxCacheEntries = 20)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _maxCacheEntries = maxCacheEntries;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandleData>> GetCandlesAsync(
        string symbol,
        TimeFrame timeFrame,
        int count = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return Array.Empty<CandleData>();
        }

        string cacheKey = BuildCacheKey(symbol, timeFrame);

        // 1. Check cache for exact match
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            cached.LastAccessed = DateTime.UtcNow;
            // Ensure we only return up to the requested count from the cache
            return (count > 0 && cached.Data.Count > count)
                ? cached.Data.Skip(cached.Data.Count - count).ToList()
                : cached.Data;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. For weekly/monthly: always aggregate from daily data
        if (timeFrame == TimeFrame.W1 || timeFrame == TimeFrame.MN1)
        {
            string dailyKey = BuildCacheKey(symbol, TimeFrame.D1);
            IReadOnlyList<CandleData> dailyData;

            if (_cache.TryGetValue(dailyKey, out var dailyCached))
            {
                dailyCached.LastAccessed = DateTime.UtcNow;
                dailyData = dailyCached.Data;
            }
            else
            {
                // Load Daily data if not in cache (e.g. initial load was Monthly)
                // Request more candles for daily to ensure enough data for Monthly/Weekly aggregation
                int dailyCount = timeFrame == TimeFrame.MN1 ? count * 30 : count * 7;
                dailyData = await _dataService.LoadCandlesAsync(symbol, TimeFrame.D1, dailyCount);
                StoreInCache(dailyKey, dailyData);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var aggregated = TimeFrameAggregator.Aggregate(dailyData, timeFrame);
            StoreInCache(cacheKey, aggregated);

            // Ensure we only return up to the requested count
            return (count > 0 && aggregated.Count > count)
                ? aggregated.Skip(aggregated.Count - count).ToList()
                : aggregated;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Load from data service directly (Daily or lower timeframes)
        var data = await _dataService.LoadCandlesAsync(symbol, timeFrame, count);

        cancellationToken.ThrowIfCancellationRequested();

        StoreInCache(cacheKey, data);
        
        // Ensure we only return up to the requested count (defensive check)
        return (count > 0 && data.Count > count)
            ? data.Skip(data.Count - count).ToList()
            : data;
    }

    /// <inheritdoc />
    public void InvalidateCache(string? symbol = null)
    {
        if (symbol == null)
        {
            _cache.Clear();
            return;
        }

        // Remove all entries for the specified symbol
        var keysToRemove = _cache.Keys
            .Where(k => k.StartsWith(symbol + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static string BuildCacheKey(string symbol, TimeFrame timeFrame)
    {
        return $"{symbol}:{timeFrame}";
    }

    private void StoreInCache(string key, IReadOnlyList<CandleData> data)
    {
        var entry = new CacheEntry(data);
        _cache[key] = entry;

        // Evict oldest entries if cache exceeds max size
        while (_cache.Count > _maxCacheEntries)
        {
            var oldest = _cache
                .OrderBy(kvp => kvp.Value.LastAccessed)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _cache.TryRemove(oldest.Key, out _);
            }
        }
    }

    private sealed class CacheEntry
    {
        public IReadOnlyList<CandleData> Data { get; }
        public DateTime LastAccessed { get; set; }

        public CacheEntry(IReadOnlyList<CandleData> data)
        {
            Data = data;
            LastAccessed = DateTime.UtcNow;
        }
    }
}
