using System;
using System.Collections.Immutable;
using System.Threading;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// H[j]/D[j] drawdown series and the two scalar drawdown metrics (spec §5.4). Clamping DD to [0,1] is
/// prohibited — short-side equity paths can legitimately exceed a 100% drawdown ratio (spec permits DD&gt;1
/// for short positions). The drawdown tie-resolution peak/trough/recovery bookkeeping described in spec §5.4 has no
/// corresponding field on <see cref="BacktestReport"/> and no named §6 acceptance fixture — a plain
/// max() over D[j]/(H[j]-E[j]) already yields the correct scalar values regardless of which index a tie
/// occurs at, so that bookkeeping is intentionally not built here (nothing in this task's scope consumes
/// it); flag to the user if a later task needs per-drawdown-period peak/trough/recovery detail exposed.
/// </summary>
internal static class DrawdownSeriesCalculator
{
    /// <summary>D[j] = (H[j]-E[j])/H[j] for j = 0..m, where H[j] = max(E[0..j]). E[0] &gt; 0 is guaranteed by BacktestConfiguration, so H[j] &gt; 0 always.</summary>
    public static ImmutableArray<double> ComputeDrawdownRatioSeries(
        ImmutableArray<decimal> equity,
        CancellationToken cancellationToken = default)
    {
        var series = ImmutableArray.CreateBuilder<double>(equity.Length);
        decimal peak = equity[0];
        for (int j = 0; j < equity.Length; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            if (equity[j] > peak)
            {
                peak = equity[j];
            }
            decimal ratio = (peak - equity[j]) / peak;
            double value = (double)ratio;
            if (!double.IsFinite(value))
            {
                throw new ArithmeticException("Drawdown ratio produced a non-finite value.");
            }
            series.Add(value);
        }
        return series.MoveToImmutable();
    }

    /// <summary>H[j]-E[j] for j = 0..m, in decimal (Rule DT-1: drawdown amount is a currency value).</summary>
    public static ImmutableArray<decimal> ComputeDrawdownAmountSeries(
        ImmutableArray<decimal> equity,
        CancellationToken cancellationToken = default)
    {
        var series = ImmutableArray.CreateBuilder<decimal>(equity.Length);
        decimal peak = equity[0];
        for (int j = 0; j < equity.Length; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            if (equity[j] > peak)
            {
                peak = equity[j];
            }
            series.Add(peak - equity[j]);
        }
        return series.MoveToImmutable();
    }

    public static MetricValue ComputeMaxDrawdown(
        ImmutableArray<double> ratioSeries,
        CancellationToken cancellationToken = default)
    {
        double max = 0d;
        for (int i = 0; i < ratioSeries.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            double d = ratioSeries[i];
            if (d > max) max = d;
        }
        return MetricCalculation.FromDouble(max, MetricUnit.DrawdownRatio);
    }

    public static MetricValue ComputeMaxDrawdownAmount(
        ImmutableArray<decimal> amountSeries,
        CancellationToken cancellationToken = default)
    {
        decimal max = 0m;
        for (int i = 0; i < amountSeries.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            decimal a = amountSeries[i];
            if (a > max) max = a;
        }
        return MetricValue.Valid(max, MetricUnit.Currency);
    }

    /// <summary>UlcerIndex = sqrt(sum((100*D[j])^2) / m), j = 1..m. m &gt;= 1 required (spec §5.4).</summary>
    public static MetricValue ComputeUlcerIndex(
        ImmutableArray<double> ratioSeries,
        int m,
        CancellationToken cancellationToken = default)
    {
        if (m < 1)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.PercentPoints, MetricReason.EmptyInput);
        }

        double sumOfSquares = 0d;
        for (int j = 1; j <= m; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            double percentPoints = 100d * ratioSeries[j];
            sumOfSquares += percentPoints * percentPoints;
        }
        double ulcer = Math.Sqrt(sumOfSquares / m);
        return MetricCalculation.FromDouble(ulcer, MetricUnit.PercentPoints);
    }
}
