using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The range regime diagnostic card (DSL expansion run D-8, decisions DX-9 and DX-9b).</summary>
public sealed class RangeRegimeDiagnosticCardValuesTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> Candles(int count, decimal first = 100m)
    {
        double state = 11.0;
        var list = new List<CoreCandleData>(count);
        decimal close = first;
        for (int i = 0; i < count; i++)
        {
            state = ((state * 1103515245.0) + 12345.0) % 2147483648.0;
            close += Math.Round((decimal)((state / 2147483648.0) - 0.5) * 2m, 4) + 0.04m;
            list.Add(new CoreCandleData(Start.AddDays(i), close, close, close, close, 1000L));
        }

        return list;
    }

    private static List<CoreCandleData> Flat(int count, decimal price = 100m)
        => Enumerable.Range(0, count).Select(i => new CoreCandleData(Start.AddDays(i), price, price, price, price, 1000L)).ToList();

    private static decimal[] SimpleReturns(List<CoreCandleData> candles, int first, int last)
    {
        var returns = new decimal[last - first];
        for (int i = 0; i < returns.Length; i++)
        {
            returns[i] = (candles[first + i + 1].Close - candles[first + i].Close) / candles[first + i].Close;
        }

        return returns;
    }

    [Fact]
    public void Rows_AreTheAnnualisationHeader_TheEightStatistics_AndTheBarCount_InThisOrder()
    {
        var candles = Candles(300);

        RangeRegimeDiagnosticCard card = RangeRegimeDiagnosticCardValues.Calculate(candles, 20, 272, 252);
        IReadOnlyList<DrawingCalculatedValue> rows = RangeRegimeDiagnosticCardValues.Calculate(card, IndicatorColor.Gray);

        Assert.Equal(
            new[] { "RegimeAnnualization", "RegimeSharpe", "RegimeSortino", "RegimeCvar95", "RegimeCagr", "RegimeHurstRs", "RegimeHurstDfa", "RegimeAdf", "RegimeKpss", "RangeBars" },
            rows.Select(r => r.Key).ToArray());
        Assert.Equal("Ann: 252/yr", rows[0].FormattedText);
        Assert.Equal(253, rows.Single(r => r.Key == "RangeBars").NumericValue);
        Assert.All(rows.Skip(1).Take(8), r => Assert.NotNull(r.NumericValue));   // a 253-bar range has every statistic
        Assert.Equal(10, DatePriceRangeValues.ValueCount);                       // the contract of the date-price range tool is untouched
    }

    [Fact]
    public void Statistics_EqualTheirKernels_OnTheSimpleReturnsOfTheRange()
    {
        var candles = Candles(300);
        RangeRegimeDiagnosticCard card = RangeRegimeDiagnosticCardValues.Calculate(candles, 20, 272, 252);
        decimal[] returns = SimpleReturns(candles, 20, 272);
        double[] asDouble = returns.Select(r => (double)r).ToArray();

        Assert.Equal(252, returns.Length);
        Assert.Equal((decimal)DslFinanceMath.SharpeRatio(asDouble, 0.0, 252).Value, card.Sharpe!.Value, 9);
        Assert.Equal(DslFinanceMath.SortinoRatio(returns, 0m, 252m).Value, card.Sortino);
        Assert.Equal(DslQuantAndInfoMath.EmpiricalCvar(returns, 0.95m).Value, card.Cvar);
        Assert.Equal((decimal)HurstMath.Exponent(asDouble), card.HurstRescaledRange!.Value, 9);
        Assert.Equal((decimal)HurstMath.DetrendedFluctuationExponent(asDouble), card.HurstDfa!.Value, 9);
        double[] closes = candles.Skip(20).Take(253).Select(c => (double)c.Close).ToArray();
        Assert.Equal((decimal)ArimaMath.ComputeKpssStatistic(closes), card.Kpss!.Value, 9);
        Assert.True(card.Cvar > 0m);   // a positive loss ratio
    }

    [Fact]
    public void Cagr_UsesElapsedStepsOverPeriodsPerYear_ADailyYearOf253BarsIsOneYear()
    {
        var candles = Candles(300);

        RangeRegimeDiagnosticCard daily = RangeRegimeDiagnosticCardValues.Calculate(candles, 20, 272, 252);
        decimal expected = (candles[272].Close / candles[20].Close) - 1m;   // years = (253 - 1) / 252 = 1
        Assert.Equal(expected, daily.Cagr!.Value, 9);

        // The same bars read as weekly (52 per year) are five years: the annualised growth is the fifth root.
        RangeRegimeDiagnosticCard weekly = RangeRegimeDiagnosticCardValues.Calculate(candles, 20, 272, 52);
        double years = 252.0 / 52.0;
        Assert.Equal(Math.Pow((double)(candles[272].Close / candles[20].Close), 1.0 / years) - 1.0, (double)weekly.Cagr!.Value, 6);
        Assert.Equal(52, weekly.PeriodsPerYear);
    }

    [Theory]
    [InlineData(63, false)]   // 62 / 252 = 0.246 years: below the 0.25 limit
    [InlineData(64, true)]    // 63 / 252 = 0.25 years: shown
    [InlineData(65, true)]
    public void Cagr_IsHiddenBelowAQuarterOfAYear(int bars, bool shown)
    {
        var candles = Candles(100);

        RangeRegimeDiagnosticCard card = RangeRegimeDiagnosticCardValues.Calculate(candles, 0, bars - 1, 252);

        Assert.Equal(shown, card.Cagr.HasValue);
        IReadOnlyList<DrawingCalculatedValue> rows = RangeRegimeDiagnosticCardValues.Calculate(card, IndicatorColor.Gray);
        if (!shown)
        {
            Assert.Equal("-", rows.Single(r => r.Key == "RegimeCagr").FormattedText);
        }

        Assert.Equal(0.25m, RangeRegimeDiagnosticCardValues.MinCagrYears);
    }

    [Fact]
    public void Sharpe_IsAnnualisedByTheSquareRootOfPeriodsPerYear()
    {
        var candles = Candles(300);

        decimal daily = RangeRegimeDiagnosticCardValues.Calculate(candles, 20, 272, 252).Sharpe!.Value;
        decimal weekly = RangeRegimeDiagnosticCardValues.Calculate(candles, 20, 272, 52).Sharpe!.Value;

        Assert.Equal((double)daily * Math.Sqrt(52.0 / 252.0), (double)weekly, 6);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public void Kpss_NeedsEightCloses(int bars, bool shown)
        => Assert.Equal(shown, RangeRegimeDiagnosticCardValues.Calculate(Candles(60), 0, bars - 1, 252).Kpss.HasValue);

    [Theory]
    [InlineData(16, false)]   // 15 returns
    [InlineData(17, true)]    // 16 returns
    public void HurstDfa_NeedsSixteenReturns(int bars, bool shown)
        => Assert.Equal(shown, RangeRegimeDiagnosticCardValues.Calculate(Candles(60), 0, bars - 1, 252).HurstDfa.HasValue);

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void Adf_NeedsTheSmallestWindowOfTheIndicator(int bars, bool shown)
        => Assert.Equal(shown, RangeRegimeDiagnosticCardValues.Calculate(Candles(60), 0, bars - 1, 252).AdfTau.HasValue);

    [Fact]
    public void Hurst_RescaledRange_NeedsTwoScales_FifteenReturnsGiveNothing()
    {
        Assert.False(RangeRegimeDiagnosticCardValues.Calculate(Candles(60), 0, 15, 252).HurstRescaledRange.HasValue);   // 15 returns: one scale
        Assert.True(RangeRegimeDiagnosticCardValues.Calculate(Candles(60), 0, 16, 252).HurstRescaledRange.HasValue);    // 16 returns: 16 and 8
    }

    [Fact]
    public void ShortRange_ShowsDashesForWhatItCannotCalculate_AndKeepsTheRest()
    {
        var candles = Candles(60);

        RangeRegimeDiagnosticCard card = RangeRegimeDiagnosticCardValues.Calculate(candles, 0, 4, 252);   // 5 bars, 4 returns

        Assert.NotNull(card.Sharpe);
        Assert.NotNull(card.Cvar);
        Assert.Null(card.Cagr);
        Assert.Null(card.HurstRescaledRange);
        Assert.Null(card.HurstDfa);
        Assert.Null(card.AdfTau);
        Assert.Null(card.Kpss);
        IReadOnlyList<DrawingCalculatedValue> rows = RangeRegimeDiagnosticCardValues.Calculate(card, IndicatorColor.Gray);
        Assert.Equal("-", rows.Single(r => r.Key == "RegimeKpss").FormattedText);
        Assert.Null(rows.Single(r => r.Key == "RegimeAdf").NumericValue);
    }

    [Fact]
    public void SingleBar_HasNoReturns_EveryStatisticIsADash()
    {
        RangeRegimeDiagnosticCard card = RangeRegimeDiagnosticCardValues.Calculate(Candles(10), 3, 3, 252);

        Assert.Equal(1, card.RangeBars);
        Assert.Null(card.Sharpe);
        Assert.Null(card.Sortino);
        Assert.Null(card.Cvar);
        Assert.Null(card.Cagr);
        Assert.Null(card.Kpss);
    }

    [Fact]
    public void FlatPrices_GiveDashesWhereTheKernelCannotTest_AndZeroWhereItCan()
    {
        var candles = Flat(300);

        RangeRegimeDiagnosticCard card = RangeRegimeDiagnosticCardValues.Calculate(candles, 0, 299, 252);

        Assert.Null(card.Sharpe);
        Assert.Null(card.Sortino);
        Assert.Null(card.HurstRescaledRange);
        Assert.Null(card.HurstDfa);
        Assert.Null(card.AdfTau);
        Assert.Null(card.Kpss);   // the kernel would answer 0.0: the flat-window guard shows a dash instead
        Assert.Equal(0m, card.Cvar);
        Assert.Equal(0m, card.Cagr);
    }

    [Fact]
    public void ANonPositiveClose_RemovesTheReturnAndLogRows_ButNotTheLevelKpss()
    {
        var candles = Candles(80);
        candles[40] = new CoreCandleData(candles[40].Timestamp, 0m, 0m, 0m, 0m, 1L);

        RangeRegimeDiagnosticCard card = RangeRegimeDiagnosticCardValues.Calculate(candles, 0, 79, 252);

        Assert.Null(card.Sharpe);
        Assert.Null(card.Cvar);
        Assert.Null(card.HurstRescaledRange);
        Assert.Null(card.AdfTau);
        Assert.NotNull(card.Kpss);
    }

    [Fact]
    public void TheBarBeforeTheRange_IsNeverRead()
    {
        var candles = Candles(300);
        RangeRegimeDiagnosticCard baseline = RangeRegimeDiagnosticCardValues.Calculate(candles, 20, 272, 252);

        var changed = candles.ToList();
        changed[19] = new CoreCandleData(changed[19].Timestamp, 0m, 0m, 0m, 0m, 1L);
        changed[273] = new CoreCandleData(changed[273].Timestamp, 0m, 0m, 0m, 0m, 1L);
        RangeRegimeDiagnosticCard again = RangeRegimeDiagnosticCardValues.Calculate(changed, 20, 272, 252);

        Assert.Equal(
            (baseline.Sharpe, baseline.Sortino, baseline.Cvar, baseline.Cagr, baseline.HurstRescaledRange, baseline.HurstDfa, baseline.AdfTau, baseline.Kpss),
            (again.Sharpe, again.Sortino, again.Cvar, again.Cagr, again.HurstRescaledRange, again.HurstDfa, again.AdfTau, again.Kpss));
    }

    [Fact]
    public void PeriodsPerYear_IsClamped_AndTheHeaderAlwaysShowsIt()
    {
        var candles = Candles(100);

        Assert.Equal(1, RangeRegimeDiagnosticCardValues.Calculate(candles, 0, 99, 0).PeriodsPerYear);
        Assert.Equal(366, RangeRegimeDiagnosticCardValues.Calculate(candles, 0, 99, 99999).PeriodsPerYear);
        Assert.Equal("Ann: 52/yr", RangeRegimeDiagnosticCardValues.AnnualizationText(52));
        string header = RangeRegimeDiagnosticCardValues.Lines(RangeRegimeDiagnosticCardValues.Calculate(candles, 0, 99, 52))[0];
        Assert.Equal("Annualization: Ann: 52/yr", header);
    }

    [Fact]
    public void NoRange_HasNoValues_AndNoLines()
    {
        var candles = Candles(50);

        Assert.Same(RangeRegimeDiagnosticCard.Empty, RangeRegimeDiagnosticCardValues.Calculate(candles, 5, 3, 252));
        Assert.Same(RangeRegimeDiagnosticCard.Empty, RangeRegimeDiagnosticCardValues.Calculate(candles, 0, 50, 252));
        Assert.Empty(RangeRegimeDiagnosticCardValues.Calculate(RangeRegimeDiagnosticCard.Empty, IndicatorColor.Gray));
        Assert.Empty(RangeRegimeDiagnosticCardValues.Lines(RangeRegimeDiagnosticCard.Empty));
    }

    [Fact]
    public void AnchorOverload_FindsTheBarsBetweenTheAnchorTimes_InEitherOrder()
    {
        var candles = Candles(300);
        var early = new DrawingAnchor(candles[20].Timestamp, 100m);
        var late = new DrawingAnchor(candles[272].Timestamp, 100m);

        IReadOnlyList<DrawingCalculatedValue> forward = RangeRegimeDiagnosticCardValues.Calculate(early, late, candles, 252, IndicatorColor.Gray);
        IReadOnlyList<DrawingCalculatedValue> backward = RangeRegimeDiagnosticCardValues.Calculate(late, early, candles, 252, IndicatorColor.Gray);

        Assert.Equal(forward.Select(v => (v.Key, v.NumericValue)), backward.Select(v => (v.Key, v.NumericValue)));
        Assert.Equal(253, forward.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Empty(RangeRegimeDiagnosticCardValues.Calculate(early, late, null, 252, IndicatorColor.Gray));
    }

    [Fact]
    public void Catalogue_ListsTheTwoToolsLast_ReadsTheOptionsAndKeepsTheOldOptionsDefault()
    {
        var candles = Candles(300);
        var anchors = new[] { new DrawingAnchor(candles[20].Timestamp, 100m), new DrawingAnchor(candles[272].Timestamp, 100m) };
        Assert.True(DrawingValueCatalog.TryGet("RangeRegimeDiagnosticCard", out DrawingValueDescriptor card));
        Assert.True(DrawingValueCatalog.TryGet("GarchVolatilityCone", out DrawingValueDescriptor garch));
        Assert.Equal(new[] { "GarchVolatilityCone", "RangeRegimeDiagnosticCard" }, DrawingValueCatalog.All.TakeLast(2).Select(d => d.Name).ToArray());
        Assert.True(card.RequiresCandles && garch.RequiresCandles);

        var defaults = new DrawingValueRequest(anchors, candles[272].Timestamp, 100m, IndicatorColor.Gray, new DrawingValueOptions(), candles);
        Assert.Equal(252, defaults.Options.RegimeCardPeriodsPerYear);
        Assert.Equal(GarchVolatilityConeValues.DefaultHorizon, defaults.Options.GarchForecastBars);
        Assert.Equal("Ann: 252/yr", card.Calculate(defaults).First().FormattedText);

        var weekly = defaults with { Options = new DrawingValueOptions { RegimeCardPeriodsPerYear = 52 } };
        Assert.Equal("Ann: 52/yr", card.Calculate(weekly).First().FormattedText);
        Assert.Equal(
            GarchVolatilityConeValues.Calculate(GarchVolatilityConeValues.Calculate(candles, 20, 272, 40), IndicatorColor.Gray).Select(v => v.NumericValue),
            garch.Calculate(defaults with { Options = new DrawingValueOptions { GarchForecastBars = 40 } }).Select(v => v.NumericValue));
    }
}
