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
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// DSL indicator and drawing tool expansion, run I-5: the one-sample t-test, the Welch two-sample t-test, the two-sample Kolmogorov-Smirnov test, the Mann-Whitney U test and the KL divergence
/// of returns, and the smoothed histogram kernel behind the last. Values are pinned to independent transcriptions of the statistics, the kernels on the same windows and the window layout.
/// </summary>
public class DslReturnTestIndicatorsTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 5)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i > count / 2 ? 0.004 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i)).ToList();

    private static double[] Returns(IReadOnlyList<CoreCandleData> candles, bool log = false) =>
        ReturnSeriesWindow.FromPrices(candles.Select(c => (decimal?)c.Close).ToList(), log).Returns;

    private static double[] Slice(double[] values, int first, int count) => values.Skip(first).Take(count).ToArray();

    private static double Mean(double[] x) => x.Average();

    private static double Variance(double[] x)
    {
        double m = Mean(x);
        return x.Sum(v => (v - m) * (v - m)) / (x.Length - 1);
    }

    // ---------------------------------------------------------------- OneSampleTTest

    [Fact]
    public void OneSample_WithThreeReturns_HasTheClosedFormTwoDegreesOfFreedomPValue()
    {
        // 3 returns -> df = 2, where the two-sided p-value has the closed form p = 1 - |t| / sqrt(t^2 + 2) (independent of the kernel's t distribution).
        var candles = FromCloses(new[] { 100m, 102m, 101m, 105m });
        var result = new CoreOneSampleTTestIndicator { Period = 3 }.Calculate(candles);
        double[] r = Returns(candles).Skip(1).ToArray();
        double t = Mean(r) / Math.Sqrt(Variance(r) / 3.0);

        Assert.Equal(t, (double)result.MainValues[3]!.Value, 9);
        Assert.Equal(1.0 - (Math.Abs(t) / Math.Sqrt((t * t) + 2.0)), (double)result.GetSeries(CoreOneSampleTTestIndicator.PValueSeriesName)[3]!.Value, 9);
        Assert.Null(result.MainValues[2]);
    }

    [Fact]
    public void OneSample_EqualsTheIndependentStatisticOnEveryWindow_AndTheNullMeanShiftsIt()
    {
        var candles = Candles(120);
        const int period = 25;
        var zero = new CoreOneSampleTTestIndicator { Period = period }.Calculate(candles);
        var shifted = new CoreOneSampleTTestIndicator { Period = period, NullMean = 0.001m, UseLogReturn = true }.Calculate(candles);
        double[] simple = Returns(candles);
        double[] logReturns = Returns(candles, log: true);

        for (int i = period; i < candles.Count; i++)
        {
            double[] ws = Slice(simple, i - period + 1, period);
            double[] wl = Slice(logReturns, i - period + 1, period);
            Assert.Equal(Mean(ws) / Math.Sqrt(Variance(ws) / period), (double)zero.MainValues[i]!.Value, 9);
            Assert.Equal((Mean(wl) - 0.001) / Math.Sqrt(Variance(wl) / period), (double)shifted.MainValues[i]!.Value, 9);
        }

        Assert.Null(zero.MainValues[period - 1]);
    }

    [Fact]
    public void OneSample_AWindowOfEqualReturnsOrAnUnusablePriceGivesNoValue()
    {
        var geometric = FromCloses(Enumerable.Range(0, 30).Select(i => 100m * (decimal)Math.Pow(1.01, i)));
        Assert.All(new CoreOneSampleTTestIndicator { Period = 10, UseLogReturn = true }.Calculate(geometric).MainValues, v => Assert.Null(v));

        var candles = Candles(60);
        candles[30] = new CoreCandleData(candles[30].Timestamp, 0m, 0m, 0m, 0m, 1000);
        var result = new CoreOneSampleTTestIndicator { Period = 5, UseLogReturn = true }.Calculate(candles);
        for (int i = 30; i <= 35; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        Assert.NotNull(result.MainValues[36]);
    }

    // ---------------------------------------------------------------- the two-sample tests

    [Fact]
    public void TheTwoWindowsAreContiguous_AndTheFirstUsableBarIsPeriodPlusBaselinePeriod()
    {
        var candles = Candles(150);
        const int period = 20;
        const int baseline = 40;
        var result = new CoreTwoSampleTTestIndicator { Period = period, BaselinePeriod = baseline }.Calculate(candles);

        Assert.All(result.MainValues.Take(period + baseline), v => Assert.Null(v));
        Assert.NotNull(result.MainValues[period + baseline]);

        // The value at bar i depends on the prices [i - Period - Baseline, i] only: the price right before that range changes nothing, its first price changes the oldest baseline return.
        const int bar = 100;
        var untouchedBefore = candles.ToList();
        untouchedBefore[bar - period - baseline - 1] = new CoreCandleData(candles[0].Timestamp, 500m, 500m, 500m, 500m, 1);
        var touchedFirst = candles.ToList();
        decimal firstPrice = candles[bar - period - baseline].Close;
        touchedFirst[bar - period - baseline] = new CoreCandleData(candles[0].Timestamp, firstPrice * 1.3m, firstPrice * 1.3m, firstPrice * 1.3m, firstPrice * 1.3m, 1);

        var a = new CoreTwoSampleTTestIndicator { Period = period, BaselinePeriod = baseline }.Calculate(untouchedBefore);
        var b = new CoreTwoSampleTTestIndicator { Period = period, BaselinePeriod = baseline }.Calculate(touchedFirst);
        Assert.Equal(result.MainValues[bar], a.MainValues[bar]);
        Assert.NotEqual(result.MainValues[bar], b.MainValues[bar]);
    }

    [Fact]
    public void Welch_EqualsTheIndependentStatistic_RecentMinusBaseline_AndTheKernelPValue()
    {
        var candles = Candles(140, 9);
        const int period = 20;
        const int baseline = 50;
        var result = new CoreTwoSampleTTestIndicator { Period = period, BaselinePeriod = baseline }.Calculate(candles);
        double[] r = Returns(candles);
        var p = result.GetSeries(CoreTwoSampleTTestIndicator.PValueSeriesName);

        for (int i = period + baseline; i < candles.Count; i++)
        {
            double[] recent = Slice(r, i - period + 1, period);
            double[] old = Slice(r, i - period - baseline + 1, baseline);
            double t = (Mean(recent) - Mean(old)) / Math.Sqrt((Variance(recent) / period) + (Variance(old) / baseline));
            Assert.Equal(t, (double)result.MainValues[i]!.Value, 9);

            var kernel = new double[3];
            Assert.Equal(MathErrorCode.None, DslBivariateAndTestMath.TTest2SampWelch(recent, old, kernel));
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(kernel[1]), p[i]);
            Assert.InRange((double)p[i]!.Value, 0.0, 1.0);
        }
    }

    [Fact]
    public void Ks_EqualsTheBruteForceDistance_AndTheKernelPValue()
    {
        var candles = Candles(140, 13);
        const int period = 25;
        const int baseline = 45;
        var result = new CoreKsTwoSampleTestIndicator { Period = period, BaselinePeriod = baseline }.Calculate(candles);
        double[] r = Returns(candles);
        var p = result.GetSeries(CoreKsTwoSampleTestIndicator.PValueSeriesName);

        for (int i = period + baseline; i < candles.Count; i++)
        {
            double[] recent = Slice(r, i - period + 1, period);
            double[] old = Slice(r, i - period - baseline + 1, baseline);
            double d = recent.Concat(old).Distinct().Max(v => Math.Abs((recent.Count(x => x <= v) / (double)period) - (old.Count(x => x <= v) / (double)baseline)));
            Assert.Equal(d, (double)result.MainValues[i]!.Value, 9);
            Assert.InRange((double)result.MainValues[i]!.Value, 0.0, 1.0);

            DslBivariateAndTestMath.Ks2Samp(recent, old, out _, out double kernelP);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(kernelP), p[i]);
        }
    }

    [Fact]
    public void MannWhitney_EqualsTheBruteForcePairCount_AndTheKernelPValue()
    {
        var candles = Candles(140, 17);
        const int period = 20;
        const int baseline = 35;
        var result = new CoreMannWhitneyUTestIndicator { Period = period, BaselinePeriod = baseline }.Calculate(candles);
        double[] r = Returns(candles);
        var p = result.GetSeries(CoreMannWhitneyUTestIndicator.PValueSeriesName);

        for (int i = period + baseline; i < candles.Count; i++)
        {
            double[] recent = Slice(r, i - period + 1, period);
            double[] old = Slice(r, i - period - baseline + 1, baseline);
            double u1 = 0.0;
            foreach (double x in recent)
            {
                foreach (double y in old)
                {
                    u1 += x > y ? 1.0 : (x == y ? 0.5 : 0.0);
                }
            }

            double expected = Math.Min(u1, ((double)period * baseline) - u1);
            Assert.Equal(expected, (double)result.MainValues[i]!.Value, 9);

            var kernel = new double[2];
            Assert.Equal(MathErrorCode.None, DslBivariateAndTestMath.MannWhitneyU(recent, old, kernel));
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(kernel[1]), p[i]);
        }
    }

    [Fact]
    public void MannWhitney_AtItsLargestRanges_StaysInsideTheKernelLimit_AndTheRangesAreNotTheOnesOfTheOtherTests()
    {
        var result = new CoreMannWhitneyUTestIndicator { Period = 200, BaselinePeriod = 300 }.Calculate(Candles(700));
        Assert.NotNull(result.MainValues[^1]);
        Assert.NotNull(result.MainValues[500]);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMannWhitneyUTestParameter { Period = 201 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMannWhitneyUTestParameter { BaselinePeriod = 301 }.Validate());
        new CoreMannWhitneyUTestParameter { Period = 200, BaselinePeriod = 300 }.Validate();
        new CoreTwoSampleTTestParameter { Period = 250, BaselinePeriod = 500 }.Validate();
        new CoreKsTwoSampleTestParameter { Period = 250, BaselinePeriod = 500 }.Validate();
        Assert.Equal(500, IndicatorDefaultConstants.MannWhitneyUMaxPeriod + IndicatorDefaultConstants.MannWhitneyUMaxBaselinePeriod);
        Assert.True(IndicatorDefaultConstants.MannWhitneyUMaxPeriod + IndicatorDefaultConstants.MannWhitneyUMaxBaselinePeriod <= 512);
        Assert.True(IndicatorDefaultConstants.MannWhitneyUMaxPeriod < IndicatorDefaultConstants.TwoSampleTTestMaxPeriod);
        Assert.True(IndicatorDefaultConstants.MannWhitneyUMaxBaselinePeriod < IndicatorDefaultConstants.TwoSampleTTestMaxBaselinePeriod);
    }

    [Fact]
    public void TheTwoSampleTests_AFlatBaselineAndAnUnusablePriceGiveNoValue_WhereTheTestRefuses()
    {
        // Welch: two flat windows are a division by zero; a flat window against a varying one is fine (the kernel contributes exactly 0 for the flat side).
        var allFlat = FromCloses(Enumerable.Repeat(100m, 40));
        Assert.All(new CoreTwoSampleTTestIndicator { Period = 5, BaselinePeriod = 10 }.Calculate(allFlat).MainValues, v => Assert.Null(v));

        var candles = Candles(80);
        candles[40] = new CoreCandleData(candles[40].Timestamp, 0m, 0m, 0m, 0m, 1000);
        var result = new CoreKsTwoSampleTestIndicator { Period = 5, BaselinePeriod = 10 }.Calculate(candles);
        // A simple return accepts a price of 0 as the current price (-100 %) and refuses it as the previous price: the return of bar 41 is unusable, so every window pair that holds it
        // (the bars 41 .. 41 + 5 + 10 - 1) has no value; the pair that ends at bar 40 holds only usable returns.
        Assert.NotNull(result.MainValues[40]);
        for (int i = 41; i <= 55; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        Assert.NotNull(result.MainValues[56]);
        Assert.NotNull(result.MainValues[39]);
    }

    // ---------------------------------------------------------------- KlDivergence and the smoothed histogram

    [Fact]
    public void SmoothedHistogram_GivesTwoProbabilityVectors_WithTheDocumentedBinRule()
    {
        // Union range [0, 4], 4 bins: 0 -> bin 0, 1 -> bin 1, 2 -> bin 2, 3 -> bin 3, 4 -> bin 3 (the maximum goes to the last bin).
        double[] first = { 0.0, 1.0, 1.5, 4.0 };
        double[] second = { 2.0, 3.0 };
        var p = new double[4];
        var q = new double[4];

        Assert.True(SmoothedHistogramMath.TryBuild(first, second, 4, p, q));

        const double alpha = SmoothedHistogramMath.LaplaceAlpha;
        double dp = 4 + (4 * alpha);
        double dq = 2 + (4 * alpha);
        Assert.Equal((1 + alpha) / dp, p[0], 15);
        Assert.Equal((2 + alpha) / dp, p[1], 15);
        Assert.Equal((0 + alpha) / dp, p[2], 15);
        Assert.Equal((1 + alpha) / dp, p[3], 15);
        Assert.Equal((0 + alpha) / dq, q[0], 15);
        Assert.Equal((1 + alpha) / dq, q[2], 15);
        Assert.Equal(1.0, p.Sum(), 12);
        Assert.Equal(1.0, q.Sum(), 12);
        Assert.All(p.Concat(q), v => Assert.True(v > 1e-12));
    }

    [Fact]
    public void SmoothedHistogram_RefusesWhatHasNoBins()
    {
        var p = new double[4];
        var q = new double[4];
        Assert.False(SmoothedHistogramMath.TryBuild(new[] { 1.0, 1.0 }, new[] { 1.0 }, 4, p, q));           // an empty range
        Assert.False(SmoothedHistogramMath.TryBuild(new[] { 1.0, double.NaN }, new[] { 2.0 }, 4, p, q));
        Assert.False(SmoothedHistogramMath.TryBuild(ReadOnlySpan<double>.Empty, new[] { 2.0 }, 4, p, q));
        Assert.False(SmoothedHistogramMath.TryBuild(new[] { 1.0 }, new[] { 2.0 }, 0, p, q));
        Assert.False(SmoothedHistogramMath.TryBuild(new[] { 1.0 }, new[] { 2.0 }, 5, p, q));                // buffers shorter than the bin count
    }

    [Fact]
    public void KlDivergence_EqualsTheIndependentSumOnEveryWindow_IsNotNegative_AndZeroForTheSameWindow()
    {
        var candles = Candles(160, 21);
        const int period = 20;
        const int baseline = 60;
        const int bins = 8;
        var result = new CoreKlDivergenceIndicator { Period = period, BaselinePeriod = baseline, BinCount = bins, UseLogReturn = false }.Calculate(candles);
        double[] r = Returns(candles);

        for (int i = period + baseline; i < candles.Count; i++)
        {
            double[] recent = Slice(r, i - period + 1, period);
            double[] old = Slice(r, i - period - baseline + 1, baseline);
            double min = Math.Min(recent.Min(), old.Min());
            double max = Math.Max(recent.Max(), old.Max());
            double[] pc = new double[bins];
            double[] qc = new double[bins];
            foreach (double v in recent) pc[Math.Min(bins - 1, (int)Math.Floor((v - min) / (max - min) * bins))] += 1;
            foreach (double v in old) qc[Math.Min(bins - 1, (int)Math.Floor((v - min) / (max - min) * bins))] += 1;
            double kl = 0.0;
            for (int b = 0; b < bins; b++)
            {
                double pb = (pc[b] + 1e-6) / (period + (bins * 1e-6));
                double qb = (qc[b] + 1e-6) / (baseline + (bins * 1e-6));
                kl += pb * Math.Log(pb / qb);
            }

            Assert.Equal(kl, (double)result.MainValues[i]!.Value, 9);
            Assert.True((double)result.MainValues[i]!.Value >= -1e-12);
        }

        var same = new double[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6 };
        var p = new double[4];
        var q = new double[4];
        Assert.True(SmoothedHistogramMath.TryBuild(same, same, 4, p, q));
        Assert.Equal(0.0, DslQuantAndInfoMath.KlDivergence(p, q).Value, 15);
    }

    [Fact]
    public void KlDivergence_EqualReturnsOnBothWindowsGiveNoValue_AndTheParametersAreValidated()
    {
        // Equal prices: every return is exactly 0, so the shared range of both windows is empty. (A geometric series is NOT exactly equal in returns: rounding noise gives a real range.)
        var flat = FromCloses(Enumerable.Repeat(100m, 60));
        Assert.All(new CoreKlDivergenceIndicator { Period = 5, BaselinePeriod = 10 }.Calculate(flat).MainValues, v => Assert.Null(v));

        var parameter = new CoreKlDivergenceParameter();
        Assert.True(parameter.UseLogReturn);
        Assert.Equal((20, 60, 10), (parameter.Period, parameter.BaselinePeriod, parameter.BinCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKlDivergenceParameter { BinCount = 3 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKlDivergenceParameter { BinCount = 65 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKlDivergenceParameter { Period = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKlDivergenceParameter { Period = 251 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKlDivergenceParameter { BaselinePeriod = 501 }.Validate());
        new CoreKlDivergenceParameter { Period = 5, BaselinePeriod = 5, BinCount = 4 }.Validate();
        new CoreKlDivergenceParameter { Period = 250, BaselinePeriod = 500, BinCount = 64 }.Validate();
        Assert.False(new CoreKlDivergenceIndicator { BinCount = 3 }.Calculate(Candles(100)).IsSuccessful);
    }

    [Fact]
    public void ParametersAreValidated_AndTheDefaultsAreTheDocumentedOnes()
    {
        Assert.Equal((30, 0m, false), (new CoreOneSampleTTestParameter().Period, new CoreOneSampleTTestParameter().NullMean, new CoreOneSampleTTestParameter().UseLogReturn));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreOneSampleTTestParameter { Period = 2 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreOneSampleTTestParameter { Period = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreOneSampleTTestParameter { NullMean = 1.01m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreOneSampleTTestParameter { NullMean = -1.01m }.Validate());
        new CoreOneSampleTTestParameter { Period = 3, NullMean = -1m }.Validate();

        foreach (CoreTwoWindowReturnParameterBase parameter in new CoreTwoWindowReturnParameterBase[] { new CoreTwoSampleTTestParameter(), new CoreKsTwoSampleTestParameter(), new CoreMannWhitneyUTestParameter() })
        {
            Assert.Equal((20, 60, false), (parameter.Period, parameter.BaselinePeriod, parameter.UseLogReturn));
            Assert.Equal(80, parameter.GetRequiredWarmupBars());
            parameter.Period = 2;
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.Validate());
            parameter.Period = 20;
            parameter.BaselinePeriod = 2;
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.Validate());
        }

        Assert.False(new CoreOneSampleTTestIndicator { Period = 2 }.Calculate(Candles(30)).IsSuccessful);
        Assert.False(new CoreTwoSampleTTestIndicator { BaselinePeriod = 2 }.Calculate(Candles(30)).IsSuccessful);
    }

    // ---------------------------------------------------------------- contract shared by the five types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.OneSampleTTest, true, ChannelNormalization.WindowZScore, true },
        new object[] { IndicatorType.TwoSampleTTest, true, ChannelNormalization.WindowZScore, true },
        new object[] { IndicatorType.KsTwoSampleTest, false, ChannelNormalization.None, true },
        new object[] { IndicatorType.MannWhitneyUTest, false, ChannelNormalization.WindowZScore, true },
        new object[] { IndicatorType.KlDivergence, false, ChannelNormalization.WindowZScore, false },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_AsASubPaneTest_DisabledByDefault(IndicatorType type, bool zeroBaseline, ChannelNormalization normalization, bool hasPValue)
    {
        _ = hasPValue;
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.False(((CoreIndicatorBase)IndicatorFactory.Default.Create(type)!).IsOverlay);

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(zeroBaseline, settings.IncludeZeroBaseline);
        settings.ParameterObject!.Validate();
        Assert.True(settings.ParameterObject!.GetRequiredWarmupBars() > 0);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);

        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { "Math" }, groups);
        Assert.Equal(normalization, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EveryDefaultSeriesIsEmitted_AndTheSuppressedPValueHasNoColourEntry(IndicatorType type, bool zeroBaseline, ChannelNormalization normalization, bool hasPValue)
    {
        _ = (zeroBaseline, normalization);
        var factory = IndicatorFactory.Default;
        var result = factory.Create(type)!.Calculate(Candles(200));
        Assert.True(result.IsSuccessful);
        Assert.Contains(result.MainValues, v => v.HasValue);
        Assert.Equal(hasPValue, result.HasSeries("PValue"));
        Assert.Equal(hasPValue, IndicatorChartSuppression.IsSuppressed(type, "PValue"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(type, IndicatorResult.MainSeriesName));

        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: factory);
            Assert.True(result.HasSeries(name), $"{type}: '{name}' is not emitted");
        }

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == IndicatorResult.MainSeriesName && sc.TargetSeries.Contains(IndicatorResult.MainSeriesName));
        foreach (SeriesColorConfig colour in settings.SeriesColors)
        {
            foreach (string target in colour.TargetSeries)
            {
                Assert.False(IndicatorChartSuppression.IsSuppressed(type, target), $"{type}: the suppressed series '{target}' must not have a colour entry");
            }
        }
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndNeverReadsTheFuture(IndicatorType type, bool zeroBaseline, ChannelNormalization normalization, bool hasPValue)
    {
        _ = (zeroBaseline, normalization, hasPValue);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));

        var original = Candles(260);
        var poisoned = original.ToList();
        for (int i = 180; i < poisoned.Count; i++)
        {
            decimal c = original[i].Close * 7m + 13m;
            poisoned[i] = new CoreCandleData(original[i].Timestamp, c, c, c, c, 1);
        }

        var factory = IndicatorFactory.Default;
        var before = factory.Create(type)!.Calculate(original).MainValues;
        var after = factory.Create(type)!.Calculate(poisoned).MainValues;
        Assert.Contains(before.Take(180), v => v.HasValue);
        for (int i = 0; i < 180; i++)
        {
            Assert.Equal(before[i], after[i]);
        }

        Assert.Empty(factory.Create(type)!.Calculate(new List<CoreCandleData>()).MainValues);
        Assert.All(factory.Create(type)!.Calculate(Candles(1)).MainValues, v => Assert.Null(v));
    }
}
