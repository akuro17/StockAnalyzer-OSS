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

/// <summary>WebAiExpansion plan 03 T5 (D-15=A): raw = 100 x (RSI - lo) / (hi - lo) over StochPeriod bars of RSI; Main = SMA(raw, K); PercentD = SMA(Main, D).</summary>
public class CoreStochRsiIndicatorTests
{
    private const string PercentD = "PercentD";

    private static List<CoreCandleData> Candles(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), c, c + 1m, c - 1m, c, 1000)).ToList();

    private static List<decimal> Wavy(int count) =>
        Enumerable.Range(0, count).Select(i => 100m + i * 0.15m + (i * 7 % 11) - 5m).ToList();

    private static IIndicatorResult Run(IEnumerable<decimal> closes, int rsi, int stoch, int k, int d) =>
        new CoreStochRsiIndicator { RsiPeriod = rsi, StochPeriod = stoch, KPeriod = k, DPeriod = d }.Calculate(Candles(closes));

    private static IReadOnlyList<decimal?> RsiOf(IEnumerable<decimal> closes, int period)
    {
        var indicator = new CoreRsiIndicator { Period = period };
        indicator.Calculate(Candles(closes));
        return indicator.Values;
    }

    [Fact]
    public void NoCandles_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreStochRsiIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(29)]
    public void FewerBarsThanNeeded_AreAllNullAndKeepTheLength(int bars)
    {
        // Defaults 14 / 14 / 3 / 3: the first RSI is at index 14, the first %K at 14 + 13 + 2 = 29, the first %D at 31. 29 bars (indices 0..28) give no value.
        var result = Run(Wavy(bars), 14, 14, 3, 3);

        Assert.True(result.IsSuccessful);
        Assert.Equal(bars, result.MainValues.Count);
        Assert.Equal(bars, result.GetSeries(PercentD).Count);
        Assert.All(result.MainValues, v => Assert.Null(v));
        Assert.All(result.GetSeries(PercentD), v => Assert.Null(v));
    }

    [Theory]
    [InlineData(14, 14, 3, 3)]
    [InlineData(5, 7, 1, 1)]
    [InlineData(6, 4, 2, 3)]
    public void MatchesTheIndependentFormulaOverTheRsiSeries_AndStartsAtTheDocumentedIndices(int rsiPeriod, int stochPeriod, int k, int d)
    {
        var closes = Wavy(150);
        var rsi = RsiOf(closes, rsiPeriod);
        var result = Run(closes, rsiPeriod, stochPeriod, k, d);
        int n = closes.Count;

        // raw, written out without the shared helpers
        var raw = new decimal?[n];
        for (int i = 0; i < n; i++)
        {
            if (i < stochPeriod - 1) continue;
            var window = rsi.Skip(i - stochPeriod + 1).Take(stochPeriod).ToList();
            if (window.Any(v => v is null)) continue;
            decimal hi = window.Max(v => v!.Value), lo = window.Min(v => v!.Value);
            raw[i] = hi == lo ? 50m : 100m * (rsi[i]!.Value - lo) / (hi - lo);
        }

        decimal?[] Sma(decimal?[] input, int period)
        {
            var output = new decimal?[n];
            for (int i = period - 1; i < n; i++)
            {
                var window = input.Skip(i - period + 1).Take(period).ToList();
                if (window.Any(v => v is null)) continue;
                output[i] = window.Sum(v => v!.Value) / period;
            }

            return output;
        }

        var expectedK = Sma(raw, k);
        var expectedD = Sma(expectedK, d);
        for (int i = 0; i < n; i++)
        {
            AssertClose(expectedK[i], result.MainValues[i], $"%K bar {i}");
            AssertClose(expectedD[i], result.GetSeries(PercentD)[i], $"%D bar {i}");
        }

        int firstK = rsiPeriod + (stochPeriod - 1) + (k - 1);
        Assert.Equal(firstK, result.MainValues.ToList().FindIndex(v => v.HasValue));
        Assert.Equal(firstK + (d - 1), result.GetSeries(PercentD).ToList().FindIndex(v => v.HasValue));
    }

    private static void AssertClose(decimal? expected, decimal? actual, string label)
    {
        if (expected is null) { Assert.True(actual is null, label); return; }
        Assert.True(actual.HasValue, label);
        Assert.True(Math.Abs(expected.Value - actual!.Value) < 0.000000001m, $"{label}: expected {expected} but was {actual}");
    }

    [Fact]
    public void WithoutSmoothing_TheBarWhereRsiIsTheWindowMaximumIs100_AndTheMinimum0()
    {
        var closes = Wavy(150);
        var rsi = RsiOf(closes, 5);
        var main = Run(closes, 5, 7, 1, 1).MainValues;

        int maxima = 0, minima = 0;
        for (int i = 5 + 6; i < closes.Count; i++)
        {
            var window = rsi.Skip(i - 6).Take(7).Select(v => v!.Value).ToList();
            if (window.Max() == window.Min()) continue;
            if (rsi[i] == window.Max()) { AssertClose(100m, main[i], $"max bar {i}"); maxima++; }
            if (rsi[i] == window.Min()) { AssertClose(0m, main[i], $"min bar {i}"); minima++; }
        }

        Assert.True(maxima > 0 && minima > 0, "the test data must contain both a window maximum and a window minimum");
    }

    [Fact]
    public void ConstantPrice_GivesTheNeutralValue()
    {
        // No gain and no loss: RSI is constant, hi = lo, so the raw value is the bounded-oscillator neutral value (50).
        var result = Run(Enumerable.Repeat(100m, 80), 14, 14, 3, 3);

        Assert.All(result.MainValues.Skip(29), v => Assert.Equal(IndicatorCalculationHelper.BoundedOscillatorNeutral, v));
        Assert.All(result.GetSeries(PercentD).Skip(31), v => Assert.Equal(IndicatorCalculationHelper.BoundedOscillatorNeutral, v));
    }

    [Fact]
    public void EveryValueStaysWithin0And100()
    {
        var result = Run(Wavy(300), 14, 14, 3, 3);

        Assert.All(result.MainValues.Where(v => v.HasValue), v => Assert.InRange(v!.Value, 0m, 100m));
        Assert.All(result.GetSeries(PercentD).Where(v => v.HasValue), v => Assert.InRange(v!.Value, 0m, 100m));
    }

    [Fact]
    public void NoDecimalRoundingResidueLeavesTheRange_OnTheScatterAuditSeries()
    {
        // The running-sum SMA left residues of about 1e-26 outside [0, 100] on this series (found by the Scatter axis audit, plan 03 T9); the indicator removes them.
        var candles = StockAnalyzer.Core.Tests.TestHelpers.ScatterMetricTestKit.Bars(600, 20261021)
            .Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

        var result = new CoreStochRsiIndicator().Calculate(candles);

        Assert.Contains(result.MainValues, v => v.HasValue);
        Assert.All(result.MainValues.Where(v => v.HasValue), v => Assert.InRange(v!.Value, 0m, 100m));
        Assert.All(result.GetSeries(PercentD).Where(v => v.HasValue), v => Assert.InRange(v!.Value, 0m, 100m));
    }

    [Fact]
    public void ValuesAtBarTDoNotDependOnLaterBars()
    {
        var closes = Wavy(150);
        var full = Run(closes, 6, 5, 3, 3);
        var cut = Run(closes.Take(90), 6, 5, 3, 3);

        for (int i = 0; i < 90; i++)
        {
            Assert.Equal(full.MainValues[i], cut.MainValues[i]);
            Assert.Equal(full.GetSeries(PercentD)[i], cut.GetSeries(PercentD)[i]);
        }
    }

    [Fact]
    public void IsRegistered_WithTheStochasticSeriesNames()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.StochRSI));
        Assert.IsType<CoreStochRsiIndicator>(IndicatorFactory.Default.Create(IndicatorType.StochRSI));

        var names = IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.StochRSI);
        Assert.Contains("PercentK", names);
        Assert.Contains(PercentD, names);
        Assert.Equal("Main", IndicatorOutputSeriesResolver.Normalize(IndicatorType.StochRSI, "PercentK"));
        Assert.Equal(new[] { "Main", PercentD }, Run(Wavy(60), 5, 5, 2, 2).SeriesNamesList.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void DefaultSettings_AreAnOscillatorSubPanelWithTheFixed0To100Axis()
    {
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.StochRSI);

        Assert.IsType<CoreStochRsiParameter>(settings.ParameterObject);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(0m, settings.MinValue);
        Assert.Equal(100m, settings.MaxValue);
        // The Stochastic definition of the two lines: %K = StochKColor, %D = StochDColor, and the two differ.
        Assert.Equal(IndicatorDefaultConstants.StochKColor, settings.Color);
        Assert.Equal(IndicatorDefaultConstants.StochKColor, settings.SeriesColors.Single(c => c.Name == "Main").Color);
        Assert.Equal(IndicatorDefaultConstants.StochDColor, settings.SeriesColors.Single(c => c.Name == PercentD).Color);
        Assert.NotEqual(settings.SeriesColors.Single(c => c.Name == "Main").Color, settings.SeriesColors.Single(c => c.Name == PercentD).Color);
    }

    [Fact]
    public void ConfigureAppliesTheParameterObject()
    {
        var indicator = new CoreStochRsiIndicator();

        indicator.Configure(new CoreStochRsiParameter { RsiPeriod = 9, StochPeriod = 8, KPeriod = 2, DPeriod = 4 });

        Assert.Equal((9, 8, 2, 4), (indicator.RsiPeriod, indicator.StochPeriod, indicator.KPeriod, indicator.DPeriod));
    }

    [Fact]
    public void DefaultParameterValidates_AndOutOfRangeValuesThrow()
    {
        var defaults = new CoreStochRsiParameter();
        defaults.Validate();
        Assert.Equal(
            (IndicatorDefaultConstants.StochRsiDefaultRsiPeriod, IndicatorDefaultConstants.StochRsiDefaultStochPeriod, IndicatorDefaultConstants.StochRsiDefaultKPeriod, IndicatorDefaultConstants.StochRsiDefaultDPeriod),
            (defaults.RsiPeriod, defaults.StochPeriod, defaults.KPeriod, defaults.DPeriod));

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStochRsiParameter { RsiPeriod = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStochRsiParameter { StochPeriod = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStochRsiParameter { KPeriod = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStochRsiParameter { DPeriod = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
    }
}
