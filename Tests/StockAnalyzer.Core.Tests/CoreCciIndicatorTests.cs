using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreCciIndicatorTests
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
        var indicator = new CoreCciIndicator { Period = 3 };
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
        Assert.NotNull(indicator.Values[2]);
        // Calculation:
        // TP = (High + Low + Close) / 3
        // TP1=10.67, TP2=11.67, TP3=12.67, TP4=13.67, TP5=14.67
        // At index 2: SMA_TP = Avg(10.67, 11.67, 12.67) = 11.67
        //             Mean Dev = Avg(|10.67-11.67|, |11.67-11.67|, |12.67-11.67|) = 0.67
        //             CCI = (12.67 - 11.67) / (0.015 * 0.67) = 100
        Assert.Equal(100m, Math.Round(indicator.Values[2]!.Value, 2));
        Assert.Equal(100m, Math.Round(indicator.Values[3]!.Value, 2));
        Assert.Equal(100m, Math.Round(indicator.Values[4]!.Value, 2));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreCciIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
