using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Spec indicators_formulas_0466: HA_Close_t = OHLC4_t; HA_Open_0 = (O_0 + C_0) / 2;
/// HA_Open_t = (HA_Open_{t-1} + HA_Close_{t-1}) / 2.
/// </summary>
public class HeikinAshiRecursionTests
{
    private static List<CoreCandleData> Candles() => new()
    {
        new(DateTime.Today.AddDays(0), 100m, 110m, 95m, 105m, 1000),
        new(DateTime.Today.AddDays(1), 106m, 115m, 102m, 112m, 1200),
        new(DateTime.Today.AddDays(2), 111m, 118m, 108m, 110m, 1100),
        new(DateTime.Today.AddDays(3), 109m, 114m, 100m, 102m, 1500),
        new(DateTime.Today.AddDays(4), 103m, 108m, 98m, 107m, 1300),
    };

    [Fact]
    public void GetStateAt_MatchesConverterSeries()
    {
        var candles = Candles();
        var series = HeikinAshiConverter.Convert(candles);

        for (int i = 0; i < candles.Count; i++)
        {
            var (haOpen, haClose) = HeikinAshiConverter.GetStateAt(candles, i);
            Assert.Equal(series[i].Open, haOpen);
            Assert.Equal(series[i].Close, haClose);
        }
    }

    [Theory]
    [InlineData(0, 102.5, 102.5)]
    [InlineData(1, 102.5, 108.75)]
    [InlineData(2, 105.625, 111.75)]
    [InlineData(3, 108.6875, 106.25)]
    [InlineData(4, 107.46875, 104)]
    public void GetStateAt_HandComputedConstants(int index, double haOpen, double haClose)
    {
        var state = HeikinAshiConverter.GetStateAt(Candles(), index);

        Assert.Equal((decimal)haOpen, state.haOpen);
        Assert.Equal((decimal)haClose, state.haClose);
    }

    [Fact]
    public void GetStateAt_SeedAtIndex_ReturnsInitialValues()
    {
        var candles = Candles();

        Assert.Equal(HeikinAshiConverter.CalculateInitialValues(candles[0]), HeikinAshiConverter.GetStateAt(candles, 0));
        Assert.Equal(HeikinAshiConverter.CalculateInitialValues(candles[3]), HeikinAshiConverter.GetStateAt(candles, 3, seedIndex: 3));
    }

    [Fact]
    public void GetStateAt_LaterSeed_ConvergesTowardExactSeries()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 80; i++)
        {
            decimal open = 100m + (i % 7) * 1.5m;
            decimal close = open + ((i % 5) - 2) * 1.25m;
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), open, Math.Max(open, close) + 1m, Math.Min(open, close) - 1m, close, 1000));
        }

        var exact = HeikinAshiConverter.GetStateAt(candles, 79);
        var warmedUp = HeikinAshiConverter.GetStateAt(candles, 79, seedIndex: 79 - 50);

        // Seed error halves every bar: 50 bars of warm-up leave well under 1e-9.
        Assert.True(Math.Abs(exact.haOpen - warmedUp.haOpen) < 0.000000001m);
        Assert.Equal(exact.haClose, warmedUp.haClose);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 0)]
    [InlineData(2, 3)]
    [InlineData(2, -1)]
    public void GetStateAt_InvalidIndices_Throw(int index, int seedIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HeikinAshiConverter.GetStateAt(Candles(), index, seedIndex));
    }

    [Fact]
    public void GetStateAt_WithPreviousState_ReturnsTheStateOfTheBarBefore()
    {
        var candles = Candles();

        // Seed bar 0: bar 2 has predecessor bar 1 (102.5, 108.75); bar 4 has predecessor bar 3 (108.6875, 106.25).
        var at2 = HeikinAshiConverter.GetStateAt(candles, 2, 0, out var previousOf2);
        Assert.Equal((105.625m, 111.75m), at2);
        Assert.Equal((102.5m, 108.75m), previousOf2);

        var at4 = HeikinAshiConverter.GetStateAt(candles, 4, 0, out var previousOf4);
        Assert.Equal((107.46875m, 104m), at4);
        Assert.Equal((108.6875m, 106.25m), previousOf4);

        // Warm-up from bar 2: bar 3 has predecessor bar 2 (the seed); the seed bar itself has none.
        HeikinAshiConverter.GetStateAt(candles, 3, 2, out var previousOf3);
        Assert.Equal(HeikinAshiConverter.CalculateInitialValues(candles[2]), previousOf3);
        HeikinAshiConverter.GetStateAt(candles, 2, 2, out var previousOfSeed);
        Assert.Null(previousOfSeed);
    }

    [Fact]
    public void NextState_EqualsConvertSingleOpenAndClose()
    {
        var candles = Candles();
        var (open, close) = HeikinAshiConverter.CalculateInitialValues(candles[0]);

        for (int i = 1; i < candles.Count; i++)
        {
            var next = HeikinAshiConverter.NextState(candles[i], open, close);
            var single = HeikinAshiConverter.ConvertSingle(candles[i], open, close);
            Assert.Equal((single.Open, single.Close), next);
            (open, close) = next;
        }
    }

    [Fact]
    public void GetStateAt_WithPreviousState_DoesNotAllocate()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 80; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 100m + i % 7, 110m + i % 5, 95m + i % 3, 105m + i % 4, 1000));
        }

        _ = HeikinAshiConverter.GetStateAt(candles, 79, 29, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        HeikinAshiConverter.GetStateAt(candles, 79, 29, out var previous);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(previous);
        Assert.Equal(0, allocated);
    }

    // The Dot chart runs the warm-up on every render and the Screener on every evaluation: no heap allocation is allowed.
    [Fact]
    public void GetStateAt_DoesNotAllocate()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 80; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 100m + i % 7, 110m + i % 5, 95m + i % 3, 105m + i % 4, 1000));
        }

        _ = HeikinAshiConverter.GetStateAt(candles, 79, 29); // JIT warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        var state = HeikinAshiConverter.GetStateAt(candles, 79, 29);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(state.haClose > 0m);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GetStateAt_NullCandles_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HeikinAshiConverter.GetStateAt(null!, 0));
    }
}
