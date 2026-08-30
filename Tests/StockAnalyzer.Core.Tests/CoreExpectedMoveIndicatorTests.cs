using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreExpectedMoveIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<(decimal open, decimal high, decimal low, decimal close)> prices)
    {
        var startDate = DateTime.Today;
        return prices.Select((p, i) => new CoreCandleData(
            startDate.AddDays(i), p.open, p.high, p.low, p.close, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectValues()
    {
        var indicator = new CoreExpectedMoveIndicator { Period = 3, Multiplier = 2.0m };
        var candles = CreateTestCandles(new[]
        {
            (9m, 10m, 8m, 9m),
            (9m, 11m, 9m, 10m),
            (10m, 12m, 10m, 11m),
            (11m, 15m, 11m, 14m),
            (14m, 13m, 10m, 12m)
        });

        indicator.Calculate(candles);
        var values = indicator.Values.Select(v => v.HasValue ? Math.Round(v.Value, 4) : (decimal?)null).ToList();

        // ATR Calculation:
        // TR1: 10-8=2
        // TR2: max(11-9, abs(11-9), abs(9-9))=2
        // TR3: max(12-10, abs(12-10), abs(10-10))=2
        // TR4: max(15-11, abs(15-11), abs(11-11))=4
        // TR5: max(13-10, abs(13-14), abs(10-14))=4
        //
        // ATR1: null
        // ATR2: null
        // ATR3: (2+2+4)/3 = 2.6667 (using TRs from candles 2,3,4)
        // ATR4: (2.6667 * 2 + 4) / 3 = 3.1111
        // Note: The ATR implementation has a slight difference in how it initializes.
        // It calculates TR from index 1.
        // TRs: [2, 2, 4, 4] (for candles 1-2, 2-3, 3-4, 4-5)
        // Values:
        // [0]: null (by default)
        // [1]: null (i < Period)
        // [2]: null (i < Period)
        // [3]: (2+2+4)/3 = 2.6667
        // [4]: (2.6667 * 2 + 4) / 3 = 3.1111

        Assert.Equal(5, values.Count);
        Assert.Null(values[0]);
        Assert.Null(values[1]);
        Assert.Null(values[2]);
        Assert.Equal(Math.Round(2.6666666m * 2.0m, 4), values[3]);
        Assert.Equal(Math.Round(3.1111111m * 2.0m, 4), values[4]);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNulls()
    {
        var indicator = new CoreExpectedMoveIndicator { Period = 5 };
        var candles = CreateTestCandles(new[]
        {
            (9m, 10m, 8m, 9m),
            (9m, 11m, 9m, 10m),
            (10m, 12m, 10m, 11m)
        });

        indicator.Calculate(candles);

        Assert.Equal(3, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreExpectedMoveIndicator { Period = 5 };

        indicator.Calculate(new List<CoreCandleData>());

        Assert.Empty(indicator.Values);
    }
}
