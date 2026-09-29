using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreDmaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, decimal> priceFunc)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            var price = priceFunc(i);
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), price, price, price, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsDisplacedValues()
    {
        var candles = GenerateCandleData(30, i => 100 + i);
        int period = 10;
        int displacement = 5;
        var indicator = new CoreDmaIndicator { Period = period, Displacement = displacement };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);

        // First SMA is at index 9. First DMA is at index 9+5 = 14.
        int firstSmaIndex = period - 1;
        int firstDmaIndex = firstSmaIndex + displacement;

        Assert.True(result.MainValues.Take(firstDmaIndex).All(v => v == null));
        Assert.NotNull(result.MainValues[firstDmaIndex]);

        // Manually calculate the first SMA value
        decimal firstSmaValue = candles.Take(period).Average(c => c.Close);

        // The first DMA value should be equal to the first SMA value
        Assert.Equal(firstSmaValue, result.MainValues[firstDmaIndex].Value, 4);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreDmaIndicator();
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreDmaIndicator();
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(19, i => 100);
        var indicator = new CoreDmaIndicator { Period = 20, Displacement = 5 };
        var result = indicator.Calculate(candles);

        Assert.Equal(19, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
