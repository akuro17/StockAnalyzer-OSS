using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>WebAiExpansion plan 03 T4: TSI = 100 x EMA(EMA(m, Long), Short) / EMA(EMA(|m|, Long), Short); Signal = EMA(TSI); Histogram = TSI - Signal.</summary>
public class CoreTsiIndicatorTests
{
    private const string Signal = "Signal";
    private const string Histogram = "Histogram";

    private static List<CoreCandleData> Candles(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), c, c + 1m, c - 1m, c, 1000)).ToList();

    private static IIndicatorResult Run(IEnumerable<decimal> closes, int longPeriod, int shortPeriod, int signalPeriod) =>
        new CoreTsiIndicator { LongPeriod = longPeriod, ShortPeriod = shortPeriod, SignalPeriod = signalPeriod }.Calculate(Candles(closes));

    private static List<decimal> Wavy(int count) =>
        Enumerable.Range(0, count).Select(i => 100m + i * 0.2m + (i * 7 % 11) - 5m).ToList();

    [Fact]
    public void NoCandles_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreTsiIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Theory]
    [InlineData(1, 25, 13)]
    [InlineData(30, 25, 13)]
    [InlineData(37, 25, 13)]
    public void FewerBarsThanNeeded_AreAllNullAndKeepTheLength(int bars, int longPeriod, int shortPeriod)
    {
        // The first Main value is at index 1 + (25 - 1) + (13 - 1) = 37, so 37 bars (indices 0..36) give no value.
        var result = Run(Wavy(bars), longPeriod, shortPeriod, 7);

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
        // Prices 10, 12, 11, 15, 14 with Long = 2, Short = 2, Signal = 2 (EMA multiplier 2/3, seed = SMA of the first two values):
        //   m = -, 2, -1, 4, -1; |m| = -, 2, 1, 4, 1
        //   EMA2(m):  idx2 = 1/2, idx3 = 17/6, idx4 = 5/18;   EMA2(EMA2(m)):  idx3 = 5/3, idx4 = 20/27
        //   EMA2(|m|): idx2 = 3/2, idx3 = 19/6, idx4 = 31/18;  EMA2(EMA2(|m|)): idx3 = 7/3, idx4 = 52/27
        //   TSI idx3 = 100 x (5/3) / (7/3) = 500/7;  idx4 = 100 x (20/27) / (52/27) = 500/13
        //   Signal idx4 = (500/7 + 500/13) / 2;  Histogram idx4 = 500/13 - Signal idx4
        var result = Run(new[] { 10m, 12m, 11m, 15m, 14m }, 2, 2, 2);
        var main = result.MainValues;

        Assert.All(main.Take(3), v => Assert.Null(v));
        Assert.Equal((double)(500m / 7m), (double)main[3]!.Value, 9);
        Assert.Equal((double)(500m / 13m), (double)main[4]!.Value, 9);

        decimal expectedSignal = (500m / 7m + 500m / 13m) / 2m;
        Assert.All(result.GetSeries(Signal).Take(4), v => Assert.Null(v));
        Assert.Equal((double)expectedSignal, (double)result.GetSeries(Signal)[4]!.Value, 9);
        Assert.Equal((double)(500m / 13m - expectedSignal), (double)result.GetSeries(Histogram)[4]!.Value, 9);
        Assert.Null(result.GetSeries(Histogram)[3]);
    }

    [Fact]
    public void ARisingSeriesGives100_AFallingSeriesGivesMinus100_AConstantSeriesGivesZero()
    {
        var rising = Run(Enumerable.Range(0, 40).Select(i => 100m + i * 1.5m), 4, 3, 2).MainValues;
        var falling = Run(Enumerable.Range(0, 40).Select(i => 200m - i * 1.5m), 4, 3, 2).MainValues;
        var flat = Run(Enumerable.Repeat(100m, 40), 4, 3, 2).MainValues;

        Assert.All(rising.Skip(6), v => Assert.Equal(100m, v));
        Assert.All(falling.Skip(6), v => Assert.Equal(-100m, v));
        Assert.All(flat.Skip(6), v => Assert.Equal(0m, v));
    }

    [Theory]
    [InlineData(4, 3)]
    [InlineData(25, 13)]
    [InlineData(1, 1)]
    public void MainStaysWithinMinus100And100_AndFirstValueIsAtTheDocumentedIndex(int longPeriod, int shortPeriod)
    {
        var result = Run(Wavy(200), longPeriod, shortPeriod, 5);
        var main = result.MainValues;

        Assert.All(main.Where(v => v.HasValue), v => Assert.InRange(v!.Value, -100m, 100m));
        Assert.Equal(1 + (longPeriod - 1) + (shortPeriod - 1), main.ToList().FindIndex(v => v.HasValue));
        Assert.All(main.Skip(1 + (longPeriod - 1) + (shortPeriod - 1)), v => Assert.NotNull(v));
    }

    [Fact]
    public void SignalIsTheEmaOfMain_AndHistogramIsMainMinusSignal()
    {
        int signalPeriod = 5;
        var result = Run(Wavy(120), 6, 4, signalPeriod);
        var main = result.MainValues;
        var signal = result.GetSeries(Signal);
        var histogram = result.GetSeries(Histogram);

        int firstMain = 1 + 5 + 3;
        Assert.Equal(firstMain + signalPeriod - 1, signal.ToList().FindIndex(v => v.HasValue));
        // The seed of the Signal is the SMA of the first SignalPeriod Main values.
        int seedIndex = firstMain + signalPeriod - 1;
        decimal seed = main.Skip(firstMain).Take(signalPeriod).Sum(v => v!.Value) / signalPeriod;
        Assert.Equal((double)seed, (double)signal[seedIndex]!.Value, 9);
        decimal multiplier = 2m / (signalPeriod + 1);
        for (int i = seedIndex + 1; i < main.Count; i++)
        {
            decimal expected = signal[i - 1]!.Value + (main[i]!.Value - signal[i - 1]!.Value) * multiplier;
            Assert.Equal((double)expected, (double)signal[i]!.Value, 9);
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
        var closes = Wavy(120);
        var full = Run(closes, 6, 4, 5);
        var cut = Run(closes.Take(70), 6, 4, 5);

        foreach (string name in new[] { "Main", Signal, Histogram })
            for (int i = 0; i < 70; i++) Assert.Equal(full.GetSeries(name)[i], cut.GetSeries(name)[i]);
    }

    [Fact]
    public void IsRegistered_WithAMainAliasAndThreeSeries()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.TSI));
        Assert.IsType<CoreTsiIndicator>(IndicatorFactory.Default.Create(IndicatorType.TSI));

        var names = IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.TSI);
        Assert.Contains("TSI", names);
        Assert.Contains(Signal, names);
        Assert.Contains(Histogram, names);
        Assert.Equal("Main", IndicatorOutputSeriesResolver.Normalize(IndicatorType.TSI, "TSI"));

        var result = Run(Wavy(60), 4, 3, 2);
        Assert.Equal(new[] { Histogram, "Main", Signal }, result.SeriesNamesList.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void DefaultSettings_AreAnOscillatorSubPanelWithAFixedAxis()
    {
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.TSI);

        Assert.IsType<CoreTsiParameter>(settings.ParameterObject);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.TsiMinValue, settings.MinValue);
        Assert.Equal(IndicatorDefaultConstants.TsiMaxValue, settings.MaxValue);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.Equal(IndicatorDefaultConstants.RsiColor, settings.Color);
        Assert.Equal(IndicatorDefaultConstants.MacdSignalColor, settings.SeriesColors.Single(c => c.Name == Signal).Color);
    }

    [Fact]
    public void ConfigureAppliesTheParameterObject()
    {
        var indicator = new CoreTsiIndicator();

        indicator.Configure(new CoreTsiParameter { LongPeriod = 20, ShortPeriod = 10, SignalPeriod = 5 });

        Assert.Equal(20, indicator.LongPeriod);
        Assert.Equal(10, indicator.ShortPeriod);
        Assert.Equal(5, indicator.SignalPeriod);
    }

    [Fact]
    public void DefaultParameterValidates_AndOutOfRangeValuesThrow()
    {
        var defaults = new CoreTsiParameter();
        defaults.Validate();
        Assert.Equal(IndicatorDefaultConstants.TsiDefaultLongPeriod, defaults.LongPeriod);
        Assert.Equal(IndicatorDefaultConstants.TsiDefaultShortPeriod, defaults.ShortPeriod);
        Assert.Equal(IndicatorDefaultConstants.TsiDefaultSignalPeriod, defaults.SignalPeriod);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTsiParameter { LongPeriod = IndicatorDefaultConstants.DefaultPeriodMin - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTsiParameter { ShortPeriod = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTsiParameter { SignalPeriod = 0 }.Validate());
    }
}
