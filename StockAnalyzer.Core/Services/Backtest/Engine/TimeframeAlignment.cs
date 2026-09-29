using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Causal forward-fill alignment of a higher-timeframe indicator series onto the run's own bar
/// indices (plan section 3.2.3 of
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md). On-disk Weekly/Monthly Parquet data
/// is period-START stamped (empirically confirmed against real data), so naively taking "the latest
/// higher-timeframe row whose Timestamp &lt;= T" would leak a still-forming period's own value
/// backward in time. The safe rule used here needs no calendar arithmetic for either Weekly or
/// Monthly: find the period CONTAINING T, then step back ONE index to its immediate predecessor — the
/// most recently CLOSED period — and use that value instead.
/// </summary>
public static class TimeframeAlignment
{
    /// <summary>
    /// For each bar in <paramref name="baseBars"/>, returns the value of the most recently CLOSED
    /// <paramref name="higherTimeframeBars"/> period as of that base bar's own timestamp, or null if
    /// no higher-timeframe period has closed yet. <paramref name="higherTimeframeBars"/> and
    /// <paramref name="higherTimeframeValues"/> must be the same length and index-aligned (value[i]
    /// belongs to bar[i]); both must already be in chronological order, as every Backtest bar array is.
    /// </summary>
    public static ImmutableArray<decimal?> Align(
        ImmutableArray<CandleData> baseBars,
        ImmutableArray<CandleData> higherTimeframeBars,
        ImmutableArray<decimal?> higherTimeframeValues)
    {
        if (higherTimeframeBars.Length != higherTimeframeValues.Length)
        {
            throw new ArgumentException(
                "higherTimeframeBars and higherTimeframeValues must have the same length (index-aligned).",
                nameof(higherTimeframeValues));
        }

        var aligned = new decimal?[baseBars.Length];
        for (int i = 0; i < baseBars.Length; i++)
        {
            int currentPeriodIndex = FindLastIndexAtOrBefore(higherTimeframeBars, baseBars[i].Timestamp);
            int lastClosedPeriodIndex = currentPeriodIndex - 1;
            aligned[i] = lastClosedPeriodIndex >= 0 ? higherTimeframeValues[lastClosedPeriodIndex] : null;
        }
        return aligned.ToImmutableArray();
    }

    /// <summary>Binary search for the last index whose Timestamp is &lt;= <paramref name="timestamp"/>.
    /// Returns -1 if every row's Timestamp is after it (no containing period found yet).</summary>
    private static int FindLastIndexAtOrBefore(ImmutableArray<CandleData> bars, DateTime timestamp)
    {
        int lo = 0, hi = bars.Length - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (bars[mid].Timestamp <= timestamp)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }
}
