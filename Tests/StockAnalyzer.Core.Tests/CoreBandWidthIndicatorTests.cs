using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreBandWidthIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, decimal> priceFunc)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceFunc(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price, price, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithStableData_ReturnsLowBandwidth()
    {
        var candles = GenerateCandleData(30, i => 100); // Stable price
        var indicator = new CoreBandWidthIndicator { Period = 20 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);
        Assert.True(result.MainValues.Take(19).All(v => v == null));
        Assert.NotNull(result.MainValues[19]);
        // For stable data, std dev is 0, so bandwidth should be 0.
        Assert.Equal(0, result.MainValues[19].Value, 4);
        Assert.True(result.MainValues.Skip(19).All(v => v.Value == 0));
    }

    [Fact]
    public void Calculate_WithVolatileData_ReturnsHigherBandwidth()
    {
        var candles = GenerateCandleData(30, i => 100 + (decimal)Math.Sin(i) * 5); // Volatile
        var indicator = new CoreBandWidthIndicator { Period = 20 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);
        Assert.True(result.MainValues.Take(19).All(v => v == null));
        Assert.NotNull(result.MainValues[19]);
        // For volatile data, bandwidth should be > 0
        Assert.True(result.MainValues[19].Value > 0);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreBandWidthIndicator();
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreBandWidthIndicator();
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(19, i => 100);
        var indicator = new CoreBandWidthIndicator { Period = 20 };
        var result = indicator.Calculate(candles);

        Assert.Equal(19, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
