using System;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Bar-presence metrics derived from the per-bar position state recorded in <see cref="EquityPoint"/>.</summary>
internal static class ExposureMetricsCalculator
{
    /// <summary>
    /// A sample bar is "in the market" when its Close snapshot shows an open position: <c>MarketValue != 0</c> (cash account, Q*Close) or
    /// <c>HeldMargin != 0</c> (margin account). Unlike a Trades-derived count this includes a position still open at the end of the run
    /// (the engine does not force-close it), and a same-bar reversal is a single in-market bar, so nothing is double counted.
    /// </summary>
    public static bool IsInMarket(EquityPoint point) => point.MarketValue != 0m || point.HeldMargin != 0m;

    /// <summary>
    /// ExposureAdjustedCAGR = CAGR / Exposure: the annualized return per unit of time in the market (unit ReturnRatio, a raw ratio). It is a
    /// linear scaling, not a compounded always-invested CAGR. Precedence follows CalmarFullPeriod: a non-Valid CAGR propagates first
    /// (including the coverage-qualified NotApplicable), then a non-Valid Exposure, then Exposure == 0 -&gt; Undefined(ZeroDivisor).
    /// </summary>
    public static MetricValue ComputeExposureAdjustedCagr(MetricValue cagr, MetricValue exposure)
    {
        if (cagr.Status != MetricStatus.Valid) return MetricCalculation.Propagate(cagr, MetricUnit.ReturnRatio);
        if (exposure.Status != MetricStatus.Valid) return MetricCalculation.Propagate(exposure, MetricUnit.ReturnRatio);
        if (exposure.Value!.Value == 0m)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.ZeroDivisor);
        }
        return MetricCalculation.Run(
            MetricUnit.ReturnRatio,
            () => MetricValue.Valid(cagr.Value!.Value / exposure.Value.Value, MetricUnit.ReturnRatio));
    }

    /// <summary>
    /// TimeInMarket = number of sample bars in the market (unit Bars); Exposure = TimeInMarket / m (unit ExposureRatio, a raw 0..1 ratio).
    /// The population is <c>points[start..]</c> with <c>start = max(historyStartIndex, tradingStartIndex)</c>: the engine evaluates the strategy
    /// only from the trading start bar, so no position can exist in <c>[historyStartIndex, tradingStartIndex)</c> and counting those warm-up bars
    /// would dilute Exposure. When the two indices are equal this is exactly the <see cref="EquitySample"/> sample E[1..m].
    /// A bar is in the market when its Close snapshot shows a position (<see cref="IsInMarket"/>) OR a trade was opened and closed on that very
    /// bar (<c>EntryBar == ExitBar</c>, e.g. a forced liquidation on the entry bar, which leaves a flat Close snapshot); each bar is counted once.
    /// m &lt; 1 -&gt; both InsufficientData(EmptyInput).
    /// </summary>
    public static (MetricValue TimeInMarket, MetricValue Exposure) Compute(
        ImmutableArray<EquityPoint> points,
        ImmutableArray<BacktestTrade> trades,
        int historyStartIndex,
        int tradingStartIndex,
        CancellationToken cancellationToken = default)
    {
        int start = Math.Max(historyStartIndex, tradingStartIndex);
        int m = Math.Max(0, points.Length - start);
        if (m < 1)
        {
            return (
                MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput),
                MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.ExposureRatio, MetricReason.EmptyInput));
        }

        var inMarket = new bool[m];
        for (int i = 0; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            inMarket[i] = IsInMarket(points[start + i]);
        }
        for (int i = 0; i < trades.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            BacktestTrade trade = trades[i];
            if (trade.EntryBar != trade.ExitBar) continue;
            int sampleIndex = trade.EntryBar - start;
            if (sampleIndex >= 0 && sampleIndex < m) inMarket[sampleIndex] = true;
        }

        int inMarketBars = 0;
        for (int i = 0; i < m; i++)
        {
            if (inMarket[i]) inMarketBars++;
        }
        return (
            MetricValue.Valid(inMarketBars, MetricUnit.Bars),
            MetricValue.Valid((decimal)inMarketBars / m, MetricUnit.ExposureRatio));
    }
}
