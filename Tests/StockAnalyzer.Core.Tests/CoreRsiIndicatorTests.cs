using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreRsiIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithUptrend_ReturnsHighRsi()
    {
        var indicator = new CoreRsiIndicator { Period = 5 };
        var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });

        indicator.Calculate(candles);

        Assert.NotNull(indicator.Values.Last());
        Assert.True(indicator.Values.Last() > 99); // Strong uptrend should be close to 100
    }

    [Fact]
    public void Calculate_WithDowntrend_ReturnsLowRsi()
    {
        var indicator = new CoreRsiIndicator { Period = 5 };
        var candles = CreateTestCandles(new decimal[] { 16, 15, 14, 13, 12, 11, 10 });

        indicator.Calculate(candles);

        Assert.NotNull(indicator.Values.Last());
        Assert.True(indicator.Values.Last() < 50); // Downtrend = RSI < 50
        // Specific value check for correctness
        Assert.Equal(0m, indicator.Values.Last());
    }


    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreRsiIndicator { Period = 14 };
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
