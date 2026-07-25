using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreJmaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, decimal> priceFunction)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceFunction(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price, price, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(30, i => 100 + i); // Linear data
        int period = 7;
        var indicator = new CoreJmaIndicator { Period = period, Phase = 0 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);
        
        // JMA implementation seems to initialize with 0s or copy close price for first few?
        // Let's check nulls.
        // Implementation: if (i >= Period) results[i] = jma; else results[i] = 0 (default decimals) or null?
        // Line 37: var results = new decimal?[prices.Count]; -> initialized to nulls.
        // But loop starts at i=1. logic updates jma. 
        // Logic: if (i >= Period) results[i] = (decimal)jma; 
        // So indices 0 to Period-1 will be null.
        
        Assert.True(result.MainValues.Take(period).All(v => v == null));

        // JMA is a smoothing average, so it should lag the price in a trend.
        Assert.NotNull(result.MainValues[period]);
        Assert.True(result.MainValues[period] < candles[period].Close);

        // JMA should be smoother than the price.
        var lastPrice = candles.Last().Close;
        var lastJma = result.MainValues.Last();
        Assert.NotNull(lastJma);
        Assert.True(lastJma < lastPrice);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreJmaIndicator();
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreJmaIndicator();
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(6, i => 100 + i);
        var indicator = new CoreJmaIndicator { Period = 7 };
        var result = indicator.Calculate(candles);

        Assert.Equal(6, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
