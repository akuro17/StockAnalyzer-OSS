using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreGmaIndicatorTests
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
    public void Calculate_WithValidData_ReturnsCorrectNumberOfLists()
    {
        var candles = GenerateCandleData(70, 100, 1);
        var result = CoreGmaIndicator.Calculate(candles);

        int expectedCount = CoreGmaIndicator.ShortTermPeriods.Length + CoreGmaIndicator.LongTermPeriods.Length;
        Assert.Equal(expectedCount, result.Count);
    }

    [Fact]
    public void Calculate_WithValidData_AllListsHaveCorrectLength()
    {
        var candles = GenerateCandleData(70, 100, 1);
        var result = CoreGmaIndicator.Calculate(candles);

        foreach (var list in result)
        {
            Assert.Equal(candles.Count, list.Count);
        }
    }

    [Fact]
    public void Calculate_WithValidData_ShortTermEmasAreCalculated()
    {
        var candles = GenerateCandleData(70, 100, 1);
        var result = CoreGmaIndicator.Calculate(candles);

        // Check the first short-term EMA (period 3)
        var ema3 = result[0];
        Assert.True(ema3.Take(2).All(v => v == null));
        Assert.NotNull(ema3[2]);
        Assert.Equal(101m, ema3[2].Value, 2); // SMA of first 3 prices (100, 101, 102) is 101
    }

    [Fact]
    public void Calculate_WithValidData_LongTermEmasAreCalculated()
    {
        var candles = GenerateCandleData(70, 100, 1);
        var result = CoreGmaIndicator.Calculate(candles);

        // Check the first long-term EMA (period 30)
        int shortTermCount = CoreGmaIndicator.ShortTermPeriods.Length;
        var ema30 = result[shortTermCount];
        Assert.True(ema30.Take(29).All(v => v == null));
        Assert.NotNull(ema30[29]);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var result = CoreGmaIndicator.Calculate(candles);
        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsEmptyList()
    {
        var result = CoreGmaIndicator.Calculate(null);
        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsListsWithNulls()
    {
        var candles = GenerateCandleData(20, 100, 1);
        var result = CoreGmaIndicator.Calculate(candles);

        int expectedCount = CoreGmaIndicator.ShortTermPeriods.Length + CoreGmaIndicator.LongTermPeriods.Length;
        Assert.Equal(expectedCount, result.Count);

        // Check a long-term EMA (period 30) which should be all nulls
        int shortTermCount = CoreGmaIndicator.ShortTermPeriods.Length;
        var ema30 = result[shortTermCount];
        Assert.True(ema30.All(v => v == null));
    }
}
