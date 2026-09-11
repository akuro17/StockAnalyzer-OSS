using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreEmaIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectEma()
    {
        var indicator = new CoreEmaIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18 });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(12m, indicator.Values[2]); // First EMA = SMA
        Assert.Equal(14m, indicator.Values[3]); // (16-12) * 0.5 + 12 = 14
        Assert.Equal(16m, indicator.Values[4]); // (18-14) * 0.5 + 14 = 16
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreEmaIndicator { Period = 5 };
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
