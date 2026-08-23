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
        Assert.True(indicator.Values.Last() > 99); // Strong uptrend should be close to 100
    }

    [Fact]
    public void CalculateSeries_WithDirectSeries_MatchesCandleCalculation()
    {
        var indicator = new CoreRsiIndicator { Period = 5 };
        var series = new List<decimal?> { 10m, 11m, 12m, 13m, 14m, 15m, 16m };

        indicator.CalculateSeries(series);

        Assert.NotNull(indicator.Values.Last());
        Assert.True(indicator.Values.Last() > 99);
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
        Assert.True(indicator.Values.Last() > 99m);
    }

    [Fact]
    public void Calculate_WithDowntrend_ReturnsLowRsi()
    {
        var indicator = new CoreRsiIndicator { Period = 5 };
        var candles = CreateTestCandles(new decimal[] { 16, 15, 14, 13, 12, 11, 10 });

        indicator.Calculate(candles);

        Assert.NotNull(indicator.Values.Last());
        Assert.True(indicator.Values.Last() < 50); // Downtrend = RSI < 50
        Assert.Equal(0m, indicator.Values.Last());
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreRsiIndicator { Period = 14 };
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
