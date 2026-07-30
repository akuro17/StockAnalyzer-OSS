using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreSmaIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectSma()
    {
        var indicator = new CoreSmaIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18 });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(12m, indicator.Values[2]);
        Assert.Equal(14m, indicator.Values[3]);
        Assert.Equal(16m, indicator.Values[4]);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNulls()
    {
        var indicator = new CoreSmaIndicator { Period = 5 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

        indicator.Calculate(candles);

        Assert.Equal(3, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreSmaIndicator { Period = 5 };

        indicator.Calculate(new List<CoreCandleData>());

        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_WithLargeData_ReturnsCorrectSma()
    {
        var indicator = new CoreSmaIndicator { Period = 3 };
        var prices = new List<decimal>();
        for(int i = 0; i < 15000; i++) prices.Add((decimal)i);
        var candles = CreateTestCandles(prices);

        // OptimizationContext context defaults might trigger threshold. Let's explicitly trigger parallel if possible, or just rely on default 10000 threshold.
        indicator.Calculate(candles);

        Assert.Equal(15000, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        // SMA of 0, 1, 2 is 1 (index 2)
        Assert.Equal(1m, indicator.Values[2]);
        // SMA of 14997, 14998, 14999 is 14998
        Assert.Equal(14998m, indicator.Values[14999]);
        
        // Let's verify middle boundary chunks
        Assert.Equal(5000m, indicator.Values[5001]);
        Assert.Equal(10000m, indicator.Values[10001]);
    }
}
