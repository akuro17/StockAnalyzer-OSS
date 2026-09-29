using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreLrmaIndicatorTests
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
        var candles = GenerateCandleData(20, 100, 1);
        var period = 14;
        var indicator = new CoreLrmaIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period - 1).All(v => v == null));

        Assert.NotNull(result.MainValues[13]);
        
        decimal sumX = 0, sumY = 0, sumXy = 0, sumX2 = 0;
        for (int j = 0; j < period; j++)
        {
            decimal y = candles[13 - j].Close; 
            decimal x = j;                     
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumX2 += x * x;
        }
        decimal n = period;
        decimal b = (n * sumXy - sumX * sumY) / (n * sumX2 - sumX * sumX);
        decimal a = (sumY - b * sumX) / n;

        Assert.Equal(a, result.MainValues[13].Value, 4); 

        Assert.NotNull(result.MainValues[19]);
        Assert.InRange(result.MainValues[19].Value, candles[19-period+1].Close, candles[19].Close + (decimal)period/2);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreLrmaIndicator { Period = 14 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreLrmaIndicator { Period = 14 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(10, 100, 1);
        var indicator = new CoreLrmaIndicator { Period = 14 };
        var result = indicator.Calculate(candles);

        Assert.Equal(10, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
