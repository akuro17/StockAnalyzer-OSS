using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// Higher High / Higher Low state engine (<c>02_Market_Structure\03_higher_high_higher_low.md</c>; plan ChartPatterns56 v2,
/// run V2-10, R-25 &amp; U2-11 A; ledger <c>docs/sa_pattern_literal_ledger_V2-10_CandlesPart3</c>).
/// <para>
/// Model P: ONE causal ZigZag pass (<see cref="CausalZigZag"/> with <c>FractalBars = 0</c>) selects the latest 4 alternating confirmed pivots
/// (<c>ConfirmedAtIndex &lt;= t</c>) containing the latest two highs <c>(H1, H2)</c> and latest two lows <c>(L1, L2)</c>.
/// Side-generic per plan R-25: <c>side = +1</c> detects Higher High / Higher Low (<c>H2 &gt; H1, L2 &gt; L1, H2 &gt; L2, C_t &gt; L2</c>),
/// while <c>side = -1</c> detects the exact Lower Low / Lower High mirror (<c>H1 &gt; H2, L1 &gt; L2, H2 &gt; L2, C_t &lt; H2</c>).
/// </para>
/// </summary>
public static class HigherHighHigherLowEngine
{
    /// <summary>Evaluates Higher High / Higher Low (<c>side = +1</c>) across <paramref name="ctx"/>.</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, CoreStructureTrendParameter p, CancellationToken cancellationToken = default)
        => Run(ctx, p, side: 1, cancellationToken);

    /// <summary>Evaluates either HH/HL (<paramref name="side"/> = +1) or its LL/LH mirror (<paramref name="side"/> = -1) across <paramref name="ctx"/> (plan R-25).</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, CoreStructureTrendParameter p, int side, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);
        if (side is not (1 or -1))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, "Structure trend side must be +1 (HH/HL) or -1 (LL/LH).");
        }

        var results = new PatternBarResult[ctx.Count];
        if (ctx.Count < PatternSpecConstants.StructureTrendMinBars)
        {
            return results;
        }

        IReadOnlyList<ConfirmedPivot> pivots = CausalZigZag.Extract(
            ctx,
            new CausalZigZagOptions(p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, PatternSpecConstants.StructureTrendFractalBars));

        decimal minStepFraction = p.MinStepUpPercent / 100m;
        int confirmedCount = 0;

        for (int t = 0; t < ctx.Count; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (confirmedCount < pivots.Count && pivots[confirmedCount].ConfirmedAtIndex <= t)
            {
                confirmedCount++;
            }

            if (t + 1 < PatternSpecConstants.StructureTrendMinBars || !ctx.IsValid(t) || confirmedCount < 4)
            {
                continue;
            }

            if (!IsStructureActive(ctx, t, pivots, confirmedCount, p, minStepFraction, side))
            {
                continue;
            }

            results[t] = new PatternBarResult(
                BullishFires: side > 0,
                BearishFires: side < 0,
                Direction: side,
                Score: null,
                ConfirmedAtIndex: t,
                RuleCode: 0);
        }

        return results;
    }

    private static bool IsStructureActive(
        PatternSeriesContext ctx,
        int t,
        IReadOnlyList<ConfirmedPivot> pivots,
        int confirmedCount,
        CoreStructureTrendParameter p,
        decimal minStepFraction,
        int side)
    {
        // CausalZigZag alternates strictly high/low, so the last 4 confirmed pivots always hold H1, H2, L1, L2 in chronological order (U2-11 A).
        ConfirmedPivot q0 = pivots[confirmedCount - 4];
        ConfirmedPivot q1 = pivots[confirmedCount - 3];
        ConfirmedPivot q2 = pivots[confirmedCount - 2];
        ConfirmedPivot q3 = pivots[confirmedCount - 1];

        ConfirmedPivot h1, h2, l1, l2;
        if (q0.IsHigh && !q1.IsHigh && q2.IsHigh && !q3.IsHigh)
        {
            h1 = q0;
            l1 = q1;
            h2 = q2;
            l2 = q3;
        }
        else if (!q0.IsHigh && q1.IsHigh && !q2.IsHigh && q3.IsHigh)
        {
            l1 = q0;
            h1 = q1;
            l2 = q2;
            h2 = q3;
        }
        else
        {
            return false;
        }

        if (h1.Price <= 0m || h2.Price <= 0m || l1.Price <= 0m || l2.Price <= 0m || h2.Price <= l2.Price)
        {
            return false;
        }

        int minIdx = q0.Index;
        int maxIdx = q3.Index;
        int span = maxIdx - minIdx;
        int maxSpan = PatternSpecConstants.StructureTrendSpanIntervalMultiplier * p.MaxPivotIntervalBars;
        int geomMaxSpan = Math.Max(0, maxSpan - 1);
        int minSpan = Math.Min(
            PatternSpecConstants.StructureTrendMinSpanBars,
            Math.Min((PatternSpecConstants.StructureTrendSpanIntervalMultiplier * p.MinPivotIntervalBars) + PatternSpecConstants.StructureTrendMinSpanOffsetBars, geomMaxSpan));
        if (span < minSpan || span > maxSpan)
        {
            return false;
        }

        int highInterval = h2.Index - h1.Index;
        int lowInterval = l2.Index - l1.Index;
        if (highInterval < p.MinPivotIntervalBars || highInterval > p.MaxPivotIntervalBars
            || lowInterval < p.MinPivotIntervalBars || lowInterval > p.MaxPivotIntervalBars)
        {
            return false;
        }

        int latestConfirmation = Math.Max(h2.ConfirmedAtIndex, l2.ConfirmedAtIndex);
        if (t - latestConfirmation > p.MaxLastPivotAgeBars)
        {
            return false;
        }

        if (p.RequireCloseAboveLastLow)
        {
            bool closeMaintained = side > 0 ? ctx[t].Close > l2.Price : ctx[t].Close < h2.Price;
            if (!closeMaintained)
            {
                return false;
            }
        }

        return StepThresholdMet(ctx, h1, h2, minStepFraction, side)
               && StepThresholdMet(ctx, l1, l2, minStepFraction, side);
    }

    private static bool StepThresholdMet(
        PatternSeriesContext ctx,
        ConfirmedPivot earlier,
        ConfirmedPivot later,
        decimal minStepFraction,
        int side)
    {
        if (earlier.Price <= 0m)
        {
            return false;
        }

        decimal diff = side > 0 ? later.Price - earlier.Price : earlier.Price - later.Price;
        if (diff <= 0m)
        {
            return false;
        }

        if (diff / earlier.Price < minStepFraction)
        {
            return false;
        }

        return PatternMath.TryAtrCtx(ctx, later.Index - 1, PatternSpecConstants.AtrPeriod, out decimal atr, out _)
               && diff >= PatternSpecConstants.StructureTrendStepAtrMultiplier * atr;
    }
}
