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
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// Statistical functions plan, Run R3: <see cref="CoreSkewnessKurtosisIndicator"/>, <see cref="CoreJarqueBeraIndicator"/> and <see cref="CoreLjungBoxQIndicator"/>.
/// The values are pinned to the window statistics of <see cref="WindowMoments"/> / <see cref="NormalityAndTailMath"/> / <see cref="SerialDependenceMath"/> computed by hand on the
/// returns of the same candles (so a wrong window, a wrong warm-up or a wrong return basis fails), together with the registration contract of plan section 4.1:
/// warm-up, null propagation, flat window, causality, constant reference lines that exist only where Main exists, chart suppression, classification and parameter ranges.
/// </summary>
public class StatisticalReturnIndicatorsTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 11)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i % 17 == 0 ? 0.03 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FlatCandles(int count) =>
        Enumerable.Range(0, count).Select(i => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), 50m, 50m, 50m, 50m, 1000L + i)).ToList();

    private static double[] LogReturns(IReadOnlyList<CoreCandleData> candles) =>
        ReturnSeriesWindow.FromPrices(candles.Select(c => (decimal?)c.Close).ToList(), useLogReturn: true).Returns;

    private static double[] SimpleReturns(IReadOnlyList<CoreCandleData> candles) =>
        ReturnSeriesWindow.FromPrices(candles.Select(c => (decimal?)c.Close).ToList(), useLogReturn: false).Returns;

    private static double Close(IReadOnlyList<CoreCandleData> candles, int i) => (double)candles[i].Close;

    [Fact]
    public void ReturnSeriesWindow_SimpleAndLogReturns_FollowTheSharedDefinitions()
    {
        var prices = new List<decimal?> { 100m, 110m, null, 120m, 0m, 130m };
        var simple = ReturnSeriesWindow.FromPrices(prices, useLogReturn: false);
        var log = ReturnSeriesWindow.FromPrices(prices, useLogReturn: true);

        Assert.Equal(6, simple.Count);
        Assert.True(double.IsNaN(simple.Returns[0]));
        Assert.Equal(0.1, simple.Returns[1], 15);
        Assert.Equal(Math.Log(1.1), log.Returns[1], 14);
        // Simple return needs only a positive previous price (a price of 0 gives -100 %); the log return needs both prices positive.
        Assert.True(double.IsNaN(simple.Returns[2]) && double.IsNaN(simple.Returns[3]) && double.IsNaN(simple.Returns[5]));
        Assert.Equal(-1.0, simple.Returns[4], 15);
        Assert.True(double.IsNaN(log.Returns[2]) && double.IsNaN(log.Returns[3]) && double.IsNaN(log.Returns[4]) && double.IsNaN(log.Returns[5]));
        Assert.True(simple.IsValid(1, 1));
        Assert.False(simple.IsValid(0, 1));
        Assert.False(log.IsValid(1, 3));
    }

    // ---------------------------------------------------------------- SkewnessKurtosis

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SkewnessKurtosis_EqualsTheMomentsOfTheReturnWindow_AndWarmsUpAtBarPeriod(bool useLogReturn)
    {
        var candles = Candles(120);
        const int period = 30;
        var result = new CoreSkewnessKurtosisIndicator { Period = period, UseLogReturn = useLogReturn }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(candles.Count, result.MainValues.Count);
        IReadOnlyList<decimal?> kurtosis = result.GetSeries(CoreSkewnessKurtosisIndicator.ExcessKurtosisSeriesName);
        double[] returns = useLogReturn ? LogReturns(candles) : SimpleReturns(candles);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(kurtosis[i]);
                continue;
            }

            Assert.True(WindowMoments.TryCompute(returns.AsSpan(i - period + 1, period), FlatWindowRule.Returns, out WindowMoments m));
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(m.SampleSkewness), result.MainValues[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(m.SampleExcessKurtosis), kurtosis[i]);
            Assert.NotNull(result.MainValues[i]);
        }
    }

    [Fact]
    public void SkewnessKurtosis_DefaultsAndLogVersusSimpleDiffer()
    {
        var candles = Candles(80);
        var byDefault = new CoreSkewnessKurtosisIndicator().Calculate(candles);
        var log = new CoreSkewnessKurtosisIndicator { UseLogReturn = true }.Calculate(candles);
        var simple = new CoreSkewnessKurtosisIndicator { UseLogReturn = false }.Calculate(candles);

        Assert.Equal(IndicatorDefaultConstants.SkewnessKurtosisDefaultPeriod, new CoreSkewnessKurtosisIndicator().Period);
        Assert.Equal(log.MainValues, byDefault.MainValues);
        Assert.NotEqual(log.MainValues[^1], simple.MainValues[^1]);
    }

    [Fact]
    public void SkewnessKurtosis_AFlatWindowGivesNullForBothSeries()
    {
        var result = new CoreSkewnessKurtosisIndicator { Period = 10 }.Calculate(FlatCandles(40));

        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, v => Assert.Null(v));
        Assert.All(result.GetSeries(CoreSkewnessKurtosisIndicator.ExcessKurtosisSeriesName), v => Assert.Null(v));
    }

    // ---------------------------------------------------------------- JarqueBera

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JarqueBera_EqualsTheTestOfTheReturnWindow_WithTheSignificanceLineWhereMainExists(bool useLogReturn)
    {
        var candles = Candles(150);
        const int period = 60;
        var result = new CoreJarqueBeraIndicator { Period = period, Alpha = 0.1, UseLogReturn = useLogReturn }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        IReadOnlyList<decimal?> level = result.GetSeries(CoreJarqueBeraIndicator.SignificanceLevelSeriesName);
        IReadOnlyList<decimal?> statistic = result.GetSeries(CoreJarqueBeraIndicator.JBStatisticSeriesName);
        double[] returns = useLogReturn ? LogReturns(candles) : SimpleReturns(candles);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(statistic[i]);
                Assert.Null(level[i]);
                continue;
            }

            Assert.True(WindowMoments.TryCompute(returns.AsSpan(i - period + 1, period), FlatWindowRule.Returns, out WindowMoments m));
            Assert.True(NormalityAndTailMath.TryJarqueBera(m, out double jb, out double p));
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(p), result.MainValues[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(jb), statistic[i]);
            Assert.Equal(0.1m, level[i]);
            Assert.InRange(result.MainValues[i]!.Value, 0m, 1m);
        }
    }

    [Fact]
    public void JarqueBera_ConstantReferenceLine_IsNullExactlyWhereMainIsNull_AndAllNullWhenTheHistoryIsTooShort()
    {
        var shortResult = new CoreJarqueBeraIndicator { Period = 60 }.Calculate(Candles(40));
        Assert.True(shortResult.IsSuccessful);
        Assert.All(shortResult.MainValues, v => Assert.Null(v));
        Assert.All(shortResult.GetSeries(CoreJarqueBeraIndicator.SignificanceLevelSeriesName), v => Assert.Null(v));
        Assert.All(shortResult.GetSeries(CoreJarqueBeraIndicator.JBStatisticSeriesName), v => Assert.Null(v));

        var flat = new CoreJarqueBeraIndicator { Period = 10 }.Calculate(FlatCandles(40));
        Assert.All(flat.MainValues, v => Assert.Null(v));
        Assert.All(flat.GetSeries(CoreJarqueBeraIndicator.SignificanceLevelSeriesName), v => Assert.Null(v));
    }

    [Fact]
    public void JarqueBera_AWindowWithAnUnusablePrice_GivesNullForEveryBarThatContainsIt()
    {
        var candles = Candles(100);
        const int period = 20;
        var prices = candles.Select(c => (decimal?)c.Close).ToList();
        prices[50] = null;
        var returns = ReturnSeriesWindow.FromPrices(prices, useLogReturn: true);

        // The null price spoils the returns of bars 50 and 51, so the windows ending at bars 50 .. 51 + period - 1 are invalid.
        for (int i = period; i < prices.Count; i++)
        {
            bool expectedValid = i < 50 || i > 51 + period - 1;
            Assert.Equal(expectedValid, returns.IsValid(i - period + 1, i));
        }
    }

    // ---------------------------------------------------------------- LjungBoxQ

    [Fact]
    public void LjungBoxQ_EqualsTheTestOfTheLogReturnWindowAtTheEffectiveLag()
    {
        var candles = Candles(150);
        foreach ((int period, int maxLag, int expectedLag) in new[] { (60, 10, 10), (20, 10, 4), (60, 30, 12) })
        {
            var result = new CoreLjungBoxQIndicator { Period = period, MaxLag = maxLag, Alpha = 0.01 }.Calculate(candles);
            Assert.True(result.IsSuccessful);
            Assert.Equal(expectedLag, SerialDependenceMath.EffectiveLjungBoxLag(period, maxLag));
            IReadOnlyList<decimal?> statistic = result.GetSeries(CoreLjungBoxQIndicator.QStatisticSeriesName);
            IReadOnlyList<decimal?> level = result.GetSeries(CoreLjungBoxQIndicator.SignificanceLevelSeriesName);
            double[] returns = LogReturns(candles);
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < period)
                {
                    Assert.Null(result.MainValues[i]);
                    Assert.Null(statistic[i]);
                    Assert.Null(level[i]);
                    continue;
                }

                Assert.True(SerialDependenceMath.TryLjungBox(returns.AsSpan(i - period + 1, period), expectedLag, out double q, out double p));
                Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(p), result.MainValues[i]);
                Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(q), statistic[i]);
                Assert.Equal(0.01m, level[i]);
            }
        }
    }

    [Fact]
    public void LjungBoxQ_AFlatWindowGivesNull_ForEverySeries()
    {
        var result = new CoreLjungBoxQIndicator { Period = 20 }.Calculate(FlatCandles(60));

        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, v => Assert.Null(v));
        Assert.All(result.GetSeries(CoreLjungBoxQIndicator.QStatisticSeriesName), v => Assert.Null(v));
        Assert.All(result.GetSeries(CoreLjungBoxQIndicator.SignificanceLevelSeriesName), v => Assert.Null(v));
    }

    // ---------------------------------------------------------------- contract shared by the three types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.SkewnessKurtosis },
        new object[] { IndicatorType.JarqueBera },
        new object[] { IndicatorType.LjungBoxQ },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_WithItsParameterObject_AndIsDisabledInASubPanelByDefault(IndicatorType type)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.NotNull(IndicatorFactory.Default.Create(type));

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.NotNull(settings.ParameterObject);
        settings.ParameterObject!.Validate();
        Assert.True(settings.ParameterObject!.GetRequiredWarmupBars() > 0);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EveryDefaultSeriesIsEmitted_AndMainIsMappedExplicitly_AndSuppressedSeriesHaveNoColourEntry(IndicatorType type)
    {
        var factory = IndicatorFactory.Default;
        var result = factory.Create(type)!.Calculate(Candles(200));
        Assert.True(result.IsSuccessful);

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
    [InlineData(IndicatorType.JarqueBera)]
    [InlineData(IndicatorType.LjungBoxQ)]
    public void ThePValueTypes_HaveAFixedUnitAxis_AndTheirStatisticLeavesIt(IndicatorType type)
    {
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.Equal(0m, settings.MinValue);
        Assert.Equal(1m, settings.MaxValue);
        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(type));
        string statistic = type == IndicatorType.JarqueBera ? CoreJarqueBeraIndicator.JBStatisticSeriesName : CoreLjungBoxQIndicator.QStatisticSeriesName;
        Assert.Contains((type, statistic), ScatterAxisRangeResolver.OutputsOutsideTheirRange);
        Assert.True(IndicatorChartSuppression.IsSuppressed(type, statistic));
    }

    [Fact]
    public void SkewnessKurtosis_HasAnAutomaticAxis_AndIsZScoredAsAFeatureChannel()
    {
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.SkewnessKurtosis);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.SkewnessKurtosis));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheScreenerGroupIsMath_InExactlyOneGroup(IndicatorType type)
    {
        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();

        Assert.Equal(new[] { "Math" }, groups);
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndSupportsAnIndicatorTimeframe(IndicatorType type)
    {
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.False(ScreenerIndicatorEligibility.IsUniverseBreadthType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void FutureBarsDoNotChangeThePast(IndicatorType type)
    {
        var original = Candles(220);
        var poisoned = original.ToList();
        for (int i = 150; i < poisoned.Count; i++)
        {
            decimal absurd = 1m + (i % 7) * 400m;
            poisoned[i] = new CoreCandleData(poisoned[i].Timestamp, absurd, absurd, absurd, absurd, 1L);
        }

        var a = IndicatorFactory.Default.Create(type)!.Calculate(original);
        var b = IndicatorFactory.Default.Create(type)!.Calculate(poisoned);
        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, IndicatorFactory.Default))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: IndicatorFactory.Default);
            Assert.Equal(a.GetSeries(name).Take(150), b.GetSeries(name).Take(150));
            Assert.Contains(a.GetSeries(name).Take(150), v => v.HasValue);
        }
    }

    [Fact]
    public void ParameterRanges_AreChecked_AndBadValuesFailTheCalculation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreSkewnessKurtosisParameter { Period = IndicatorDefaultConstants.SkewnessKurtosisMinPeriod - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreJarqueBeraParameter { Period = IndicatorDefaultConstants.JarqueBeraMaxPeriod + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreJarqueBeraParameter { Alpha = 0.0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreJarqueBeraParameter { Alpha = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreLjungBoxQParameter { Period = IndicatorDefaultConstants.LjungBoxMinPeriod - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreLjungBoxQParameter { MaxLag = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreLjungBoxQParameter { Alpha = 0.5 }.Validate());
        new CoreLjungBoxQParameter { Period = IndicatorDefaultConstants.LjungBoxMinPeriod, MaxLag = IndicatorDefaultConstants.LjungBoxMaxMaxLag }.Validate();

        // No cross-field rule (the settings dialog commits one field at a time): a lag above the Period/5 cap is valid and handled at calculation time.
        var result = new CoreLjungBoxQIndicator { Period = 20, MaxLag = 30 }.Calculate(Candles(60));
        Assert.True(result.IsSuccessful);
        Assert.Contains(result.MainValues, v => v.HasValue);

        Assert.False(new CoreJarqueBeraIndicator { Period = 3 }.Calculate(Candles(60)).IsSuccessful);
    }

    [Fact]
    public void EachCalculationAllocatesNothingPerBarInTheMathKernels()
    {
        double[] window = LogReturns(Candles(100)).AsSpan(40, 60).ToArray();
        WindowMoments.TryCompute(window, FlatWindowRule.Returns, out WindowMoments warm);
        NormalityAndTailMath.TryJarqueBera(warm, out _, out _);
        SerialDependenceMath.TryLjungBox(window, 10, out _, out _);

        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                WindowMoments.TryCompute(window, FlatWindowRule.Returns, out WindowMoments m);
                NormalityAndTailMath.TryJarqueBera(m, out _, out _);
                SerialDependenceMath.TryLjungBox(window, 10, out _, out _);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
