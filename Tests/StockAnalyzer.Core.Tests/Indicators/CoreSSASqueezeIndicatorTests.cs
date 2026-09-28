using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreSSASqueezeIndicatorTests
{
    private static List<CoreCandleData> GenerateTestCandles(int count, decimal basePrice = 100m, decimal amplitude = 5m, decimal trend = 0.5m)
    {
        var list = new List<CoreCandleData>();
        var baseDate = new DateTime(2023, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal wave = (decimal)Math.Sin(i * 0.2) * amplitude;
            decimal price = basePrice + wave + i * trend;
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price - 1m,
                price + 2m,
                price - 2m,
                price,
                1000 + i * 10
            ));
        }
        return list;
    }

    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.SSASqueeze));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.SSASqueeze);
        Assert.NotNull(indicator);
        Assert.IsType<CoreSSASqueezeIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.SSASqueeze);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeMomentumUpColor, settings.Color);
        Assert.IsType<CoreSSASqueezeParameter>(settings.ParameterObject);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreSSASqueezeParameter
        {
            WindowSize = 64,
            EmbeddingDimension = 20,
            NumComponents = 2,
            SsaMultiplier = 2.0m,
            AtrPeriod = 20,
            AtrMultiplier = 1.5m,
            MomentumPeriod = 12,
            SqueezeThreshold = 1.0m
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

        param.NumComponents = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.NumComponents = 21;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.NumComponents = 2;

        param.SsaMultiplier = 0m;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.SsaMultiplier = 2.0m;

        param.AtrPeriod = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.AtrPeriod = 20;

        param.AtrMultiplier = 0m;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.AtrMultiplier = 1.5m;

        param.MomentumPeriod = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.MomentumPeriod = 12;

        param.SqueezeThreshold = 0m;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsMultiSeriesResult()
    {
        var candles = GenerateTestCandles(80);
        var indicator = new CoreSSASqueezeIndicator
        {
            WindowSize = 20,
            EmbeddingDimension = 8,
            NumComponents = 2,
            AtrPeriod = 10,
            MomentumPeriod = 5
        };

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.True(result.HasSeries(CoreSSASqueezeIndicator.MomentumSeriesName));
        Assert.True(result.HasSeries(CoreSSASqueezeIndicator.SqueezeStatusSeriesName));
        Assert.True(result.HasSeries(CoreSSASqueezeIndicator.SqueezeRatioSeriesName));

        Assert.Equal(80, indicator.Momentum.Count);
        Assert.Equal(80, indicator.SqueezeStatus.Count);
        Assert.Equal(80, indicator.SqueezeRatio.Count);

        // After warmup bars, results must have finite non-null values
        int validCount = result.MainValues.Count(v => v.HasValue);
        Assert.True(validCount > 0, "Expected non-null momentum values after warmup");
    }

    [Fact]
    public void Calculate_NarrowRange_ProducesSqueezeOn()
    {
        // Generate very tight range candles
        var list = new List<CoreCandleData>();
        var baseDate = new DateTime(2023, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            // Consistent minimal fluctuation
            decimal price = 100m + (i % 2 == 0 ? 0.01m : -0.01m);
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price,
                price + 0.5m, // ATR is relatively wide compared to SSA low residual
                price - 0.5m,
                price,
                1000
            ));
        }

        var indicator = new CoreSSASqueezeIndicator
        {
            WindowSize = 16,
            EmbeddingDimension = 6,
            NumComponents = 2,
            AtrPeriod = 10,
            MomentumPeriod = 5,
            SqueezeThreshold = 1.0m
        };

        indicator.Calculate(list);
        var statusValues = indicator.SqueezeStatus.Where(s => s.HasValue).ToList();
        Assert.NotEmpty(statusValues);
        // Due to minimal residual vs ATR, squeeze should be ON (1.0m)
        Assert.Contains(1.0m, statusValues);
    }

    [Fact]
    public void Calculate_CausalMomentum_NoLookahead()
    {
        var candles100 = GenerateTestCandles(100);
        var candles150 = GenerateTestCandles(150);

        var indicator1 = new CoreSSASqueezeIndicator
        {
            WindowSize = 20,
            EmbeddingDimension = 8,
            NumComponents = 2,
            AtrPeriod = 10,
            MomentumPeriod = 6
        };
        var indicator2 = new CoreSSASqueezeIndicator
        {
            WindowSize = 20,
            EmbeddingDimension = 8,
            NumComponents = 2,
            AtrPeriod = 10,
            MomentumPeriod = 6
        };

        var result1 = indicator1.Calculate(candles100);
        var result2 = indicator2.Calculate(candles150);

        // Verify past 100 bars are bitwise identical
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(result1.MainValues[i], result2.MainValues[i]);
            Assert.Equal(indicator1.SqueezeStatus[i], indicator2.SqueezeStatus[i]);
            Assert.Equal(indicator1.SqueezeRatio[i], indicator2.SqueezeRatio[i]);
        }
    }

    [Fact]
    public void Calculate_ConstantPrice_DoesNotThrow()
    {
        var flatCandles = new List<CoreCandleData>();
        var baseDate = new DateTime(2023, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            flatCandles.Add(new CoreCandleData(
                baseDate.AddDays(i),
                100m, 100m, 100m, 100m, 1000
            ));
        }

        var indicator = new CoreSSASqueezeIndicator
        {
            WindowSize = 16,
            EmbeddingDimension = 6,
            NumComponents = 2
        };

        var result = indicator.Calculate(flatCandles);
        Assert.True(result.IsSuccessful);
        foreach (var val in result.MainValues.Where(v => v.HasValue))
        {
            Assert.Equal(0m, val!.Value);
        }
    }

    [Fact]
    public void Calculate_InsufficientBars_ReturnsNulls()
    {
        var smallCandles = GenerateTestCandles(5);
        var indicator = new CoreSSASqueezeIndicator
        {
            WindowSize = 20,
            EmbeddingDimension = 8
        };

        var result = indicator.Calculate(smallCandles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(5, result.MainValues.Count);
        Assert.All(result.MainValues, Assert.Null);
    }
}
