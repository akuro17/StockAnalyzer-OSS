using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreHoughIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(int count)
    {
        var candles = new List<CoreCandleData>(count);
        var baseTime = new DateTime(2025, 1, 1);
        decimal price = 500m;

        for (int i = 0; i < count; i++)
        {
            decimal wave = (decimal)Math.Sin(i * 0.5) * 20m;
            decimal trend = i * 2.0m;
            decimal close = price + trend + wave;
            decimal high = close + 5m;
            decimal low = close - 5m;
            decimal open = close - 1m;

            candles.Add(new CoreCandleData(
                baseTime.AddDays(i),
                open,
                high,
                low,
                close,
                50000 + i * 500));
        }

        return candles;
    }

    [Fact]
    public void Factory_ShouldCreateHoughTrendStrengthIndicator()
    {
        var indicator = IndicatorFactory.Default.Create(IndicatorType.HoughTrendStrength);
        Assert.NotNull(indicator);
        Assert.IsType<CoreHoughTrendStrengthIndicator>(indicator);
        Assert.Equal("HoughTrendStrength(100,3,3)", indicator.Name);
    }

    [Fact]
    public void Factory_ShouldCreateHoughTrendAngleIndicator()
    {
        var indicator = IndicatorFactory.Default.Create(IndicatorType.HoughTrendAngle);
        Assert.NotNull(indicator);
        Assert.IsType<CoreHoughTrendAngleIndicator>(indicator);
        Assert.Equal("HoughTrendAngle(100,3,3)", indicator.Name);
    }

    [Fact]
    public void HoughTrendStrength_Calculate_ReturnsValidSeries()
    {
        var indicator = new CoreHoughTrendStrengthIndicator
        {
            Lookback = 30,
            PivotWindow = 2,
            VoteThreshold = 2
        };

        var candles = CreateTestCandles(60);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(60, indicator.Values.Count);

        // Warmup checks
        for (int i = 0; i < 29; i++)
        {
            Assert.Null(indicator.Values[i]);
        }

        // Active checks (values in [0, 100])
        for (int i = 29; i < 60; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            decimal val = indicator.Values[i]!.Value;
            Assert.True(val >= 0m && val <= 100m, $"Value {val} was out of range [0, 100]");
        }
    }

    [Fact]
    public void HoughTrendAngle_Calculate_ReturnsValidAngles()
    {
        var indicator = new CoreHoughTrendAngleIndicator
        {
            Lookback = 30,
            PivotWindow = 2,
            VoteThreshold = 2
        };

        var candles = CreateTestCandles(60);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(60, indicator.Values.Count);

        // Warmup checks
        for (int i = 0; i < 29; i++)
        {
            Assert.Null(indicator.Values[i]);
        }

        // Angle checks (-90 to +90 degrees)
        for (int i = 29; i < 60; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            decimal angle = indicator.Values[i]!.Value;
            Assert.True(angle >= -90m && angle <= 90m, $"Angle {angle} was out of range [-90, +90]");
        }
    }

    [Fact]
    public void HoughIndicators_Configure_UpdatesParameters()
    {
        var strengthInd = new CoreHoughTrendStrengthIndicator();
        var strengthParam = new CoreHoughTrendStrengthParameter
        {
            Lookback = 50,
            PivotWindow = 4,
            VoteThreshold = 4,
            MaxLines = 3,
            Normalization = HoughNormalizationMode.Log
        };
        strengthInd.Configure(strengthParam);

        Assert.Equal(50, strengthInd.Lookback);
        Assert.Equal(4, strengthInd.PivotWindow);
        Assert.Equal(4, strengthInd.VoteThreshold);
        Assert.Equal(3, strengthInd.MaxLines);
        Assert.Equal(HoughNormalizationMode.Log, strengthInd.Normalization);

        var angleInd = new CoreHoughTrendAngleIndicator();
        var angleParam = new CoreHoughTrendAngleParameter
        {
            Lookback = 80,
            PivotWindow = 5,
            VoteThreshold = 5,
            MaxLines = 2,
            Normalization = HoughNormalizationMode.ZScore
        };
        angleInd.Configure(angleParam);

        Assert.Equal(80, angleInd.Lookback);
        Assert.Equal(5, angleInd.PivotWindow);
        Assert.Equal(5, angleInd.VoteThreshold);
        Assert.Equal(2, angleInd.MaxLines);
        Assert.Equal(HoughNormalizationMode.ZScore, angleInd.Normalization);
    }

    [Fact]
    public void HoughIndicators_WithFlatMarketCandles_CalculateSafelyWithoutExceptions()
    {
        // Candles with identical prices (flat line, zero ATR, zero range)
        var candles = new List<CoreCandleData>();
        var baseTime = new DateTime(2025, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            candles.Add(new CoreCandleData(baseTime.AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }

        var strength = new CoreHoughTrendStrengthIndicator { Lookback = 30 };
        var strengthRes = strength.Calculate(candles);
        Assert.True(strengthRes.IsSuccessful);
        Assert.Equal(50, strength.Values.Count);

        var angle = new CoreHoughTrendAngleIndicator { Lookback = 30 };
        var angleRes = angle.Calculate(candles);
        Assert.True(angleRes.IsSuccessful);
        Assert.Equal(50, angle.Values.Count);
    }
}
