using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using Xunit;

namespace StockAnalyzer.Tests.Services;

public class ScreenerSprint2Tests
{
    private static List<CoreCandleData> CreateTestCandles(int count = 100)
    {
        var candles = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + i * 0.5m + (decimal)Math.Sin(i * 0.2) * 5m;
            candles.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price,
                price + 2m,
                price - 2m,
                price + 1m,
                10000 + i * 100));
        }
        return candles;
    }

    #region ADX Wilder Smoothing Tests

    [Fact]
    public void CoreAdx_WarmupLength_MatchesPeriodMinusOne()
    {
        var indicator = new CoreAdxIndicator { Period = 14 };
        var candles = CreateTestCandles(50);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(50, indicator.Values.Count);
        Assert.Equal(50, indicator.PlusDI.Count);
        Assert.Equal(50, indicator.MinusDI.Count);

        // Indices 0 to Period - 2 (0..12) should be null
        for (int i = 0; i < 13; i++)
        {
            Assert.Null(indicator.Values[i]);
            Assert.Null(indicator.PlusDI[i]);
            Assert.Null(indicator.MinusDI[i]);
        }

        // Index Period - 1 (13) must be non-null
        Assert.NotNull(indicator.Values[13]);
        Assert.NotNull(indicator.PlusDI[13]);
        Assert.NotNull(indicator.MinusDI[13]);

        // Subsequent bars should also be non-null
        for (int i = 14; i < 50; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.NotNull(indicator.PlusDI[i]);
            Assert.NotNull(indicator.MinusDI[i]);
        }
    }

    [Fact]
    public void CoreAdx_PeriodLessThanOrEqualToZero_ReturnsAllNulls()
    {
        var indicator = new CoreAdxIndicator { Period = 0 };
        var candles = CreateTestCandles(20);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
        Assert.All(indicator.PlusDI, v => Assert.Null(v));
        Assert.All(indicator.MinusDI, v => Assert.Null(v));
    }

    [Fact]
    public void CoreAdx_CandlesFewerThanPeriod_ReturnsAllNulls()
    {
        var indicator = new CoreAdxIndicator { Period = 14 };
        var candles = CreateTestCandles(10);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(10, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void CoreAdx_ValuesWithinRangeZeroToHundred()
    {
        var indicator = new CoreAdxIndicator { Period = 14 };
        var candles = CreateTestCandles(100);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        foreach (var val in indicator.Values.Where(v => v.HasValue))
        {
            Assert.InRange(val!.Value, 0m, 100m);
        }
        foreach (var pdi in indicator.PlusDI.Where(v => v.HasValue))
        {
            Assert.InRange(pdi!.Value, 0m, 100m);
        }
        foreach (var mdi in indicator.MinusDI.Where(v => v.HasValue))
        {
            Assert.InRange(mdi!.Value, 0m, 100m);
        }
    }

    #endregion

    #region RCI Mid-Rank & Tie Tests

    [Fact]
    public void CoreRci_PeriodLessThanTwo_ReturnsAllNullsWithoutException()
    {
        var indicator = new CoreRciIndicator { Period = 1 };
        var candles = CreateTestCandles(20);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void CoreRci_FlatPricesWindow_ReturnsZero()
    {
        var indicator = new CoreRciIndicator { Period = 5 };
        var baseDate = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            candles.Add(new CoreCandleData(baseDate.AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(10, indicator.Values.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Null(indicator.Values[i]);
        }
        for (int i = 4; i < 10; i++)
        {
            Assert.Equal(0m, indicator.Values[i]);
        }
    }

    [Fact]
    public void CoreRci_StrictlyIncreasingPrices_ReturnsPositiveHundred()
    {
        var indicator = new CoreRciIndicator { Period = 5 };
        var baseDate = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            decimal p = 10m + i * 5m;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), p, p + 1m, p - 1m, p, 1000));
        }

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = 4; i < 10; i++)
        {
            Assert.Equal(100m, indicator.Values[i]);
        }
    }

    [Fact]
    public void CoreRci_StrictlyDecreasingPrices_ReturnsNegativeHundred()
    {
        var indicator = new CoreRciIndicator { Period = 5 };
        var baseDate = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            decimal p = 100m - i * 5m;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), p, p + 1m, p - 1m, p, 1000));
        }

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = 4; i < 10; i++)
        {
            Assert.Equal(-100m, indicator.Values[i]);
        }
    }

    [Fact]
    public void CoreRci_TiedPrices_CalculatesSymmetricalMidRanks()
    {
        // Window of 3 bars: prices [10, 20, 20]
        // time ranks: [1, 2, 3]
        // price ranks: [1.0, 2.5, 2.5]
        // diffs: [0, -0.5, 0.5], diff^2: [0, 0.25, 0.25] => sum = 0.5
        // denom: 3 * (9 - 1) = 24
        // RCI = (1 - (6 * 0.5)/24) * 100 = (1 - 3/24) * 100 = (1 - 0.125) * 100 = 87.5m
        var indicator = new CoreRciIndicator { Period = 3 };
        var baseDate = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>
        {
            new(baseDate, 10m, 10m, 10m, 10m, 1000),
            new(baseDate.AddDays(1), 20m, 20m, 20m, 20m, 1000),
            new(baseDate.AddDays(2), 20m, 20m, 20m, 20m, 1000)
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(87.5m, indicator.Values[2]);
    }

    #endregion

    #region Yang-Zhang Volatility Tests

    [Fact]
    public void CoreYangZhang_PeriodLessThanTwo_ReturnsAllNullsWithoutException()
    {
        var indicator = new CoreYangZhangVolatilityIndicator { Period = 1 };
        var candles = CreateTestCandles(20);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void CoreYangZhang_CalculatesNonNegativeVolatility()
    {
        var indicator = new CoreYangZhangVolatilityIndicator { Period = 10 };
        var candles = CreateTestCandles(50);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(50, indicator.Values.Count);

        for (int i = 0; i < 9; i++)
        {
            Assert.Null(indicator.Values[i]);
        }

        for (int i = 9; i < 50; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.True(indicator.Values[i] >= 0m, $"Volatility at index {i} was negative: {indicator.Values[i]}");
        }
    }

    [Fact]
    public void CoreYangZhang_InvalidPriceLeqZero_OutputsNullForWindow()
    {
        var indicator = new CoreYangZhangVolatilityIndicator { Period = 5 };
        var baseDate = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            // Inject non-positive price at index 5
            decimal p = (i == 5) ? 0m : 100m + i;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), p, p + 1m, p > 0 ? p - 1m : 0m, p, 1000));
        }

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        // Any window containing index 5 (indices 5, 6, 7, 8, 9) must produce null
        for (int i = 5; i <= 9; i++)
        {
            Assert.Null(indicator.Values[i]);
        }
    }

    #endregion

    #region AdaptiveSmoothingHelper Tests

    [Theory]
    [InlineData(SmoothingMethod.Median)]
    [InlineData(SmoothingMethod.TrimmedMean10)]
    [InlineData(SmoothingMethod.Midhinge)]
    public void AdaptiveSmoothing_ApplySpanSmoothing_MatchesApplySorting_BitForBit(SmoothingMethod method)
    {
        var testSizes = new[] { 1, 2, 3, 4, 5, 9, 14, 20, 25, 130 }; // Includes both <= 128 (stackalloc) and > 128 (ArrayPool)

        foreach (int size in testSizes)
        {
            var list = new List<decimal>(size);
            for (int i = 0; i < size; i++)
            {
                list.Add((decimal)Math.Sin(i * 1.5) * 100m + (i * 0.7m));
            }

            decimal expected = AdaptiveSmoothingHelper.ApplySorting(list, method);
            decimal actual = AdaptiveSmoothingHelper.ApplySpanSmoothing(list.ToArray().AsSpan(), method);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void AdaptiveSmoothing_CalculateAdaptiveRolling_MatchesSpanSmoothingPerWindow()
    {
        var series = new List<decimal?> { 10m, 20m, 15m, 30m, 25m, 35m, 40m, 5m, 12m, 18m };
        int period = 4;

        var rolling = AdaptiveSmoothingHelper.CalculateAdaptiveRolling(series, period, SmoothingMethod.Median);

        Assert.Equal(series.Count, rolling.Count);
        for (int i = 0; i < period - 1; i++)
        {
            Assert.Null(rolling[i]);
        }

        for (int i = period - 1; i < series.Count; i++)
        {
            var window = series.Skip(i - period + 1).Take(period).Select(v => v!.Value).ToArray();
            decimal expected = AdaptiveSmoothingHelper.ApplySpanSmoothing(window.AsSpan(), SmoothingMethod.Median);
            Assert.Equal(expected, rolling[i]);
        }
    }

    #endregion
}
