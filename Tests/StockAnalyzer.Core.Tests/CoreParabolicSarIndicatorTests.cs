using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreParabolicSarIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<(decimal open, decimal high, decimal low, decimal close)> values)
    {
        var startDate = DateTime.Today;
        return values.Select((v, i) => new CoreCandleData(
            startDate.AddDays(i), v.open, v.high, v.low, v.close, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectValues()
    {
        var indicator = new CoreParabolicSarIndicator();
        var candles = CreateTestCandles(new[]
        {
            (10m, 12m, 9m, 11m),
            (11m, 13m, 10m, 12m),
            (12m, 14m, 11m, 13m),
            (13m, 15m, 12m, 14m),
            (14m, 12m, 9m, 10m) // Sharper reversal
        });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.NotNull(indicator.Values[1]);
        // The SAR should be below the price in an uptrend
        Assert.True(indicator.Values[1] < candles[1].Low);
        Assert.True(indicator.Values[2] < candles[2].Low);
        Assert.True(indicator.Values[3] < candles[3].Low);
        // The SAR should be above the price in a downtrend
        Assert.True(indicator.Values[4] > candles[4].High);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreParabolicSarIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
