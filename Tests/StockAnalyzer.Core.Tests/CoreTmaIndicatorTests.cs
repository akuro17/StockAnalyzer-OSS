using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreTmaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, decimal startPrice)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CoreCandleData(date.AddDays(i), startPrice + i, startPrice + i + 1, startPrice + i - 1, startPrice + i, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(20, 100);
        var period = 14;
        var indicator = new CoreTmaIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, result.MainValues.Count);
        // We expect the first few values to be null
        Assert.True(result.MainValues.Take(12).All(v => v == null));

        Assert.NotNull(result.MainValues[13]);
        Assert.True(result.MainValues[13] > 0);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreTmaIndicator { Period = 14 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreTmaIndicator { Period = 14 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(10, 100);
        var indicator = new CoreTmaIndicator { Period = 14 };
        var result = indicator.Calculate(candles);

        Assert.Equal(10, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
