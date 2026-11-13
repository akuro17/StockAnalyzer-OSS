using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volume;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>WebAiExpansion plan 03 T6 (D-16=A): VF = V x |2 x dm / cm - 1| x T x 100; KVO = EMA(VF, Fast) - EMA(VF, Slow); Signal = EMA(KVO); Histogram = KVO - Signal.</summary>
public class CoreKvoIndicatorTests
{
    private const string Signal = "Signal";
    private const string Histogram = "Histogram";
    private const decimal Tolerance = 0.000000001m;

    private static CoreCandleData Bar(int day, decimal high, decimal low, decimal close, long volume) =>
        new(new DateTime(2024, 1, 1).AddDays(day), close, high, low, close, volume);

    private static IIndicatorResult Run(IReadOnlyList<CoreCandleData> candles, int fast, int slow, int signal) =>
        new CoreKvoIndicator { FastPeriod = fast, SlowPeriod = slow, SignalPeriod = signal }.Calculate(candles);

    // Hand-computed case (Fast = 1, Slow = 2, Signal = 2):
    //   bar:   0        1         2         3
    //   H/L/C: 10/8/9   12/9/11   13/10/12  12/8/9
    //   V:     1000     1000      2000      700
    //   hlc:   27       32        35        29;   dm: 2, 3, 3, 4
    //   T1 = +1 (32 > 27): cm1 = dm0 + dm1 = 5;   VF1 = 1000 x |2 x 3 / 5 - 1| x 1 x 100 = 1000 x 0.2 x 100 = 20000
    //   T2 = +1 (35 > 32, same trend): cm2 = cm1 + dm2 = 8;   VF2 = 2000 x |2 x 3 / 8 - 1| x 100 = 2000 x 0.25 x 100 = 50000
    //   T3 = -1 (29 < 35, trend change): cm3 = dm2 + dm3 = 7;   VF3 = 700 x |2 x 4 / 7 - 1| x (-1) x 100 = 700 x (1/7) x (-100) = -10000
    //   EMA(VF, 1) = VF (first at bar 1); EMA(VF, 2) = SMA seed (VF1 + VF2) / 2 = 35000 at bar 2, then 35000 + (VF3 - 35000) x 2/3 = 5000 at bar 3
    //   KVO2 = VF2 - 35000 = 15000;  KVO3 = VF3 - 5000 = -15000;  Signal3 = (KVO2 + KVO3) / 2 = 0;  Histogram3 = -15000
    private static List<CoreCandleData> HandCandles() => new()
    {
        Bar(0, 10m, 8m, 9m, 1000),
        Bar(1, 12m, 9m, 11m, 1000),
        Bar(2, 13m, 10m, 12m, 2000),
        Bar(3, 12m, 8m, 9m, 700),
    };

    private static List<CoreCandleData> Wavy(int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            decimal close = 100m + i * 0.1m + (i * 7 % 11) - 5m;
            return Bar(i, close + 1m + i % 3, close - 1m - i % 2, close, 1000 + (i * 37 % 500));
        }).ToList();

    [Fact]
    public void NoCandles_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreKvoIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(55)]
    public void FewerBarsThanNeeded_AreAllNullAndKeepTheLength(int bars)
    {
        // The volume force starts at bar 1, so the slow EMA (55) gives its first value at index 55: 55 bars (indices 0..54) give no value.
        var result = Run(Wavy(bars), 34, 55, 13);

        Assert.True(result.IsSuccessful);
        foreach (string name in new[] { "Main", Signal, Histogram })
        {
            Assert.Equal(bars, result.GetSeries(name).Count);
            Assert.All(result.GetSeries(name), v => Assert.Null(v));
        }
    }

    [Fact]
    public void HandComputedValues_MatchTheFormula()
    {
        var result = Run(HandCandles(), 1, 2, 2);
        var main = result.MainValues;

        Assert.All(main.Take(2), v => Assert.Null(v));
        Assert.Equal((double)15000m, (double)main[2]!.Value, 6);
        Assert.Equal((double)-15000m, (double)main[3]!.Value, 6);
        Assert.All(result.GetSeries(Signal).Take(3), v => Assert.Null(v));
        Assert.True(Math.Abs(result.GetSeries(Signal)[3]!.Value) < Tolerance);
        Assert.Equal((double)-15000m, (double)result.GetSeries(Histogram)[3]!.Value, 6);
        Assert.Null(result.GetSeries(Histogram)[2]);
    }

    [Fact]
    public void FlatBars_GiveZeroVolumeForce_AndAZeroOscillator()
    {
        // H = L = C on every bar: dm = 0 and cm = 0, so VF = 0 whatever the volume and the trend.
        var candles = Enumerable.Range(0, 30).Select(i => Bar(i, 100m + i, 100m + i, 100m + i, 5000 + i)).ToList();

        var main = Run(candles, 3, 5, 2).MainValues;

        Assert.All(main.Skip(5), v => Assert.Equal(0m, v));
    }

    [Fact]
    public void WhenTheTrendChangesEveryBar_TheCumulativeMeasurementIsThePreviousTwoRanges()
    {
        // Every bar has dm = 2 and the hlc alternates up/down: cm_i = dm_(i-1) + dm_i = 4, so |2 x dm / cm - 1| = 0 and VF = 0 on every bar.
        // If cm kept accumulating on a trend change (cm = 2, 4, 6, ...), VF would not be 0.
        var candles = Enumerable.Range(0, 40).Select(i =>
        {
            decimal close = 100m + (i % 2 == 0 ? 0m : 5m);
            return Bar(i, close + 1m, close - 1m, close, 1000);
        }).ToList();

        var main = Run(candles, 3, 5, 2).MainValues;

        Assert.All(main.Skip(5), v => Assert.Equal(0m, v));
    }

    [Fact]
    public void WhenTheTrendKeepsItsDirection_TheCumulativeMeasurementAccumulates()
    {
        // The same ranges (dm = 2) in a steady up-trend: cm_i = 2 x (i + 1) grows, so |2 x dm / cm - 1| > 0 and the volume force is positive.
        var candles = Enumerable.Range(0, 40).Select(i => Bar(i, 101m + i, 99m + i, 100m + i, 1000)).ToList();

        var main = Run(candles, 1, 2, 2).MainValues;

        Assert.Contains(main, v => v.HasValue && v.Value != 0m);
    }

    [Fact]
    public void SignalIsTheEmaOfMain_AndHistogramIsMainMinusSignal()
    {
        int signalPeriod = 5;
        var result = Run(Wavy(150), 6, 12, signalPeriod);
        var main = result.MainValues;
        var signal = result.GetSeries(Signal);
        var histogram = result.GetSeries(Histogram);

        int firstMain = 12;
        Assert.Equal(firstMain, main.ToList().FindIndex(v => v.HasValue));
        int seedIndex = firstMain + signalPeriod - 1;
        Assert.Equal(seedIndex, signal.ToList().FindIndex(v => v.HasValue));
        decimal seed = main.Skip(firstMain).Take(signalPeriod).Sum(v => v!.Value) / signalPeriod;
        Assert.True(Math.Abs(seed - signal[seedIndex]!.Value) < Tolerance);
        decimal multiplier = 2m / (signalPeriod + 1);
        for (int i = seedIndex + 1; i < main.Count; i++)
        {
            decimal expected = signal[i - 1]!.Value + (main[i]!.Value - signal[i - 1]!.Value) * multiplier;
            Assert.True(Math.Abs(expected - signal[i]!.Value) < Tolerance, $"bar {i}");
        }

        for (int i = 0; i < main.Count; i++)
        {
            if (histogram[i] is null) { Assert.True(main[i] is null || signal[i] is null); continue; }
            Assert.Equal(main[i]!.Value - signal[i]!.Value, histogram[i]!.Value);
        }
    }

    [Fact]
    public void ValuesAtBarTDoNotDependOnLaterBars()
    {
        var candles = Wavy(150);
        var full = Run(candles, 6, 12, 5);
        var cut = Run(candles.Take(90).ToList(), 6, 12, 5);

        foreach (string name in new[] { "Main", Signal, Histogram })
            for (int i = 0; i < 90; i++) Assert.Equal(full.GetSeries(name)[i], cut.GetSeries(name)[i]);
    }

    [Fact]
    public void IsRegistered_WithAMainAliasAndThreeSeries()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.KVO));
        Assert.IsType<CoreKvoIndicator>(IndicatorFactory.Default.Create(IndicatorType.KVO));

        var names = IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.KVO);
        Assert.Contains("KVO", names);
        Assert.Contains(Signal, names);
        Assert.Contains(Histogram, names);
        Assert.Equal("Main", IndicatorOutputSeriesResolver.Normalize(IndicatorType.KVO, "KVO"));
        Assert.Equal(new[] { Histogram, "Main", Signal }, Run(Wavy(60), 3, 5, 2).SeriesNamesList.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void DefaultSettings_AreAVolumeSubPanelWithAZeroBaseline()
    {
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.KVO);

        Assert.IsType<CoreKvoParameter>(settings.ParameterObject);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volume, settings.Category);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.Equal(IndicatorDefaultConstants.Cyan, settings.Color);
        Assert.Equal(IndicatorDefaultConstants.MacdSignalColor, settings.SeriesColors.Single(c => c.Name == Signal).Color);
    }

    [Fact]
    public void ConfigureAppliesTheParameterObject()
    {
        var indicator = new CoreKvoIndicator();

        indicator.Configure(new CoreKvoParameter { FastPeriod = 10, SlowPeriod = 20, SignalPeriod = 5 });

        Assert.Equal((10, 20, 5), (indicator.FastPeriod, indicator.SlowPeriod, indicator.SignalPeriod));
    }

    [Fact]
    public void DefaultParameterValidates_AndOutOfRangeValuesThrow()
    {
        var defaults = new CoreKvoParameter();
        defaults.Validate();
        Assert.Equal(
            (IndicatorDefaultConstants.KvoDefaultFastPeriod, IndicatorDefaultConstants.KvoDefaultSlowPeriod, IndicatorDefaultConstants.KvoDefaultSignalPeriod),
            (defaults.FastPeriod, defaults.SlowPeriod, defaults.SignalPeriod));

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKvoParameter { FastPeriod = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKvoParameter { SlowPeriod = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKvoParameter { SignalPeriod = 0 }.Validate());
    }
}
