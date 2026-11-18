using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// Statistical functions plan, Run R4: <see cref="CoreDownsideDeviationIndicator"/> and <see cref="CoreBipowerVariationIndicator"/>. The values are pinned to the window kernels
/// (<see cref="AsymmetricRiskMath"/>, <see cref="JumpVariationMath"/>) evaluated by hand on the returns of the same candles, together with the registration contract of plan section 4.1.
/// </summary>
public class StatisticalVolatilityIndicatorsTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 23)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i % 19 == 0 ? -0.04 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FlatCandles(int count) =>
        Enumerable.Range(0, count).Select(i => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), 50m, 50m, 50m, 50m, 1000L + i)).ToList();

    private static ReturnSeriesWindow Returns(IReadOnlyList<CoreCandleData> candles, bool log) =>
        ReturnSeriesWindow.FromPrices(candles.Select(c => (decimal?)c.Close).ToList(), log);

    // ---------------------------------------------------------------- DownsideDeviation

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.002)]
    [InlineData(-0.01)]
    public void DownsideDeviation_EqualsTheKernelOnTheSimpleReturnWindow_AndWarmsUpAtBarPeriod(double target)
    {
        var candles = Candles(120);
        const int period = 20;
        var result = new CoreDownsideDeviationIndicator { Period = period, TargetReturn = target }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(candles.Count, result.MainValues.Count);
        ReturnSeriesWindow returns = Returns(candles, log: false);
        decimal targetDecimal = (decimal)target;
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(result.GetSeries(CoreDownsideDeviationIndicator.UpsideDeviationSeriesName)[i]);
                continue;
            }

            Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(returns.DecimalReturns.AsSpan(i - period + 1, period), targetDecimal, out DownsideStatistics s));
            Assert.Equal(s.DownsideDeviation, result.MainValues[i]);
            Assert.Equal(s.UpsideDeviation, result.GetSeries(CoreDownsideDeviationIndicator.UpsideDeviationSeriesName)[i]);
            Assert.Equal(s.RollingSortino, result.GetSeries(CoreDownsideDeviationIndicator.RollingSortinoSeriesName)[i]);
            Assert.Equal(s.SemivarianceAsymmetry, result.GetSeries(CoreDownsideDeviationIndicator.SemivarianceAsymmetrySeriesName)[i]);
        }
    }

    [Fact]
    public void DownsideDeviation_AFlatPriceSeries_GivesZeroDeviationsAndNullQuotients()
    {
        var result = new CoreDownsideDeviationIndicator { Period = 10 }.Calculate(FlatCandles(40));

        Assert.True(result.IsSuccessful);
        Assert.Null(result.MainValues[9]);
        for (int i = 10; i < 40; i++)
        {
            Assert.Equal(0m, result.MainValues[i]);
            Assert.Equal(0m, result.GetSeries(CoreDownsideDeviationIndicator.UpsideDeviationSeriesName)[i]);
            Assert.Null(result.GetSeries(CoreDownsideDeviationIndicator.RollingSortinoSeriesName)[i]);
            Assert.Null(result.GetSeries(CoreDownsideDeviationIndicator.SemivarianceAsymmetrySeriesName)[i]);
        }
    }

    [Fact]
    public void DownsideDeviation_ADefaultRunIsCausalInTheDecimalDomain_AndTargetOutOfRangeFails()
    {
        Assert.Equal(IndicatorDefaultConstants.DownsideDeviationDefaultPeriod, new CoreDownsideDeviationIndicator().Period);
        Assert.False(new CoreDownsideDeviationIndicator { TargetReturn = 0.5 }.Calculate(Candles(60)).IsSuccessful);
        Assert.False(new CoreDownsideDeviationIndicator { TargetReturn = double.NaN }.Calculate(Candles(60)).IsSuccessful);
        Assert.False(new CoreDownsideDeviationIndicator { Period = 4 }.Calculate(Candles(60)).IsSuccessful);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreDownsideDeviationParameter { TargetReturn = 0.11 }.Validate());
    }

    // ---------------------------------------------------------------- BipowerVariation

    [Fact]
    public void BipowerVariation_UnderAConstantDrift_ShowsAContinuousVolatilityAboveTheTotalOne_AsAPropertyOfTheEstimator()
    {
        // Owner decision D-2 = B of the peer-review follow-up: Main = sqrt(BPV) is the literature estimator and is kept. Its theory assumes no drift: with a return of +1 % in every bar
        // RV = r^2 but BPV = (pi / 2) r^2, so Main = sqrt(pi / 2) r is about 1.2533 times TotalVolatility = r, and the jump part max(0, RV - BPV) is 0. This is documented behaviour, not a defect.
        decimal close = 100m;
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 40; i++)
        {
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), close, close, close, close, 1000L));
            close *= 1.01m;
        }

        var result = new CoreBipowerVariationIndicator { Period = 20 }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        double logReturn = Math.Log(1.01);
        double main = (double)result.MainValues[^1]!.Value;
        double total = (double)result.GetSeries(CoreBipowerVariationIndicator.TotalVolatilitySeriesName)[^1]!.Value;
        Assert.Equal(logReturn, total, 12);
        Assert.Equal(Math.Sqrt(Math.PI / 2.0) * logReturn, main, 12);
        Assert.True(main > total);
        Assert.Equal(0m, result.GetSeries(CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName)[^1]);
        Assert.Equal(0m, result.GetSeries(CoreBipowerVariationIndicator.JumpRatioSeriesName)[^1]);
    }

    [Fact]
    public void BipowerVariation_EqualsTheKernelOnTheLogReturnWindow_AndWarmsUpAtBarPeriod()
    {
        var candles = Candles(120);
        const int period = 20;
        var result = new CoreBipowerVariationIndicator { Period = period }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        ReturnSeriesWindow returns = Returns(candles, log: true);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(result.GetSeries(CoreBipowerVariationIndicator.TotalVolatilitySeriesName)[i]);
                Assert.Null(result.GetSeries(CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName)[i]);
                Assert.Null(result.GetSeries(CoreBipowerVariationIndicator.JumpRatioSeriesName)[i]);
                continue;
            }

            Assert.True(JumpVariationMath.TryCompute(returns.Returns.AsSpan(i - period + 1, period), out JumpVariation v));
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(v.BipowerVolatility), result.MainValues[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(v.TotalVolatility), result.GetSeries(CoreBipowerVariationIndicator.TotalVolatilitySeriesName)[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(v.JumpVolatility), result.GetSeries(CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName)[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(v.JumpRatio), result.GetSeries(CoreBipowerVariationIndicator.JumpRatioSeriesName)[i]);
            Assert.InRange(result.GetSeries(CoreBipowerVariationIndicator.JumpRatioSeriesName)[i]!.Value, 0m, 1m);
        }
    }

    [Fact]
    public void BipowerVariation_AFlatPriceSeries_GivesZeroVolatilitiesAndNoJumpRatio()
    {
        var result = new CoreBipowerVariationIndicator { Period = 10 }.Calculate(FlatCandles(40));

        Assert.True(result.IsSuccessful);
        for (int i = 10; i < 40; i++)
        {
            Assert.Equal(0m, result.MainValues[i]);
            Assert.Equal(0m, result.GetSeries(CoreBipowerVariationIndicator.TotalVolatilitySeriesName)[i]);
            Assert.Equal(0m, result.GetSeries(CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName)[i]);
            Assert.Null(result.GetSeries(CoreBipowerVariationIndicator.JumpRatioSeriesName)[i]);
        }
    }

    [Fact]
    public void BipowerVariation_AJumpBar_RaisesTheJumpComponentAboveTheContinuousVolatility()
    {
        var candles = Candles(80);
        var jump = candles.ToList();
        decimal c = jump[60].Close * 1.2m; // a +20 % gap on one bar
        jump[60] = new CoreCandleData(jump[60].Timestamp, c, c, c, c, jump[60].Volume);
        for (int i = 61; i < jump.Count; i++)
        {
            decimal p = jump[i].Close * 1.2m;
            jump[i] = new CoreCandleData(jump[i].Timestamp, p, p, p, p, jump[i].Volume);
        }

        var calm = new CoreBipowerVariationIndicator { Period = 20 }.Calculate(candles);
        var jumpy = new CoreBipowerVariationIndicator { Period = 20 }.Calculate(jump);
        IReadOnlyList<decimal?> jumpComponent = jumpy.GetSeries(CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName);

        Assert.True(jumpComponent[60] > calm.GetSeries(CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName)[60]);
        Assert.True(jumpy.GetSeries(CoreBipowerVariationIndicator.TotalVolatilitySeriesName)[60] > jumpy.MainValues[60]);
        // Bar 59 precedes the jump: its window is identical in both series.
        Assert.Equal(calm.MainValues[59], jumpy.MainValues[59]);
    }

    // ---------------------------------------------------------------- contract shared by the two types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.DownsideDeviation },
        new object[] { IndicatorType.BipowerVariation },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_WithItsParameterObject_AndIsDisabledInASubPanelByDefault(IndicatorType type)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volatility, settings.Category);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
        Assert.NotNull(settings.ParameterObject);
        settings.ParameterObject!.Validate();
        Assert.Equal(20, settings.ParameterObject!.GetRequiredWarmupBars());
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

    [Fact]
    public void TheScaleSeries_AreChartSuppressed_AndTheDrawnSeriesAreNot()
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.DownsideDeviation, "RollingSortino"));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.DownsideDeviation, "SemivarianceAsymmetry"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.DownsideDeviation, "Main"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.DownsideDeviation, "UpsideDeviation"));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.BipowerVariation, "JumpRatio"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.BipowerVariation, "TotalVolatility"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.BipowerVariation, "JumpComponentHistogram"));
        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.DownsideDeviation, "RollingSortino"));
        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.BipowerVariation, "JumpRatio"));
    }

    [Fact]
    public void TheJumpSeriesName_ContainsHistogram_SoTheExistingRendererDrawsBars()
    {
        Assert.Contains("Histogram", CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName, StringComparison.OrdinalIgnoreCase);
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.BipowerVariation);
        Assert.Contains(settings.SeriesColors, sc => sc.TargetSeries.Contains(CoreBipowerVariationIndicator.JumpComponentHistogramSeriesName));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheClassification_IsVolatilityGroup_ZScoredFeatureChannel_AndTheTypeIsCausalAndEligible(IndicatorType type)
    {
        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { "Volatility" }, groups);

        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(type));
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
    public void AnUnusablePriceInTheWindow_GivesNullForEveryBarThatContainsIt()
    {
        var candles = Candles(100);
        var prices = candles.Select(c => (decimal?)c.Close).ToList();
        prices[50] = null;
        const int period = 20;
        foreach (bool log in new[] { false, true })
        {
            ReturnSeriesWindow returns = ReturnSeriesWindow.FromPrices(prices, log);
            for (int i = period; i < prices.Count; i++)
            {
                Assert.Equal(i < 50 || i > 51 + period - 1, returns.IsValid(i - period + 1, i));
            }
        }
    }
}
