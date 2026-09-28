using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreHilbertTransformIndicatorTests
{
    private readonly DateTime _baseDate = new(2023, 1, 1);

    private List<CoreCandleData> GenerateSineWaveCandles(int count, double period, double amplitude = 10.0, double basePrice = 100.0)
    {
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            double angle = (2.0 * Math.PI * i) / period;
            decimal price = (decimal)(basePrice + amplitude * Math.Sin(angle));
            decimal high = price + 1.0m;
            decimal low = price - 1.0m;
            candles.Add(new CoreCandleData(_baseDate.AddDays(i), price, high, low, price, 1000));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithEmptyCandles_ReturnsSuccessEmpty()
    {
        var indicator = new CoreHilbertTransformIndicator();
        var result = indicator.Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithShortCandles_ReturnsNullsDuringWarmup()
    {
        var indicator = new CoreHilbertTransformIndicator();
        var candles = GenerateSineWaveCandles(40, period: 20);

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(40, result.MainValues.Count);
        Assert.All(result.MainValues, Assert.Null);
    }

    [Fact]
    public void Calculate_With20PeriodSineWave_EstimatesPeriodAround20()
    {
        var indicator = new CoreHilbertTransformIndicator();

        var candles = GenerateSineWaveCandles(150, period: 20.0);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(150, result.MainValues.Count);

        // Warmup (first 50 bars) should be null
        for (int i = 0; i < 50; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        // Post-warmup steady-state (bars 70..140) should be near 20 (within 17..23 range)
        for (int i = 70; i < 140; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 17.0m, 23.0m);
        }
    }

    [Fact]
    public void Calculate_With30PeriodSineWave_EstimatesPeriodAround30()
    {
        var indicator = new CoreHilbertTransformIndicator();

        var candles = GenerateSineWaveCandles(180, period: 30.0);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = 80; i < 160; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 26.0m, 34.0m);
        }
    }

    [Fact]
    public void Calculate_ScaleInvariance_MultiplicationAndOffset_YieldIdenticalPeriod()
    {
        var indicatorBase = new CoreHilbertTransformIndicator();
        var indicatorScaled = new CoreHilbertTransformIndicator();
        var indicatorOffset = new CoreHilbertTransformIndicator();

        var candlesBase = GenerateSineWaveCandles(120, period: 20.0, amplitude: 10.0, basePrice: 100.0);
        var candlesScaled = GenerateSineWaveCandles(120, period: 20.0, amplitude: 100.0, basePrice: 1000.0);
        var candlesOffset = GenerateSineWaveCandles(120, period: 20.0, amplitude: 10.0, basePrice: 5000.0);

        var resultBase = indicatorBase.Calculate(candlesBase);
        var resultScaled = indicatorScaled.Calculate(candlesScaled);
        var resultOffset = indicatorOffset.Calculate(candlesOffset);

        for (int i = 60; i < 120; i++)
        {
            Assert.NotNull(resultBase.MainValues[i]);
            Assert.NotNull(resultScaled.MainValues[i]);
            Assert.NotNull(resultOffset.MainValues[i]);

            // Period should be invariant to scale / offset within precision tolerance
            Assert.Equal(resultBase.MainValues[i]!.Value, resultScaled.MainValues[i]!.Value, 1);
            Assert.Equal(resultBase.MainValues[i]!.Value, resultOffset.MainValues[i]!.Value, 1);
        }
    }

    [Fact]
    public void Calculate_WithConstantPrices_DoesNotProduceNanOrThrow()
    {
        var indicator = new CoreHilbertTransformIndicator();
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 100; i++)
        {
            candles.Add(new CoreCandleData(_baseDate.AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = 50; i < 100; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, indicator.MinPeriod, indicator.MaxPeriod);
        }
    }

    [Fact]
    public void Calculate_WithConfiguredBounds_ClampsCorrectly()
    {
        var indicator = new CoreHilbertTransformIndicator();
        indicator.Configure(new CoreHilbertTransformParameter
        {
            MinPeriod = 8,
            MaxPeriod = 30
        });

        var candles = GenerateSineWaveCandles(120, period: 50.0);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = 50; i < result.MainValues.Count; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 8.0m, 30.0m);
        }
    }

    [Fact]
    public void CalculateSeries_MatchesCalculateCoreResult()
    {
        var indicator1 = new CoreHilbertTransformIndicator();
        var indicator2 = new CoreHilbertTransformIndicator();

        var candles = GenerateSineWaveCandles(80, period: 20.0);
        var series = candles.Select(c => (decimal?)((c.High + c.Low + c.Close) / 3.0m)).ToList();

        var coreResult = indicator1.Calculate(candles);
        var seriesResult = indicator2.CalculateSeries(series);

        Assert.Equal(coreResult.MainValues.Count, seriesResult.MainValues.Count);
        for (int i = 0; i < coreResult.MainValues.Count; i++)
        {
            Assert.Equal(coreResult.MainValues[i], seriesResult.MainValues[i]);
        }
    }

    [Fact]
    public void IndicatorFactory_CanCreateHilbertTransform()
    {
        var factory = IndicatorFactory.Default;
        Assert.True(factory.IsRegistered(IndicatorType.HilbertTransform));

        var indicator = factory.Create(IndicatorType.HilbertTransform, new CoreHilbertTransformParameter
        {
            MinPeriod = 6,
            MaxPeriod = 45
        });

        Assert.NotNull(indicator);
        Assert.IsType<CoreHilbertTransformIndicator>(indicator);
        var ht = (CoreHilbertTransformIndicator)indicator;
        Assert.Equal(6, ht.MinPeriod);
        Assert.Equal(45, ht.MaxPeriod);
    }

    [Fact]
    public void Parameter_Validation_ThrowsOnInvalidRanges()
    {
        var param = new CoreHilbertTransformParameter();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            param.MinPeriod = 50;
            param.MaxPeriod = 20;
            param.Validate();
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            param.MinPeriod = 1; // < 2
            param.Validate();
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            param.SmoothBeta = 0m;
            param.Validate();
        });
    }

    [Fact]
    public void Calculate_WithDifferentSmoothBeta_ProducesDifferentOutput()
    {
        var candles = GenerateSineWaveCandles(120, period: 20.0);

        var lowBeta = new CoreHilbertTransformIndicator();
        lowBeta.Configure(new CoreHilbertTransformParameter { SmoothBeta = 0.05m });
        var lowBetaResult = lowBeta.Calculate(candles);

        var highBeta = new CoreHilbertTransformIndicator();
        highBeta.Configure(new CoreHilbertTransformParameter { SmoothBeta = 0.9m });
        var highBetaResult = highBeta.Calculate(candles);

        bool foundDifference = false;
        for (int i = 50; i < candles.Count; i++)
        {
            if (lowBetaResult.MainValues[i].HasValue && highBetaResult.MainValues[i].HasValue &&
                lowBetaResult.MainValues[i]!.Value != highBetaResult.MainValues[i]!.Value)
            {
                foundDifference = true;
                break;
            }
        }

        Assert.True(foundDifference, "SmoothBeta must influence the calculated output.");
    }

    [Fact]
    public void Calculate_WithTighterDeltaLimit_SlowsAdaptationToTrueCycle()
    {
        var candles = GenerateSineWaveCandles(120, period: 20.0);

        var wideLimit = new CoreHilbertTransformIndicator();
        wideLimit.Configure(new CoreHilbertTransformParameter { DeltaLimit = 20.0m });
        var wideResult = wideLimit.Calculate(candles);

        var tightLimit = new CoreHilbertTransformIndicator();
        tightLimit.Configure(new CoreHilbertTransformParameter { DeltaLimit = 0.5m });
        var tightResult = tightLimit.Calculate(candles);

        // Shortly after warmup (bar 55), a wide DeltaLimit lets the estimate converge
        // toward the true ~20-bar cycle almost immediately, while a tight DeltaLimit caps
        // the per-bar period jump and keeps the estimate lagging further behind.
        const int checkIndex = 55;
        decimal wideDistance = Math.Abs(wideResult.MainValues[checkIndex]!.Value - 20.0m);
        decimal tightDistance = Math.Abs(tightResult.MainValues[checkIndex]!.Value - 20.0m);

        Assert.True(tightDistance > wideDistance,
            "A tighter DeltaLimit must slow convergence toward the true cycle relative to a wide DeltaLimit.");
    }

    [Fact]
    public void Calculate_WithNonPositiveDeltaLimit_DoesNotThrow()
    {
        var indicator = new CoreHilbertTransformIndicator();
        indicator.Configure(new CoreHilbertTransformParameter { DeltaLimit = -5.0m });

        var candles = GenerateSineWaveCandles(80, period: 20.0);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void PriceSource_DefaultsToTypical()
    {
        var indicator = new CoreHilbertTransformIndicator();
        Assert.Equal(PriceType.Typical, indicator.PriceSource);
    }

    [Fact]
    public void Calculate_PriceSourceIsEffective()
    {
        // High/Low move independently of Close (not a fixed +-1 offset), so Typical vs Close
        // based smoothing/detrending genuinely diverge rather than differing by a constant.
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 120; i++)
        {
            double angle = (2.0 * Math.PI * i) / 20.0;
            decimal close = (decimal)(100.0 + 10.0 * Math.Sin(angle));
            decimal high = close + 1.0m + (i % 5);
            decimal low = close - 1.0m - (i % 3);
            candles.Add(new CoreCandleData(_baseDate.AddDays(i), close, high, low, close, 1000));
        }

        var typicalBased = new CoreHilbertTransformIndicator { PriceSource = PriceType.Typical };
        var closeBased = new CoreHilbertTransformIndicator { PriceSource = PriceType.Close };
        var typicalResult = typicalBased.Calculate(candles);
        var closeResult = closeBased.Calculate(candles);

        bool foundDifference = false;
        for (int i = 50; i < candles.Count; i++)
        {
            if (typicalResult.MainValues[i].HasValue && closeResult.MainValues[i].HasValue &&
                typicalResult.MainValues[i]!.Value != closeResult.MainValues[i]!.Value)
            {
                foundDifference = true;
                break;
            }
        }

        Assert.True(foundDifference, "PriceSource must influence the calculated output.");
    }

    [Fact]
    public void HilbertTransform_AsDynamicPeriodDriver_DrivesAdaptiveEma()
    {
        var htIndicator = new CoreHilbertTransformIndicator();
        var candles = GenerateSineWaveCandles(100, period: 20.0);
        var htResult = htIndicator.Calculate(candles);

        Assert.True(htResult.IsSuccessful);
        var dynamicPeriods = htResult.MainValues;

        // Drive AdaptiveSmoothingHelper directly
        var adaptiveEma = AdaptiveSmoothingHelper.CalculateAdaptiveEma(candles, dynamicPeriods, defaultPeriod: 20);

        Assert.Equal(candles.Count, adaptiveEma.Count);
        for (int i = 50; i < adaptiveEma.Count; i++)
        {
            Assert.NotNull(adaptiveEma[i]);
        }
    }
}
