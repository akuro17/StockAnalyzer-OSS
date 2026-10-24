using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Manages multi-timeframe candle data with an LRU-like cache.
/// Supports lazy loading, local aggregation (daily → weekly/monthly),
/// and CancellationToken-based task cancellation for rapid timeframe switching.
/// </summary>
public class TimeFrameManager : ITimeFrameManager
{
    // Upper bounds of the daily bars of one period (trading days are fewer), used to size the daily load for a weekly or monthly list.
    private const int DailyBarsPerWeekBound = 7;
    private const int DailyBarsPerMonthBound = 30;

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

        // 1. Check cache for exact match. An entry answers a request only when it holds enough: a list that was loaded for a smaller count (or
        // that is a truncated daily list kept for an aggregation) must not be returned as if it were the whole history.
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Covers(count))
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

            // Daily bars the period count needs (generously: a week or month holds fewer than 7 or 30 daily bars); the whole history for count <= 0.
            int dailyCount = count <= 0 ? 0 : (int)Math.Min((long)count * (timeFrame == TimeFrame.MN1 ? DailyBarsPerMonthBound : DailyBarsPerWeekBound), int.MaxValue);
            IReadOnlyList<CandleData> dailyData;
            bool dailyComplete;

            if (_cache.TryGetValue(dailyKey, out var dailyCached) && dailyCached.Covers(dailyCount))
            {
                dailyCached.LastAccessed = DateTime.UtcNow;
                dailyData = dailyCached.Data;
                dailyComplete = dailyCached.Complete;
            }
            else
            {
                // No daily list in the cache, or one too short for this count (for example loaded for a daily chart of 200 bars): load what is needed.
                dailyData = await _dataService.LoadCandlesAsync(symbol, TimeFrame.D1, dailyCount);
                dailyComplete = dailyCount <= 0 || dailyData.Count < dailyCount;
                StoreInCache(dailyKey, dailyData, dailyCount, dailyComplete);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var aggregated = TimeFrameAggregator.Aggregate(dailyData, timeFrame);
            // A period list built from a truncated daily list is only as long as that list allows.
            StoreInCache(cacheKey, aggregated, dailyComplete ? count : Math.Min(count, aggregated.Count), dailyComplete);

            // Ensure we only return up to the requested count
            return (count > 0 && aggregated.Count > count)
                ? aggregated.Skip(aggregated.Count - count).ToList()
                : aggregated;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Load from data service directly (Daily or lower timeframes)
        var data = await _dataService.LoadCandlesAsync(symbol, timeFrame, count);

        cancellationToken.ThrowIfCancellationRequested();

        StoreInCache(cacheKey, data, count, count <= 0 || data.Count < count);

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

    private void StoreInCache(string key, IReadOnlyList<CandleData> data, int requestedCount, bool complete)
    {
        var entry = new CacheEntry(data, requestedCount, complete);
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

        /// <summary>The count the list was loaded for (0 = the whole history was asked for).</summary>
        public int RequestedCount { get; }

        /// <summary>True when the list holds the whole stored history: it was asked for, or the source returned fewer bars than asked.</summary>
        public bool Complete { get; }

        public CacheEntry(IReadOnlyList<CandleData> data, int requestedCount, bool complete)
        {
            Data = data;
            RequestedCount = requestedCount;
            Complete = complete;
            LastAccessed = DateTime.UtcNow;
        }

        /// <summary>Whether this list can answer a request for <paramref name="count"/> bars (count <= 0 asks for everything).</summary>
        public bool Covers(int count) => Complete || (count > 0 && RequestedCount >= count);
    }
}
