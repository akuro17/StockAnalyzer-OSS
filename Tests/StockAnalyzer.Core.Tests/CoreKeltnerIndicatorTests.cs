using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreKeltnerIndicatorTests
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
    public void Calculate_WithValidData_ReturnsTupleOfLists()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 100+i, 102+i, 98+i, 101+i, 100));
        
        var indicator = new CoreKeltnerChannelIndicator { EmaPeriod = 20, AtrPeriod = 10, Multiplier = 2 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        
        var middle = result.MainValues;
        var upper = indicator.UpperBand;
        var lower = indicator.LowerBand;

        Assert.NotNull(middle);
        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.Equal(30, middle.Count);
        Assert.Equal(30, upper.Count);
        Assert.Equal(30, lower.Count);

        Assert.True(middle.Take(19).All(v => v == null));
        Assert.NotNull(middle[19]);
    }

    [Fact]
    public void Calculate_WithStableData_BandsAreEquidistant()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 100, 101, 99, 100, 100)); // ATR = 2
        
        var indicator = new CoreKeltnerChannelIndicator { EmaPeriod = 20, AtrPeriod = 10, Multiplier = 2 };
        var result = indicator.Calculate(candles);
        
        var middle = result.MainValues;
        var upper = indicator.UpperBand;
        var lower = indicator.LowerBand;

        Assert.NotNull(middle[25]);
        Assert.NotNull(upper[25]);
        Assert.NotNull(lower[25]);

        Assert.Equal(100, middle[25].Value, 4);
        Assert.Equal(100 + 2 * 2, upper[25].Value, 4); 
        Assert.Equal(100 - 2 * 2, lower[25].Value, 4); 
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyLists()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreKeltnerChannelIndicator();
        var result = indicator.Calculate(candles);
        
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
        Assert.Empty(indicator.UpperBand);
        Assert.Empty(indicator.LowerBand);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreKeltnerChannelIndicator();
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullLists()
    {
        var candles = GenerateCandleData(19, i => new CoreCandleData(DateTime.Today,100,100,100,100,100));
        var indicator = new CoreKeltnerChannelIndicator { EmaPeriod = 20, AtrPeriod = 10, Multiplier = 2 };
        var result = indicator.Calculate(candles);

        Assert.True(result.MainValues.All(v => v == null));
        Assert.True(indicator.UpperBand.All(v => v == null));
        Assert.True(indicator.LowerBand.All(v => v == null));
    }
}
