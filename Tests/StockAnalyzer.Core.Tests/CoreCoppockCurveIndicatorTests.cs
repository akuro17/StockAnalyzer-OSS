using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreCoppockCurveIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(int count, decimal basePrice = 100m, int zeroIndex = -1)
    {
        var startDate = DateTime.Today;
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal price = (i == zeroIndex) ? 0m : basePrice + (i * 0.5m);
            candles.Add(new CoreCandleData(startDate.AddDays(i), price, price + 1, price - 1, price, 1000));
        }
        return candles;
    }

    private static List<decimal?> CalculateOldCoppock(IReadOnlyList<CoreCandleData> candles, int longPeriod, int shortPeriod, int wmaPeriod)
    {
        int maxRoc = Math.Max(longPeriod, shortPeriod);
        var priceSeries = candles.Select(c => (decimal?)c.Close).ToList();
        var rocSum = new List<decimal>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < maxRoc)
            {
                rocSum.Add(0);
                continue;
            }

            decimal longBase = priceSeries[i - longPeriod] ?? 0m;
            decimal shortBase = priceSeries[i - shortPeriod] ?? 0m;
            decimal current = priceSeries[i] ?? 0m;

            decimal rocLong = (current - longBase) / longBase * 100m;
            decimal rocShort = (current - shortBase) / shortBase * 100m;
            rocSum.Add(rocLong + rocShort);
        }

        var values = new List<decimal?>();
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < maxRoc + wmaPeriod - 1)
            {
                values.Add(null);
                continue;
            }

            decimal numerator = 0;
            decimal denominator = 0;
            for (int j = 0; j < wmaPeriod; j++)
            {
                decimal val = rocSum[i - j];
                decimal weight = wmaPeriod - j;
                numerator += val * weight;
                denominator += weight;
            }
            values.Add(numerator / denominator);
        }

        return values;
    }

    [Fact]
    public void Calculate_WithZeroBasePrice_DoesNotThrowAndReturnsValidResult()
    {
        // 50 candles, candle at index 5 has price 0.
        // For LongRocPeriod = 14, at i = 19, longBase = priceSeries[19 - 14] = priceSeries[5] = 0.
        // In the old implementation, this caused a DivideByZeroException.
        var candles = CreateTestCandles(50, zeroIndex: 5);

        var indicator = new CoreCoppockCurveIndicator
        {
            LongRocPeriod = 14,
            ShortRocPeriod = 11,
            WmaPeriod = 10
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(50, indicator.Values.Count);

        // Indices before maxRoc + WmaPeriod - 1 (14 + 10 - 1 = 23) must be null
        for (int i = 0; i < 23; i++)
        {
            Assert.Null(indicator.Values[i]);
        }

        // Indices >= 23 must be finite (not throwing and not null)
        for (int i = 23; i < 50; i++)
        {
            Assert.NotNull(indicator.Values[i]);
        }
    }

    [Fact]
    public void Calculate_MatchesPreChangeAlgorithm_OnNormalPositiveSeries()
    {
        var candles = CreateTestCandles(60, basePrice: 50m);

        var indicator = new CoreCoppockCurveIndicator
        {
            LongRocPeriod = 14,
            ShortRocPeriod = 11,
            WmaPeriod = 10
        };

        var actualResult = indicator.Calculate(candles);
        var expectedValues = CalculateOldCoppock(candles, 14, 11, 10);

        Assert.True(actualResult.IsSuccessful);
        Assert.Equal(60, indicator.Values.Count);
        Assert.Equal(60, expectedValues.Count);

        for (int i = 0; i < 60; i++)
        {
            Assert.Equal(expectedValues[i], indicator.Values[i]);
        }
    }
}
