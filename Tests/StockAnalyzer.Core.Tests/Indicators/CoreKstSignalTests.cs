using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Rendering;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>Plan 02 T1: the KST Signal line (SMA of KST), always calculated; ShowSignal hides only the chart line.</summary>
public class CoreKstSignalTests
{
    private const int Bars = 160;
    private const int SignalPeriod = 9;

    private static List<CoreCandleData> Candles()
    {
        var list = new List<CoreCandleData>();
        for (int i = 0; i < Bars; i++)
        {
            decimal close = 100m + i * 0.3m + (i * 7 % 13) - 6m;
            list.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), close, close + 2m, close - 2m, close, 1000));
        }

        return list;
    }

    // Independent recomputation of the pre-existing KST (Main): ROC and SMA written out without the shared helpers.
    private static decimal?[] ExpectedMain(IReadOnlyList<CoreCandleData> candles)
    {
        int[] roc = { 10, 15, 20, 30 };
        int[] sma = { 10, 10, 10, 15 };
        decimal[] weight = { 1m, 2m, 3m, 4m };
        int n = candles.Count;
        var smoothed = new decimal?[4][];
        for (int k = 0; k < 4; k++)
        {
            var r = new decimal?[n];
            for (int i = roc[k]; i < n; i++)
            {
                decimal past = candles[i - roc[k]].Close;
                r[i] = past == 0m ? null : (candles[i].Close - past) / past * 100m;
            }

            var s = new decimal?[n];
            for (int i = sma[k] - 1; i < n; i++)
            {
                decimal sum = 0m;
                bool ok = true;
                for (int j = i - sma[k] + 1; j <= i; j++)
                {
                    if (r[j] is null) { ok = false; break; }
                    sum += r[j]!.Value;
                }

                s[i] = ok ? sum / sma[k] : null;
            }

            smoothed[k] = s;
        }

        var main = new decimal?[n];
        for (int i = 0; i < n; i++)
        {
            if (smoothed.Any(s => s[i] is null)) continue;
            main[i] = Enumerable.Range(0, 4).Sum(k => weight[k] * smoothed[k][i]!.Value);
        }

        return main;
    }

    [Fact]
    public void Main_IsUnchanged_AndSignalIsTheMeanOfTheLastNineMainValues()
    {
        var candles = Candles();
        var indicator = new CoreKstIndicator();
        IIndicatorResult result = indicator.Calculate(candles);

        decimal?[] expectedMain = ExpectedMain(candles);
        IReadOnlyList<decimal?> main = result.GetSeries("Main");
        IReadOnlyList<decimal?> signal = result.GetSeries("Signal");
        Assert.Equal(Bars, main.Count);
        Assert.Equal(Bars, signal.Count);

        for (int i = 0; i < Bars; i++)
        {
            AssertClose(expectedMain[i], main[i]);

            decimal? expectedSignal = null;
            if (i >= SignalPeriod - 1 && Enumerable.Range(i - SignalPeriod + 1, SignalPeriod).All(j => expectedMain[j] is not null))
            {
                expectedSignal = Enumerable.Range(i - SignalPeriod + 1, SignalPeriod).Sum(j => expectedMain[j]!.Value) / SignalPeriod;
            }

            AssertClose(expectedSignal, signal[i]);
        }
    }

    // The shared SMA helper keeps a running sum, so the last decimal digits can differ from a fresh sum; null-ness must match exactly.
    private static void AssertClose(decimal? expected, decimal? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected.HasValue)
        {
            Assert.True(Math.Abs(expected.Value - actual!.Value) < 1e-15m, $"expected {expected} but was {actual}");
        }
    }

    [Fact]
    public void LeadingNullRuns_AreTheWarmUpOfMainAndOfTheSignal()
    {
        IIndicatorResult result = new CoreKstIndicator().Calculate(Candles());
        IReadOnlyList<decimal?> main = result.GetSeries("Main");
        IReadOnlyList<decimal?> signal = result.GetSeries("Signal");

        int firstMain = main.TakeWhile(v => v is null).Count();
        int firstSignal = signal.TakeWhile(v => v is null).Count();
        Assert.Equal(30 + 15 - 1, firstMain); // ROC 30 + SMA 15, zero-based first index
        Assert.Equal(firstMain + SignalPeriod - 1, firstSignal);
    }

    [Fact]
    public void ZeroBars_GiveTwoEmptySeries()
    {
        IIndicatorResult result = new CoreKstIndicator().Calculate(new List<CoreCandleData>());
        Assert.True(result.IsSuccessful);
        Assert.Empty(result.GetSeries("Main"));
        Assert.Empty(result.GetSeries("Signal"));
    }

    [Fact]
    public void SignalPeriodParameter_ChangesTheSignalOnly()
    {
        var candles = Candles();
        IReadOnlyList<decimal?> mainDefault = new CoreKstIndicator().Calculate(candles).GetSeries("Main");

        var indicator = new CoreKstIndicator();
        indicator.Configure(new CoreKstParameter { SignalPeriod = 3 });
        IIndicatorResult result = indicator.Calculate(candles);

        Assert.Equal(mainDefault, result.GetSeries("Main"));
        decimal?[] expectedMain = ExpectedMain(candles);
        AssertClose((expectedMain[100]!.Value + expectedMain[99]!.Value + expectedMain[98]!.Value) / 3m, result.GetSeries("Signal")[100]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowSignal_NeverRemovesTheSignalFromTheResult(bool showSignal)
    {
        var indicator = new CoreKstIndicator();
        indicator.Configure(new CoreKstParameter { ShowSignal = showSignal });
        IIndicatorResult result = indicator.Calculate(Candles());

        Assert.Contains("Signal", result.SeriesNames);
        Assert.Contains(result.GetSeries("Signal"), v => v.HasValue);
    }

    [Theory]
    [InlineData(9, 53)]    // default: Main 30 + 15 = 45, Signal 9 - 1 = 8 more
    [InlineData(1, 45)]    // SignalPeriod 1 adds nothing
    [InlineData(20, 64)]
    public void WarmUp_IsTheMainWarmUpPlusSignalPeriodMinusOne(int signalPeriod, int expected)
    {
        Assert.Equal(expected, new CoreKstParameter { SignalPeriod = signalPeriod }.GetRequiredWarmupBars());
    }

    [Fact]
    public void Parameter_DefaultsAndRange()
    {
        var p = new CoreKstParameter();
        Assert.Equal(9, p.SignalPeriod);
        Assert.False(p.ShowSignal);
        p.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKstParameter { SignalPeriod = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKstParameter { SignalPeriod = 1001 }.Validate());
    }

    // ---- chart line: which series are drawn ----

    [Fact]
    public void ChartSuppression_HidesSignalOnlyWhileShowSignalIsOff()
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.KST, "Signal", new CoreKstParameter { ShowSignal = false }));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.KST, "Signal", new CoreKstParameter { ShowSignal = true }));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.KST, "Signal", null)); // no parameter object = defaults = hidden
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.KST, "Signal"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.KST, "Main", new CoreKstParameter { ShowSignal = false }));

        // The Data Window keeps showing it in both cases.
        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.KST, "Signal"));
    }

    [Fact]
    public void ChartSuppression_OtherIndicatorsAreUnaffectedByAParameter()
    {
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.MACD, "Signal", new CoreKstParameter()));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.SMA, "Main", new CoreKstParameter()));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.DMI, "Main", new CoreKstParameter()));
    }

    [Fact]
    public void AiChartImage_DrawsSignalOnlyWhenShowSignalIsOn_AndItsRangeInputExcludesIt()
    {
        var candles = Candles();
        IIndicatorResult result = new CoreKstIndicator().Calculate(candles);

        IReadOnlyList<AiChartSelectedSeries> hidden = AiChartSeriesSelector.Select(IndicatorType.KST, result, 100, 60, new CoreKstParameter { ShowSignal = false });
        IReadOnlyList<AiChartSelectedSeries> legacy = AiChartSeriesSelector.Select(IndicatorType.KST, result, 100, 60);
        IReadOnlyList<AiChartSelectedSeries> shown = AiChartSeriesSelector.Select(IndicatorType.KST, result, 100, 60, new CoreKstParameter { ShowSignal = true });

        Assert.Equal(new[] { "Main" }, hidden.Select(s => s.Name));
        Assert.Equal(new[] { "Main" }, legacy.Select(s => s.Name));
        Assert.Equal(new[] { "Main", "Signal" }, shown.Select(s => s.Name));

        // The image's value range is computed from the chosen series only, so a hidden Signal cannot widen it.
        var mainOnly = new IReadOnlyList<decimal?>[] { result.GetSeries("Main").Skip(100).Take(60).ToArray() };
        Assert.Equal(AiChartPriceRange.ComputeOscillator(mainOnly, null, null, false),
                     AiChartPriceRange.ComputeOscillator(hidden.Select(s => s.Values), null, null, false));
        Assert.True(AiChartSeriesSelector.IsLeftOut(IndicatorType.KST, "Signal", new CoreKstParameter()));
        Assert.False(AiChartSeriesSelector.IsLeftOut(IndicatorType.KST, "Signal", new CoreKstParameter { ShowSignal = true }));
    }

    // ---- output names (screener / backtest / registration dialogs) ----

    [Fact]
    public void OutputNames_KeepTheMainOutputKst_AndAddSignal()
    {
        Assert.Equal(new[] { "KST", "Signal" }, IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.KST));

        // A saved reference to the output "KST" still resolves to the main series; "Signal" resolves to itself.
        Assert.Equal("Main", IndicatorOutputSeriesResolver.Normalize(IndicatorType.KST, "KST"));
        Assert.Equal("Signal", IndicatorOutputSeriesResolver.Normalize(IndicatorType.KST, "Signal"));

        IIndicatorResult result = new CoreKstIndicator().Calculate(Candles());
        Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, IndicatorType.KST, "KST", PriceType.Close, out var main));
        Assert.Equal(result.GetSeries("Main"), main);
        Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, IndicatorType.KST, "Signal", PriceType.Close, out var signal));
        Assert.Equal(result.GetSeries("Signal"), signal);
    }

    // ---- default settings entry ----

    [Fact]
    public void DefaultSettingsEntry_KeepsTheValuesTheKstHadBefore_AndColoursTheSignalLikeMacd()
    {
        CoreIndicatorSettings entry = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.KST);
        CoreIndicatorSettings before = IndicatorFactory.CreateStatic(IndicatorType.KST)!.GetDefaultSettings();

        Assert.Equal(before.Category, entry.Category);
        Assert.Equal(before.IsOverlay, entry.IsOverlay);
        Assert.Equal(before.Color, entry.Color);
        Assert.Equal(before.Thickness, entry.Thickness);
        Assert.Equal(before.Style, entry.Style);
        Assert.False(entry.IsEnabled);
        var parameter = Assert.IsType<CoreKstParameter>(entry.ParameterObject);
        Assert.False(parameter.ShowSignal);

        SeriesColorConfig main = entry.SeriesColors.Single(c => c.TargetSeries.Contains("Main"));
        SeriesColorConfig signal = entry.SeriesColors.Single(c => c.TargetSeries.Contains("Signal"));
        Assert.Equal(before.Color, main.Color);
        CoreIndicatorSettings macd = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.MACD);
        Assert.Equal(macd.SeriesColors.Single(c => c.TargetSeries.Contains("Signal")).Color, signal.Color);
    }
}
