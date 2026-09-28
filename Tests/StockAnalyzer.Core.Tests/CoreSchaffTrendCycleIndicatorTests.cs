using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Regression coverage for the Schaff Trend Cycle indicator, whose <c>CalculateCore</c> was
/// previously a stub that emitted only nulls (so the chart line never rendered).
/// </summary>
public class CoreSchaffTrendCycleIndicatorTests
{
    private static List<CoreCandleData> GenerateCandles(int count, Func<int, decimal> priceFunc)
    {
        var candles = new List<CoreCandleData>(count);
        var date = new DateTime(2023, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal price = priceFunc(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price + 2m, price - 2m, price, 1000));
        }

        return candles;
    }

    [Fact]
    public void Calculate_WithRealisticData_ProducesNonNullValuesInZeroToHundredRange()
    {
        var candles = GenerateCandles(300, i => 100m + (decimal)Math.Sin(i * 0.15) * 20m + i * 0.1m);

        var indicator = new CoreSchaffTrendCycleIndicator();
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(300, result.MainValues.Count);

        // The core bug: the line never rendered because every value was null.
        Assert.Contains(result.MainValues, v => v.HasValue);

        Assert.All(result.MainValues.Where(v => v.HasValue), v => Assert.InRange(v!.Value, 0m, 100m));
    }

    [Fact]
    public void Calculate_OutputLengthAlwaysMatchesCandleCount()
    {
        foreach (int n in new[] { 1, 10, 75, 260 })
        {
            var candles = GenerateCandles(n, i => 100m + i);
            var result = new CoreSchaffTrendCycleIndicator().Calculate(candles);
            Assert.Equal(n, result.MainValues.Count);
        }
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsAllNulls()
    {
        var candles = GenerateCandles(20, i => 100m + i);

        var result = new CoreSchaffTrendCycleIndicator().Calculate(candles);

        Assert.Equal(20, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }

    [Fact]
    public void Calculate_CyclicalMarket_StcSweepsFullZeroToHundredRange()
    {
        // Multi-cycle sine wave => MACD oscillates => STC must swing across (near) its full range.
        var candles = GenerateCandles(400, i => 100m + (decimal)Math.Sin(i * 0.25) * 25m);

        var valued = new CoreSchaffTrendCycleIndicator()
            .Calculate(candles).MainValues
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        Assert.NotEmpty(valued);
        Assert.True(valued.Max() > 90m, $"STC should reach near 100 on a rising cycle phase, max was {valued.Max()}");
        Assert.True(valued.Min() < 10m, $"STC should reach near 0 on a falling cycle phase, min was {valued.Min()}");
    }

    [Fact]
    public void Calculate_MatchesCanonicalReferenceImplementation()
    {
        var candles = GenerateCandles(320, i => 100m + (decimal)Math.Sin(i * 0.11) * 15m + (decimal)Math.Cos(i * 0.03) * 8m);

        var actual = new CoreSchaffTrendCycleIndicator { CyclePeriod = 10, ShortPeriod = 23, LongPeriod = 50 }
            .Calculate(candles).MainValues;

        var expected = ReferenceStc(candles, cyclePeriod: 10, shortPeriod: 23, longPeriod: 50, smoothing: 3);

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].HasValue, actual[i].HasValue);
            if (expected[i].HasValue)
            {
                Assert.Equal(expected[i]!.Value, actual[i]!.Value, precision: 6);
            }
        }
    }

    // --- Inline transcription of Web/Site/Indicator/C#/Code/SchaffTrendCycle.cs (canonical reference) ---

    private static List<decimal?> ReferenceStc(
        IReadOnlyList<CoreCandleData> candles, int cyclePeriod, int shortPeriod, int longPeriod, int smoothing)
    {
        var macd = ReferenceMacd(candles, shortPeriod, longPeriod);
        var stoch1 = ReferenceStoch(macd, cyclePeriod);
        var pf1 = ReferenceEmaSmooth(stoch1, smoothing);
        var stoch2 = ReferenceStoch(pf1, cyclePeriod);
        var stc = ReferenceEmaSmooth(stoch2, smoothing);

        return stc.Select(v => v.HasValue ? (decimal?)Math.Max(0m, Math.Min(100m, v.Value)) : null).ToList();
    }

    private static List<decimal?> ReferenceMacd(IReadOnlyList<CoreCandleData> candles, int shortPeriod, int longPeriod)
    {
        var s = ReferenceEma(candles, shortPeriod);
        var l = ReferenceEma(candles, longPeriod);
        var macd = new List<decimal?>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            macd.Add(s[i].HasValue && l[i].HasValue ? s[i]!.Value - l[i]!.Value : (decimal?)null);
        }

        return macd;
    }

    private static List<decimal?> ReferenceEma(IReadOnlyList<CoreCandleData> candles, int period)
    {
        var results = new List<decimal?>(candles.Count);
        decimal k = 2.0m / (period + 1);
        decimal? prevEma = null;
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1) { results.Add(null); continue; }
            if (i == period - 1)
            {
                decimal sum = 0;
                for (int j = 0; j < period; j++) sum += candles[i - j].Close;
                prevEma = sum / period;
                results.Add(prevEma);
                continue;
            }

            prevEma = (candles[i].Close - prevEma!.Value) * k + prevEma.Value;
            results.Add(prevEma);
        }

        return results;
    }

    private static List<decimal?> ReferenceStoch(IReadOnlyList<decimal?> source, int cyclePeriod)
    {
        var result = new List<decimal?>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            if (i < cyclePeriod - 1 || !source[i].HasValue) { result.Add(null); continue; }

            decimal highest = decimal.MinValue;
            decimal lowest = decimal.MaxValue;
            for (int j = i - cyclePeriod + 1; j <= i; j++)
            {
                if (!source[j].HasValue) continue;
                if (source[j]!.Value > highest) highest = source[j]!.Value;
                if (source[j]!.Value < lowest) lowest = source[j]!.Value;
            }

            result.Add(highest == lowest ? 0m : (source[i]!.Value - lowest) / (highest - lowest) * 100m);
        }

        return result;
    }

    private static List<decimal?> ReferenceEmaSmooth(IReadOnlyList<decimal?> source, int period)
    {
        var results = new List<decimal?>(source.Count);
        decimal k = 2.0m / (period + 1);
        decimal? prev = null;
        for (int i = 0; i < source.Count; i++)
        {
            if (!source[i].HasValue) { results.Add(null); continue; }
            prev = prev.HasValue ? (source[i]!.Value - prev.Value) * k + prev.Value : source[i]!.Value;
            results.Add(prev);
        }

        return results;
    }
}
