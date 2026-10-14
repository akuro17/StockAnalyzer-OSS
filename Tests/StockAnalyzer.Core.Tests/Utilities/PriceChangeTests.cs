using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Utilities;

public class PriceChangeTests
{
    private static CandleData Bar(int day, decimal open, decimal close)
        => new(new DateTime(2026, 1, 1).AddDays(day), open, Math.Max(open, close), Math.Min(open, close), close, 1000);

    private static List<CandleData> TwoBars(decimal previousClose, decimal open, decimal close)
        => new() { Bar(0, previousClose, previousClose), Bar(1, open, close) };

    [Fact]
    public void FromPreviousClose_Rise_IsPositiveAmountAndPercent()
    {
        var candles = TwoBars(previousClose: 100m, open: 100m, close: 105m);

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(5m, change.Amount);
        Assert.Equal(5m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_Fall_IsNegativeAmountAndPercent()
    {
        var candles = TwoBars(previousClose: 100m, open: 100m, close: 95m);

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(-5m, change.Amount);
        Assert.Equal(-5m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_GapUpRedCandle_IsPositiveBecauseOpenIsNotUsed()
    {
        // The case that made the watchlist use Close - Open in 2026-07: opened above the previous close, closed below the open.
        var candles = TwoBars(previousClose: 100m, open: 110m, close: 105m);

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(5m, change.Amount);
        Assert.Equal(5m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_GapDownGreenCandle_IsNegativeBecauseOpenIsNotUsed()
    {
        var candles = TwoBars(previousClose: 100m, open: 90m, close: 95m);

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(-5m, change.Amount);
        Assert.Equal(-5m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_Unchanged_IsZero()
    {
        var candles = TwoBars(previousClose: 100m, open: 101m, close: 100m);

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(0m, change.Amount);
        Assert.Equal(0m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_SingleBar_IsZero()
    {
        var candles = new List<CandleData> { Bar(0, 100m, 105m) };

        var change = PriceChange.FromPreviousClose(candles, 0);

        Assert.Equal(0m, change.Amount);
        Assert.Equal(0m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_PreviousCloseZero_AmountIsDifferenceAndPercentIsZero()
    {
        var candles = TwoBars(previousClose: 0m, open: 9m, close: 10m);

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(10m, change.Amount);
        Assert.Equal(0m, change.Percent);
    }

    [Fact]
    public void FromCloses_NegativePreviousClose_IsDividedAsItIs()
    {
        // Owner decision 2026-10-14: only a previous close of 0 yields a 0 percentage.
        Assert.Equal(new PriceChange(20m, -50m), PriceChange.FromCloses(-20m, -40m));
        Assert.Equal(new PriceChange(-20m, 50m), PriceChange.FromCloses(-60m, -40m));
        Assert.Equal(new PriceChange(50m, -125m), PriceChange.FromCloses(10m, -40m));   // crossing zero: -40 -> +10
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void FromPreviousClose_IndexWithoutPreviousBarOrOutOfRange_IsZeroAndDoesNotThrow(int index)
    {
        var candles = TwoBars(previousClose: 100m, open: 100m, close: 105m);

        var change = PriceChange.FromPreviousClose(candles, index);

        Assert.Equal(0m, change.Amount);
        Assert.Equal(0m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_NullOrEmptyList_IsZeroAndDoesNotThrow()
    {
        Assert.Equal(PriceChange.None, PriceChange.FromPreviousClose((IReadOnlyList<CandleData>?)null, 0));
        Assert.Equal(PriceChange.None, PriceChange.FromPreviousClose(new List<CandleData>(), 0));
        Assert.Equal(PriceChange.None, PriceChange.FromPreviousClose((IReadOnlyList<CoreCandleData>?)null, 0));
        Assert.Equal(PriceChange.None, PriceChange.FromPreviousClose(new List<CoreCandleData>(), 0));
    }

    [Fact]
    public void FromCloses_IsTheSameDefinitionAsFromPreviousClose()
    {
        Assert.Equal(new PriceChange(5m, 5m), PriceChange.FromCloses(105m, 100m));
        Assert.Equal(new PriceChange(-5m, -5m), PriceChange.FromCloses(95m, 100m));
        Assert.Equal(new PriceChange(10m, 0m), PriceChange.FromCloses(10m, 0m));
        Assert.Equal(PriceChange.None, PriceChange.FromCloses(100m, 100m));
        Assert.Equal(PriceChange.FromPreviousClose(TwoBars(66570m, 69570m, 68640m), 1), PriceChange.FromCloses(68640m, 66570m));
    }

    [Fact]
    public void FromPreviousClose_UsesTheBarBeforeTheIndexNotTheLatestBars()
    {
        var candles = new List<CandleData> { Bar(0, 50m, 50m), Bar(1, 50m, 60m), Bar(2, 60m, 30m) };

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(10m, change.Amount);
        Assert.Equal(20m, change.Percent);
    }

    [Fact]
    public void FromPreviousClose_IsDecimalExact()
    {
        // 8035-T: previous close 66570, today's close 68640 (open 69570 must not matter).
        var candles = TwoBars(previousClose: 66570m, open: 69570m, close: 68640m);

        var change = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(2070m, change.Amount);
        Assert.Equal(2070m / 66570m * 100m, change.Percent);
        Assert.Equal(3.1095, (double)change.Percent, 4);
    }

    [Fact]
    public void FromPreviousClose_DoesNotChangeTheInput()
    {
        var candles = TwoBars(previousClose: 100m, open: 110m, close: 105m);
        var snapshot = new List<CandleData>(candles);

        _ = PriceChange.FromPreviousClose(candles, 1);

        Assert.Equal(snapshot, candles);
    }
}
