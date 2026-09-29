using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreCrossCorrelationIndicatorTests
{
    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.CrossCorrelation));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.CrossCorrelation);
        Assert.NotNull(indicator);
        Assert.IsType<CoreCrossCorrelationIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.CrossCorrelation);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(-1.0m, settings.MinValue);
        Assert.Equal(1.0m, settings.MaxValue);
        Assert.IsType<CoreCrossCorrelationParameter>(settings.ParameterObject);
        var param = (CoreCrossCorrelationParameter)settings.ParameterObject!;
        Assert.True(param.EnableDynamicLag);
        Assert.Equal(IndicatorDefaultConstants.CrossCorrelationMinLag, param.MinLag);
        Assert.Equal(IndicatorDefaultConstants.CrossCorrelationMaxLag, param.MaxLag);
        Assert.Equal(IndicatorDefaultConstants.CrossCorrelationLagSmoothingPeriod, param.LagSmoothingPeriod);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreCrossCorrelationParameter
        {
            Period = 50,
            Lag = 0,
            MinLag = -20,
            MaxLag = 20,
            LagSmoothingPeriod = 3,
            Threshold = 0.30
        };
        param.Validate();

        param.Period = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 30; // 30 < MaxLag - MinLag = 40
        Assert.Throws<ArgumentException>(() => param.Validate());

        // When dynamic lag is disabled, small period is allowed
        param.EnableDynamicLag = false;
        param.Validate();
        param.EnableDynamicLag = true;
        param.Period = 50;

        param.Lag = 101;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Lag = 0;

        param.MinLag = -101;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.MinLag = 10;
        param.MaxLag = 5; // MinLag > MaxLag
        Assert.Throws<ArgumentException>(() => param.Validate());

        // One-sided positive lag scan is valid
        param.MinLag = 1;
        param.MaxLag = 10;
        param.Validate();
        param.MinLag = -20;
        param.MaxLag = 20;

        param.MaxLag = 101;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.MaxLag = 20;

        param.LagSmoothingPeriod = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.LagSmoothingPeriod = 21;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.LagSmoothingPeriod = 3;

        param.Threshold = 1.5;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Calculate_SyntheticSineWave_IdentifiesCorrectLagLead()
    {
        // 100 bars: X = sin(2 * pi * t / 16), Y = sin(2 * pi * (t - 5) / 16)
        // X leads Y by 5 bars. At Lag = 5, CCF should be near +1.0.
        int n = 100;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            decimal priceX = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * t / 16.0);
            decimal priceY = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * (t - 5) / 16.0);

            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), priceX, priceX, priceX, priceX, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), priceY, priceY, priceY, priceY, 1000));
        }

        var indicatorLag5 = new CoreCrossCorrelationIndicator(16, seriesY, lag: 5)
        {
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        var result5 = indicatorLag5.Calculate(seriesX);

        var indicatorLag0 = new CoreCrossCorrelationIndicator(16, seriesY, lag: 0)
        {
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        var result0 = indicatorLag0.Calculate(seriesX);

        // At index 80, Lag 5 should be nearly 1.0 (perfect correlation), whereas Lag 0 should be noticeably lower
        decimal? corr5 = result5.MainValues[80];
        decimal? corr0 = result0.MainValues[80];

        Assert.NotNull(corr5);
        Assert.NotNull(corr0);
        Assert.True(corr5.Value > 0.95m, $"Expected CCF(lag=5) > 0.95, got {corr5.Value}");
        Assert.True(corr5.Value > corr0.Value, $"Expected CCF(lag=5) > CCF(lag=0), got {corr5.Value} vs {corr0.Value}");
    }

    [Fact]
    public void Calculate_DynamicOptimalLag_IdentifiesDominantLeadLag()
    {
        // X leads Y by 4 bars
        int n = 120;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            decimal priceX = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * t / 20.0);
            decimal priceY = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * (t - 4) / 20.0);

            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), priceX, priceX, priceX, priceX, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), priceY, priceY, priceY, priceY, 1000));
        }

        var indicator = new CoreCrossCorrelationIndicator
        {
            Period = 20,
            EnableDynamicLag = true,
            MinLag = -10,
            MaxLag = 10,
            Threshold = 0.30,
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        indicator.SetSecondaryCandles(seriesY);

        var result = indicator.Calculate(seriesX);
        Assert.True(result.HasSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName));

        var lags = result.GetSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName);
        decimal? detectedLag = lags[80];

        Assert.NotNull(detectedLag);
        // The detected lag should be approximately 4 (within 0.5 of 4.0 due to parabolic sub-bin resolution)
        Assert.True(Math.Abs(detectedLag.Value - 4.0m) < 0.5m, $"Expected detected lag near 4, got {detectedLag.Value}");
    }

    [Fact]
    public void Calculate_FlatSeries_EmitsNullWithoutCrashing()
    {
        int n = 50;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), 100m, 100m, 100m, 100m, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), 100m, 100m, 100m, 100m, 1000));
        }

        var indicator = new CoreCrossCorrelationIndicator(20, seriesY, lag: 2);
        var result = indicator.Calculate(seriesX);

        Assert.NotNull(result);
        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WarmupBoundary_EmitsNullUntilWindowPlusLag()
    {
        int n = 50;
        int window = 10;
        int lag = 5;
        int requiredWarmup = window + lag - 1; // 14

        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            decimal pX = 100m + t * 0.5m;
            decimal pY = 100m + (t - lag) * 0.5m;
            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), pX, pX, pX, pX, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), pY, pY, pY, pY, 1000));
        }

        var indicator = new CoreCrossCorrelationIndicator(window, seriesY, lag)
        {
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        var result = indicator.Calculate(seriesX);

        for (int i = 0; i < requiredWarmup; i++)
        {
            Assert.Null(result.MainValues[i]);
        }
        Assert.NotNull(result.MainValues[requiredWarmup]);
    }

    [Fact]
    public void Calculate_LogReturnMode_EliminatesSpuriousCoTrendingCorrelation()
    {
        // Two independent noisy series with strong identical upward trend:
        // Series X = 100 + 2*t + noiseX
        // Series Y = 100 + 2*t + noiseY
        // Raw PriceLevel will exhibit high spurious correlation (r > 0.90),
        // whereas LogReturn (differenced) correlation removes the drift trend and shows near-zero correlation (|r| < 0.40).
        int n = 150;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        var rngX = new Random(42);
        var rngY = new Random(999);

        for (int t = 0; t < n; t++)
        {
            decimal trend = 100m + 2.0m * t;
            decimal noiseX = (decimal)((rngX.NextDouble() - 0.5) * 4.0);
            decimal noiseY = (decimal)((rngY.NextDouble() - 0.5) * 4.0);

            decimal px = trend + noiseX;
            decimal py = trend + noiseY;

            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), px, px, px, px, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), py, py, py, py, 1000));
        }

        var indPrice = new CoreCrossCorrelationIndicator(30, seriesY, lag: 0)
        {
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        var indReturn = new CoreCrossCorrelationIndicator(30, seriesY, lag: 0)
        {
            CalculationMode = CorrelationCalculationMode.LogReturn
        };

        var resPrice = indPrice.Calculate(seriesX);
        var resReturn = indReturn.Calculate(seriesX);

        decimal? priceCorr = resPrice.MainValues[120];
        decimal? returnCorr = resReturn.MainValues[120];

        Assert.NotNull(priceCorr);
        Assert.NotNull(returnCorr);

        Assert.True(priceCorr.Value > 0.90m, $"Expected high spurious PriceLevel correlation > 0.90, got {priceCorr.Value}");
        Assert.True(Math.Abs(returnCorr.Value) < 0.40m, $"Expected low LogReturn correlation < 0.40, got {returnCorr.Value}");
    }

    [Fact]
    public void Calculate_FixedLag_OptimalLagIsNullAcrossAllBars()
    {
        int n = 50;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), 100m + t, 100m + t, 100m + t, 100m + t, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), 100m + t, 100m + t, 100m + t, 100m + t, 1000));
        }

        var indicator = new CoreCrossCorrelationIndicator(10, seriesY, lag: 0)
        {
            EnableDynamicLag = false
        };
        var result = indicator.Calculate(seriesX);

        Assert.True(result.HasSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName));
        var optimalLagSeries = result.GetSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName);

        // Fixed lag mode must not output static horizontal line of 0. OptimalLag must be null for all bars.
        Assert.Equal(n, optimalLagSeries.Count);
        Assert.All(optimalLagSeries, lag => Assert.Null(lag));
    }

    [Fact]
    public void Calculate_DynamicOptimalLag_EdgePeakGuards()
    {
        // X leads Y by 8 bars, but MaxLag is clamped at 8.
        // Peak is on boundary k* = MaxLag = 8.
        int n = 60;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            decimal px = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * t / 16.0);
            decimal py = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * (t - 8) / 16.0);
            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), px, px, px, px, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), py, py, py, py, 1000));
        }

        var indicator = new CoreCrossCorrelationIndicator
        {
            Period = 20,
            EnableDynamicLag = true,
            MinLag = -8,
            MaxLag = 8,
            LagSmoothingPeriod = 1, // Disable smoothing to check raw edge peak lag
            Threshold = 0.30,
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        indicator.SetSecondaryCandles(seriesY);
        var result = indicator.Calculate(seriesX);

        var lags = result.GetSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName);
        decimal? detectedLag = lags[50];
        Assert.NotNull(detectedLag);
        Assert.Equal(8.0m, detectedLag.Value);
    }

    [Fact]
    public void Calculate_MaxAbsoluteCorrelation_FitsNegativePeaks()
    {
        // Aperiodic series where Y is inversely correlated with X delayed by 3 bars: Y(t) = -X(t - 3)
        int n = 80;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        var rng = new Random(42);
        decimal[] rawX = new decimal[n];
        decimal currentX = 100m;
        for (int t = 0; t < n; t++)
        {
            currentX += (decimal)((rng.NextDouble() - 0.5) * 5.0);
            rawX[t] = currentX;
        }

        for (int t = 0; t < n; t++)
        {
            decimal px = rawX[t];
            decimal py = t >= 3 ? 500m - rawX[t - 3] : 500m - rawX[0];
            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), px, px, px, px, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), py, py, py, py, 1000));
        }

        var indicator = new CoreCrossCorrelationIndicator
        {
            Period = 20,
            EnableDynamicLag = true,
            MinLag = -6,
            MaxLag = 6,
            LagSmoothingPeriod = 1,
            Threshold = 0.30,
            PeakSelectionMode = CrossCorrelationPeakSelectionMode.MaxAbsoluteCorrelation,
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        indicator.SetSecondaryCandles(seriesY);
        var result = indicator.Calculate(seriesX);

        var peaks = result.MainValues;
        var lags = result.GetSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName);

        decimal? peakCorr = peaks[60];
        decimal? detectedLag = lags[60];

        Assert.NotNull(peakCorr);
        Assert.NotNull(detectedLag);
        // Under MaxAbsoluteCorrelation, the peak correlation retains the signed value (near -1.0)
        Assert.True(peakCorr.Value < -0.90m, $"Expected strong negative correlation < -0.90, got {peakCorr.Value}");
        Assert.True(Math.Abs(detectedLag.Value - 3.0m) < 0.5m, $"Expected detected lag near 3, got {detectedLag.Value}");
    }

    [Fact]
    public void Calculate_LagSmoothingPeriod_SmoothsJitter()
    {
        // Step change in lead: lag 2 for first half, lag 6 for second half
        int n = 80;
        var seriesX = new List<CoreCandleData>(n);
        var seriesY = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            int lead = t < 40 ? 2 : 6;
            decimal px = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * t / 16.0);
            decimal py = 100m + 10m * (decimal)Math.Sin(2.0 * Math.PI * (t - lead) / 16.0);
            seriesX.Add(new CoreCandleData(baseTime.AddDays(t), px, px, px, px, 1000));
            seriesY.Add(new CoreCandleData(baseTime.AddDays(t), py, py, py, py, 1000));
        }

        var indUnsmoothed = new CoreCrossCorrelationIndicator
        {
            Period = 16,
            EnableDynamicLag = true,
            MinLag = 0,
            MaxLag = 8,
            LagSmoothingPeriod = 1,
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        indUnsmoothed.SetSecondaryCandles(seriesY);
        var resUnsmoothed = indUnsmoothed.Calculate(seriesX);

        var indSmoothed = new CoreCrossCorrelationIndicator
        {
            Period = 16,
            EnableDynamicLag = true,
            MinLag = 0,
            MaxLag = 8,
            LagSmoothingPeriod = 5,
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        indSmoothed.SetSecondaryCandles(seriesY);
        var resSmoothed = indSmoothed.Calculate(seriesX);

        var lagsRaw = resUnsmoothed.GetSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName);
        var lagsSmooth = resSmoothed.GetSeries(CoreCrossCorrelationIndicator.OptimalLagSeriesName);

        // At transition bar 41, the smoothed lag should be intermediate (between 2 and 6), not jumping abruptly to ~6
        decimal? raw41 = lagsRaw[41];
        decimal? smooth41 = lagsSmooth[41];

        Assert.NotNull(raw41);
        Assert.NotNull(smooth41);
        Assert.True(smooth41.Value < raw41.Value, $"Smoothed lag ({smooth41.Value}) should lag behind jump to raw lag ({raw41.Value})");
    }

    [Fact]
    public void Calculate_PriceVsVolume_ZeroVolumeSafe()
    {
        int n = 50;
        var candles = new List<CoreCandleData>(n);
        var baseTime = DateTime.UtcNow.Date;

        for (int t = 0; t < n; t++)
        {
            // Inject zero volumes on some bars
            long vol = (t % 5 == 0) ? 0L : 1000L * (t + 1);
            decimal px = 100m + t * 0.5m;
            candles.Add(new CoreCandleData(baseTime.AddDays(t), px, px, px, px, vol));
        }

        var indicator = new CoreCrossCorrelationIndicator
        {
            Period = 15,
            EnableDynamicLag = true,
            MinLag = -3,
            MaxLag = 3,
            ComparisonSymbol = string.Empty, // Price vs Volume mode
            CalculationMode = CorrelationCalculationMode.LogReturn
        };

        var result = indicator.Calculate(candles);
        Assert.NotNull(result);
        Assert.Equal(n, result.MainValues.Count);
        // Ensure no NaN or infinite decimal conversion exception occurred
        for (int i = 25; i < n; i++)
        {
            var val = result.MainValues[i];
            if (val.HasValue)
            {
                Assert.True(val.Value >= -1.0m && val.Value <= 1.0m);
            }
        }
    }

    [Fact]
    public void IndicatorChartSuppression_SuppressesOptimalLagFromChart_AllowsInDataWindow()
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.CrossCorrelation, "OptimalLag"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.CrossCorrelation, "Main"));
        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.CrossCorrelation, "OptimalLag"));

        var settings = DefaultCoreIndicatorSettings.GetDefault().First(s => s.TypeEnum == IndicatorType.CrossCorrelation);
        Assert.DoesNotContain(settings.SeriesColors, sc => sc.TargetSeries.Contains("OptimalLag"));
        Assert.Contains(settings.SeriesColors, sc => sc.TargetSeries.Contains("Main"));
    }

    [Fact]
    public void Calculate_SeriesOutputs_OnlyContainsMainAndOptimalLagWithoutDuplicates()
    {
        var candles = new List<CoreCandleData>();
        var baseTime = DateTime.UtcNow.Date;
        for (int i = 0; i < 30; i++)
        {
            candles.Add(new CoreCandleData(baseTime.AddDays(i), 100m + i, 105m + i, 95m + i, 102m + i, 1000));
        }

        var indicator = new CoreCrossCorrelationIndicator { Period = 10, EnableDynamicLag = true };
        var result = indicator.Calculate(candles);

        Assert.NotNull(result);
        Assert.Equal(2, result.SeriesNamesList.Count);
        Assert.Contains(IndicatorResult.MainSeriesName, result.SeriesNamesList);
        Assert.Contains("OptimalLag", result.SeriesNamesList);
        Assert.DoesNotContain("CrossCorrelation", result.SeriesNamesList);
        Assert.DoesNotContain("PeakCorrelation", result.SeriesNamesList);
    }

    [Fact]
    public void Calculate_WithNullData_PropagatesNullSafelyWithoutZeroDistortion()
    {
        var seriesX = new List<decimal?> { 10m, 11m, 12m, null, 14m, 15m, 16m, 17m, 18m, 19m };
        var seriesY = new List<decimal?> { 20m, 22m, 24m, 26m, 28m, 30m, 32m, 34m, 36m, 38m };

        var rolling = CrossCorrelationEngine.CalculateRollingCcf(seriesX, seriesY, windowSize: 3, lag: 0);
        Assert.Equal(seriesX.Count, rolling.Count);
        // Window covering index 3 (the null bar) should evaluate to null
        Assert.Null(rolling[3]);
        Assert.Null(rolling[4]);
        Assert.Null(rolling[5]);
        // After window passes the null bar (index 6: bars 4, 5, 6), correlation should recover
        Assert.NotNull(rolling[6]);
    }
}
