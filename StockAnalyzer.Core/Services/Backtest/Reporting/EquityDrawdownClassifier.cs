using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Public, UI-facing view of the spec §5.4 drawdown definition: which equity points sit below the running high.
/// The high and the drawdown amount are NOT recomputed here; they are delegated to
/// <see cref="DrawdownSeriesCalculator.ComputeDrawdownAmountSeries"/> so there is a single definition
/// (H[j] = max(E[0..j]) with E[0] = initial capital, amount = H[j] - E[j]).
/// </summary>
public static class EquityDrawdownClassifier
{
    /// <summary>
    /// For each drawn equity point j (0..N-1) returns whether it is in drawdown, i.e. <c>H[j+1] - E[j+1] &gt; 0</c> on the full series
    /// E[0] = <paramref name="initialCapital"/>, E[j+1] = points[j].Equity. A point equal to the running high is not in drawdown.
    /// Empty input yields an empty array; a decimal overflow yields the default (unavailable) array so callers can degrade.
    /// </summary>
    public static ImmutableArray<bool> ComputeUnderwaterFlags(decimal initialCapital, ImmutableArray<EquityPoint> points)
    {
        if (points.IsDefaultOrEmpty)
        {
            return ImmutableArray<bool>.Empty;
        }

        try
        {
            ImmutableArray<decimal>.Builder equity = ImmutableArray.CreateBuilder<decimal>(points.Length + 1);
            equity.Add(initialCapital);
            foreach (EquityPoint point in points)
            {
                equity.Add(point.Equity);
            }

            ImmutableArray<decimal> amount = DrawdownSeriesCalculator.ComputeDrawdownAmountSeries(equity.MoveToImmutable());
            ImmutableArray<bool>.Builder flags = ImmutableArray.CreateBuilder<bool>(points.Length);
            for (int j = 0; j < points.Length; j++)
            {
                flags.Add(amount[j + 1] > 0m);
            }
            return flags.MoveToImmutable();
        }
        catch (OverflowException)
        {
            return default;
        }
    }
}
