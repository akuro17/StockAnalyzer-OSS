using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreMacdIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithSufficientData_CalculatesLines()
    {
        var indicator = new CoreMacdIndicator { FastPeriod = 3, SlowPeriod = 6, SignalPeriod = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });

        indicator.Calculate(candles);

        Assert.Equal(candles.Count, indicator.MacdLine.Count);
        Assert.Equal(candles.Count, indicator.Signal.Count);
        Assert.Equal(candles.Count, indicator.Histogram.Count);

        // Check that values are null until enough data is available
        Assert.Null(indicator.MacdLine[4]);
        Assert.NotNull(indicator.MacdLine[5]);

        // Signal line needs more data
        Assert.Null(indicator.Signal[6]);
        Assert.NotNull(indicator.Signal[7]);

        Assert.NotNull(indicator.Histogram.Last());
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreMacdIndicator();
        indicator.Calculate(new List<CoreCandleData>());

        Assert.Empty(indicator.MacdLine);
        Assert.Empty(indicator.Signal);
        Assert.Empty(indicator.Histogram);
    }
}
