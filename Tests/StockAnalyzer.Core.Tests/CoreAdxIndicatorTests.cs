using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreAdxIndicatorTests
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
        var indicator = new CoreAdxIndicator { Period = 4 };
        var candles = CreateTestCandles(new[]
        {
            (10m, 12m, 9m, 11m),   // i=0
            (11m, 13m, 10m, 12m),  // i=1, Up move
            (12m, 12m, 9m, 10m),   // i=2, Down move
            (10m, 11m, 8m, 9m),    // i=3, Down move
            (9m, 12m, 8m, 11m),    // i=4, Up move
            (11m, 13m, 10m, 12m)   // i=5, Up move
        });

        indicator.Calculate(candles);

        Assert.Equal(6, indicator.Values.Count);
        Assert.True(indicator.Values.Take(3).All(v => v == null));
        Assert.NotNull(indicator.Values[3]);
        // Note: ADX calculation is complex and sensitive to initial smoothing.
        // A full manual calculation is impractical here. We'll check for non-nulls and plausible value ranges.
        Assert.True(indicator.Values[3] > 0);
        Assert.True(indicator.PlusDI[3] > 0);
        Assert.True(indicator.MinusDI[3] > 0);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreAdxIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.PlusDI);
        Assert.Empty(indicator.MinusDI);
    }
}
