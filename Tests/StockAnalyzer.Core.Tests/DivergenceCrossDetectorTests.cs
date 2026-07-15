using Xunit;
using StockAnalyzer.Core.Models.DivergenceCross;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class DivergenceCrossDetectorTests
{
    #region Divergence Tests

    [Fact]
    public void DetectDivergences_WithRegularBullish_ReturnsSignal()
    {
        // Price makes lower low, RSI makes higher low => bullish divergence
        // Pattern: a clear downtrend in price but oscillator shows strength
        int count = 30;
        var highs = new List<decimal>();
        var lows = new List<decimal>();
        var indicator = new List<decimal?>();

        for (int i = 0; i < count; i++)
        {
            // Create price that has two distinct lows at i=7 and i=22
            // with the second low LOWER than the first
            decimal baseLow;
            decimal baseHigh;
            if (i == 7)
            {
                baseLow = 90m;
                baseHigh = 95m;
            }
            else if (i == 22)
            {
                baseLow = 85m; // Lower low
                baseHigh = 90m;
            }
            else
            {
                baseLow = 100m + i * 0.1m;
                baseHigh = 105m + i * 0.1m;
            }

            lows.Add(baseLow);
            highs.Add(baseHigh);

            // Indicator: higher low at second pivot (divergence)
            decimal? indVal;
            if (i == 7)
                indVal = 20m; // First indicator low
            else if (i == 22)
                indVal = 25m; // Higher indicator low (divergence)
            else
                indVal = 50m + i * 0.5m;

            indicator.Add(indVal);
        }

        var signals = DivergenceCrossDetector.DetectDivergences(highs, lows, indicator, pivotOrder: 3);

        Assert.True(signals.Any(s => s.Type == SignalType.RegularBullishDivergence),
            "Expected at least one Regular Bullish Divergence signal");
    }

    [Fact]
    public void DetectDivergences_WithRegularBearish_ReturnsSignal()
    {
        // Price makes higher high, indicator makes lower high => bearish divergence
        int count = 30;
        var highs = new List<decimal>();
        var lows = new List<decimal>();
        var indicator = new List<decimal?>();

        for (int i = 0; i < count; i++)
        {
            decimal baseHigh;
            if (i == 7)
                baseHigh = 110m;      // First price high
            else if (i == 22)
                baseHigh = 120m;      // Higher price high
            else
                baseHigh = 100m;

            decimal baseLow = baseHigh - 5m;
            highs.Add(baseHigh);
            lows.Add(baseLow);

            // Indicator: lower high at second pivot (divergence)
            decimal? indVal;
            if (i == 7)
                indVal = 80m;         // First indicator high
            else if (i == 22)
                indVal = 70m;         // Lower indicator high (divergence)
            else
                indVal = 50m;

            indicator.Add(indVal);
        }

        var signals = DivergenceCrossDetector.DetectDivergences(highs, lows, indicator, pivotOrder: 3);

        Assert.True(signals.Any(s => s.Type == SignalType.RegularBearishDivergence),
            "Expected at least one Regular Bearish Divergence signal");
    }

    [Fact]
    public void DetectDivergences_WithHiddenBullish_ReturnsSignal()
    {
        // Price makes higher low, indicator makes lower low => hidden bullish
        int count = 30;
        var highs = new List<decimal>();
        var lows = new List<decimal>();
        var indicator = new List<decimal?>();

        for (int i = 0; i < count; i++)
        {
            decimal baseLow;
            if (i == 7)
                baseLow = 90m;        // First price low
            else if (i == 22)
                baseLow = 95m;        // Higher price low
            else
                baseLow = 105m;

            highs.Add(baseLow + 10m);
            lows.Add(baseLow);

            decimal? indVal;
            if (i == 7)
                indVal = 30m;         // First indicator low
            else if (i == 22)
                indVal = 20m;         // Lower indicator low (hidden)
            else
                indVal = 50m;

            indicator.Add(indVal);
        }

        var signals = DivergenceCrossDetector.DetectDivergences(highs, lows, indicator, pivotOrder: 3);

        Assert.True(signals.Any(s => s.Type == SignalType.HiddenBullishDivergence),
            "Expected at least one Hidden Bullish Divergence signal");
    }

    [Fact]
    public void DetectDivergences_WithHiddenBearish_ReturnsSignal()
    {
        // Price makes lower high, indicator makes higher high => hidden bearish
        int count = 30;
        var highs = new List<decimal>();
        var lows = new List<decimal>();
        var indicator = new List<decimal?>();

        for (int i = 0; i < count; i++)
        {
            decimal baseHigh;
            if (i == 7)
                baseHigh = 120m;      // First price high
            else if (i == 22)
                baseHigh = 115m;      // Lower price high
            else
                baseHigh = 100m;

            highs.Add(baseHigh);
            lows.Add(baseHigh - 5m);

            decimal? indVal;
            if (i == 7)
                indVal = 70m;         // First indicator high
            else if (i == 22)
                indVal = 80m;         // Higher indicator high (hidden)
            else
                indVal = 50m;

            indicator.Add(indVal);
        }

        var signals = DivergenceCrossDetector.DetectDivergences(highs, lows, indicator, pivotOrder: 3);

        Assert.True(signals.Any(s => s.Type == SignalType.HiddenBearishDivergence),
            "Expected at least one Hidden Bearish Divergence signal");
    }

    [Fact]
    public void DetectDivergences_WithEmptyData_ReturnsEmpty()
    {
        var result = DivergenceCrossDetector.DetectDivergences(
            new List<decimal>(),
            new List<decimal>(),
            new List<decimal?>());

        Assert.Empty(result);
    }

    [Fact]
    public void DetectDivergences_WithNullInput_ReturnsEmpty()
    {
        var result = DivergenceCrossDetector.DetectDivergences(null!, null!, null!);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectDivergences_WithInsufficientData_ReturnsEmpty()
    {
        var result = DivergenceCrossDetector.DetectDivergences(
            new List<decimal> { 100, 101, 102 },
            new List<decimal> { 99, 100, 101 },
            new List<decimal?> { 50, 55, 60 });

        Assert.Empty(result);
    }

    #endregion

    #region Cross Tests

    [Fact]
    public void DetectCrosses_GoldenCross_ReturnsSignal()
    {
        // Short series crosses above long series
        var shortSeries = new List<decimal?> { 10m, 12m, 14m, 16m, 18m };
        var longSeries = new List<decimal?>  { 15m, 15m, 15m, 15m, 15m };

        var signals = DivergenceCrossDetector.DetectCrosses(shortSeries, longSeries);

        Assert.Single(signals.Where(s => s.Type == SignalType.GoldenCross));
        var gc = signals.First(s => s.Type == SignalType.GoldenCross);
        Assert.Equal(3, gc.CrossIndex); // At index 3, short (16) > long (15)
    }

    [Fact]
    public void DetectCrosses_DeadCross_ReturnsSignal()
    {
        // Short series crosses below long series
        var shortSeries = new List<decimal?> { 20m, 18m, 16m, 14m, 12m };
        var longSeries = new List<decimal?>  { 15m, 15m, 15m, 15m, 15m };

        var signals = DivergenceCrossDetector.DetectCrosses(shortSeries, longSeries);

        Assert.Single(signals.Where(s => s.Type == SignalType.DeadCross));
        var dc = signals.First(s => s.Type == SignalType.DeadCross);
        Assert.Equal(3, dc.CrossIndex); // At index 3, short (14) < long (15)
    }

    [Fact]
    public void DetectCrosses_MultiplesCrosses_ReturnsAll()
    {
        // Multiple crosses
        var shortSeries = new List<decimal?> { 10m, 20m, 10m, 20m, 10m };
        var longSeries = new List<decimal?>  { 15m, 15m, 15m, 15m, 15m };

        var signals = DivergenceCrossDetector.DetectCrosses(shortSeries, longSeries);

        Assert.Equal(4, signals.Count);
    }

    [Fact]
    public void DetectCrosses_NoCross_ReturnsEmpty()
    {
        // Short always above long
        var shortSeries = new List<decimal?> { 20m, 22m, 24m, 26m };
        var longSeries = new List<decimal?>  { 10m, 10m, 10m, 10m };

        var signals = DivergenceCrossDetector.DetectCrosses(shortSeries, longSeries);

        Assert.Empty(signals);
    }

    [Fact]
    public void DetectCrosses_WithNullValues_SkipsNulls()
    {
        var shortSeries = new List<decimal?> { null, null, 10m, 20m };
        var longSeries = new List<decimal?>  { null, null, 15m, 15m };

        var signals = DivergenceCrossDetector.DetectCrosses(shortSeries, longSeries);

        Assert.Single(signals);
        Assert.Equal(SignalType.GoldenCross, signals[0].Type);
    }

    [Fact]
    public void DetectCrosses_EmptyData_ReturnsEmpty()
    {
        var result = DivergenceCrossDetector.DetectCrosses(
            new List<decimal?>(),
            new List<decimal?>());

        Assert.Empty(result);
    }

    [Fact]
    public void DetectCrosses_NullInput_ReturnsEmpty()
    {
        var result = DivergenceCrossDetector.DetectCrosses(null!, null!);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectCrosses_WithDifferentDataStarts_HandlesNullsCorrectly()
    {
        // One indicator starts calculating later (more nulls at the beginning) than the other.
        // E.g., RSI14 vs RSI7. RSI14 has 14 nulls, RSI7 has 7 nulls.
        var shortSeries = new List<decimal?> { null, null, 40m, 50m, 60m, 70m, 40m };
        var longSeries = new List<decimal?> { null, null, null, null, 30m, 60m, 50m };

        // Index 0: short=null, long=null
        // Index 1: short=null, long=null
        // Index 2: short=40, long=null
        // Index 3: short=50, long=null
        // Index 4: short=60, long=30  (Initial valid state: short > long)
        // Index 5: short=70, long=60  (short > long)
        // Index 6: short=40, long=50  (short < long => Dead Cross)

        var crosses = DivergenceCrossDetector.DetectCrosses(shortSeries, longSeries);

        Assert.Single(crosses);
        Assert.Equal(SignalType.DeadCross, crosses[0].Type);
        Assert.Equal(6, crosses[0].CrossIndex);
        Assert.Equal(40m, crosses[0].ShortValue);
        Assert.Equal(50m, crosses[0].LongValue);
    }

    #endregion

    #region Local Extrema Tests

    [Fact]
    public void ExtractLocalMaxima_FindsCorrectPeaks()
    {
        // Clear peak at index 5
        var values = new List<decimal> { 1, 2, 3, 4, 5, 10, 5, 4, 3, 2, 1 };

        var maxima = DivergenceCrossDetector.ExtractLocalMaxima(values, order: 2);

        Assert.Single(maxima);
        Assert.Equal(5, maxima[0].Index);
        Assert.Equal(10m, maxima[0].Value);
    }

    [Fact]
    public void ExtractLocalMinima_FindsCorrectTroughs()
    {
        // Clear trough at index 5
        var values = new List<decimal> { 10, 8, 6, 4, 3, 1, 3, 4, 6, 8, 10 };

        var minima = DivergenceCrossDetector.ExtractLocalMinima(values, order: 2);

        Assert.Single(minima);
        Assert.Equal(5, minima[0].Index);
        Assert.Equal(1m, minima[0].Value);
    }

    #endregion
}
