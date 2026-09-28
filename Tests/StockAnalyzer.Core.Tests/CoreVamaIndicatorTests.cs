using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreVamaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, decimal> priceFunc)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceFunc(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price + 2, price - 2, price, 1000));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsResult()
    {
        var candles = GenerateCandleData(30, i => 100 + i);
        var indicator = new CoreVamaIndicator { Period = 14 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(result.MainValues);
        Assert.Equal(30, result.MainValues.Count);
        // VAMA requires data, so first few might be null or initial values?
        // Logic says first valid index is 2*Period - 2.
        Assert.Null(result.MainValues[0]);
    }

    [Fact]
    public void Calculate_WithDataFewerThanRequired_ReturnsNulls()
    {
        int period = 14;
        // Logic requires 2*Period - 1 candles.
        var candles = GenerateCandleData(period, i => 100 + i);
        var indicator = new CoreVamaIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreVamaIndicator { Period = 14 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }
}
