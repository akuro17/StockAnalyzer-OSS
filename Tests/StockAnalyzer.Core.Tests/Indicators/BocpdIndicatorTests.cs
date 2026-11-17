using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class BocpdIndicatorTests
{
    private static List<CoreCandleData> BuildGaussianRegimeCandles(
        int count,
        int jumpIndex = -1,
        double jumpReturn = 0.0,
        double sigma = 0.004,
        int seed = 42)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;

        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double logRet = sigma * z;
            if (jumpIndex >= 0 && i == jumpIndex)
            {
                logRet = jumpReturn;
            }

            close *= Math.Exp(logRet);
            decimal c = (decimal)Math.Round(close, 6);
            decimal h = c * 1.002m;
            decimal l = c * 0.998m;
            candles.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                c,
                h,
                l,
                c,
                1000L + i));
        }

        return candles;
    }

    [Fact]
    public void LogGamma_MatchesExactIntegerFactorials()
    {
        Assert.Equal(0.0, BocpdMath.LogGamma(1.0), 10);
        Assert.Equal(0.0, BocpdMath.LogGamma(2.0), 10);
        Assert.Equal(Math.Log(24.0), BocpdMath.LogGamma(5.0), 10);
        Assert.Equal(0.5 * Math.Log(Math.PI), BocpdMath.LogGamma(0.5), 10);
    }

    [Fact]
    public void Calculate_RegimeJump_SpikesChangepointProbabilityAndDropsExpectedRunLength()
    {
        // Stationary sigma=0.004 followed by a >5-sigma log return shock at bar 120.
        var candles = BuildGaussianRegimeCandles(count: 160, jumpIndex: 120, jumpReturn: 0.045, sigma: 0.004, seed: 101);
        var indicator = new CoreBocpdIndicator
        {
            WarmupPeriod = 40,
            HazardLambda = 80.0,
            MaxRunLength = 150
        };

        IIndicatorResult result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        IReadOnlyList<decimal?> main = result.GetSeries(IndicatorResult.MainSeriesName)!;
        IReadOnlyList<decimal?> expRun = result.GetSeries(CoreBocpdIndicator.ExpectedRunLengthSeriesName)!;
        IReadOnlyList<decimal?> mapRun = result.GetSeries(CoreBocpdIndicator.MapRunLengthSeriesName)!;

        Assert.Equal(candles.Count, main.Count);
        for (int i = 0; i < indicator.WarmupPeriod; i++)
        {
            Assert.Null(main[i]);
            Assert.Null(expRun[i]);
            Assert.Null(mapRun[i]);
        }

        for (int i = indicator.WarmupPeriod; i < candles.Count; i++)
        {
            Assert.NotNull(main[i]);
            Assert.InRange(main[i]!.Value, 0m, 1m);
        }

        decimal baselineCp = main.Skip(80).Take(35).Max()!.Value;
        decimal postJumpCp = main.Skip(120).Take(4).Max()!.Value;
        Assert.True(postJumpCp > baselineCp * 2m, $"Expected post-jump CP ({postJumpCp}) to exceed 2x baseline ({baselineCp}).");

        decimal preJumpExpRun = expRun[119]!.Value;
        decimal minPostJumpExpRun = expRun.Skip(120).Take(4).Min()!.Value;
        Assert.True(minPostJumpExpRun < preJumpExpRun * 0.5m, $"Expected run length ({minPostJumpExpRun}) to drop below 50% of pre-jump ({preJumpExpRun}).");
    }

    [Fact]
    public void Calculate_TwoSidedCusum_AccumulatesAndResetsAboveThreshold()
    {
        // Steady small noise for warmup, then strong consecutive positive returns, then negative shock.
        var candles = BuildGaussianRegimeCandles(count: 80, sigma: 0.002, seed: 7);
        double c = (double)candles[49].Close;
        for (int i = 50; i < 65; i++)
        {
            c *= Math.Exp(0.015);
            decimal dec = (decimal)Math.Round(c, 6);
            candles[i] = new CoreCandleData(candles[i].Timestamp, dec, dec * 1.001m, dec * 0.999m, dec, 1000L + i);
        }
        for (int i = 65; i < 80; i++)
        {
            c *= Math.Exp(-0.02);
            decimal dec = (decimal)Math.Round(c, 6);
            candles[i] = new CoreCandleData(candles[i].Timestamp, dec, dec * 1.001m, dec * 0.999m, dec, 1000L + i);
        }

        var indicator = new CoreBocpdIndicator
        {
            WarmupPeriod = 25,
            CusumDrift = 0.25,
            CusumThreshold = 2.0
        };

        IIndicatorResult result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        IReadOnlyList<decimal?> cusumUp = result.GetSeries(CoreBocpdIndicator.CusumUpSeriesName)!;
        IReadOnlyList<decimal?> cusumDown = result.GetSeries(CoreBocpdIndicator.CusumDownSeriesName)!;

        int breachIdx = -1;
        for (int i = 50; i < 65; i++)
        {
            if (cusumUp[i]!.Value > 2.0m)
            {
                breachIdx = i;
                break;
            }
        }

        Assert.True(breachIdx > 0 && breachIdx + 1 < 65, "Expected upward CUSUM to exceed threshold during positive drift.");
        Assert.True(cusumUp[breachIdx + 1]!.Value < cusumUp[breachIdx]!.Value, "Expected CUSUM Up to reset on the bar after breaching threshold.");
        Assert.True(cusumDown.Skip(65).Max()!.Value > 1.5m, "Expected downward CUSUM to accumulate during negative returns.");
    }

    [Fact]
    public void Calculate_IsStrictlyCausal()
    {
        var candles = BuildGaussianRegimeCandles(count: 100, sigma: 0.005, seed: 33);
        var indicator = new CoreBocpdIndicator { WarmupPeriod = 30 };

        IIndicatorResult baseline = indicator.Calculate(candles);

        var extended = new List<CoreCandleData>(candles);
        decimal last = candles[^1].Close * 5m;
        for (int i = 0; i < 5; i++)
        {
            extended.Add(new CoreCandleData(
                candles[^1].Timestamp.AddDays(i + 1),
                last,
                last * 1.05m,
                last * 0.95m,
                last,
                2000L + i));
        }

        IIndicatorResult withFuture = indicator.Calculate(extended);

        for (int i = 0; i < candles.Count; i++)
        {
            Assert.Equal(baseline.MainValues[i], withFuture.MainValues[i]);
            Assert.Equal(
                baseline.GetSeries(CoreBocpdIndicator.ExpectedRunLengthSeriesName)![i],
                withFuture.GetSeries(CoreBocpdIndicator.ExpectedRunLengthSeriesName)![i]);
            Assert.Equal(
                baseline.GetSeries(CoreBocpdIndicator.CusumUpSeriesName)![i],
                withFuture.GetSeries(CoreBocpdIndicator.CusumUpSeriesName)![i]);
        }
    }

    [Fact]
    public void Calculate_InvalidPriceGap_ResetsAndResumesCleanly()
    {
        var candles = BuildGaussianRegimeCandles(count: 120, sigma: 0.005, seed: 19);
        const int badIdx = 60;
        candles[badIdx] = new CoreCandleData(candles[badIdx].Timestamp, 0m, 0m, 0m, 0m, 1000L);

        var indicator = new CoreBocpdIndicator { WarmupPeriod = 20 };
        IIndicatorResult result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        // Log return at badIdx and badIdx+1 are both NaN; window of length 20 clears only once start >= badIdx + 2,
        // i.e. at bar badIdx + WarmupPeriod + 1 = 81.
        for (int i = badIdx; i <= badIdx + indicator.WarmupPeriod; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        Assert.NotNull(result.MainValues[badIdx + indicator.WarmupPeriod + 1]);
        Assert.InRange(result.MainValues[^1]!.Value, 0m, 1m);
    }

    [Fact]
    public void Registration_And_ParameterValidation_AreConsistentWithSsot()
    {
        var param = new CoreBocpdParameter();
        param.Validate();
        Assert.Equal("Bocpd (50)", param.GetDisplayName("Bocpd"));
        Assert.Equal(50, param.GetRequiredWarmupBars());

        ICoreIndicator? created = IndicatorFactory.CreateStatic(IndicatorType.Bocpd);
        var bocpd = Assert.IsType<CoreBocpdIndicator>(created);
        Assert.False(bocpd.IsOverlay);
        bocpd.Configure(param);
        Assert.Equal("BOCPD Regime (50)", bocpd.Name);

        param.HazardLambda = 0.5;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        var setting = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.Bocpd);
        Assert.Equal(CoreIndicatorCategory.Other, setting.Category);
        Assert.IsType<CoreBocpdParameter>(setting.ParameterObject);
        Assert.Equal(0m, setting.MinValue);
        Assert.Equal(1m, setting.MaxValue);

        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.Bocpd, IndicatorResult.MainSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.Bocpd, CoreBocpdIndicator.ExpectedRunLengthSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.Bocpd, CoreBocpdIndicator.MapRunLengthSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.Bocpd, CoreBocpdIndicator.CusumUpSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.Bocpd, CoreBocpdIndicator.CusumDownSeriesName));

        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.Bocpd));
        ScreenerIndicatorGroup mathGroup = ScreenerIndicatorGroup.GetDefaultGroups().Single(g => g.IndicatorTypes.Contains(IndicatorType.Bocpd));
        Assert.Contains(IndicatorType.HurstExponent, mathGroup.IndicatorTypes);
    }

    [Fact]
    public void Calculate_InitialPostWarmupBars_DoNotEmitDeterministicOneSpike()
    {
        var candles = BuildGaussianRegimeCandles(count: 100, sigma: 0.004, seed: 55);
        var indicator = new CoreBocpdIndicator
        {
            WarmupPeriod = 40,
            HazardLambda = 100.0,
            MaxRunLength = 150
        };

        IIndicatorResult result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = 40; i <= 44; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.True(
                result.MainValues[i]!.Value <= 0.25m,
                $"Expected stationary post-warmup CP prob at bar {i} <= 0.25, got {result.MainValues[i]}");
        }
    }
}
