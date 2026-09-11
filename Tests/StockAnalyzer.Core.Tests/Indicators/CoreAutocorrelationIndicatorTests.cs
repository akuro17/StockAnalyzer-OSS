using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreAutocorrelationIndicatorTests
{
    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.Autocorrelation));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.Autocorrelation);
        Assert.NotNull(indicator);
        Assert.IsType<CoreAutocorrelationIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration_SingleColorWithoutSeriesColors()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.Autocorrelation);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(-1.0m, settings.MinValue);
        Assert.Equal(1.0m, settings.MaxValue);
        Assert.Equal("Autocorrelation", settings.DisplayName);
        Assert.IsType<CoreAutocorrelationParameter>(settings.ParameterObject);

        // Single line configuration: SeriesColors must be empty so Simple Color Settings (Main Color/Thickness/Offset) is shown
        Assert.True(settings.SeriesColors == null || settings.SeriesColors.Count == 0);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreAutocorrelationParameter
        {
            Period = 30,
            Lag = 0,
            MinLag = 5,
            MaxLag = 50,
            Threshold = 0.30,
            SmoothingPeriod = 5,
            FallbackPeriod = 20,
            MaxHoldBars = 5
        };
        param.Validate();

        param.Lag = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Lag = 0;

        param.Period = 9;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 30;

        param.MinLag = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.MinLag = 5;

        param.MaxLag = 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.MaxLag = 50;

        param.Threshold = -0.1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Threshold = 1.1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Threshold = 0.30;

        param.SmoothingPeriod = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.SmoothingPeriod = 5;

        param.FallbackPeriod = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.FallbackPeriod = 20;

        param.MaxHoldBars = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void CalculateCore_ChangingPeriod_ModifiesAutocorrelationLine()
    {
        int count = 120;
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal p = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * i / 18.0) + (decimal)Math.Cos(i * 0.8) * 4m;
            candles.Add(new CoreCandleData(startDate.AddDays(i), p, p + 1m, p - 1m, p, 1000L));
        }

        var indShortPeriod = new CoreAutocorrelationIndicator
        {
            Period = 15,
            MinLag = 5,
            MaxLag = 30
        };

        var indLongPeriod = new CoreAutocorrelationIndicator
        {
            Period = 50,
            MinLag = 5,
            MaxLag = 30
        };

        var resShort = indShortPeriod.Calculate(candles);
        var resLong = indLongPeriod.Calculate(candles);

        Assert.True(resShort.IsSuccessful);
        Assert.True(resLong.IsSuccessful);

        // Verification: Changing Period MUST produce distinct autocorrelation values across valid bars
        bool hasDifference = false;
        for (int i = 85; i < count; i++)
        {
            if (resShort.MainValues[i].HasValue && resLong.MainValues[i].HasValue &&
                resShort.MainValues[i]!.Value != resLong.MainValues[i]!.Value)
            {
                hasDifference = true;
                break;
            }
        }
        Assert.True(hasDifference, "Modifying Period must change the output autocorrelation line values.");
    }

    [Fact]
    public void CalculateCore_WithSineWaveCandles_ProducesAccurateAutocorrelationAndPeriod()
    {
        var indicator = new CoreAutocorrelationIndicator
        {
            Period = 25,
            MinLag = 5,
            MaxLag = 30,
            Threshold = 0.30,
            SmoothingPeriod = 3,
            FallbackPeriod = 15
        };

        int count = 100;
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal p = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * i / 16.0);
            candles.Add(new CoreCandleData(startDate.AddDays(i), p, p + 2m, p - 2m, p, 1000L));
        }

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(count, result.MainValues.Count);

        // Verify named series
        Assert.True(result.HasSeries("Period"));
        Assert.True(result.HasSeries(CoreAutocorrelationIndicator.DominantPeriodSeriesName));
        Assert.True(result.HasSeries(CoreAutocorrelationIndicator.RawPeriodSeriesName));
        Assert.True(result.HasSeries(CoreAutocorrelationIndicator.PeakCorrelationSeriesName));

        var periodSeries = result.GetSeries("Period");
        var peakCorrSeries = result.GetSeries(CoreAutocorrelationIndicator.PeakCorrelationSeriesName);

        // MainValues is Autocorrelation [-1..+1]
        Assert.Equal(result.MainValues, peakCorrSeries);

        // Warmup: 25 + 30 = 55
        Assert.Null(result.MainValues[54]);
        Assert.NotNull(result.MainValues[55]);
        Assert.Null(periodSeries[54]);
        Assert.NotNull(periodSeries[55]);

        // Autocorrelation should be high (close to 1.0) and Period should converge close to 16
        for (int i = 65; i < count; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 0.8m, 1.0m);

            Assert.NotNull(periodSeries[i]);
            Assert.InRange(periodSeries[i]!.Value, 15.0m, 17.0m);
        }
    }

    [Fact]
    public void CalculateCore_PriceSource_AffectsCalculation()
    {
        var indClose = new CoreAutocorrelationIndicator
        {
            Period = 20,
            MinLag = 5,
            MaxLag = 25,
            PriceSource = PriceType.Close
        };

        var indHigh = new CoreAutocorrelationIndicator
        {
            Period = 20,
            MinLag = 5,
            MaxLag = 25,
            PriceSource = PriceType.High
        };

        int count = 70;
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (decimal)Math.Sin(i * 0.5) * 5m;
            decimal high = close + 10m + (decimal)Math.Cos(i * 1.2) * 4m; // Distinct pattern
            candles.Add(new CoreCandleData(startDate.AddDays(i), close, high, close - 2m, close, 1000L));
        }

        var resClose = indClose.Calculate(candles);
        var resHigh = indHigh.Calculate(candles);

        Assert.True(resClose.IsSuccessful);
        Assert.True(resHigh.IsSuccessful);

        // Results should differ due to distinct High wave shape
        bool hasDiff = false;
        for (int i = 50; i < count; i++)
        {
            if (resClose.MainValues[i] != resHigh.MainValues[i])
            {
                hasDiff = true;
                break;
            }
        }
        Assert.True(hasDiff, "Calculations for Close and High should produce distinct autocorrelation values.");
    }

    [Fact]
    public void CalculateCore_FixedLag_CalculatesSpecificLagCorrelation()
    {
        var indicator = new CoreAutocorrelationIndicator
        {
            Period = 20,
            MinLag = 5,
            MaxLag = 30,
            Lag = 16 // Explicit lag
        };

        int count = 80;
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal p = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * i / 16.0);
            candles.Add(new CoreCandleData(startDate.AddDays(i), p, p + 1m, p - 1m, p, 1000L));
        }

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        // At lag 16 on a sine wave of period 16, correlation should be nearly 1.0
        for (int i = 55; i < count; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 0.95m, 1.0m);
        }
    }

    [Fact]
    public void CalculateSeriesCore_WithLogReturnMode_CalculatesSuccessfully()
    {
        var indicator = new CoreAutocorrelationIndicator
        {
            Period = 20,
            MinLag = 5,
            MaxLag = 25,
            Threshold = 0.20,
            CalculationMode = CorrelationCalculationMode.LogReturn,
            FallbackPeriod = 12
        };

        var series = new List<decimal?>();
        for (int i = 0; i < 80; i++)
        {
            series.Add(100m + (decimal)(8 * Math.Sin(2 * Math.PI * i / 14.0)));
        }

        var result = indicator.CalculateSeries(series);
        Assert.True(result.IsSuccessful);
        Assert.Equal(80, result.MainValues.Count);
        Assert.NotNull(result.MainValues[75]);
    }

    [Fact]
    public void Parameter_Validation_MinProminence_EnforcesRange()
    {
        var param = new CoreAutocorrelationParameter
        {
            Period = 30,
            MinLag = 5,
            MaxLag = 25,
            MinProminence = 0.05
        };
        param.Validate();

        param.MinProminence = -0.01;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.MinProminence = 0.51;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void CalculateCore_FlatCandles_OutputsNullCorrelation()
    {
        int count = 60;
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CoreCandleData(startDate.AddDays(i), 100m, 100m, 100m, 100m, 1000L));
        }

        var indicator = new CoreAutocorrelationIndicator
        {
            Period = 15,
            MinLag = 5,
            MaxLag = 20,
            FallbackPeriod = 10
        };

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        // Due to zero variance (flat price), correlation must be null for all bars
        for (int i = 0; i < count; i++)
        {
            Assert.Null(result.MainValues[i]);
        }
    }

    [Fact]
    public void CalculateCore_HoldPrevious_RevertsToFallbackAfterMaxHoldBars()
    {
        // 50 bars of sine wave (period 14) followed by 30 bars of noisy flat line (loss of cycle)
        int count = 90;
        var candles = new List<CoreCandleData>();
        var startDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            decimal p = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * i / 14.0);
            candles.Add(new CoreCandleData(startDate.AddDays(i), p, p + 1m, p - 1m, p, 1000L));
        }
        for (int i = 50; i < count; i++)
        {
            // Pure random/flat with no periodic structure
            candles.Add(new CoreCandleData(startDate.AddDays(i), 100m, 100m, 100m, 100m, 1000L));
        }

        int maxHold = 4;
        int fallback = 25;
        var indicator = new CoreAutocorrelationIndicator
        {
            Period = 15,
            MinLag = 5,
            MaxLag = 20,
            Threshold = 0.40,
            SmoothingPeriod = 1, // instantaneous for clear testing
            FallbackPeriod = fallback,
            FallbackPolicy = AutocorrelationFallbackPolicy.HoldPrevious,
            MaxHoldBars = maxHold
        };

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        var periodSeries = result.GetSeries("Period");

        // During sine wave, period should be close to 14
        Assert.NotNull(periodSeries[45]);
        Assert.InRange(periodSeries[45]!.Value, 13.0m, 15.0m);

        // At bar 70 (20 bars into flat), maxHold (4) is long exceeded, so it must revert to fallback (25)
        Assert.NotNull(periodSeries[70]);
        Assert.Equal(fallback, periodSeries[70]!.Value);
    }
}
