using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreHilbertSuiteTests
{
    private static List<CoreCandleData> GenerateSineWaveCandles(int count, double period, double amplitude = 10.0, double basePrice = 100.0)
    {
        var baseDate = new DateTime(2023, 1, 1);
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            double angle = (2.0 * Math.PI * i) / period;
            decimal price = (decimal)(basePrice + amplitude * Math.Sin(angle));
            decimal high = price + 1.0m;
            decimal low = price - 1.0m;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), price, high, low, price, 1000));
        }
        return candles;
    }

    [Fact]
    public void Factory_CanCreateAllHilbertSuiteIndicators()
    {
        var factory = IndicatorFactory.Default;

        Assert.True(factory.IsRegistered(IndicatorType.HilbertTransform));
        Assert.True(factory.IsRegistered(IndicatorType.HilbertSine));
        Assert.True(factory.IsRegistered(IndicatorType.HilbertTrendline));
        Assert.True(factory.IsRegistered(IndicatorType.HilbertTrendMode));

        var ht = factory.Create(IndicatorType.HilbertTransform);
        var sine = factory.Create(IndicatorType.HilbertSine);
        var trendline = factory.Create(IndicatorType.HilbertTrendline);
        var mode = factory.Create(IndicatorType.HilbertTrendMode);

        Assert.IsType<CoreHilbertTransformIndicator>(ht);
        Assert.IsType<CoreHilbertSineIndicator>(sine);
        Assert.IsType<CoreHilbertTrendlineIndicator>(trendline);
        Assert.IsType<CoreHilbertTrendModeIndicator>(mode);
    }

    [Fact]
    public void HilbertSine_ProducesSineAndLeadSineWithinBounds()
    {
        var indicator = new CoreHilbertSineIndicator();
        var candles = GenerateSineWaveCandles(120, period: 20.0);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.True(result.HasSeries("Sine"));
        Assert.True(result.HasSeries("LeadSine"));

        var sineSeries = result.GetSeries("Sine");
        var leadSineSeries = result.GetSeries("LeadSine");

        Assert.Equal(candles.Count, sineSeries.Count);
        Assert.Equal(candles.Count, leadSineSeries.Count);

        // Post-warmup check (after 50 bars)
        for (int i = 50; i < candles.Count; i++)
        {
            if (sineSeries[i].HasValue)
            {
                Assert.InRange(sineSeries[i]!.Value, -1.01m, 1.01m);
            }
            if (leadSineSeries[i].HasValue)
            {
                Assert.InRange(leadSineSeries[i]!.Value, -1.01m, 1.01m);
            }
        }
    }

    [Fact]
    public void HilbertTrendline_IsOverlayAndTracksPriceScale()
    {
        var indicator = new CoreHilbertTrendlineIndicator();
        Assert.True(indicator.IsOverlay);

        var candles = GenerateSineWaveCandles(120, period: 20.0, amplitude: 10.0, basePrice: 100.0);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);

        // After warmup, trendline should be near base price 100 (smoothed average over cycle)
        for (int i = 70; i < candles.Count; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 90.0m, 110.0m);
        }
    }

    [Fact]
    public void HilbertTrendMode_ProducesBinaryOutput()
    {
        var indicator = new CoreHilbertTrendModeIndicator();
        var candles = GenerateSineWaveCandles(120, period: 20.0);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);

        for (int i = 50; i < candles.Count; i++)
        {
            if (result.MainValues[i].HasValue)
            {
                decimal val = result.MainValues[i]!.Value;
                Assert.True(val == 0m || val == 1m, $"Expected 0 or 1, got {val}");
            }
        }
    }

    [Fact]
    public void Parameters_Validation_WorksCorrectly()
    {
        var sineParam = new CoreHilbertSineParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            sineParam.MinPeriod = 60;
            sineParam.MaxPeriod = 20;
            sineParam.Validate();
        });

        var trendlineParam = new CoreHilbertTrendlineParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            trendlineParam.SmoothBeta = -0.1m;
            trendlineParam.Validate();
        });

        var modeParam = new CoreHilbertTrendModeParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            modeParam.StabilityWindow = 1;
            modeParam.Validate();
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            modeParam.StabilityThreshold = 0.0;
            modeParam.Validate();
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            modeParam.StabilityThreshold = 100.0;
            modeParam.Validate();
        });
    }

    [Fact]
    public void HilbertTrendline_CalculatesDynamicWeightedMovingAverage()
    {
        var indicator = new CoreHilbertTrendlineIndicator();
        var candles = GenerateSineWaveCandles(120, period: 20.0, amplitude: 20.0, basePrice: 100.0);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);

        // Verify valid WMA values are produced post-warmup
        for (int i = 55; i < 115; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            // Trendline smooths out cyclical fluctuations, staying bounded around base price
            Assert.InRange(result.MainValues[i]!.Value, 85m, 115m);
        }
    }

    [Fact]
    public void HilbertTrendMode_CustomStabilityThreshold_AltersSensitivity()
    {
        var sensitiveIndicator = new CoreHilbertTrendModeIndicator();
        sensitiveIndicator.Configure(new CoreHilbertTrendModeParameter { StabilityThreshold = 2.0 });

        var strictIndicator = new CoreHilbertTrendModeIndicator();
        strictIndicator.Configure(new CoreHilbertTrendModeParameter { StabilityThreshold = 30.0 });

        var candles = GenerateSineWaveCandles(120, period: 20.0);
        var sensitiveResult = sensitiveIndicator.Calculate(candles);
        var strictResult = strictIndicator.Calculate(candles);

        Assert.True(sensitiveResult.IsSuccessful);
        Assert.True(strictResult.IsSuccessful);

        // Stricter threshold (30 deg) should have fewer or equal trend mode triggers than very sensitive (2 deg)
        int sensitiveTrendCount = 0;
        int strictTrendCount = 0;
        for (int i = 50; i < candles.Count; i++)
        {
            if (sensitiveResult.MainValues[i] == 1m) sensitiveTrendCount++;
            if (strictResult.MainValues[i] == 1m) strictTrendCount++;
        }

        Assert.True(sensitiveTrendCount >= strictTrendCount,
            $"Expected sensitive trend triggers ({sensitiveTrendCount}) >= strict trend triggers ({strictTrendCount})");
    }
}
