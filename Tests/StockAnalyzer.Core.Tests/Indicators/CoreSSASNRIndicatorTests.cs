using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreSSASNRIndicatorTests
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
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.SSASNR));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.SSASNR);
        Assert.NotNull(indicator);
        Assert.IsType<CoreSSASNRIndicator>(indicator);
        Assert.Equal("SSA Signal-to-Noise Ratio", IndicatorType.SSASNR.GetDescription());
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.SSASNR);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SsaSnrColor, settings.Color);
        Assert.IsType<CoreSSASNRParameter>(settings.ParameterObject);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreSSASNRParameter
        {
            WindowSize = 64,
            EmbeddingDimension = 20,
            NumComponents = 2,
            ThresholdHigh = 10.0m,
            ThresholdLow = 3.0m
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

        param.ThresholdLow = 15.0m;
        Assert.Throws<ArgumentException>(() => param.Validate());
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsMultiSeriesResult()
    {
        var candles = GenerateTestCandles(80);
        var indicator = new CoreSSASNRIndicator
        {
            WindowSize = 20,
            EmbeddingDimension = 8,
            NumComponents = 2
        };

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.True(result.HasSeries(CoreSSASNRIndicator.SnrSeriesName));
        Assert.True(result.HasSeries(CoreSSASNRIndicator.SignalPuritySeriesName));
        Assert.True(result.HasSeries(CoreSSASNRIndicator.ThresholdHighScoreSeriesName));
        Assert.True(result.HasSeries(CoreSSASNRIndicator.ThresholdLowScoreSeriesName));

        Assert.Equal(80, indicator.SNR.Count);
        Assert.Equal(80, indicator.SignalPurity.Count);
        Assert.Equal(80, indicator.ThresholdHighSeries.Count);
        Assert.Equal(80, indicator.ThresholdLowSeries.Count);

        int validCount = result.MainValues.Count(v => v.HasValue);
        Assert.True(validCount > 0, "Expected non-null SNR values after warmup");
    }

    [Fact]
    public void Calculate_PureSineWave_HighDecibel()
    {
        var list = new List<CoreCandleData>();
        var baseDate = new DateTime(2023, 1, 1);
        for (int i = 0; i < 60; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(i * (2.0 * Math.PI / 20.0)) * 10m;
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price, price, price, price, 1000
            ));
        }

        var indicator = new CoreSSASNRIndicator
        {
            WindowSize = 40,
            EmbeddingDimension = 20,
            NumComponents = 2
        };

        indicator.Calculate(list);
        var lastSnr = indicator.SNR[^1];
        var lastPurity = indicator.SignalPurity[^1];

        Assert.NotNull(lastSnr);
        Assert.NotNull(lastPurity);
        // Clean harmonic signal should achieve high SNR (>= 12 dB) and high purity (>= 90%)
        Assert.True(lastSnr!.Value >= 12.0m, $"Expected high SNR for pure sine wave, got {lastSnr.Value}");
        Assert.True(lastPurity!.Value >= 90.0m, $"Expected high purity for pure sine wave, got {lastPurity.Value}");
    }

    [Fact]
    public void Calculate_ScaleInvariance_HighAndLowPrice()
    {
        var baseCandles = GenerateTestCandles(60, basePrice: 100m, amplitude: 5m, trend: 0.5m);
        var highCandles = new List<CoreCandleData>();
        var lowCandles = new List<CoreCandleData>();

        decimal scaleHigh = 10000m;
        decimal scaleLow = 0.0001m;

        for (int i = 0; i < 60; i++)
        {
            var b = baseCandles[i];
            highCandles.Add(new CoreCandleData(b.Timestamp, b.Open * scaleHigh, b.High * scaleHigh, b.Low * scaleHigh, b.Close * scaleHigh, b.Volume));
            lowCandles.Add(new CoreCandleData(b.Timestamp, b.Open * scaleLow, b.High * scaleLow, b.Low * scaleLow, b.Close * scaleLow, b.Volume));
        }

        var indicatorBase = new CoreSSASNRIndicator { WindowSize = 20, EmbeddingDimension = 8, NumComponents = 2 };
        var indicatorHigh = new CoreSSASNRIndicator { WindowSize = 20, EmbeddingDimension = 8, NumComponents = 2 };
        var indicatorLow = new CoreSSASNRIndicator { WindowSize = 20, EmbeddingDimension = 8, NumComponents = 2 };

        indicatorBase.Calculate(baseCandles);
        indicatorHigh.Calculate(highCandles);
        indicatorLow.Calculate(lowCandles);

        for (int i = 19; i < 60; i++)
        {
            Assert.Equal(indicatorBase.SNR[i], indicatorHigh.SNR[i]);
            Assert.Equal(indicatorBase.SNR[i], indicatorLow.SNR[i]);
            Assert.Equal(indicatorBase.SignalPurity[i], indicatorHigh.SignalPurity[i]);
            Assert.Equal(indicatorBase.SignalPurity[i], indicatorLow.SignalPurity[i]);
        }
    }

    [Fact]
    public void Calculate_ConstantPrice_ReturnsZero()
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

        var indicator = new CoreSSASNRIndicator
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
        var indicator = new CoreSSASNRIndicator
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
