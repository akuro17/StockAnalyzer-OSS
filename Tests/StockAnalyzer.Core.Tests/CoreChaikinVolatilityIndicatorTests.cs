using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreChaikinVolatilityIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, Func<int, (decimal, decimal)> priceFunc)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var (high, low) = priceFunc(i);
            candles.Add(new CoreCandleData(date.AddDays(i), low, high, low, (high+low)/2, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        // Increasing volatility
        var candles = GenerateCandleData(30, i => (100 + i, 95));
        int period = 10;
        int rocPeriod = 10;
        var indicator = new CoreChaikinVolatilityIndicator { Period = period, RocPeriod = rocPeriod };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);

        // Initial nulls = period - 1 (for EMA) + rocPeriod
        int expectedNulls = period -1 + rocPeriod;
        // Wait, logically checking nulls.
        // Ema needs period items. So index period-1 is first valid.
        // ROC needs current EMA and EMA[i-rocPeriod]. 
        // So i-rocPeriod >= period-1 => i >= period-1 + rocPeriod.
        // So first valid index is period - 1 + rocPeriod.
        
        Assert.True(result.MainValues.Take(expectedNulls).All(v => v == null));

        Assert.NotNull(result.MainValues[expectedNulls]);

        // With consistently increasing spread, volatility should be positive
        Assert.True(result.MainValues[expectedNulls] > 0);
    }

    [Fact]
    public void Calculate_WithConstantVolatility_ReturnsZero()
    {
        var candles = GenerateCandleData(30, i => (105, 95)); // Constant 10-point spread
        var indicator = new CoreChaikinVolatilityIndicator { Period = 10, RocPeriod = 10 };
        var result = indicator.Calculate(candles);

        int expectedNulls = 10 -1 + 10;
        Assert.NotNull(result.MainValues[expectedNulls]);
        // With constant spread, the change (ROC) should be zero
        Assert.Equal(0, result.MainValues[expectedNulls].Value, 4);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreChaikinVolatilityIndicator();
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreChaikinVolatilityIndicator();
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(18, i => (100, 95));
        var indicator = new CoreChaikinVolatilityIndicator { Period = 10, RocPeriod = 10 };
        var result = indicator.Calculate(candles);

        Assert.Equal(18, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }
}
