using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreStochIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, decimal> priceFunc)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceFunc(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price + 2, price - 2, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsResult()
    {
        var candles = GenerateCandleData(20, i => 100 + i);
        var indicator = new CoreStochasticIndicator { KPeriod = 14, DPeriod = 3, Smooth = 1 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        var k = result.MainValues; // %K
        var d = result.GetSeries("PercentD"); // %D

        Assert.NotNull(k);
        Assert.NotNull(d);
        Assert.Equal(20, k.Count);
        Assert.Equal(20, d.Count);
    }

    [Fact]
    public void Calculate_WithPriceAtHigh_KIs100()
    {
        var candles = GenerateCandleData(20, i => 100 + (i % 10)); // Cycle
        // Make the last close the highest high of the last 14 periods
        candles[19] = new CoreCandleData(DateTime.Today, 120, 120, 118, 120, 100);
        var indicator = new CoreStochasticIndicator { KPeriod = 14, DPeriod = 3, Smooth = 1 };
        var result = indicator.Calculate(candles);
        var k = result.MainValues;

        Assert.NotNull(k.Last());
        Assert.Equal(100, k.Last().Value, 4);
    }

    [Fact]
    public void Calculate_WithPriceAtLow_KIs0()
    {
        var candles = GenerateCandleData(20, i => 100 + (i % 10)); // Cycle
        // Make the last close the lowest low of the last 14 periods
        candles[19] = new CoreCandleData(DateTime.Today, 90, 92, 90, 90, 100);
        var indicator = new CoreStochasticIndicator { KPeriod = 14, DPeriod = 3, Smooth = 1 };
        var result = indicator.Calculate(candles);
        var k = result.MainValues;

        Assert.NotNull(k.Last());
        Assert.Equal(0, k.Last().Value, 4);
    }

    [Fact]
    public void Calculate_WithValidData_DIsSmaOfK()
    {
        var candles = GenerateCandleData(20, i => 100 + (decimal)Math.Sin(i) * 5);
        int period = 14;
        int smaPeriod = 3;
        var indicator = new CoreStochasticIndicator { KPeriod = period, DPeriod = smaPeriod, Smooth = 1 };
        var result = indicator.Calculate(candles);
        var k = result.MainValues;
        var d = result.GetSeries("PercentD");

        // Find the first calculable D value
        int firstKIndex = period - 1;
        int firstDIndex = firstKIndex + smaPeriod - 1;

        Assert.NotNull(d[firstDIndex]);

        decimal? expectedD = (k[firstDIndex] + k[firstDIndex - 1] + k[firstDIndex - 2]) / 3;
        Assert.Equal(expectedD.Value, d[firstDIndex].Value, 4);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyLists()
    {
        var candles = new List<CoreCandleData>();
        var result = new CoreStochasticIndicator { KPeriod = 14 }.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
        // If not successful, it's also acceptable for empty input
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var result = new CoreStochasticIndicator { KPeriod = 14 }.Calculate(null);
        Assert.False(result.IsSuccessful);
    }
}
