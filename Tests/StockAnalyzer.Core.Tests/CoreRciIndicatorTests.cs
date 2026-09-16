using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreRciIndicatorTests
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
    public void Calculate_WithPerfectUptrend_ReturnsPositive100()
    {
        // In a perfect uptrend, time and price ranks are identical.
        // dSum should be 0, and RCI should be 100.
        var candles = GenerateCandleData(20, i => 100 + i);
        var indicator = new CoreRciIndicator { Period = 9 };
        indicator.Calculate(candles);
        var result = indicator.Values;

        Assert.Equal(20, result.Count);
        Assert.True(result.Take(8).All(v => v == null));
        Assert.NotNull(result[8]);
        Assert.Equal(100m, result[8].Value, 4);
        Assert.True(result.Skip(8).All(v => v.Value == 100m));
    }

    [Fact]
    public void Calculate_WithPerfectDowntrend_ReturnsNegative100()
    {
        // In a perfect downtrend, time and price ranks are perfectly inverted.
        // RCI should be -100.
        var candles = GenerateCandleData(20, i => 100 - i);
        var indicator = new CoreRciIndicator { Period = 9 };
        indicator.Calculate(candles);
        var result = indicator.Values;

        Assert.Equal(20, result.Count);
        Assert.True(result.Take(8).All(v => v == null));
        Assert.NotNull(result[8]);
        Assert.Equal(-100m, result[8].Value, 4);
        Assert.True(result.Skip(8).All(v => v.Value == -100m));
    }

    [Fact]
    public void Calculate_WithNoTrend_ReturnsNearZero()
    {
        var candles = GenerateCandleData(20, i => 100 + (decimal)Math.Sin(i)); // No clear trend
        var indicator = new CoreRciIndicator { Period = 9 };
        indicator.Calculate(candles);
        var result = indicator.Values;

        Assert.NotNull(result[9]);
        Assert.InRange(result[9].Value, -50, 50); // Expect a value around 0 for non-trending data
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreRciIndicator { Period = 9 };
        indicator.Calculate(candles);
        var result = indicator.Values;
        
        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsEmptyList()
    {
        var indicator = new CoreRciIndicator { Period = 9 };
        // Check if passing null throws or returns empty. 
        // CoreIndicatorBase checks if (candles == null) return; usually.
        // But let's check the implementation.
        // If we can't be sure, we'll assert that Values is empty (it is initialized empty).
        // indicator.Calculate(null); // This might throw if logic doesn't support null
        
        // Based on CoreRciIndicator logic I saw: if (candles == null || ...) return ...
        indicator.Calculate(null);
        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(8, i => 100 + i);
        var indicator = new CoreRciIndicator { Period = 9 };
        indicator.Calculate(candles);
        var result = indicator.Values;

        Assert.Equal(8, result.Count);
        Assert.True(result.All(v => v == null));
    }
}
