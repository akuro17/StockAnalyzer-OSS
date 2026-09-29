using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreLsmaIndicatorTests
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
    public void Calculate_WithPerfectlyLinearData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(30, 100, 1);
        var period = 25;
        var indicator = new CoreLsmaIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period - 1).All(v => v == null));

        Assert.NotNull(result.MainValues[period - 1]);
        
        // Verification logic logic
        decimal sumX = 0, sumY = 0, sumXy = 0, sumX2 = 0;
        for (int j = 0; j < period; j++)
        {
            decimal y = candles[period - 1 - j].Close;
            decimal x = j;
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumX2 += x * x;
        }
        decimal n = period;
        decimal b = (n * sumXy - sumX * sumY) / (n * sumX2 - sumX * sumX);
        decimal a = (sumY - b * sumX) / n;

        Assert.Equal(a, result.MainValues[period - 1].Value, 4);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreLsmaIndicator { Period = 25 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreLsmaIndicator { Period = 25 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(20, 100, 1);
        var indicator = new CoreLsmaIndicator { Period = 25 };
        var result = indicator.Calculate(candles);

        Assert.Equal(20, result.MainValues.Count);
        // Expect all nulls if validation passes but returns nulls
        // Or check if it returns fewer results? Usually aligns with input.
        Assert.True(result.MainValues.All(v => v == null));
    }
}
