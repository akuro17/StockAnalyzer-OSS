using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreDmiIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, CoreCandleData> candleFunc)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(candleFunc(i));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsResult()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 100+i, 102+i, 98+i, 101+i, 100));
        var indicator = new CoreDmiIndicator { Period = 14 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        
        var plusDi = result.GetSeries("PlusDI");
        var minusDi = result.GetSeries("MinusDI");

        Assert.NotNull(plusDi);
        Assert.NotNull(minusDi);
        Assert.Equal(30, plusDi.Count);
        Assert.Equal(30, minusDi.Count);

        Assert.True(plusDi.Take(13).All(v => v == null));
        Assert.NotNull(plusDi[13]);
    }

    [Fact]
    public void Calculate_WithStrongUptrend_PlusDIGreaterThanMinusDI()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 100 + i*2, 102 + i*2, 98 + i*2, 101 + i*2, 100));
        var indicator = new CoreDmiIndicator { Period = 14 };
        var result = indicator.Calculate(candles);
        
        var plusDi = result.GetSeries("PlusDI");
        var minusDi = result.GetSeries("MinusDI");

        Assert.NotNull(plusDi[20]);
        Assert.NotNull(minusDi[20]);
        Assert.True(plusDi[20] > minusDi[20]);
    }

    [Fact]
    public void Calculate_WithStrongDowntrend_MinusDIGreaterThanPlusDI()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 200 - i*2, 202 - i*2, 198 - i*2, 201 - i*2, 100));
        var indicator = new CoreDmiIndicator { Period = 14 };
        var result = indicator.Calculate(candles);
        
        var plusDi = result.GetSeries("PlusDI");
        var minusDi = result.GetSeries("MinusDI");

        Assert.NotNull(plusDi[20]);
        Assert.NotNull(minusDi[20]);
        Assert.True(minusDi[20] > plusDi[20]);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyLists()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreDmiIndicator();
        var result = indicator.Calculate(candles);
        
        if (result.IsSuccessful)
        {
             Assert.Empty(result.MainValues);
        }
        else
        {
            // If empty data is considered failure or returns empty, verify explicitly.
            // CoreIndicatorBase usually checks count and might return success with empty or failure.
            // Let's assume empty result is checked via MainValues if success, or we accept IsSuccessful=false
            // If IsSuccessful is true, MainValues must be empty.
            Assert.Empty(result.MainValues);
        }
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreDmiIndicator();
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }
}
