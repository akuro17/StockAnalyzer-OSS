using Xunit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// Controllable mock IDataService for testing TimeFrameManager.
/// </summary>
internal class SpyDataService : IDataService
{
    public int CallCount { get; private set; }
    public TimeFrame? LastRequestedTimeFrame { get; private set; }

    private readonly Func<string, TimeFrame, int, Task<IReadOnlyList<CandleData>>>? _handler;

    public SpyDataService(Func<string, TimeFrame, int, Task<IReadOnlyList<CandleData>>>? handler = null)
    {
        _handler = handler;
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(
        string symbol, TimeFrame timeFrame, int count = 100)
    {
        CallCount++;
        LastRequestedTimeFrame = timeFrame;

        if (_handler != null)
        {
            return await _handler(symbol, timeFrame, count);
        }

        return GenerateCandles(count, timeFrame);
    }

    public static IReadOnlyList<CandleData> GenerateCandles(int count, TimeFrame tf)
    {
        var candles = new List<CandleData>();
        var baseDate = new DateTime(2026, 1, 5); // Monday
        for (int i = 0; i < count; i++)
        {
            var date = tf switch
            {
                TimeFrame.W1 => baseDate.AddDays(i * 7),
                TimeFrame.MN1 => baseDate.AddMonths(i),
                _ => baseDate.AddDays(i)
            };
            candles.Add(new CandleData(date, 100m + i, 110m + i, 90m + i, 105m + i, 1000L + i));
        }
        return candles;
    }
}

public class TimeFrameManagerTests
{
    [Fact]
    public async Task GetCandlesAsync_FirstCall_LoadsFromDataService()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        var result = await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);

        Assert.NotNull(result);
        Assert.Equal(100, result.Count);
        Assert.Equal(1, spy.CallCount);
    }

    [Fact]
    public async Task GetCandlesAsync_SecondCall_ReturnsCachedData()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        var result1 = await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);
        var result2 = await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);

        // DataService should only be called once
        Assert.Equal(1, spy.CallCount);
        Assert.Same(result1, result2);
    }

    [Fact]
    public async Task GetCandlesAsync_DifferentSymbols_LoadsSeparately()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);
        await manager.GetCandlesAsync("MSFT", TimeFrame.D1, 100);

        Assert.Equal(2, spy.CallCount);
    }

    [Fact]
    public async Task GetCandlesAsync_WeeklyWithCachedDaily_AggregatesLocally()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        // Load daily first
        await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 10);

        // Request weekly - should NOT call DataService again
        var weekly = await manager.GetCandlesAsync("AAPL", TimeFrame.W1, 100);

        // Only 1 call (the daily load), weekly was aggregated locally
        Assert.Equal(1, spy.CallCount);
        Assert.NotNull(weekly);
        Assert.True(weekly.Count > 0);
        Assert.True(weekly.Count < 10); // Aggregation reduces count
    }

    [Fact]
    public async Task GetCandlesAsync_MonthlyWithCachedDaily_AggregatesLocally()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        // Load 60 daily candles
        await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 60);

        // Request monthly
        var monthly = await manager.GetCandlesAsync("AAPL", TimeFrame.MN1, 100);

        Assert.Equal(1, spy.CallCount);
        Assert.NotNull(monthly);
        Assert.True(monthly.Count > 0);
    }

    [Fact]
    public async Task GetCandlesAsync_WeeklyWithoutCachedDaily_LoadsDailyAndAggregates()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        // Request weekly directly (no daily cache)
        var weekly = await manager.GetCandlesAsync("AAPL", TimeFrame.W1, 50);

        Assert.Equal(1, spy.CallCount);
        // It should request Daily data under the hood to ensure consistency and caching
        Assert.Equal(TimeFrame.D1, spy.LastRequestedTimeFrame);
        Assert.NotNull(weekly);
    }

    [Fact]
    public async Task GetCandlesAsync_CancellationToken_ThrowsWhenCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100, cts.Token));

        // DataService should NOT have been called
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task InvalidateCache_ClearsSpecificSymbol()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);
        Assert.Equal(1, spy.CallCount);

        // Invalidate AAPL
        manager.InvalidateCache("AAPL");

        // Should load again
        await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);
        Assert.Equal(2, spy.CallCount);
    }

    [Fact]
    public async Task InvalidateCache_Null_ClearsAll()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);
        await manager.GetCandlesAsync("MSFT", TimeFrame.D1, 100);
        Assert.Equal(2, spy.CallCount);

        // Clear all
        manager.InvalidateCache(null);

        // Both should reload
        await manager.GetCandlesAsync("AAPL", TimeFrame.D1, 100);
        await manager.GetCandlesAsync("MSFT", TimeFrame.D1, 100);
        Assert.Equal(4, spy.CallCount);
    }

    [Fact]
    public async Task GetCandlesAsync_EmptySymbol_ReturnsEmpty()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy);

        var result = await manager.GetCandlesAsync("", TimeFrame.D1, 100);

        Assert.Empty(result);
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task CacheEviction_ExceedsMaxSize_EvictsOldest()
    {
        var spy = new SpyDataService();
        var manager = new TimeFrameManager(spy, maxCacheEntries: 3);

        // Fill cache with 3 entries
        await manager.GetCandlesAsync("A", TimeFrame.D1, 10);
        await Task.Delay(10); // Ensure different timestamps
        await manager.GetCandlesAsync("B", TimeFrame.D1, 10);
        await Task.Delay(10);
        await manager.GetCandlesAsync("C", TimeFrame.D1, 10);
        Assert.Equal(3, spy.CallCount);

        // Add 4th entry -> should evict oldest ("A")
        await manager.GetCandlesAsync("D", TimeFrame.D1, 10);
        Assert.Equal(4, spy.CallCount);

        // "A" should have been evicted -> reloads
        await manager.GetCandlesAsync("A", TimeFrame.D1, 10);
        Assert.Equal(5, spy.CallCount);

        // "B" or "C" should still be cached
        await manager.GetCandlesAsync("C", TimeFrame.D1, 10);
        // If eviction worked correctly, C might still be cached -> no additional load
        // (depends on eviction order, but we verify A was evicted)
    }
}
