using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;

namespace StockAnalyzer.Core.Tests;

public class PriceSourceMovingAverageTests
{
    private static List<CoreCandleData> CreateTestCandles(int count = 10)
    {
        var startDate = DateTime.Today;
        return Enumerable.Range(0, count).Select(i => new CoreCandleData(
            startDate.AddDays(i),
            Open: 10m + i,
            High: 50m + i,
            Low: 5m + i,
            Close: 20m + i,
            Volume: 1000
        )).ToList();
    }

    [Fact]
    public void CoreWmaIndicator_WithPriceSourceHigh_CalculatesUsingHighPrices()
    {
        var indicatorClose = new CoreWmaIndicator { Period = 3, PriceSource = PriceType.Close };
        var indicatorHigh = new CoreWmaIndicator { Period = 3, PriceSource = PriceType.High };

        var candles = CreateTestCandles(5);

        indicatorClose.Calculate(candles);
        indicatorHigh.Calculate(candles);

        Assert.NotNull(indicatorClose.Values[2]);
        Assert.NotNull(indicatorHigh.Values[2]);

        // High prices are 50, 51, 52 vs Close prices 20, 21, 22
        // WMA with High should be significantly higher than WMA with Close
        Assert.True(indicatorHigh.Values[2] > indicatorClose.Values[2]);
    }

    [Fact]
    public void CoreHmaIndicator_WithPriceSourceOpen_CalculatesUsingOpenPrices()
    {
        var indicatorClose = new CoreHmaIndicator { Period = 4, PriceSource = PriceType.Close };
        var indicatorOpen = new CoreHmaIndicator { Period = 4, PriceSource = PriceType.Open };

        var candles = CreateTestCandles(10);

        indicatorClose.Calculate(candles);
        indicatorOpen.Calculate(candles);

        var lastCloseVal = indicatorClose.Values.Last();
        var lastOpenVal = indicatorOpen.Values.Last();

        Assert.NotNull(lastCloseVal);
        Assert.NotNull(lastOpenVal);
        Assert.NotEqual(lastCloseVal.Value, lastOpenVal.Value);
    }

    [Fact]
    public void CoreSmmaIndicator_WithPriceSourceLow_CalculatesUsingLowPrices()
    {
        var indicatorClose = new CoreSmmaIndicator { Period = 3, PriceSource = PriceType.Close };
        var indicatorLow = new CoreSmmaIndicator { Period = 3, PriceSource = PriceType.Low };

        var candles = CreateTestCandles(5);

        indicatorClose.Calculate(candles);
        indicatorLow.Calculate(candles);

        Assert.NotNull(indicatorClose.Values[2]);
        Assert.NotNull(indicatorLow.Values[2]);
        Assert.True(indicatorLow.Values[2] < indicatorClose.Values[2]);
    }
}
