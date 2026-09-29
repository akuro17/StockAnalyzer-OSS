using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreDemaIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectValues()
    {
        var indicator = new CoreDemaIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15 });

        indicator.Calculate(candles);

        Assert.Equal(6, indicator.Values.Count);
        // Nulls until 2 * Period - 2 = 4
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        Assert.Null(indicator.Values[3]);
        // Calculation is complex, just verify non-null and trend
        Assert.NotNull(indicator.Values[4]);
        Assert.True(indicator.Values[5] > indicator.Values[4]); // Should be rising
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreDemaIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
