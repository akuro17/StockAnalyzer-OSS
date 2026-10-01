using System;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Money, ratio, and trade-count metrics with no cross-metric dependency (spec §5.4). Every <c>BacktestTrade</c> in <c>BacktestResult.Trades</c> is already a closed round-trip (P1 schema), so K = Trades.Length.</summary>
internal static class BasicMetricsCalculator
{
    /// <summary>Mean Gregorian year length in days used by the CAGR period Y (Gate B).</summary>
    private const double DaysPerYear = 365.2425;

    private static double PeriodYears(BacktestReportOptions options) =>
        (options.EvaluationEndUtc.Ticks - options.EvaluationStartUtc.Ticks) / (double)TimeSpan.TicksPerDay / DaysPerYear;

    /// <summary>TotalPnL = E[m] - E[0]. m &gt;= 1 required; m &lt; 1 -> InsufficientData.</summary>
    public static MetricValue ComputeTotalPnL(ImmutableArray<decimal> equity)
    {
        int m = equity.Length - 1;
        if (m < 1)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Currency, MetricReason.EmptyInput);
        }
        return MetricValue.Valid(equity[m] - equity[0], MetricUnit.Currency);
    }

    /// <summary>TotalReturn = E[m]/E[0] - 1. Always valid: E[0] &gt; 0 is guaranteed by BacktestConfiguration.</summary>
    public static MetricValue ComputeTotalReturn(ImmutableArray<decimal> equity)
    {
        int m = equity.Length - 1;
        decimal ratio = equity[m] / equity[0] - 1m;
        return MetricValue.Valid(ratio, MetricUnit.ReturnRatio);
    }

    /// <summary>
    /// CAGR = (E[m]/E[0])^(1/Y) - 1, Y = (EndUtc.Ticks - StartUtc.Ticks) / TicksPerDay / 365.2425 (Gate B:
    /// Start/End = BacktestReportOptions.EvaluationStartUtc/EvaluationEndUtc). Evaluated in the row's
    /// literal order: Y&lt;=0 -&gt; Undefined(ZeroPeriod); else Eend==0 -&gt; Valid(-1); else Eend&lt;0 -&gt;
    /// Undefined(NegativeFinalEquity); else the normal power-law formula (non-linear op -> double, Gate G3).
    /// </summary>
    public static MetricValue ComputeCagr(
        ImmutableArray<decimal> equity,
        BacktestReportOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int m = equity.Length - 1;
        decimal eStart = equity[0];
        decimal eEnd = equity[m];

        double y = PeriodYears(options);

        if (y <= 0d)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.ZeroPeriod);
        }
        if (eEnd == 0m)
        {
            return MetricValue.Valid(-1m, MetricUnit.ReturnRatio);
        }
        if (eEnd < 0m)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.NegativeFinalEquity);
        }

        return ComputePowerLawCagr(eStart, eEnd, y);
    }

    /// <summary>
    /// Coverage-qualified CAGR (evaluation master §4). Precedence: Y&lt;=0 -&gt; Undefined(ZeroPeriod); Eend&lt;0 -&gt;
    /// Undefined(NegativeFinalEquity); coverage not Verified -&gt; NotApplicable(EvaluationCoverageUnverified) before any
    /// division or power; Eend==0 -&gt; Valid(-1); else the unchanged power-law formula. <see cref="ComputeCagr"/> stays the ungated legacy entry.
    /// </summary>
    public static MetricValue ComputeQualifiedCagr(
        ImmutableArray<decimal> equity,
        BacktestReportOptions options,
        bool coverageVerified,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int m = equity.Length - 1;
        decimal eStart = equity[0];
        decimal eEnd = equity[m];

        double y = PeriodYears(options);
        if (y <= 0d)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.ZeroPeriod);
        }
        if (eEnd < 0m)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.NegativeFinalEquity);
        }
        if (!coverageVerified)
        {
            return MetricValue.NonValid(MetricStatus.NotApplicable, MetricUnit.ReturnRatio, MetricReason.EvaluationCoverageUnverified);
        }
        if (eEnd == 0m)
        {
            return MetricValue.Valid(-1m, MetricUnit.ReturnRatio);
        }
        return ComputePowerLawCagr(eStart, eEnd, y);
    }

    private static MetricValue ComputePowerLawCagr(decimal eStart, decimal eEnd, double years)
    {
        double ratio = (double)(eEnd / eStart);
        double cagr = Math.Pow(ratio, 1.0 / years) - 1.0;
        return MetricCalculation.FromDouble(cagr, MetricUnit.ReturnRatio);
    }

    /// <summary>WinRate = wins / K. K = Trades.Length. K == 0 -> InsufficientData(NoClosedTrades).</summary>
    public static MetricValue ComputeWinRate(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeWinRate(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeWinRate(in TradeStatistics stats)
    {
        if (stats.Count == 0)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.WinRateRatio, MetricReason.NoClosedTrades);
        }
        return MetricValue.Valid((decimal)stats.Wins / stats.Count, MetricUnit.WinRateRatio);
    }

    /// <summary>
    /// ProfitFactor = G/L. K==0 -> InsufficientData(NoClosedTrades). G=L=0 -> Undefined(AllBreakeven).
    /// L=0, G&gt;0 -> PositiveInfinity (Value stays null per Rule DT-4; ZeroDivisor is the closest-matching
    /// reason, same underlying zero-denominator cause as Calmar/Sharpe's ZeroDivisor case).
    /// G=0, L&gt;0 -> Valid(0).
    /// </summary>
    public static MetricValue ComputeProfitFactor(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeProfitFactor(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeProfitFactor(in TradeStatistics stats)
    {
        if (stats.Count == 0)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Dimensionless, MetricReason.NoClosedTrades);
        }
        ThrowIfGrossOverflowed(stats);

        if (stats.GrossProfit == 0m && stats.GrossLoss == 0m)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.AllBreakeven);
        }
        if (stats.GrossLoss == 0m)
        {
            return MetricValue.NonValid(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.ZeroDivisor);
        }
        return MetricValue.Valid(stats.GrossProfit / stats.GrossLoss, MetricUnit.Dimensionless);
    }

    private static MetricValue NoClosedTrades(MetricUnit unit) =>
        MetricValue.NonValid(MetricStatus.InsufficientData, unit, MetricReason.NoClosedTrades);

    // A decimal overflow of a shared sum must fail exactly the metrics that depend on that sum. They surface it as the OverflowException
    // that MetricCalculation.Run maps to ArithmeticOverflow, as they did when each metric summed the trades itself.
    private static void ThrowIfGrossOverflowed(in TradeStatistics stats)
    {
        if (stats.GrossOverflowed) throw new OverflowException();
    }

    /// <summary>GrossProfit = sum of ClosedNet over trades with ClosedNet &gt; 0 (0 when there is none). K == 0 -> InsufficientData(NoClosedTrades).</summary>
    public static MetricValue ComputeGrossProfit(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeGrossProfit(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeGrossProfit(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Currency);
        ThrowIfGrossOverflowed(stats);
        return MetricValue.Valid(stats.GrossProfit, MetricUnit.Currency);
    }

    /// <summary>GrossLoss = sum of -ClosedNet over trades with ClosedNet &lt; 0, a non-negative magnitude (0 when there is none). K == 0 -> InsufficientData(NoClosedTrades).</summary>
    public static MetricValue ComputeGrossLoss(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeGrossLoss(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeGrossLoss(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Currency);
        ThrowIfGrossOverflowed(stats);
        return MetricValue.Valid(stats.GrossLoss, MetricUnit.Currency);
    }

    /// <summary>AverageWin = GrossProfit / W. K == 0 -> InsufficientData(NoClosedTrades); W == 0 -> Undefined(NoWinningTrades).</summary>
    public static MetricValue ComputeAverageWin(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeAverageWin(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeAverageWin(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Currency);
        ThrowIfGrossOverflowed(stats);
        if (stats.Wins == 0)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoWinningTrades);
        }
        return MetricValue.Valid(stats.GrossProfit / stats.Wins, MetricUnit.Currency);
    }

    /// <summary>AverageLoss = GrossLoss / L, a non-negative magnitude. K == 0 -> InsufficientData(NoClosedTrades); L == 0 -> Undefined(NoLosingTrades).</summary>
    public static MetricValue ComputeAverageLoss(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeAverageLoss(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeAverageLoss(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Currency);
        ThrowIfGrossOverflowed(stats);
        if (stats.Losses == 0)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoLosingTrades);
        }
        return MetricValue.Valid(stats.GrossLoss / stats.Losses, MetricUnit.Currency);
    }

    /// <summary>
    /// PayoffRatio = AverageWin / AverageLoss. Precedence: K == 0 -> InsufficientData(NoClosedTrades); W == 0 and L == 0 ->
    /// Undefined(AllBreakeven); L == 0 (W &gt; 0) -> PositiveInfinity(ZeroDivisor, Value stays null per Rule DT-4, same as ProfitFactor);
    /// W == 0 (L &gt; 0) -> Undefined(NoWinningTrades).
    /// </summary>
    public static MetricValue ComputePayoffRatio(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputePayoffRatio(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputePayoffRatio(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Dimensionless);
        ThrowIfGrossOverflowed(stats);
        if (stats.Wins == 0 && stats.Losses == 0)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.AllBreakeven);
        }
        if (stats.Losses == 0)
        {
            return MetricValue.NonValid(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.ZeroDivisor);
        }
        if (stats.Wins == 0)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.NoWinningTrades);
        }
        decimal averageWin = stats.GrossProfit / stats.Wins;
        decimal averageLoss = stats.GrossLoss / stats.Losses;
        return MetricValue.Valid(averageWin / averageLoss, MetricUnit.Dimensionless);
    }

    /// <summary>LargestWin = max ClosedNet over trades with ClosedNet &gt; 0. K == 0 -> InsufficientData(NoClosedTrades); W == 0 -> Undefined(NoWinningTrades).</summary>
    public static MetricValue ComputeLargestWin(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeLargestWin(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeLargestWin(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Currency);
        ThrowIfGrossOverflowed(stats);
        if (stats.Wins == 0)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoWinningTrades);
        }
        return MetricValue.Valid(stats.LargestWin, MetricUnit.Currency);
    }

    /// <summary>LargestLoss = max(-ClosedNet) over trades with ClosedNet &lt; 0, a non-negative magnitude. K == 0 -> InsufficientData(NoClosedTrades); L == 0 -> Undefined(NoLosingTrades).</summary>
    public static MetricValue ComputeLargestLoss(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeLargestLoss(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeLargestLoss(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Currency);
        ThrowIfGrossOverflowed(stats);
        if (stats.Losses == 0)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoLosingTrades);
        }
        return MetricValue.Valid(stats.LargestLoss, MetricUnit.Currency);
    }

    /// <summary>
    /// AverageHoldingPeriod = sum(HoldingBars) / K over all closed trades (wins, losses and breakevens), in bars regardless of the chart
    /// timeframe. K == 0 -> InsufficientData(NoClosedTrades).
    /// </summary>
    public static MetricValue ComputeAverageHoldingPeriod(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeAverageHoldingPeriod(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeAverageHoldingPeriod(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Bars);
        return MetricValue.Valid((decimal)stats.HoldingBarsSum / stats.Count, MetricUnit.Bars);
    }

    /// <summary>
    /// MaxConsecutiveWins = longest run of trades with ClosedNet &gt; 0. A breakeven trade (ClosedNet == 0) resets both this and the loss
    /// streak. <paramref name="trades"/> must be in the engine's chronological order (single position, appended on close: ExitBar
    /// non-decreasing); the order is consumed as given, never sorted. K == 0 -> InsufficientData(NoClosedTrades); no wins -> Valid(0).
    /// </summary>
    public static MetricValue ComputeMaxConsecutiveWins(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeMaxConsecutiveWins(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeMaxConsecutiveWins(in TradeStatistics stats) =>
        stats.Count == 0 ? NoClosedTrades(MetricUnit.Count) : MetricValue.Valid(stats.MaxWinStreak, MetricUnit.Count);

    /// <summary>MaxConsecutiveLosses = longest run of trades with ClosedNet &lt; 0. Same ordering, breakeven and K == 0 rules as <see cref="ComputeMaxConsecutiveWins(ImmutableArray{BacktestTrade}, CancellationToken)"/>.</summary>
    public static MetricValue ComputeMaxConsecutiveLosses(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeMaxConsecutiveLosses(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeMaxConsecutiveLosses(in TradeStatistics stats) =>
        stats.Count == 0 ? NoClosedTrades(MetricUnit.Count) : MetricValue.Valid(stats.MaxLossStreak, MetricUnit.Count);

    /// <summary>ExpectedPayoff = sum(ClosedNet) / K. K == 0 -> InsufficientData(NoClosedTrades).</summary>
    public static MetricValue ComputeExpectedPayoff(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default) =>
        ComputeExpectedPayoff(TradeStatistics.Compute(trades, cancellationToken));

    internal static MetricValue ComputeExpectedPayoff(in TradeStatistics stats)
    {
        if (stats.Count == 0) return NoClosedTrades(MetricUnit.Currency);
        if (stats.NetSumOverflowed) throw new OverflowException();
        return MetricValue.Valid(stats.NetSum / stats.Count, MetricUnit.Currency);
    }
}
