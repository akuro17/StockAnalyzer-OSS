using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreSSAEntropyIndicatorTests
{
    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.SSAEntropy));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.SSAEntropy);
        Assert.NotNull(indicator);
        Assert.IsType<CoreSSAEntropyIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.SSAEntropy);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SsaEntropyColor, settings.Color);
        Assert.IsType<CoreSSAEntropyParameter>(settings.ParameterObject);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreSSAEntropyParameter
        {
            WindowSize = 64,
            EmbeddingDimension = 20
        };
        param.Validate();

        param.WindowSize = 3;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.WindowSize = 64;

        param.EmbeddingDimension = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.EmbeddingDimension = 33;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Test_SSAEntropy_ConstantSeries_ReturnsZero()
    {
        // 80 bars of perfectly flat constant prices
        var flatData = new List<CoreCandleData>();
        for (int i = 0; i < 80; i++)
        {
            flatData.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                100m, 100m, 100m, 100m, 1000
            ));
        }

        var indicator = new CoreSSAEntropyIndicator
        {
            WindowSize = 32,
            EmbeddingDimension = 10
        };

        var result = indicator.Calculate(flatData);
        Assert.True(result.IsSuccessful);
        Assert.Equal(80, indicator.Values.Count);

        // First 31 bars are null warmup
        for (int i = 0; i < 31; i++)
        {
            Assert.Null(indicator.Values[i]);
        }

        // Subsequent bars should be 0.0 (no entropy / constant)
        for (int i = 31; i < 80; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.Equal(0.0m, indicator.Values[i]!.Value);
        }
    }

    [Fact]
    public void Test_SSAEntropy_PureSineWave_LowEntropy()
    {
        // 100 bars of pure single sine wave (period = 20 bars)
        var sineData = new List<CoreCandleData>();
        for (int i = 0; i < 100; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(2.0 * Math.PI * i / 20.0) * 10m;
            sineData.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                price, price + 1, price - 1, price, 1000
            ));
        }

        var indicator = new CoreSSAEntropyIndicator
        {
            WindowSize = 40,
            EmbeddingDimension = 16,
            DetrendMode = SsaDetrendMode.None
        };

        var result = indicator.Calculate(sineData);
        Assert.True(result.IsSuccessful);

        // For a pure single harmonic wave, 2 dominant eigenvalues capture almost 100% of the energy.
        // Shannon entropy H(p)/ln(L) is very low (< 0.35)
        for (int i = 39; i < 100; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.InRange(indicator.Values[i]!.Value, 0.0m, 0.35m);
        }
    }

    [Fact]
    public void Test_SSAEntropy_RandomNoise_HighEntropy()
    {
        // 120 bars of pseudorandom uniform noise with zero autocorrelation
        var noiseData = new List<CoreCandleData>();
        var rand = new Random(42);
        for (int i = 0; i < 120; i++)
        {
            decimal price = 100m + (decimal)(rand.NextDouble() - 0.5) * 20m;
            noiseData.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                price, price + 1, price - 1, price, 1000
            ));
        }

        var indicator = new CoreSSAEntropyIndicator
        {
            WindowSize = 40,
            EmbeddingDimension = 16
        };

        var result = indicator.Calculate(noiseData);
        Assert.True(result.IsSuccessful);

        // For white noise, eigenvalues are flatly distributed -> High Shannon entropy (> 0.70)
        for (int i = 39; i < 120; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.True(indicator.Values[i]!.Value > 0.70m, $"Expected entropy > 0.70, got {indicator.Values[i]} at bar {i}");
        }
    }

    [Fact]
    public void Test_SSAEntropy_InsufficientBars_ReturnsNullWarmup()
    {
        var shortData = new List<CoreCandleData>
        {
            new(new DateTime(2023, 1, 1), 100m, 101m, 99m, 100m, 1000),
            new(new DateTime(2023, 1, 2), 102m, 103m, 101m, 102m, 1000)
        };

        var indicator = new CoreSSAEntropyIndicator { WindowSize = 32 };
        var result = indicator.Calculate(shortData);
        Assert.True(result.IsSuccessful);
        Assert.Equal(2, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
    }

    [Fact]
    public void Test_SSAEntropy_StrictCausality_FutureChangesDoNotAffectPast()
    {
        // 80 bars dataset
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 80; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(i * 0.2) * 5m;
            candles.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                price, price + 1, price - 1, price, 1000
            ));
        }

        var indicator = new CoreSSAEntropyIndicator { WindowSize = 32, EmbeddingDimension = 10 };
        indicator.Calculate(candles.Take(60).ToList());
        var values60 = indicator.Values.Take(60).ToList();

        indicator.Calculate(candles);
        var values80 = indicator.Values.Take(60).ToList();

        for (int i = 0; i < 60; i++)
        {
            Assert.Equal(values60[i], values80[i]);
        }
    }
}
