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
/// Statistical functions plan, Run R5: <see cref="CoreAdfStationarityIndicator"/>. The values are pinned to the kernel on the same window of (log) prices and, end to end,
/// to the 50-digit mpmath reference of the regression (the last bar of a 40-bar series), together with the registration contract of plan section 4.1.
/// </summary>
public class StatisticalAdfIndicatorTests
{
    // Same 40 values as StationarityTestMathTests.MeanReverting (mpmath references: tau -2.7387..., -1.8616..., -1.4741... for lag order 0, 1, 3).
    private static readonly decimal[] MeanReverting =
    {
        4.6m, 4.611406m, 4.59997m, 4.607909m, 4.602372m, 4.597298m, 4.605703m, 4.607145m, 4.603603m, 4.62m, 4.591081m, 4.588951m, 4.586326m, 4.605156m, 4.599063m, 4.602735m, 4.594345m,
        4.61325m, 4.613829m, 4.584433m, 4.589647m, 4.575581m, 4.596475m, 4.597884m, 4.592537m, 4.589711m, 4.582046m, 4.595235m, 4.583876m, 4.585968m, 4.572599m, 4.577121m, 4.581433m,
        4.58502m, 4.587108m, 4.602916m, 4.604576m, 4.606538m, 4.613233m, 4.625811m
    };

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

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i)).ToList();

    private static double[] Levels(IReadOnlyList<CoreCandleData> candles, bool log) =>
        candles.Select(c => log ? Math.Log((double)c.Close) : (double)c.Close).ToArray();

    private static AugmentedDickeyFullerResult Kernel(double[] window, int lagOrder)
    {
        double[] scratch = new double[StationarityTestMath.ScratchLength(window.Length, lagOrder)];
        Assert.True(StationarityTestMath.TryAugmentedDickeyFuller(window, lagOrder, IndicatorDefaultConstants.HalfLifeLogEpsilon, scratch, out AugmentedDickeyFullerResult r));
        return r;
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 4)]
    public void Indicator_EqualsTheKernelOnTheWindow_WarmsUpAtBarPeriodMinusOne_AndTheLineExistsWhereMainExists(bool useLogPrice, int lagOrder)
    {
        var candles = Candles(160);
        const int period = 60;
        var result = new CoreAdfStationarityIndicator { Period = period, LagOrder = lagOrder, UseLogPrice = useLogPrice }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(candles.Count, result.MainValues.Count);
        IReadOnlyList<decimal?> line = result.GetSeries(CoreAdfStationarityIndicator.CriticalValue5PctSeriesName);
        IReadOnlyList<decimal?> halfLife = result.GetSeries(CoreAdfStationarityIndicator.HalfLifeSeriesName);
        IReadOnlyList<decimal?> rho = result.GetSeries(CoreAdfStationarityIndicator.RhoSeriesName);
        double[] levels = Levels(candles, useLogPrice);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(line[i]);
                Assert.Null(halfLife[i]);
                Assert.Null(rho[i]);
                continue;
            }

            AugmentedDickeyFullerResult r = Kernel(levels.AsSpan(i - period + 1, period).ToArray(), lagOrder);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(r.Tau), result.MainValues[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(r.Rho), rho[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(r.HalfLife), halfLife[i]);
            Assert.NotNull(result.MainValues[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(StationarityTestMath.CriticalValue5Percent(period - lagOrder - 1)), line[i]);
        }
    }

    [Theory]
    [InlineData(0, -2.7387398004946406, -0.40501452598339244, 1.3349822179068336, 39)]
    [InlineData(1, -1.8616288089265098, -0.31922208889688164, 1.8026337169833722, 38)]
    [InlineData(3, -1.474199676571452, -0.3226511290477388, 1.7792676038232005, 36)]
    public void LastBarOfTheReferenceSeries_MatchesTheMpmathReference(int lagOrder, double tau, double rho, double halfLife, int rows)
    {
        var result = new CoreAdfStationarityIndicator { Period = MeanReverting.Length, LagOrder = lagOrder, UseLogPrice = false }.Calculate(FromCloses(MeanReverting));

        Assert.True(result.IsSuccessful);
        Assert.Equal(tau, (double)result.MainValues[^1]!.Value, 9);
        Assert.Equal(rho, (double)result.GetSeries(CoreAdfStationarityIndicator.RhoSeriesName)[^1]!.Value, 10);
        Assert.Equal(halfLife, (double)result.GetSeries(CoreAdfStationarityIndicator.HalfLifeSeriesName)[^1]!.Value, 8);
        Assert.Equal((decimal)StationarityTestMath.CriticalValue5Percent(rows), result.GetSeries(CoreAdfStationarityIndicator.CriticalValue5PctSeriesName)[^1]!.Value, 10);
        // Every earlier bar is before the first full window.
        Assert.All(result.MainValues.Take(MeanReverting.Length - 1), v => Assert.Null(v));
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var indicator = new CoreAdfStationarityIndicator();
        Assert.Equal(60, indicator.Period);
        Assert.Equal(1, indicator.LagOrder);
        Assert.True(indicator.UseLogPrice);
        Assert.Equal(10, IndicatorDefaultConstants.AdfStationarityMaxLagOrder);
        Assert.Equal(new[] { "ADF (60, 1)" }, new[] { indicator.Name });
    }

    [Fact]
    public void ExactGeometricDecay_HasARhoAndAHalfLifeWhereTauIsNull()
    {
        // 100 * 0.9^t with the raw price and no lag: a perfect fit. tau and its reference line are null, Rho and HalfLife are present (independence rule of plan section 3.6).
        var closes = new List<decimal>();
        decimal price = 100m;
        for (int i = 0; i < 40; i++)
        {
            closes.Add(price);
            price *= 0.9m;
        }

        var result = new CoreAdfStationarityIndicator { Period = 30, LagOrder = 0, UseLogPrice = false }.Calculate(FromCloses(closes));

        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, v => Assert.Null(v));
        Assert.All(result.GetSeries(CoreAdfStationarityIndicator.CriticalValue5PctSeriesName), v => Assert.Null(v));
        decimal? lastRho = result.GetSeries(CoreAdfStationarityIndicator.RhoSeriesName)[^1];
        decimal? lastHalfLife = result.GetSeries(CoreAdfStationarityIndicator.HalfLifeSeriesName)[^1];
        Assert.NotNull(lastRho);
        Assert.NotNull(lastHalfLife);
        Assert.Equal(-0.1, (double)lastRho!.Value, 10);
        Assert.Equal(-Math.Log(2.0) / Math.Log(0.9), (double)lastHalfLife!.Value, 8);
    }

    [Fact]
    public void AFlatWindowGivesNullForEverySeries()
    {
        var flat = Enumerable.Range(0, 80).Select(_ => 50m);
        var result = new CoreAdfStationarityIndicator { Period = 30 }.Calculate(FromCloses(flat));

        Assert.True(result.IsSuccessful);
        foreach (string name in new[] { CoreAdfStationarityIndicator.CriticalValue5PctSeriesName, CoreAdfStationarityIndicator.HalfLifeSeriesName, CoreAdfStationarityIndicator.RhoSeriesName })
        {
            Assert.All(result.GetSeries(name), v => Assert.Null(v));
        }

        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void AWindowShorterThanTwoLagsPlusFive_GivesNullEverywhere()
    {
        // Period 20 (the minimum) with the largest lag order 10: 20 < 2 * 10 + 5. No cross-field validation error: the bars are null.
        var result = new CoreAdfStationarityIndicator { Period = 20, LagOrder = 10 }.Calculate(Candles(120));

        Assert.True(result.IsSuccessful);
        foreach (string name in new[] { CoreAdfStationarityIndicator.CriticalValue5PctSeriesName, CoreAdfStationarityIndicator.HalfLifeSeriesName, CoreAdfStationarityIndicator.RhoSeriesName })
        {
            Assert.All(result.GetSeries(name), v => Assert.Null(v));
        }

        Assert.All(result.MainValues, v => Assert.Null(v));

        // The edge W = 2p + 5 = 25 is the first valid window.
        var edge = new CoreAdfStationarityIndicator { Period = 25, LagOrder = 10 }.Calculate(Candles(120));
        Assert.Contains(edge.MainValues, v => v.HasValue);
    }

    [Fact]
    public void ANonPositivePrice_BlocksTheLogWindowsOnly()
    {
        var closes = Candles(100).Select(c => c.Close).ToList();
        closes[50] = 0m;
        var candles = FromCloses(closes);

        var log = new CoreAdfStationarityIndicator { Period = 30, UseLogPrice = true }.Calculate(candles);
        var raw = new CoreAdfStationarityIndicator { Period = 30, UseLogPrice = false }.Calculate(candles);

        // Windows containing bar 50 are bars 50..79 (log: null); the bar before and after are valid.
        for (int i = 50; i <= 79; i++)
        {
            Assert.Null(log.MainValues[i]);
            Assert.Null(log.GetSeries(CoreAdfStationarityIndicator.RhoSeriesName)[i]);
        }

        Assert.NotNull(log.MainValues[49]);
        Assert.NotNull(log.MainValues[80]);
        Assert.NotNull(raw.MainValues[60]);
    }

    // ---------------------------------------------------------------- registration contract

    [Fact]
    public void TheIndicatorIsRegistered_WithItsParameterObject_AndIsDisabledInASubPanelByDefault()
    {
        const IndicatorType type = IndicatorType.ADFStationarity;
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.NotNull(IndicatorFactory.Default.Create(type));

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.NotNull(settings.ParameterObject);
        settings.ParameterObject!.Validate();
        Assert.Equal(IndicatorDefaultConstants.AdfStationarityDefaultPeriod, settings.ParameterObject!.GetRequiredWarmupBars());
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
    }

    [Fact]
    public void EveryDefaultSeriesIsEmitted_AndMainIsMappedExplicitly_AndSuppressedSeriesHaveNoColourEntry()
    {
        const IndicatorType type = IndicatorType.ADFStationarity;
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
        Assert.Contains(settings.SeriesColors, sc => sc.TargetSeries.Contains(CoreAdfStationarityIndicator.CriticalValue5PctSeriesName));
        foreach (SeriesColorConfig colour in settings.SeriesColors)
        {
            foreach (string target in colour.TargetSeries)
            {
                Assert.False(IndicatorChartSuppression.IsSuppressed(type, target), $"{type}: the suppressed series '{target}' must not have a colour entry");
            }
        }

        Assert.True(IndicatorChartSuppression.IsSuppressed(type, CoreAdfStationarityIndicator.HalfLifeSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(type, CoreAdfStationarityIndicator.RhoSeriesName));
        Assert.False(IndicatorChartSuppression.IsSuppressed(type, CoreAdfStationarityIndicator.CriticalValue5PctSeriesName));
    }

    [Fact]
    public void TheClassification_IsMathGroup_WindowZScore_AndAutomaticAxis()
    {
        const IndicatorType type = IndicatorType.ADFStationarity;
        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { "Math" }, groups);
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Fact]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndSupportsAnIndicatorTimeframe()
    {
        const IndicatorType type = IndicatorType.ADFStationarity;
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.False(ScreenerIndicatorEligibility.IsUniverseBreadthType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Fact]
    public void FutureBarsDoNotChangeThePast()
    {
        const IndicatorType type = IndicatorType.ADFStationarity;
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreAdfStationarityParameter { Period = IndicatorDefaultConstants.AdfStationarityMinPeriod - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreAdfStationarityParameter { Period = IndicatorDefaultConstants.AdfStationarityMaxPeriod + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreAdfStationarityParameter { LagOrder = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreAdfStationarityParameter { LagOrder = 11 }.Validate());
        new CoreAdfStationarityParameter { Period = IndicatorDefaultConstants.AdfStationarityMaxPeriod, LagOrder = 10 }.Validate();

        Assert.False(new CoreAdfStationarityIndicator { LagOrder = 11 }.Calculate(Candles(100)).IsSuccessful);
        Assert.False(new CoreAdfStationarityIndicator { Period = 5 }.Calculate(Candles(100)).IsSuccessful);

        // The largest window and lag order (scratch matrix pooled) calculate.
        var large = new CoreAdfStationarityIndicator { Period = 500, LagOrder = 10 }.Calculate(Candles(520));
        Assert.True(large.IsSuccessful);
        Assert.Contains(large.MainValues, v => v.HasValue);
    }
}
