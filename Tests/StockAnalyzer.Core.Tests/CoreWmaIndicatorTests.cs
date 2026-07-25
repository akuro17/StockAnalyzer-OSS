using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreWmaIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectWma()
    {
        var indicator = new CoreWmaIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18 });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);

        Assert.NotNull(indicator.Values[2]);
        Assert.Equal(12.666667m, indicator.Values[2].Value, 6); // (10*1 + 12*2 + 14*3) / 6 = 12.666...

        Assert.NotNull(indicator.Values[3]);
        Assert.Equal(14.666667m, indicator.Values[3].Value, 6); // (12*1 + 14*2 + 16*3) / 6 = 14.666...

        Assert.NotNull(indicator.Values[4]);
        Assert.Equal(16.666667m, indicator.Values[4].Value, 6); // (14*1 + 16*2 + 18*3) / 6 = 16.666...
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreWmaIndicator { Period = 5 };
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
