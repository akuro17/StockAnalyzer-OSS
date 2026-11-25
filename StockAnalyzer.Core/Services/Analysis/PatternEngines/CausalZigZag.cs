using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>A confirmed swing point: bar <see cref="Index"/> holds the extreme <see cref="Price"/>; it may be used by a bar only when <see cref="ConfirmedAtIndex"/> is not later than that bar.</summary>
public readonly record struct ConfirmedPivot(int Index, decimal Price, bool IsHigh, int ConfirmedAtIndex);

/// <summary>
/// Options of <see cref="CausalZigZag"/> (spec 5.1, 5.2).
/// </summary>
/// <param name="ThresholdPercent">Reversal size in percent of the last extreme (delta); above zero.</param>
/// <param name="MinAtrMultiplier">The swing must also span at least this many context ATRs measured at the bar before the reversal (theta_atr_piv); 0 switches the ATR test off.</param>
/// <param name="FractalBars">Bars on each side of the fractal combination (k_L = k_R): the pivot is confirmed no earlier than <c>pivot index + FractalBars</c>; 0 = no fractal delay.</param>
/// <param name="AtrPeriod">Period of the context ATR.</param>
public readonly record struct CausalZigZagOptions(
    decimal ThresholdPercent,
    decimal MinAtrMultiplier = PatternSpecConstants.ZigZagMinAtrMultiplierDefault,
    int FractalBars = PatternSpecConstants.FractalBarsDefault,
    int AtrPeriod = PatternSpecConstants.AtrPeriod);

/// <summary>
/// The deterministic, causal ZigZag of the chart-pattern specification (<c>00_COMMON_MATHEMATICAL_DEFINITIONS.md</c> 5.2). A pivot is confirmed on the first strictly later bar whose
/// reversal from the last extreme is at least the percent threshold and (when switched on) at least the ATR multiple; a bar that sets a new extreme never confirms a reversal itself
/// (the order of high and low inside a bar is unknown). The result depends on bars <c>0..t</c> only: the pivots whose <see cref="ConfirmedPivot.ConfirmedAtIndex"/> is at most <c>t</c> are the
/// same whether the series ends at <c>t</c> or later (prefix invariance, tested).
/// <para>
/// This is a new function, not <c>MarketStructureDetector.ExtractConfirmedPivots</c>, which has the same name in the specification text but no ATR amplitude and no fractal delay (that function is
/// unchanged). With <c>MinAtrMultiplier = 0</c> and <c>FractalBars = 0</c> the result equals the legacy one (tested), so the two differ only by the rules the specification adds.
/// </para>
/// Interpretation (plan ledger Common): the fractal combination only delays the confirmation (<c>max(reversal bar, pivot + FractalBars)</c>); a pivot is never dropped for failing a strict fractal test, which
/// would break the alternation and the immutability of confirmed pivots. An inconsistent bar is ignored (it neither moves an extreme nor confirms a reversal, and cannot be a pivot; plan R-18).
/// </summary>
public static class CausalZigZag
{
    /// <summary>Extracts the confirmed pivots of the series held by <paramref name="ctx"/>, in chronological order, alternating high and low.</summary>
    public static IReadOnlyList<ConfirmedPivot> Extract(PatternSeriesContext ctx, CausalZigZagOptions options)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (options.ThresholdPercent <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The ZigZag threshold must be greater than zero.");
        }

        if (options.MinAtrMultiplier < 0m || options.FractalBars < 0 || options.AtrPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Negative ATR multiple or fractal bars, or a non-positive ATR period.");
        }

        var pivots = new List<ConfirmedPivot>();
        decimal threshold = options.ThresholdPercent / 100m;

        bool started = false;
        decimal lastHigh = 0m;
        decimal lastLow = 0m;
        int lastHighIdx = 0;
        int lastLowIdx = 0;
        int direction = 0;

        for (int i = 0; i < ctx.Count; i++)
        {
            if (!ctx.IsValid(i))
            {
                continue;
            }

            decimal high = ctx[i].High;
            decimal low = ctx[i].Low;

            if (!started)
            {
                lastHigh = high;
                lastLow = low;
                lastHighIdx = i;
                lastLowIdx = i;
                started = true;
                continue;
            }

            if (direction >= 0)
            {
                if (high > lastHigh)
                {
                    lastHigh = high;
                    lastHighIdx = i;
                }

                if (lastHighIdx < i && lastHigh > 0m && (lastHigh - low) / lastHigh >= threshold && AmplitudeAllowed(ctx, options, i, lastHigh - low))
                {
                    if (direction != 0 || pivots.Count == 0)
                    {
                        pivots.Add(new ConfirmedPivot(lastHighIdx, lastHigh, true, ConfirmedAt(options, lastHighIdx, i)));
                    }

                    direction = -1;
                    lastLow = low;
                    lastLowIdx = i;
                    continue;
                }
            }

            if (direction <= 0)
            {
                if (low < lastLow)
                {
                    lastLow = low;
                    lastLowIdx = i;
                }

                if (lastLowIdx < i && lastLow > 0m && (high - lastLow) / lastLow >= threshold && AmplitudeAllowed(ctx, options, i, high - lastLow))
                {
                    if (direction != 0 || pivots.Count == 0)
                    {
                        pivots.Add(new ConfirmedPivot(lastLowIdx, lastLow, false, ConfirmedAt(options, lastLowIdx, i)));
                    }

                    direction = 1;
                    lastHigh = high;
                    lastHighIdx = i;
                }
            }
        }

        return pivots;
    }

    private static int ConfirmedAt(CausalZigZagOptions options, int pivotIndex, int reversalBar)
        => Math.Max(reversalBar, pivotIndex + options.FractalBars);

    /// <summary>Spec 5.2 step 2: the amplitude must reach the ATR multiple of the bar before the reversal; with no multiple the test is off, with no computable ATR the reversal is not confirmed.</summary>
    private static bool AmplitudeAllowed(PatternSeriesContext ctx, CausalZigZagOptions options, int bar, decimal amplitude)
    {
        if (options.MinAtrMultiplier == 0m)
        {
            return true;
        }

        return PatternMath.TryAtrCtx(ctx, bar - 1, options.AtrPeriod, out decimal atr, out _) && amplitude >= options.MinAtrMultiplier * atr;
    }
}
