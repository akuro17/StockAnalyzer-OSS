using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreArimaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandles(int count, decimal startPrice = 100m, int seed = 42)
    {
        var candles = new List<CoreCandleData>(count);
        var date = new DateTime(2023, 1, 1);
        var rand = new Random(seed);
        decimal price = startPrice;

        for (int i = 0; i < count; i++)
        {
            double drift = (rand.NextDouble() - 0.48) * 0.02;
            price *= (decimal)(1.0 + drift);
            if (price <= 1.0m) price = 1.0m;

            candles.Add(new CoreCandleData(
                Timestamp: date.AddDays(i),
                Open: price,
                High: price * 1.01m,
                Low: price * 0.99m,
                Close: price,
                Volume: 10000
            ));
        }

        return candles;
    }

    [Fact]
    public void Factory_IsRegistered_AndCreatesInstance()
    {
        var factory = IndicatorFactory.Default;
        Assert.True(factory.IsRegistered(IndicatorType.ARIMA));

        var indicator = factory.Create(IndicatorType.ARIMA);
        Assert.NotNull(indicator);
        var arima = Assert.IsType<CoreArimaIndicator>(indicator);
        Assert.True(arima.IsOverlay);
    }

    [Fact]
    public void Calculate_NullOrEmptyCandles_ReturnsFailureOrEmpty()
    {
        var indicator = new CoreArimaIndicator();

        var nullResult = indicator.Calculate(null!);
        Assert.False(nullResult.IsSuccessful);

        var emptyResult = indicator.Calculate(new List<CoreCandleData>());
        Assert.True(emptyResult.IsSuccessful);
        Assert.Empty(emptyResult.MainValues);
    }

    [Fact]
    public void Calculate_WarmupPeriod_ReturnsNulls()
    {
        var indicator = new CoreArimaIndicator();
        indicator.Configure(new CoreArimaParameter { Period = 15 });

        var candles = GenerateCandles(20);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, result.MainValues.Count);

        // Indices 0 to 13 must be null
        for (int i = 0; i < 14; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        // Index 14 (15th candle) onward must not be null
        for (int i = 14; i < 20; i++)
        {
            Assert.NotNull(result.MainValues[i]);
        }
    }

    [Fact]
    public void Calculate_FlatSeries_ReturnsConstantPrice()
    {
        var indicator = new CoreArimaIndicator();
        indicator.Configure(new CoreArimaParameter { Period = 15, P = 1, D = 1, Q = 1 });

        var candles = new List<CoreCandleData>(30);
        var date = new DateTime(2023, 1, 1);
        for (int i = 0; i < 30; i++)
        {
            candles.Add(new CoreCandleData(
                Timestamp: date.AddDays(i),
                Open: 100m,
                High: 100m,
                Low: 100m,
                Close: 100m,
                Volume: 5000
            ));
        }

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = 14; i < 30; i++)
        {
            Assert.Equal(100m, result.MainValues[i]);
        }
    }

    [Fact]
    public void Calculate_CausalInvariant_FutureBarsDoNotAffectPastForecasts()
    {
        var indicatorA = new CoreArimaIndicator();
        indicatorA.Configure(new CoreArimaParameter { Period = 20, P = 1, D = 1, Q = 1 });

        var indicatorB = new CoreArimaIndicator();
        indicatorB.Configure(new CoreArimaParameter { Period = 20, P = 1, D = 1, Q = 1 });

        var candlesA = GenerateCandles(40, startPrice: 100m, seed: 123);

        // candlesB has identical bars 0..29, but drastically altered bars 30..39
        var candlesB = new List<CoreCandleData>(40);
        for (int i = 0; i < 30; i++)
        {
            candlesB.Add(candlesA[i]);
        }
        for (int i = 30; i < 40; i++)
        {
            candlesB.Add(new CoreCandleData(
                Timestamp: candlesA[i].Timestamp,
                Open: candlesA[i].Open * 5.0m,
                High: candlesA[i].High * 5.0m,
                Low: candlesA[i].Low * 5.0m,
                Close: candlesA[i].Close * 5.0m,
                Volume: candlesA[i].Volume
            ));
        }

        var resultA = indicatorA.Calculate(candlesA);
        var resultB = indicatorB.Calculate(candlesB);

        Assert.True(resultA.IsSuccessful);
        Assert.True(resultB.IsSuccessful);

        // Verification of strict causality: forecasts for indices 0 to 29 must be 100% identical
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(resultA.MainValues[i], resultB.MainValues[i]);
        }
    }

    [Fact]
    public void Configure_SetsParametersCorrectly()
    {
        var indicator = new CoreArimaIndicator();
        var param = new CoreArimaParameter
        {
            P = 2,
            D = 0,
            Q = 3,
            Period = 50,
            PriceSource = PriceType.High
        };

        indicator.Configure(param);

        Assert.Equal(2, indicator.P);
        Assert.Equal(0, indicator.D);
        Assert.Equal(3, indicator.Q);
        Assert.Equal(50, indicator.Period);
        Assert.Equal(PriceType.High, indicator.PriceSource);
        Assert.Equal("ARIMA(50,2,0,3)", indicator.Name);
    }

    [Fact]
    public void Calculate_PeriodLessThanTwo_ReturnsNulls()
    {
        var indicator = new CoreArimaIndicator { Period = 1 };
        var candles = GenerateCandles(10);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(10, result.MainValues.Count);
        Assert.All(result.MainValues, Assert.Null);
    }
}
