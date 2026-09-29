using System;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// The E[0..m] equity sample and r[1..m] per-bar return sample shared by every P2 metric (spec §5.2).
/// <c>BacktestResult.EquityPoints</c> is not pre-filtered for warmup — it records one point per bar of
/// the whole run (verified against <c>BacktestEngine.Run</c>'s <c>for (int i = 0; i &lt; n; i++)</c> loop)
/// — so this builder applies the <see cref="BacktestReportOptions.HistoryStartIndex"/> cutoff itself.
/// </summary>
internal sealed class EquitySample
{
    /// <summary>E[0..m]: index 0 is InitialCapital, index 1..m are the post-warmup EquityPoint.Equity values.</summary>
    public required ImmutableArray<decimal> Equity { get; init; }

    /// <summary>
    /// r[1..m] stored 0-indexed (Returns[0] == r[1], ..., Returns[m-1] == r[m]), decimal-divided per spec
    /// §5.2 then narrowed to double for statistical consumption (Rule DT-2). Entries are meaningless
    /// (0d placeholder) whenever <see cref="HasNegativeEquityPeriod"/> is true — callers must check that
    /// flag first and resolve to <see cref="MetricStatus.Undefined"/>/<see cref="MetricReason.NegativeEquityInPeriod"/>
    /// instead of reading this array (spec §5.2: period return statistics must resolve to Undefined, never skipped).
    /// </summary>
    public required ImmutableArray<double> Returns { get; init; }

    /// <summary>True if any E[j-1] &lt;= 0 for j in 1..m.</summary>
    public required bool HasNegativeEquityPeriod { get; init; }

    /// <summary>Arithmetic failure encountered while constructing returns; null when return construction succeeded.</summary>
    public MetricReason? ReturnFailureReason { get; init; }

    /// <summary>m: the number of post-warmup trading-target bars.</summary>
    public int SampleCount => Equity.Length - 1;

    public static EquitySample Build(
        BacktestResult result,
        BacktestReportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (options is null) throw new ArgumentNullException(nameof(options));

        ImmutableArray<EquityPoint> points = result.EquityPoints;
        int start = options.HistoryStartIndex;
        int m = Math.Max(0, points.Length - start);

        var equity = ImmutableArray.CreateBuilder<decimal>(m + 1);
        equity.Add(result.Configuration.InitialCapital);
        for (int i = 0; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            equity.Add(points[start + i].Equity);
        }

        var returns = ImmutableArray.CreateBuilder<double>(m);
        bool hasNegativeEquityPeriod = false;
        for (int j = 1; j <= m; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            if (equity[j - 1] <= 0m)
            {
                hasNegativeEquityPeriod = true;
            }
        }

        MetricReason? returnFailureReason = null;
        for (int j = 1; j <= m; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            decimal prior = equity[j - 1];
            if (hasNegativeEquityPeriod || returnFailureReason.HasValue)
            {
                returns.Add(0d);
                continue;
            }

            try
            {
                decimal rDec = equity[j] / prior - 1m;
                double value = (double)rDec;
                if (!double.IsFinite(value))
                {
                    returnFailureReason = MetricReason.NonFiniteResult;
                    returns.Add(0d);
                }
                else
                {
                    returns.Add(value);
                }
            }
            catch (OverflowException)
            {
                returnFailureReason = MetricReason.ArithmeticOverflow;
                returns.Add(0d);
            }
            catch (DivideByZeroException)
            {
                returnFailureReason = MetricReason.UnexpectedZeroDivisor;
                returns.Add(0d);
            }
        }

        return new EquitySample
        {
            Equity = equity.MoveToImmutable(),
            Returns = returns.MoveToImmutable(),
            HasNegativeEquityPeriod = hasNegativeEquityPeriod,
            ReturnFailureReason = returnFailureReason,
        };
    }
}
