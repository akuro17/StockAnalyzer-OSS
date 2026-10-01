using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Independent reference implementation of the 15 extended metrics (Y:\Temp\sa_implementation_plan_BacktestReportVerificationOracle.md, T4).
/// It shares no code and deliberately no algorithm shape with production: LINQ partitions instead of one accumulating loop, run-length
/// strings instead of a streak counter, an O(m^2) enumeration of drawdown episodes instead of one running-peak pass, and a position rebuilt
/// from the cumulative signed fill quantity instead of the per-bar MarketValue/HeldMargin snapshot. Obviously-correct and slow on purpose.
/// Everything is decimal, so results are compared exactly. The spec it encodes is Y:\Temp\sa_technical_review_spec_BacktestReportExtendedMetrics.md.
/// </summary>
internal static class ExtendedMetricsReference
{
    public sealed record Expected(
        MetricValue GrossProfit, MetricValue GrossLoss, MetricValue AverageWin, MetricValue AverageLoss, MetricValue PayoffRatio,
        MetricValue LargestWin, MetricValue LargestLoss, MetricValue AverageHoldingPeriod, MetricValue MaxConsecutiveWins,
        MetricValue MaxConsecutiveLosses, MetricValue MaxDepthDrawdownDuration, MetricValue LongestDrawdownDuration, MetricValue TimeInMarket,
        MetricValue Exposure, MetricValue ExposureAdjustedCAGR,
        bool? MaxDepthDrawdownDurationRightCensored, bool? LongestDrawdownDurationRightCensored)
    {
        public IEnumerable<(string Name, MetricValue Metric)> Named()
        {
            yield return (nameof(GrossProfit), GrossProfit);
            yield return (nameof(GrossLoss), GrossLoss);
            yield return (nameof(AverageWin), AverageWin);
            yield return (nameof(AverageLoss), AverageLoss);
            yield return (nameof(PayoffRatio), PayoffRatio);
            yield return (nameof(LargestWin), LargestWin);
            yield return (nameof(LargestLoss), LargestLoss);
            yield return (nameof(AverageHoldingPeriod), AverageHoldingPeriod);
            yield return (nameof(MaxConsecutiveWins), MaxConsecutiveWins);
            yield return (nameof(MaxConsecutiveLosses), MaxConsecutiveLosses);
            yield return (nameof(MaxDepthDrawdownDuration), MaxDepthDrawdownDuration);
            yield return (nameof(LongestDrawdownDuration), LongestDrawdownDuration);
            yield return (nameof(TimeInMarket), TimeInMarket);
            yield return (nameof(Exposure), Exposure);
            yield return (nameof(ExposureAdjustedCAGR), ExposureAdjustedCAGR);
        }
    }

    /// <param name="result">The backtest result the report is generated from.</param>
    /// <param name="historyStartIndex">First EquityPoint that belongs to the report sample.</param>
    /// <param name="tradingStartIndex">First bar on which the strategy is evaluated; the bar-presence population starts at max(history, trading).</param>
    /// <param name="cagr">CAGR as produced by the report; it is an input of ExposureAdjustedCAGR, not something this oracle re-derives.</param>
    public static Expected Compute(BacktestResult result, int historyStartIndex, int tradingStartIndex, MetricValue cagr)
    {
        IReadOnlyList<decimal> nets = result.Trades.Select(trade => trade.ClosedNet).ToList();
        IReadOnlyList<int> holdingBars = result.Trades.Select(trade => trade.HoldingBars).ToList();

        // Equity path E[0..m]: the initial capital, then the sample bars.
        var equity = new List<decimal> { result.Configuration.InitialCapital };
        equity.AddRange(result.EquityPoints.Skip(historyStartIndex).Select(point => point.Equity));

        var durations = DrawdownDurationPair(equity);
        (MetricValue timeInMarket, MetricValue exposure) = TimeAndExposure(result, Math.Max(historyStartIndex, tradingStartIndex));
        return new Expected(
            GrossProfit: NoTradesOr(nets, MetricUnit.Currency, () => MetricValue.Valid(Wins(nets).Sum(), MetricUnit.Currency)),
            GrossLoss: NoTradesOr(nets, MetricUnit.Currency, () => MetricValue.Valid(Losses(nets).Sum(), MetricUnit.Currency)),
            AverageWin: NoTradesOr(nets, MetricUnit.Currency, () => Wins(nets).Any()
                ? MetricValue.Valid(Wins(nets).Average(), MetricUnit.Currency)
                : NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoWinningTrades)),
            AverageLoss: NoTradesOr(nets, MetricUnit.Currency, () => Losses(nets).Any()
                ? MetricValue.Valid(Losses(nets).Average(), MetricUnit.Currency)
                : NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoLosingTrades)),
            PayoffRatio: NoTradesOr(nets, MetricUnit.Dimensionless, () => Payoff(nets)),
            LargestWin: NoTradesOr(nets, MetricUnit.Currency, () => Wins(nets).Any()
                ? MetricValue.Valid(Wins(nets).Max(), MetricUnit.Currency)
                : NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoWinningTrades)),
            LargestLoss: NoTradesOr(nets, MetricUnit.Currency, () => Losses(nets).Any()
                ? MetricValue.Valid(Losses(nets).Max(), MetricUnit.Currency)
                : NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoLosingTrades)),
            AverageHoldingPeriod: NoTradesOr(nets, MetricUnit.Bars,
                () => MetricValue.Valid(holdingBars.Sum(bars => (decimal)bars) / holdingBars.Count, MetricUnit.Bars)),
            MaxConsecutiveWins: NoTradesOr(nets, MetricUnit.Count,
                () => MetricValue.Valid(LongestRun(SignString(nets), 'W'), MetricUnit.Count)),
            MaxConsecutiveLosses: NoTradesOr(nets, MetricUnit.Count,
                () => MetricValue.Valid(LongestRun(SignString(nets), 'L'), MetricUnit.Count)),
            MaxDepthDrawdownDuration: durations.Depth,
            LongestDrawdownDuration: durations.Longest,
            TimeInMarket: timeInMarket,
            Exposure: exposure,
            ExposureAdjustedCAGR: ExposureAdjusted(cagr, exposure),
            MaxDepthDrawdownDurationRightCensored: durations.DepthCensored,
            LongestDrawdownDurationRightCensored: durations.LongestCensored);
    }

    /// <summary>Exact comparison of one metric (status, reason, unit and value), naming the case and the metric on a mismatch.</summary>
    public static void AssertSameMetric(string label, string name, MetricValue expected, MetricValue actual) =>
        Xunit.Assert.True(
            expected == actual,
            $"{label}: {name} differs. expected {Describe(expected)} but production gave {Describe(actual)}.");

    private static string Describe(MetricValue metric) => $"[{metric.Status}/{metric.Reason}/{metric.Unit}/{metric.Value?.ToString() ?? "null"}]";

    /// <summary>Asserts that <paramref name="report"/> equals the reference for <paramref name="result"/>: all 15 extended metrics and both censoring flags.</summary>
    public static void AssertMatches(string label, BacktestResult result, BacktestReport report, int historyStartIndex, int tradingStartIndex)
    {
        Expected expected = Compute(result, historyStartIndex, tradingStartIndex, report.CAGR);

        Dictionary<string, MetricValue> actual = report.EnumerateExtendedMetrics().ToDictionary(entry => entry.Name, entry => entry.Metric);
        Xunit.Assert.Equal(15, actual.Count);
        foreach ((string name, MetricValue metric) in expected.Named())
        {
            AssertSameMetric(label, name, metric, actual[name]);
        }
        Xunit.Assert.True(
            expected.MaxDepthDrawdownDurationRightCensored == report.MaxDepthDrawdownDurationRightCensored,
            $"{label}: max-depth censoring flag expected {expected.MaxDepthDrawdownDurationRightCensored?.ToString() ?? "null"} but production gave {report.MaxDepthDrawdownDurationRightCensored?.ToString() ?? "null"}.");
        Xunit.Assert.True(
            expected.LongestDrawdownDurationRightCensored == report.LongestDrawdownDurationRightCensored,
            $"{label}: longest censoring flag expected {expected.LongestDrawdownDurationRightCensored?.ToString() ?? "null"} but production gave {report.LongestDrawdownDurationRightCensored?.ToString() ?? "null"}.");
    }

    private static MetricValue NonValid(MetricStatus status, MetricUnit unit, MetricReason reason) => MetricValue.NonValid(status, unit, reason);

    private static MetricValue NoTradesOr(IReadOnlyList<decimal> nets, MetricUnit unit, Func<MetricValue> whenTradesExist) =>
        nets.Count == 0 ? NonValid(MetricStatus.InsufficientData, unit, MetricReason.NoClosedTrades) : whenTradesExist();

    private static IEnumerable<decimal> Wins(IReadOnlyList<decimal> nets) => nets.Where(net => net > 0m);

    /// <summary>Loss magnitudes (positive numbers).</summary>
    private static IEnumerable<decimal> Losses(IReadOnlyList<decimal> nets) => nets.Where(net => net < 0m).Select(net => -net);

    private static MetricValue Payoff(IReadOnlyList<decimal> nets)
    {
        bool anyWin = Wins(nets).Any();
        bool anyLoss = Losses(nets).Any();
        if (!anyWin && !anyLoss) return NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.AllBreakeven);
        if (!anyLoss) return NonValid(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.ZeroDivisor);
        if (!anyWin) return NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.NoWinningTrades);
        return MetricValue.Valid(Wins(nets).Average() / Losses(nets).Average(), MetricUnit.Dimensionless);
    }

    /// <summary>One letter per trade: W (net &gt; 0), L (net &lt; 0), B (breakeven).</summary>
    private static string SignString(IReadOnlyList<decimal> nets) =>
        string.Concat(nets.Select(net => net > 0m ? 'W' : net < 0m ? 'L' : 'B'));

    private static int LongestRun(string signs, char target) =>
        signs.Split("WLB".Where(letter => letter != target).ToArray(), StringSplitOptions.RemoveEmptyEntries)
            .Select(run => run.Length)
            .DefaultIfEmpty(0)
            .Max();

    private readonly record struct Episode(int Peak, int End, decimal Depth, bool Recovered);

    /// <summary>
    /// Every drawdown episode of E[0..m]: for each index p whose equity is at least every earlier equity (a running high, equal highs
    /// included), the episode runs to the first later bar with E &gt;= E[p] (or to m when there is none). It exists only when some bar in
    /// between is below E[p]. Its depth is the largest (E[p]-E[j])/E[p] in between.
    /// </summary>
    private static List<Episode> EnumerateEpisodes(IReadOnlyList<decimal> equity)
    {
        int m = equity.Count - 1;
        var episodes = new List<Episode>();
        for (int p = 0; p <= m; p++)
        {
            if (Enumerable.Range(0, p).Any(earlier => equity[earlier] > equity[p])) continue;

            int recovery = Enumerable.Range(p + 1, m - p).Where(later => equity[later] >= equity[p]).DefaultIfEmpty(-1).First();
            int end = recovery == -1 ? m : recovery;
            int lastInside = recovery == -1 ? m : recovery - 1;
            List<int> below = Enumerable.Range(p + 1, lastInside - p).Where(between => equity[between] < equity[p]).ToList();
            if (below.Count == 0) continue;

            decimal depth = below.Max(between => (equity[p] - equity[between]) / equity[p]);
            episodes.Add(new Episode(p, end, depth, Recovered: recovery != -1));
        }
        return episodes;
    }

    /// <summary>
    /// The two duration metrics plus their right-censoring flags. Censored means an episode that never regained its peak (the last one) is
    /// behind the value: for the deepest episode when it is unrecovered; for the longest when an unrecovered episode is as long as the value.
    /// </summary>
    private static (MetricValue Depth, MetricValue Longest, bool? DepthCensored, bool? LongestCensored) DrawdownDurationPair(IReadOnlyList<decimal> equity)
    {
        if (equity.Count - 1 < 1)
        {
            MetricValue empty = NonValid(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput);
            return (empty, empty, null, null);
        }

        List<Episode> episodes = EnumerateEpisodes(equity);
        if (episodes.Count == 0) return (MetricValue.Valid(0m, MetricUnit.Bars), MetricValue.Valid(0m, MetricUnit.Bars), false, false);

        // Deepest ratio episode; equal depth -> the earlier one (first maximal trough).
        Episode deepest = episodes.OrderByDescending(episode => episode.Depth).ThenBy(episode => episode.Peak).First();
        int longest = episodes.Max(episode => episode.End - episode.Peak);
        bool longestIsLowerBound = episodes.Any(episode => !episode.Recovered && episode.End - episode.Peak == longest);
        return (
            MetricValue.Valid(deepest.End - deepest.Peak, MetricUnit.Bars),
            MetricValue.Valid(longest, MetricUnit.Bars),
            !deepest.Recovered,
            longestIsLowerBound);
    }

    /// <summary>
    /// Bar presence rebuilt from the fills (Buy +, Sell -), never from MarketValue/HeldMargin. A bar is in the market when the position after its
    /// last fill is non-zero, or when a position was opened during the bar (a fill that moves the running position away from exactly zero) and
    /// closed again before the bar ended - the same-bar round trip whose Close snapshot is flat.
    /// </summary>
    private static (MetricValue TimeInMarket, MetricValue Exposure) TimeAndExposure(BacktestResult result, int firstSampleBar)
    {
        int sampleBars = Math.Max(0, result.EquityPoints.Length - firstSampleBar);
        if (sampleBars < 1)
        {
            return (
                NonValid(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput),
                NonValid(MetricStatus.InsufficientData, MetricUnit.ExposureRatio, MetricReason.EmptyInput));
        }

        int barsInMarket = Enumerable.Range(firstSampleBar, sampleBars).Count(bar => WasInMarket(result, bar));
        return (
            MetricValue.Valid(barsInMarket, MetricUnit.Bars),
            MetricValue.Valid((decimal)barsInMarket / sampleBars, MetricUnit.ExposureRatio));
    }

    private static decimal Signed(BacktestFill fill) => fill.Side == OrderSide.Buy ? fill.Quantity : -fill.Quantity;

    private static bool WasInMarket(BacktestResult result, int bar)
    {
        decimal running = result.Fills.Where(fill => fill.BarIndex < bar).Sum(Signed);
        bool openedDuringTheBar = false;
        foreach (BacktestFill fill in result.Fills.Where(fill => fill.BarIndex == bar))
        {
            decimal after = running + Signed(fill);
            if (running == 0m && after != 0m) openedDuringTheBar = true;
            running = after;
        }
        return running != 0m || openedDuringTheBar;
    }

    private static MetricValue ExposureAdjusted(MetricValue cagr, MetricValue exposure)
    {
        if (cagr.Status != MetricStatus.Valid) return NonValid(cagr.Status, MetricUnit.ReturnRatio, cagr.Reason);
        if (exposure.Status != MetricStatus.Valid) return NonValid(exposure.Status, MetricUnit.ReturnRatio, exposure.Reason);
        if (exposure.Value == 0m) return NonValid(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.ZeroDivisor);
        return MetricValue.Valid(cagr.Value!.Value / exposure.Value!.Value, MetricUnit.ReturnRatio);
    }
}
