using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreRsiIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithUptrend_ReturnsHighRsi()
    {
        var indicator = new CoreRsiIndicator { Period = 5 };
        var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });

        indicator.Calculate(candles);

        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(100.0m, indicator.Values.Last()); // Strong uptrend should be exactly 100
    }

    [Fact]
    public void CalculateSeries_WithDirectSeries_MatchesCandleCalculation()
    {
        var indicator = new CoreRsiIndicator { Period = 5 };
        var series = new List<decimal?> { 10m, 11m, 12m, 13m, 14m, 15m, 16m };

        indicator.CalculateSeries(series);

        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(100.0m, indicator.Values.Last());
    }

    [Fact]
    public void Calculate_WithPriceSource_ExtractsCorrectPriceType()
    {
        var indicator = new CoreRsiIndicator { Period = 3, PriceSource = PriceType.Open };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 10m, 20m, 5m, 15m, 100),
            new(DateTime.Today.AddDays(1), 11m, 20m, 5m, 15m, 100),
            new(DateTime.Today.AddDays(2), 12m, 20m, 5m, 15m, 100),
            new(DateTime.Today.AddDays(3), 13m, 20m, 5m, 15m, 100),
            new(DateTime.Today.AddDays(4), 14m, 20m, 5m, 15m, 100),
        };

        indicator.Calculate(candles);

        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(100.0m, indicator.Values.Last());
    }

    [Fact]
    public void Calculate_WithDowntrend_ReturnsLowRsi()
    {
        var indicator = new CoreRsiIndicator { Period = 5 };
        var candles = CreateTestCandles(new decimal[] { 16, 15, 14, 13, 12, 11, 10 });

        indicator.Calculate(candles);

        Assert.NotNull(indicator.Values.Last());
        Assert.True(indicator.Values.Last() < 50); // Downtrend = RSI < 50
        Assert.Equal(0.0m, indicator.Values.Last());
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreRsiIndicator { Period = 14 };
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_SpecTestCase_MatchesIndicatorsFormulasDoc()
    {
        // docs/indicators_formulas.md §6 test case:
        // Candles = [100, 102, 104, 103, 105, 104] (Period=3)
        // i=0: null
        // i=1: +2, gain=2, loss=0 (accumulate)
        // i=2: +2, gain=2, loss=0 (accumulate)
        // i=3: -1, gain=0, loss=1 -> AvgGain = 4/3, AvgLoss = 1/3, RS = 4, RSI = 80.0
        // i=4: +2, gain=2, loss=0 -> AvgGain = (4/3 * 2 + 2) / 3 = 14/9, AvgLoss = (1/3 * 2 + 0) / 3 = 2/9, RS = 7, RSI = 87.5
        var indicator = new CoreRsiIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 100m, 102m, 104m, 103m, 105m, 104m });

        indicator.Calculate(candles);

        Assert.Equal(6, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        Assert.Equal(80.0m, indicator.Values[3]);
        Assert.Equal(87.5m, indicator.Values[4]);
        Assert.NotNull(indicator.Values[5]);
        Assert.Equal(Math.Round(2800m / 41m, 4), Math.Round(indicator.Values[5]!.Value, 4));
    }

    [Fact]
    public void Calculate_WithFlatMarket_ReturnsNeutralFifty()
    {
        var indicator = new CoreRsiIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 100m, 100m, 100m, 100m, 100m });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        Assert.Equal(50.0m, indicator.Values[3]);
        Assert.Equal(50.0m, indicator.Values[4]);
    }
}

