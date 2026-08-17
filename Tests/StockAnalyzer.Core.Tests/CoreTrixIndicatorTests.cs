using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreTrixIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithSufficientData_CalculatesCorrectly()
    {
        var indicator = new CoreTrixIndicator { Period = 3 };
        // Need enough data for triple EMA + 1 for ROC
        var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });

        indicator.Calculate(candles);

        Assert.Equal(candles.Count, indicator.Values.Count);
        // TRIX is a complex calculation involving triple-smoothed EMA.
        // We will verify that the values are calculated and seem reasonable.
        // The first few values will be null
        Assert.True(indicator.Values.Take(indicator.Period * 3 - 3).All(v => v == null));
        Assert.NotNull(indicator.Values.Last());
        // In a steady uptrend, TRIX should be positive.
        Assert.True(indicator.Values.Last() > 0);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreTrixIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
