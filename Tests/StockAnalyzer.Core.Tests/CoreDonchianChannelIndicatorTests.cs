using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreDonchianChannelIndicatorTests
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
        var indicator = new CoreDonchianChannelIndicator { Period = 3 };
        var candles = CreateTestCandles(new[]
        {
            (10m, 12m, 9m, 11m),
            (11m, 13m, 10m, 12m),
            (12m, 14m, 11m, 13m),
            (13m, 15m, 12m, 14m),
            (14m, 16m, 13m, 15m)
        });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);

        // At index 2: Period [0,1,2]. Highest High = 14, Lowest Low = 9.
        Assert.Equal(14m, indicator.UpperBand[2]);
        Assert.Equal(9m, indicator.LowerBand[2]);
        Assert.Equal(11.5m, indicator.Values[2]); // Middle Line

        // At index 3: Period [1,2,3]. Highest High = 15, Lowest Low = 10.
        Assert.Equal(15m, indicator.UpperBand[3]);
        Assert.Equal(10m, indicator.LowerBand[3]);
        Assert.Equal(12.5m, indicator.Values[3]);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreDonchianChannelIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.UpperBand);
        Assert.Empty(indicator.LowerBand);
    }
}
