using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreSmaIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectSma()
    {
        var indicator = new CoreSmaIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18 });

        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(12m, indicator.Values[2]);
        Assert.Equal(14m, indicator.Values[3]);
        Assert.Equal(16m, indicator.Values[4]);
    }

    [Fact]
    public void CalculateSeries_WithDirectSeries_ReturnsIdenticalValues()
    {
        var indicator = new CoreSmaIndicator { Period = 3 };
        var series = new List<decimal?> { 10m, 12m, 14m, 16m, 18m };

        indicator.CalculateSeries(series);

        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(12m, indicator.Values[2]);
        Assert.Equal(14m, indicator.Values[3]);
        Assert.Equal(16m, indicator.Values[4]);
    }

    [Fact]
    public void Calculate_WithPriceSource_ExtractsSpecifiedPriceType()
    {
        var indicator = new CoreSmaIndicator { Period = 2, PriceSource = PriceType.Open };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 100m, 150m, 90m, 120m, 100),
            new(DateTime.Today.AddDays(1), 200m, 250m, 190m, 220m, 100),
        };

        indicator.Calculate(candles);

        Assert.Equal(2, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Equal(150m, indicator.Values[1]); // (100 + 200) / 2
    }

    [Fact]
    public void CalculateSeries_WithDynamicPeriods_AppliesDynamicSma()
    {
        var indicator = new CoreSmaIndicator { Period = 5 };
        var series = new List<decimal?> { 10m, 12m, 14m, 16m, 18m, 20m };
        var dynamicPeriods = new List<decimal?> { 5m, 5m, 5m, 5m, 5m, 5m };

        indicator.CalculateSeries(series, dynamicPeriods);

        Assert.Equal(6, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        Assert.Null(indicator.Values[3]);
        Assert.Equal(14m, indicator.Values[4]); // (10+12+14+16+18)/5 = 14
        Assert.Equal(16m, indicator.Values[5]); // (12+14+16+18+20)/5 = 16
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNulls()
    {
        var indicator = new CoreSmaIndicator { Period = 5 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14 });

        indicator.Calculate(candles);

        Assert.Equal(3, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreSmaIndicator { Period = 5 };

        indicator.Calculate(new List<CoreCandleData>());

        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_WithLargeData_ReturnsCorrectSma()
    {
        var indicator = new CoreSmaIndicator { Period = 3 };
        var prices = new List<decimal>();
        for(int i = 0; i < 15000; i++) prices.Add((decimal)i);
        var candles = CreateTestCandles(prices);

        indicator.Calculate(candles);

        Assert.Equal(15000, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(1m, indicator.Values[2]);
        Assert.Equal(14998m, indicator.Values[14999]);
        Assert.Equal(5000m, indicator.Values[5001]);
        Assert.Equal(10000m, indicator.Values[10001]);
    }
}
