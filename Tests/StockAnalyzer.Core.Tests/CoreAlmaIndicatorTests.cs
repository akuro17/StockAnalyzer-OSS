using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreAlmaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, decimal startPrice, decimal increment)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = startPrice + (i * increment);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price, price, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(20, 100, 1);
        int period = 9;
        var indicator = new CoreAlmaIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period - 1).All(v => v == null));

        // For a simple increasing series, ALMA should be a smooth average,
        // lagging the price but less so than a simple moving average.
        Assert.NotNull(result.MainValues[period - 1]);
        Assert.True(result.MainValues[period - 1] > candles[0].Close);
        Assert.True(result.MainValues[period - 1] < candles[period - 1].Close);

        // Check the last value
        Assert.NotNull(result.MainValues[19]);
        Assert.True(result.MainValues[19] > candles[19 - period].Close);
        Assert.True(result.MainValues[19] < candles[19].Close);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreAlmaIndicator();
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreAlmaIndicator();
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(8, 100, 1);
        var indicator = new CoreAlmaIndicator { Period = 9 };
        var result = indicator.Calculate(candles);

        Assert.Equal(8, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
