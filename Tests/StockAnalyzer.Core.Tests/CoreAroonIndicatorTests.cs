using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreAroonIndicatorTests
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
        var indicator = new CoreAroonIndicator { Period = 3 };
        var candles = CreateTestCandles(new[]
        {
            (10m, 12m, 9m, 11m), // High=12, Low=9
            (11m, 11m, 8m, 10m), // High=11, Low=8
            (10m, 13m, 9m, 12m), // High=13, Low=9
            (12m, 14m, 11m, 13m),// High=14, Low=11
            (13m, 13m, 10m, 11m) // High=13, Low=10
        });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);

        // At index 3: Period [1,2,3]. High=14(idx 3), Low=8(idx 1).
        // DaysSinceHigh=0, DaysSinceLow=2
        // Up = (3-0)/3 * 100 = 100
        // Down = (3-2)/3 * 100 = 33.33
        Assert.Equal(100m, Math.Round(indicator.AroonUp[3]!.Value, 2));
        Assert.Equal(33.33m, Math.Round(indicator.AroonDown[3]!.Value, 2));
        Assert.Equal(66.67m, Math.Round(indicator.Values[3]!.Value, 2)); // Oscillator = Up - Down
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreAroonIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.AroonUp);
        Assert.Empty(indicator.AroonDown);
    }
}
