using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreStochasticIndicatorTests
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
        var indicator = new CoreStochasticIndicator { KPeriod = 3, DPeriod = 2, Smooth = 1 };
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

        Assert.Equal(80m, Math.Round(indicator.PercentK[2]!.Value, 2));
        Assert.Equal(80m, Math.Round(indicator.PercentK[3]!.Value, 2));
        Assert.Equal(80m, Math.Round(indicator.PercentK[4]!.Value, 2));

        Assert.Null(indicator.PercentD[0]);
        Assert.Null(indicator.PercentD[1]);
        Assert.Null(indicator.PercentD[2]);
        Assert.Equal(80m, Math.Round(indicator.PercentD[3]!.Value, 2));
        Assert.Equal(80m, Math.Round(indicator.PercentD[4]!.Value, 2));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreStochasticIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.PercentK);
        Assert.Empty(indicator.PercentD);
    }
}
