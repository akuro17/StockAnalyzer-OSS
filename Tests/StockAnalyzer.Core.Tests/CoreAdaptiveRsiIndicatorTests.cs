using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreAdaptiveRsiIndicatorTests
{
    private static List<CoreCandleData> CreateZigZagCandles(int count)
    {
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2023, 1, 1);
        decimal price = 100m;
        for (int i = 0; i < count; i++)
        {
            price += (i % 2 == 0) ? 3.0m : -2.0m;
            candles.Add(new CoreCandleData(startDate.AddDays(i), price - 1, price + 2, price - 2, price, 1000));
        }
        return candles;
    }

    private static List<CoreCandleData> CreateSineCandles(int count, int period)
    {
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2023, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal mid = 100m + (decimal)(Math.Sin(2 * Math.PI * i / period) * 10.0) + 0.1m * i;
            candles.Add(new CoreCandleData(startDate.AddDays(i), mid, mid + 1m, mid - 1m, mid, 1000));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        indicator.Calculate(new List<CoreCandleData>());

        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_SeriesShorterThanFftWindow_UsesDefaultPeriodRsi()
    {
        var candles = CreateZigZagCandles(40);
        var indicator = new CoreAdaptiveRsiIndicator { WindowSize = 64 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(candles.Count, indicator.Values.Count);
        for (int i = indicator.DefaultPeriod; i < indicator.Values.Count; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.InRange(indicator.Values[i]!.Value, 0m, 100m);
        }
        Assert.Equal(candles.Count, indicator.DominantPeriod.Count);
        Assert.All(indicator.DominantPeriod, Assert.Null);
    }

    [Fact]
    public void Calculate_WithFullFftWindow_SetsDominantPeriodAndBoundedRsi()
    {
        const int period = 16;
        var candles = CreateSineCandles(200, period);
        var indicator = new CoreAdaptiveRsiIndicator { WindowSize = 64 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(candles.Count, indicator.Values.Count);
        Assert.Equal(candles.Count, indicator.DominantPeriod.Count);
        Assert.True(indicator.DominantPeriod.Take(63).All(v => v == null));
        Assert.True(indicator.DominantPeriod.Skip(63).All(v => v.HasValue));
        Assert.All(indicator.DominantPeriod.Skip(63), v => Assert.InRange(v!.Value, period - 2m, period + 2m));
        Assert.All(indicator.Values.Skip(indicator.MaxPeriod * 2).Where(v => v.HasValue), v => Assert.InRange(v!.Value, 0m, 100m));
    }

    [Fact]
    public void Calculate_IsCausal_ValuesDoNotChangeWhenLaterBarsAreRemoved()
    {
        var candles = CreateSineCandles(200, 16);
        var full = new CoreAdaptiveRsiIndicator { WindowSize = 64 };
        full.Calculate(candles);
        var truncated = new CoreAdaptiveRsiIndicator { WindowSize = 64 };
        truncated.Calculate(candles.Take(150).ToList());

        Assert.Equal(full.Values.Take(150), truncated.Values);
        Assert.Equal(full.DominantPeriod.Take(150), truncated.DominantPeriod);
    }

    [Fact]
    public void IndicatorFactory_ShouldCreateAdaptiveRsiIndicator()
    {
        var factory = new IndicatorFactory();
        Assert.True(factory.IsRegistered(IndicatorType.AdaptiveRSI));

        var indicator = factory.Create(IndicatorType.AdaptiveRSI);
        Assert.NotNull(indicator);
        Assert.IsType<CoreAdaptiveRsiIndicator>(indicator);
    }

    [Fact]
    public void Configure_SetsParametersCorrectly()
    {
        var indicator = new CoreAdaptiveRsiIndicator();
        var param = new CoreAdaptiveRsiParameter
        {
            WindowSize = 128,
            DefaultPeriod = 21,
            MinPeriod = 8,
            MaxPeriod = 64
        };

        indicator.Configure(param);

        Assert.Equal(128, indicator.WindowSize);
        Assert.Equal(21, indicator.DefaultPeriod);
        Assert.Equal(8, indicator.MinPeriod);
        Assert.Equal(64, indicator.MaxPeriod);
        Assert.Equal("Adaptive RSI (128, 21)", indicator.Name);
    }
}
