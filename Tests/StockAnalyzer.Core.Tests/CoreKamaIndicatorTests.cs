using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreKamaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, decimal startPrice, Func<int, decimal> priceProgression)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceProgression(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price, price, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(30, 100, i => 100 + (decimal)Math.Sin(i / 5.0) * 5); 
        int period = 10;
        var indicator = new CoreKamaIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period).All(v => v == null));

        Assert.NotNull(result.MainValues[period]);
        Assert.True(result.MainValues[period] > 0);

        var priceRange = candles.Skip(period).Max(c => c.Close) - candles.Skip(period).Min(c => c.Close);
        var kamaRange = result.MainValues.Skip(period).Max(v => v.Value) - result.MainValues.Skip(period).Min(v => v.Value);
        Assert.True(kamaRange < priceRange);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreKamaIndicator { Period = 10 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreKamaIndicator { Period = 10 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(9, 100, i => 100 + i);
        var indicator = new CoreKamaIndicator { Period = 10 };
        var result = indicator.Calculate(candles);

        Assert.Equal(9, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
