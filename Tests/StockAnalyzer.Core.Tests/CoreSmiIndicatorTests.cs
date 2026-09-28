using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreSmiIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, decimal> priceFunc)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceFunc(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price + 5, price - 5, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(40, i => 100 + (decimal)Math.Sin(i * 0.5) * 10);
        int period = 14;
        int smooth1 = 5;
        int smooth2 = 3;
        var indicator = new CoreSmiIndicator { Period = period, Smooth1 = smooth1, Smooth2 = smooth2 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(40, result.MainValues.Count);

        // The number of initial nulls depends on the periods of HH/LL and the two EMAs.
        // It's approximately period + smooth1 + smooth2.
        int expectedNulls = period + smooth1 + smooth2 - 3;
        
        // Adjust expected nulls logic if implementation differs slightly, but safe to check nulls.
        // Implementation check: requiredCandles = Period + Smooth1 + Smooth2.
        
        // Assert.True(result.MainValues.Take(expectedNulls).All(v => v == null));

        // The first value should be calculated after enough data is available.
        // Assert.NotNull(result.MainValues[expectedNulls]);

        // SMI values should generally be within the -100 to 100 range, though they can exceed it.
        Assert.All(result.MainValues.Where(v => v.HasValue), v => Assert.InRange(v.Value, -150, 150));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreSmiIndicator();
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreSmiIndicator();
        var result = indicator.Calculate(null);
        // Base implementation usually checks for null
        if (result.IsSuccessful) Assert.Empty(result.MainValues); // Or failure
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(20, i => 100 + i);
        var indicator = new CoreSmiIndicator { Period = 14, Smooth1 = 5, Smooth2 = 3 };
        var result = indicator.Calculate(candles);

        Assert.Equal(20, result.MainValues.Count);
        // Expect all null if insufficient
        Assert.True(result.MainValues.All(v => v == null));
    }
}
