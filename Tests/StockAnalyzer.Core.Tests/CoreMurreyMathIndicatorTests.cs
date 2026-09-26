using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Mathematical;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreMurreyMathIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<(decimal high, decimal low)> prices)
    {
        var startDate = DateTime.Today;
        return prices.Select((p, i) => new CoreCandleData(
            startDate.AddDays(i), p.low, p.high, p.low, p.high, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectLevels()
    {
        var indicator = new CoreMurreyMathIndicator { Period = 4 };
        var candles = CreateTestCandles(new[]
        {
            (10m, 8m),
            (12m, 10m),
            (11m, 9m),
            (14m, 12m),
            (15m, 13m)
        });

        indicator.Calculate(candles);

        // Last window is candles[1] to candles[4]
        // Highest high = 15, Lowest low = 9
        // Range = 15 - 9 = 6
        // Interval = 6 / 8 = 0.75
        var last = 4;
        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[2]);
        Assert.Equal(9 + 0.75m * 0, indicator.Level0_8[last]);
        Assert.Equal(9 + 0.75m * 1, indicator.Level1_8[last]);
        Assert.Equal(9 + 0.75m * 2, indicator.Level2_8[last]);
        Assert.Equal(9 + 0.75m * 3, indicator.Level3_8[last]);
        Assert.Equal(9 + 0.75m * 4, indicator.Level4_8[last]);
        Assert.Equal(9 + 0.75m * 5, indicator.Level5_8[last]);
        Assert.Equal(9 + 0.75m * 6, indicator.Level6_8[last]);
        Assert.Equal(9 + 0.75m * 7, indicator.Level7_8[last]);
        Assert.Equal(15, indicator.Level8_8[last]);

        // Check main values collection
        Assert.Equal(indicator.Level4_8[last], indicator.Values[last]);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNulls()
    {
        var indicator = new CoreMurreyMathIndicator { Period = 5 };
        var candles = CreateTestCandles(new[] { (10m, 8m), (12m, 10m) });

        indicator.Calculate(candles);

        Assert.Equal(2, indicator.Values.Count);
        Assert.All(indicator.Values, Assert.Null);
        Assert.All(indicator.Level0_8, Assert.Null);
        Assert.All(indicator.Level8_8, Assert.Null);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreMurreyMathIndicator { Period = 5 };
        indicator.Calculate(new List<CoreCandleData>());

        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.Level0_8);
    }
}
