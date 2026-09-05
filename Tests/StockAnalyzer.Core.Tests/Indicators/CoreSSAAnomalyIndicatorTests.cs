using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreSSAAnomalyIndicatorTests
{
    private static List<CoreCandleData> GenerateCandles(int n, Func<int, decimal> priceFunc)
    {
        var list = new List<CoreCandleData>(n);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < n; i++)
        {
            decimal close = priceFunc(i);
            list.Add(new CoreCandleData(
                baseTime.AddHours(i),
                close - 1m,
                close + 2m,
                close - 2m,
                close,
                1000
            ));
        }

        return list;
    }

    [Fact]
    public void Factory_CanCreate_SSAAnomalyIndicator()
    {
        var factory = IndicatorFactory.Default;
        Assert.True(factory.IsRegistered(IndicatorType.SSAAnomaly));

        var indicator = factory.Create(IndicatorType.SSAAnomaly);
        Assert.NotNull(indicator);
        Assert.IsType<CoreSSAAnomalyIndicator>(indicator);
    }

    [Fact]
    public void Indicator_Configure_UpdatesProperties()
    {
        var indicator = new CoreSSAAnomalyIndicator();
        var param = new CoreSSAAnomalyParameter
        {
            WindowSize = 50,
            EmbeddingDimension = 20,
            NumComponents = 3,
            EnterThreshold = 2.5m,
            ExitThreshold = 1.2m,
            CoolDownPeriod = 4,
            MinDuration = 3
        };

        indicator.Configure(param);

        Assert.Equal(50, indicator.WindowSize);
        Assert.Equal(20, indicator.EmbeddingDimension);
        Assert.Equal(3, indicator.NumComponents);
        Assert.Equal(2.5m, indicator.EnterThreshold);
        Assert.Equal(1.2m, indicator.ExitThreshold);
        Assert.Equal(4, indicator.CoolDownPeriod);
        Assert.Equal(3, indicator.MinDuration);
    }

    [Fact]
    public void Indicator_InsufficientCandles_ReturnsWarmupNulls()
    {
        var indicator = new CoreSSAAnomalyIndicator { WindowSize = 30 };
        var candles = GenerateCandles(15, i => 100m);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(15, indicator.ZScore.Count);
        Assert.All(indicator.ZScore, z => Assert.Null(z));
        Assert.All(indicator.AnomalyStateSeries, s => Assert.Null(s));
    }

    [Fact]
    public void Indicator_NormalOperation_DetectsAnomalies()
    {
        int n = 80;
        var candles = GenerateCandles(n, i =>
        {
            decimal basePrice = 100m + 0.1m * i + (decimal)Math.Sin(i * 0.3) * 10m;
            if (i >= 50 && i <= 53)
            {
                basePrice -= 25m; // Shock drop
            }
            return basePrice;
        });

        var indicator = new CoreSSAAnomalyIndicator
        {
            WindowSize = 30,
            EmbeddingDimension = 12,
            NumComponents = 2,
            AutoRank = false,
            EnterThreshold = 2.0m,
            ExitThreshold = 1.0m
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(n, indicator.ZScore.Count);
        Assert.Equal(n, indicator.AnomalyStateSeries.Count);

        // At index 50-53, there should be negative Z and Bearish anomaly state (-1m)
        var bearishStates = indicator.AnomalyStateSeries.Skip(50).Take(4).ToList();
        Assert.Contains(bearishStates, s => s == -1m);
    }

    [Fact]
    public void Indicator_IsCausal_FutureDataDoesNotAlterPastValues()
    {
        int baseCount = 60;
        var candles = GenerateCandles(baseCount + 10, i => 100m + 0.1m * i + (decimal)Math.Sin(i * 0.4) * 8m);

        var indicator1 = new CoreSSAAnomalyIndicator { WindowSize = 30, EmbeddingDimension = 12, NumComponents = 2, AutoRank = false };
        var indicator2 = new CoreSSAAnomalyIndicator { WindowSize = 30, EmbeddingDimension = 12, NumComponents = 2, AutoRank = false };

        var candlesShort = candles.Take(baseCount).ToList();
        var candlesLong = candles.ToList();

        indicator1.Calculate(candlesShort);
        indicator2.Calculate(candlesLong);

        // All values up to baseCount must be exactly identical
        for (int i = 0; i < baseCount; i++)
        {
            if (indicator1.ZScore[i].HasValue)
            {
                Assert.True(indicator2.ZScore[i].HasValue);
                Assert.Equal(indicator1.ZScore[i]!.Value, indicator2.ZScore[i]!.Value);
                Assert.Equal(indicator1.AnomalyStateSeries[i], indicator2.AnomalyStateSeries[i]);
            }
            else
            {
                Assert.Null(indicator2.ZScore[i]);
            }
        }
    }
}
