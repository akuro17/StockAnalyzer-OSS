using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreKeltnerChannelIndicatorTests
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
        var indicator = new CoreKeltnerChannelIndicator { EmaPeriod = 3, AtrPeriod = 3, Multiplier = 2 };
        var candles = CreateTestCandles(new[]
        {
            (10m, 12m, 9m, 10m),
            (10m, 12m, 9m, 11m),
            (11m, 13m, 10m, 12m),
            (12m, 14m, 11m, 13m),
            (13m, 15m, 12m, 14m)
        });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.True(indicator.Values.Take(2).All(v => v == null));

        // At index 3: EMA(11,12,13)=12. ATR=... Upper=12+2*ATR, Lower=12-2*ATR
        Assert.NotNull(indicator.Values[3]);
        Assert.NotNull(indicator.UpperBand[3]);
        Assert.NotNull(indicator.LowerBand[3]);
        Assert.True(indicator.UpperBand[3] > indicator.Values[3]);
        Assert.True(indicator.LowerBand[3] < indicator.Values[3]);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreKeltnerChannelIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.UpperBand);
        Assert.Empty(indicator.LowerBand);
    }
}
