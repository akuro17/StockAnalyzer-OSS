using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreClothoidIndicatorTests
{
    private static List<CoreCandleData> CreateCandles(params decimal[] closes)
    {
        return closes
            .Select((c, i) => new CoreCandleData(DateTime.Today.AddDays(i), c, c, c, c, 1000))
            .ToList();
    }

    [Fact]
    public void IndicatorFactory_RegistersAndCreatesClothoidIndicators()
    {
        IIndicatorFactory factory = new IndicatorFactory();

        Assert.True(factory.IsRegistered(IndicatorType.ClothoidOscillator));
        Assert.True(factory.IsRegistered(IndicatorType.ClothoidMovingAverage));

        var osc = factory.Create(IndicatorType.ClothoidOscillator);
        Assert.NotNull(osc);
        var typedOsc = Assert.IsType<CoreClothoidOscillatorIndicator>(osc);
        Assert.False(typedOsc.IsOverlay);

        var ma = factory.Create(IndicatorType.ClothoidMovingAverage);
        Assert.NotNull(ma);
        var typedMa = Assert.IsType<CoreClothoidMovingAverageIndicator>(ma);
        Assert.True(typedMa.IsOverlay);
    }

    [Fact]
    public void IndicatorFactory_ConfigureWithParameters_SetsPropertiesCorrectly()
    {
        IIndicatorFactory factory = new IndicatorFactory();

        var oscParam = new CoreClothoidOscillatorParameter { Period = 20, DecayAlpha = 2.0, SignalPeriod = 5 };
        var osc = factory.Create(IndicatorType.ClothoidOscillator, oscParam) as CoreClothoidOscillatorIndicator;
        Assert.NotNull(osc);
        Assert.Equal(20, osc.Period);
        Assert.Equal(2.0, osc.DecayAlpha);
        Assert.Equal(5, osc.SignalPeriod);

        var maParam = new CoreClothoidMovingAverageParameter { Period = 30, Offset = 0.5, Sigma = 4.0 };
        var ma = factory.Create(IndicatorType.ClothoidMovingAverage, maParam) as CoreClothoidMovingAverageIndicator;
        Assert.NotNull(ma);
        Assert.Equal(30, ma.Period);
        Assert.Equal(0.5, ma.Offset);
        Assert.Equal(4.0, ma.Sigma);
    }

    [Fact]
    public void ClothoidOscillator_WarmupAndConstantPrices_ProducesExpectedValues()
    {
        var indicator = new CoreClothoidOscillatorIndicator
        {
            Period = 6,
            SignalPeriod = 3
        };

        decimal[] closes = Enumerable.Repeat(150m, 20).ToArray();
        var candles = CreateCandles(closes);

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        var main = result.GetSeries(IndicatorResult.MainSeriesName);
        var signal = result.GetSeries("Signal");
        var hist = result.GetSeries("Histogram");

        Assert.Equal(20, main.Count);

        // Warmup period: first Period - 1 bars must be null
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(main[i]);
            Assert.Null(signal[i]);
            Assert.Null(hist[i]);
        }

        // For constant prices, curvature rate should be exactly 0
        for (int i = 5; i < 20; i++)
        {
            Assert.NotNull(main[i]);
            Assert.Equal(0.0m, main[i]!.Value);
        }

        // Signal warmup: takes 5 + 3 - 1 = 7 bars
        for (int i = 7; i < 20; i++)
        {
            Assert.NotNull(signal[i]);
            Assert.NotNull(hist[i]);
            Assert.Equal(0.0m, signal[i]!.Value);
            Assert.Equal(0.0m, hist[i]!.Value);
        }
    }

    [Fact]
    public void ClothoidMovingAverage_ConstantPrices_ReturnsExactPrice()
    {
        var indicator = new CoreClothoidMovingAverageIndicator
        {
            Period = 10,
            Offset = 0.85,
            Sigma = 6.0
        };

        decimal[] closes = Enumerable.Repeat(200m, 30).ToArray();
        var candles = CreateCandles(closes);

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        var values = result.GetSeries(IndicatorResult.MainSeriesName);
        Assert.Equal(30, values.Count);

        // First 9 bars must be null
        for (int i = 0; i < 9; i++)
        {
            Assert.Null(values[i]);
        }

        // Remaining bars must equal exactly 200m
        for (int i = 9; i < 30; i++)
        {
            Assert.NotNull(values[i]);
            Assert.Equal(200.0m, Math.Round(values[i]!.Value, 4));
        }
    }

    [Fact]
    public void ClothoidMovingAverage_CausalityInvariant_FutureBarsDoNotAffectPastOutput()
    {
        var indicator1 = new CoreClothoidMovingAverageIndicator { Period = 5 };
        var indicator2 = new CoreClothoidMovingAverageIndicator { Period = 5 };

        var candlesA = CreateCandles(10m, 11m, 12m, 13m, 14m, 15m);
        var candlesB = CreateCandles(10m, 11m, 12m, 13m, 14m, 50m); // Change last bar

        var resA = indicator1.Calculate(candlesA);
        var resB = indicator2.Calculate(candlesB);

        var valsA = resA.GetSeries(IndicatorResult.MainSeriesName);
        var valsB = resB.GetSeries(IndicatorResult.MainSeriesName);

        // Bar 4 (index 4) should be identical because it only depends on bars 0..4
        Assert.Equal(valsA[4], valsB[4]);

        // Bar 5 (index 5) should differ because bar 5 changed
        Assert.NotEqual(valsA[5], valsB[5]);
    }

    [Fact]
    public void ParameterClasses_Validation_EnforcesRanges()
    {
        var oscParam = new CoreClothoidOscillatorParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            oscParam.Period = 3; // Min is 4
            oscParam.Validate();
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            oscParam.Period = 14;
            oscParam.DecayAlpha = 10.0; // Max is 5.0
            oscParam.Validate();
        });

        var maParam = new CoreClothoidMovingAverageParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            maParam.Period = 1; // Min is 2
            maParam.Validate();
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            maParam.Period = 20;
            maParam.Offset = 1.5; // Max is 1.0
            maParam.Validate();
        });
    }

    [Fact]
    public void ClothoidMovingAverage_DegenerateKernel_ReturnsAllNullsInsteadOfSMA()
    {
        var indicator = new CoreClothoidMovingAverageIndicator
        {
            Period = 2,
            Offset = 0.5,
            Sigma = 20.0
        };

        var candles = CreateCandles(100m, 102m, 104m, 106m, 108m);
        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        var values = result.GetSeries(IndicatorResult.MainSeriesName);
        Assert.Equal(5, values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            Assert.Null(values[i]);
        }
    }

    [Fact]
    public void ParameterClasses_Validation_RejectsNonFiniteAndInvalidEnums()
    {
        var oscParam = new CoreClothoidOscillatorParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            oscParam.DecayAlpha = double.NaN;
            oscParam.Validate();
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            oscParam.DecayAlpha = double.PositiveInfinity;
            oscParam.Validate();
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            oscParam.DecayAlpha = 1.5;
            oscParam.PriceSource = (PriceType)9999;
            oscParam.Validate();
        });

        var maParam = new CoreClothoidMovingAverageParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            maParam.Offset = double.NaN;
            maParam.Validate();
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            maParam.Sigma = double.PositiveInfinity;
            maParam.Validate();
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            maParam.Sigma = 5.0;
            maParam.PriceSource = (PriceType)9999;
            maParam.Validate();
        });
    }

    [Fact]
    public void ClothoidOscillator_DiagnosticRawCurvatureRate_EmittedAndMatchesUnscaledCurvature()
    {
        var indicator = new CoreClothoidOscillatorIndicator { Period = 6, SignalPeriod = 3 };
        var candles = CreateCandles(100m, 101m, 103m, 106m, 110m, 115m, 122m, 131m);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        var main = result.GetSeries(IndicatorResult.MainSeriesName);
        var raw = result.GetSeries("RawCurvatureRate");

        Assert.NotNull(raw);
        Assert.Equal(candles.Count, raw.Count);

        // Warmup: first 5 bars null
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(raw[i]);
        }

        // For calculated bars, main must equal clamp(raw * 100, -100, 100)
        for (int i = 5; i < candles.Count; i++)
        {
            Assert.NotNull(raw[i]);
            decimal expectedMain = Math.Clamp(raw[i]!.Value * 100.0m, -100.0m, 100.0m);
            Assert.Equal(expectedMain, main[i]!.Value);
        }
    }

    [Fact]
    public void ClothoidMovingAverage_DiagnosticLagAndSampleSize_EmittedAndWithinTheoreticalBounds()
    {
        int period = 10;
        var indicator = new CoreClothoidMovingAverageIndicator
        {
            Period = period,
            Offset = 0.85,
            Sigma = 6.0
        };

        var candles = CreateCandles(100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m, 108m, 109m, 110m, 111m);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        var lag = result.GetSeries("EffectiveLag");
        var sampleSize = result.GetSeries("EffectiveSampleSize");

        Assert.NotNull(lag);
        Assert.NotNull(sampleSize);
        Assert.Equal(candles.Count, lag.Count);
        Assert.Equal(candles.Count, sampleSize.Count);

        // Warmup: first period - 1 bars must be null
        for (int i = 0; i < period - 1; i++)
        {
            Assert.Null(lag[i]);
            Assert.Null(sampleSize[i]);
        }

        // Calculated bars: lag in [0, period - 1], sample size in [1, period]
        for (int i = period - 1; i < candles.Count; i++)
        {
            Assert.NotNull(lag[i]);
            Assert.NotNull(sampleSize[i]);

            Assert.InRange(lag[i]!.Value, 0.0m, (decimal)(period - 1));
            Assert.InRange(sampleSize[i]!.Value, 1.0m, (decimal)period);
        }
    }
}
