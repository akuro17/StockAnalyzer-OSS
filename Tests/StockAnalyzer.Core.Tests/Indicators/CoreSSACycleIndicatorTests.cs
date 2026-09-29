using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreSSACycleIndicatorTests
{
    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.SSACycle));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.SSACycle);
        Assert.NotNull(indicator);
        Assert.IsType<CoreSSACycleIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.SSACycle);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SsaCycleColor, settings.Color);
        Assert.IsType<CoreSSACycleParameter>(settings.ParameterObject);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreSSACycleParameter
        {
            WindowSize = 64,
            EmbeddingDimension = 20,
            DeltaPair = 0.25
        };
        param.Validate();

        param.WindowSize = 3;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.WindowSize = 64;

        param.EmbeddingDimension = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.EmbeddingDimension = 33;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.EmbeddingDimension = 20;

        param.DeltaPair = 0.0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.DeltaPair = 1.0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Calculate_PureSineWave_DetectsCycleAndPeriod()
    {
        // 100 bars of pure sine wave with period = 20 bars
        var sineData = new List<CoreCandleData>();
        for (int i = 0; i < 100; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(2.0 * Math.PI * i / 20.0) * 10m;
            sineData.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                price, price + 1, price - 1, price, 1000
            ));
        }

        var indicator = new CoreSSACycleIndicator
        {
            WindowSize = 40,
            EmbeddingDimension = 20,
            DeltaPair = 0.30
        };

        var result = indicator.Calculate(sineData);
        Assert.True(result.IsSuccessful);

        Assert.Equal(100, indicator.Cycle.Count);
        Assert.Equal(100, indicator.InPhase.Count);
        Assert.Equal(100, indicator.Quadrature.Count);
        Assert.Equal(100, indicator.Phase.Count);
        Assert.Equal(100, indicator.DominantPeriod.Count);

        // Check warmups
        for (int i = 0; i < 39; i++)
        {
            Assert.Null(indicator.Cycle[i]);
            Assert.Null(indicator.Phase[i]);
        }

        // Check active bars
        for (int i = 39; i < 100; i++)
        {
            Assert.NotNull(indicator.Cycle[i]);
            Assert.NotNull(indicator.InPhase[i]);
            Assert.NotNull(indicator.Quadrature[i]);
            Assert.NotNull(indicator.Phase[i]);
            Assert.NotNull(indicator.DominantPeriod[i]);

            // Phase in [-pi, pi]
            Assert.InRange((double)indicator.Phase[i]!, -Math.PI - 1e-6, Math.PI + 1e-6);

            // Estimated period should be 20 bars (or integer divisor bin)
            Assert.InRange((double)indicator.DominantPeriod[i]!, 18.0, 22.0);
        }
    }

    [Fact]
    public void Calculate_ConstantSeries_ReturnsNullsGracefully()
    {
        var flatData = new List<CoreCandleData>();
        for (int i = 0; i < 50; i++)
        {
            flatData.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }

        var indicator = new CoreSSACycleIndicator
        {
            WindowSize = 20,
            EmbeddingDimension = 10,
            DeltaPair = 0.25
        };

        var result = indicator.Calculate(flatData);
        Assert.True(result.IsSuccessful);

        // Constant series has zero variance/eigenvalues, so harmonic pairs cannot form -> all nulls
        Assert.All(indicator.Cycle, val => Assert.Null(val));
    }

    [Fact]
    public void Calculate_Causality_StrictNonRepaintingTest()
    {
        var sineData = new List<CoreCandleData>();
        for (int i = 0; i < 80; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(2.0 * Math.PI * i / 16.0) * 10m;
            sineData.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                price, price + 1, price - 1, price, 1000
            ));
        }

        var indicator = new CoreSSACycleIndicator
        {
            WindowSize = 32,
            EmbeddingDimension = 16,
            DeltaPair = 0.25
        };

        var data50 = sineData.Take(50).ToList();
        var result50 = indicator.Calculate(data50);
        Assert.True(result50.IsSuccessful);
        var cycle50 = indicator.Cycle.ToList();
        var phase50 = indicator.Phase.ToList();

        var result80 = indicator.Calculate(sineData);
        Assert.True(result80.IsSuccessful);
        var cycle80 = indicator.Cycle.ToList();
        var phase80 = indicator.Phase.ToList();

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(cycle50[i], cycle80[i]);
            Assert.Equal(phase50[i], phase80[i]);
        }
    }

    [Fact]
    public void Calculate_NonIntegerPeriodSineWave_SubBinInterpolationRecoversPeriod()
    {
        // 14.5-period sine wave
        var nonIntegerSine = new List<CoreCandleData>();
        for (int i = 0; i < 100; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(2.0 * Math.PI * i / 14.5) * 10m;
            nonIntegerSine.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), price, price + 1, price - 1, price, 1000));
        }

        var indicator = new CoreSSACycleIndicator
        {
            WindowSize = 60,
            EmbeddingDimension = 30,
            DeltaPair = 0.35
        };

        var result = indicator.Calculate(nonIntegerSine);
        Assert.True(result.IsSuccessful);

        // Period should be smoothly estimated near 14.5 bars (interpolating between k=2 (T=15) and k=3 (T=10))
        for (int i = 59; i < 100; i++)
        {
            Assert.NotNull(indicator.DominantPeriod[i]);
            Assert.InRange((double)indicator.DominantPeriod[i]!, 13.5, 15.5);
        }
    }
}
