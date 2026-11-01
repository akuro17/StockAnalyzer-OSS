using Xunit;
using Xunit.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreAdxIndicatorTests
{
    private readonly ITestOutputHelper _output;

    public CoreAdxIndicatorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static List<CoreCandleData> CreateTestCandles(IEnumerable<(decimal open, decimal high, decimal low, decimal close)> values)
    {
        var startDate = DateTime.Today;
        return values.Select((v, i) => new CoreCandleData(
            startDate.AddDays(i), v.open, v.high, v.low, v.close, 1000
        )).ToList();
    }

    [Fact]
    public void Measure_Adx_OldVsNew_Discrepancy()
    {
        // Fixed seed deterministic random walk for T0 measurement
        var random = new Random(12345);
        decimal price = 100m;
        var candles = new List<CoreCandleData>();
        var start = DateTime.Today;
        for (int i = 0; i < 200; i++)
        {
            decimal change = (decimal)(random.NextDouble() * 10 - 5);
            price = Math.Max(10m, price + change);
            candles.Add(new CoreCandleData(start.AddDays(i), price - 1, price + 3, price - 3, price, 1000));
        }

        int period = 14;
        var (_, _, dxList) = IndicatorCalculationHelper.CalculateWilderDmiSeries(candles, period);

        // Old inline ADX algorithm (single DX seed at Period - 1)
        var oldAdx = new List<decimal?>(candles.Count);
        decimal oldPrev = 0m;
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                oldAdx.Add(null);
            }
            else if (i == period - 1)
            {
                oldPrev = dxList[i] ?? 0m;
                oldAdx.Add(oldPrev);
            }
            else
            {
                decimal dx = dxList[i] ?? 0m;
                decimal adx = (oldPrev * (period - 1) + dx) / period;
                oldPrev = adx;
                oldAdx.Add(adx);
            }
        }

        // New Wilder standard ADX
        var newAdx = IndicatorCalculationHelper.CalculateWilderAdxSeries(dxList, period);

        int seedIndex = 2 * period - 2; // 26
        decimal maxDiff = 0m;
        int maxDiffIndex = -1;
        int barsUntilBelowPointOne = -1;

        for (int i = seedIndex; i < candles.Count; i++)
        {
            if (oldAdx[i].HasValue && newAdx[i].HasValue)
            {
                decimal diff = Math.Abs(oldAdx[i]!.Value - newAdx[i]!.Value);
                if (diff > maxDiff)
                {
                    maxDiff = diff;
                    maxDiffIndex = i;
                }
                if (diff < 0.1m && barsUntilBelowPointOne == -1)
                {
                    barsUntilBelowPointOne = i - seedIndex; // bars elapsed from first valid new ADX
                }
            }
        }

        _output.WriteLine($"T0_MEASUREMENT: maxDiff={maxDiff:F4} at index {maxDiffIndex}, barsUntilBelowPointOne={barsUntilBelowPointOne}");
        Assert.True(maxDiff > 0m);
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectValues()
    {
        // Period = 4 requires n >= 2*Period - 1 = 7 for first valid ADX at index 6
        var indicator = new CoreAdxIndicator { Period = 4 };
        var candles = CreateTestCandles(new[]
        {
            (10m, 12m, 9m, 11m),   // i=0
            (11m, 13m, 10m, 12m),  // i=1, Up move
            (12m, 12m, 9m, 10m),   // i=2, Down move
            (10m, 11m, 8m, 9m),    // i=3, Down move
            (9m, 12m, 8m, 11m),    // i=4, Up move
            (11m, 13m, 10m, 12m),  // i=5, Up move
            (12m, 14m, 11m, 13m)   // i=6, Up move
        });

        indicator.Calculate(candles);

        Assert.Equal(7, indicator.Values.Count);
        // Indices 0..5 must be null
        Assert.True(indicator.Values.Take(6).All(v => v == null));

        // Independent oracle for DX at index 3..6
        var (_, _, dxList) = IndicatorCalculationHelper.CalculateWilderDmiSeries(candles, 4);
        decimal expectedSeed = (dxList[3]!.Value + dxList[4]!.Value + dxList[5]!.Value + dxList[6]!.Value) / 4m;

        Assert.NotNull(indicator.Values[6]);
        Assert.Equal(expectedSeed, indicator.Values[6]!.Value);
        Assert.True(indicator.Values[6] > 0);
        Assert.True(indicator.PlusDI[6] > 0);
        Assert.True(indicator.MinusDI[6] > 0);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreAdxIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.PlusDI);
        Assert.Empty(indicator.MinusDI);
    }

    [Fact]
    public void CalculateWilderAdxSeries_EdgeCases_IndependentOracle()
    {
        // 1. null dx -> empty
        Assert.Empty(IndicatorCalculationHelper.CalculateWilderAdxSeries(null!, 14));

        // 2. period <= 0 -> all null
        var dummy = new decimal?[] { 10m, 20m, 30m };
        var resPeriodZero = IndicatorCalculationHelper.CalculateWilderAdxSeries(dummy, 0);
        Assert.Equal(3, resPeriodZero.Count);
        Assert.All(resPeriodZero, v => Assert.Null(v));

        // 3. n < 2 * period - 1 -> all null
        // For period = 2, s = 2, min length = 3. Given length 2:
        var resTooShort = IndicatorCalculationHelper.CalculateWilderAdxSeries(new decimal?[] { 10m, 20m }, 2);
        Assert.Equal(2, resTooShort.Count);
        Assert.All(resTooShort, v => Assert.Null(v));

        // 4. period = 1: s = 0. adx[i] = (prev * 0 + dx[i]) / 1 = dx[i]
        var dxP1 = new decimal?[] { 15m, 25m, 35m };
        var resP1 = IndicatorCalculationHelper.CalculateWilderAdxSeries(dxP1, 1);
        Assert.Equal(3, resP1.Count);
        Assert.Equal(15m, resP1[0]);
        Assert.Equal(25m, resP1[1]);
        Assert.Equal(35m, resP1[2]);

        // 5. null in seed range -> null for all >= s
        // period = 2, s = 2. seed uses k in [1, 2]. If dx[1] is null:
        var dxNullSeed = new decimal?[] { 10m, null, 20m, 30m };
        var resNullSeed = IndicatorCalculationHelper.CalculateWilderAdxSeries(dxNullSeed, 2);
        Assert.Equal(4, resNullSeed.Count);
        Assert.All(resNullSeed, v => Assert.Null(v));

        // 6. null after seed -> null for all >= null index
        var dxNullPostSeed = new decimal?[] { 10m, 20m, 30m, null, 50m };
        var resNullPost = IndicatorCalculationHelper.CalculateWilderAdxSeries(dxNullPostSeed, 2);
        Assert.Equal(5, resNullPost.Count);
        Assert.Null(resNullPost[0]);
        Assert.Null(resNullPost[1]);
        Assert.Equal(25m, resNullPost[2]); // mean(20, 30) = 25
        Assert.Null(resNullPost[3]); // null at index 3
        Assert.Null(resNullPost[4]); // null after index 3
    }
}
