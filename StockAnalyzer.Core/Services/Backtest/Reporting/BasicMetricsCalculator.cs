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
        CancellationToken cancellationToken = default)
    {
        int k = trades.Length;
        if (k == 0)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.WinRateRatio, MetricReason.NoClosedTrades);
        }
        int wins = 0;
        for (int i = 0; i < trades.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            BacktestTrade t = trades[i];
            if (t.ClosedNet > 0m) wins++;
        }
        return MetricValue.Valid((decimal)wins / k, MetricUnit.WinRateRatio);
    }

    /// <summary>
    /// ProfitFactor = G/L. K==0 -> InsufficientData(NoClosedTrades). G=L=0 -> Undefined(AllBreakeven).
    /// L=0, G&gt;0 -> PositiveInfinity (Value stays null per Rule DT-4; ZeroDivisor is the closest-matching
    /// reason, same underlying zero-denominator cause as Calmar/Sharpe's ZeroDivisor case).
    /// G=0, L&gt;0 -> Valid(0).
    /// </summary>
    public static MetricValue ComputeProfitFactor(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default)
    {
        if (trades.Length == 0)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Dimensionless, MetricReason.NoClosedTrades);
        }

        decimal gains = 0m;
        decimal losses = 0m;
        for (int i = 0; i < trades.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            BacktestTrade t = trades[i];
            if (t.ClosedNet > 0m) gains += t.ClosedNet;
            else if (t.ClosedNet < 0m) losses += -t.ClosedNet;
        }

        if (gains == 0m && losses == 0m)
        {
            return MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.AllBreakeven);
        }
        if (losses == 0m)
        {
            return MetricValue.NonValid(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.ZeroDivisor);
        }
        return MetricValue.Valid(gains / losses, MetricUnit.Dimensionless);
    }

    /// <summary>ExpectedPayoff = sum(ClosedNet) / K. K == 0 -> InsufficientData(NoClosedTrades).</summary>
    public static MetricValue ComputeExpectedPayoff(
        ImmutableArray<BacktestTrade> trades,
        CancellationToken cancellationToken = default)
    {
        int k = trades.Length;
        if (k == 0)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Currency, MetricReason.NoClosedTrades);
        }
        decimal sum = 0m;
        for (int i = 0; i < trades.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            BacktestTrade t = trades[i];
            sum += t.ClosedNet;
        }
        return MetricValue.Valid(sum / k, MetricUnit.Currency);
    }
}
