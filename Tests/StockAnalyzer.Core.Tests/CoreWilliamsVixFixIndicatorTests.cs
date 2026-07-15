using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreWilliamsVixFixIndicatorTests
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
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 100-i, 102-i, 98-i, 100-i, 1000));
        var indicator = new CoreWilliamsVixFixIndicator { Period = 22 };
        var result = indicator.Calculate(candles).MainValues;

        Assert.Equal(30, result.Count);
        Assert.True(result.Take(21).All(v => v == null));
        Assert.NotNull(result[21]);
    }

    [Fact]
    public void Calculate_WithLowAtHighestClose_ReturnsZero()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 100, 105, 95, 100, 1000));
        // Set the last low to be the highest close of the period
        candles[29] = new CoreCandleData(DateTime.Today.AddDays(29), 100, 105, 100, 100, 1000);

        var indicator = new CoreWilliamsVixFixIndicator { Period = 22 };
        var result = indicator.Calculate(candles).MainValues;

        Assert.NotNull(result[29]);
        Assert.Equal(0, result[29].Value, 4);
    }

    [Fact]
    public void Calculate_WithLowFarBelowHighestClose_ReturnsHighValue()
    {
        var candles = GenerateCandleData(30, i =>
            new CoreCandleData(DateTime.Today.AddDays(i), 100, 105, 95, 100, 1000));
        // Set the last low to be much lower
        candles[29] = new CoreCandleData(DateTime.Today.AddDays(29), 50, 55, 45, 50, 1000);

        var indicator = new CoreWilliamsVixFixIndicator { Period = 22 };
        var result = indicator.Calculate(candles).MainValues;

        Assert.NotNull(result[29]);
        // (100 - 45) / 100 * 100 = 55
        Assert.Equal(55, result[29].Value, 4);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreWilliamsVixFixIndicator { Period = 22 };
        var result = indicator.Calculate(candles).MainValues;
        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsEmptyList()
    {
        var indicator = new CoreWilliamsVixFixIndicator { Period = 22 };
        var result = indicator.Calculate(null).MainValues;
        Assert.Empty(result); // Actually calling with null returns failure, maybe check return?
        // Wait, failing null inputs usually return Failure result.
        // But Values property might be empty or null?
        // CoreIndicatorBase initializes _values = new().
        // If Calculate returns Failure, it might not populate _values?
        // Let's check CoreIndicatorBase logic for null input.
        // It returns IndicatorResult.Failure("Candles data is null.").
        // Does it clear _values? Yes.
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(21, i => new CoreCandleData(DateTime.Today,100,100,100,100,100));
        var indicator = new CoreWilliamsVixFixIndicator { Period = 22 };
        var result = indicator.Calculate(candles).MainValues;

        Assert.Equal(21, result.Count);
        Assert.True(result.All(v => v == null));
    }
}
