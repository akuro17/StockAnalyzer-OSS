using System;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// Head and Shoulders (top = bearish, inverse = bullish) of <c>01_Pattern_Recognition\01_head_and_shoulders.md</c> (plan ChartPatterns56 v2, run V2-3; ledger <c>sa_pattern_literal_ledger_V2-3_HeadShouldersDouble.md</c>).
/// <para>
/// Model P: one pass over the whole series. The pivots are the causal ZigZag pivots (<see cref="CausalZigZag"/>); every run of five consecutive pivots (left shoulder, valley, head, valley, right shoulder) is one candidate.
/// A candidate whose shape holds (prior trend, price zone, durations, neckline depth and slope, head prominence, shoulder tolerance) waits for its break: the first bar that is not before the confirmation of the right shoulder,
/// within the wait limit, whose own close passes the neckline by the margin, with the volume and total-length conditions (plan R-19, no look-back before the confirmation). A close beyond the head before the break
/// (including the bars between the right shoulder and its confirmation) ends the candidate. The candidate is consumed (fires once) and lights the break bar and <c>SignalPersistenceBars</c> bars after it unless a close
/// beyond the head cuts it (spec 7).
/// </para>
/// Every decision at bar <c>t</c> reads bars up to <c>t</c> only, and a pivot only after its confirmation: appending bars never changes an earlier result (tested). An inconsistent bar is "not met" for every candidate
/// that reads it (plan R-16).
/// </summary>
public static class HeadAndShouldersEngine
{
    /// <summary>The pivots of a candidate: the identity that is consumed.</summary>
    private readonly record struct Candidate(int LeftShoulder, int Valley1, int Head, int Valley2, int RightShoulder);

    /// <summary>Runs the engine over the series of <paramref name="ctx"/>; one result per bar (the indicator makes the bars before the minimum history null).</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, CoreHeadAndShouldersParameter p, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        var timeline = new PatternEventTimeline<Candidate>(ctx.Count);
        var options = new CausalZigZagOptions(p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, p.FractalWindowBars);
        System.Collections.Generic.IReadOnlyList<ConfirmedPivot> pivots = CausalZigZag.Extract(ctx, options);

        for (int k = 4; k < pivots.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConfirmedPivot ls = pivots[k - 4];
            ConfirmedPivot v1 = pivots[k - 3];
            ConfirmedPivot head = pivots[k - 2];
            ConfirmedPivot v2 = pivots[k - 1];
            ConfirmedPivot rs = pivots[k];

            bool top = ls.IsHigh;
            if (top ? !PatternEngineSupport.AllowsBearish(p.DirectionMode) : !PatternEngineSupport.AllowsBullish(p.DirectionMode))
            {
                continue;
            }

            if (!ShapeHolds(ctx, p, top, ls, v1, head, v2, rs))
            {
                continue;
            }

            FireOnBreak(ctx, p, timeline, top, ls, v1, head, v2, rs);
        }

        PatternBarResult[] results = timeline.Resolve(PatternEngineSupport.AllowsBullish(p.DirectionMode), PatternEngineSupport.AllowsBearish(p.DirectionMode));
        PatternEngineSupport.ClearInconsistentBars(ctx, results);
        return results;
    }

    /// <summary>The neckline through the two valleys at bar <paramref name="index"/> (spec 4; extrapolated beyond the second valley).</summary>
    private static decimal Neckline(ConfirmedPivot v1, ConfirmedPivot v2, int index)
        => v1.Price + (v2.Price - v1.Price) / (v2.Index - v1.Index) * (index - v1.Index);

    private static bool ShapeHolds(PatternSeriesContext ctx, CoreHeadAndShouldersParameter p, bool top, ConfirmedPivot ls, ConfirmedPivot v1, ConfirmedPivot head, ConfirmedPivot v2, ConfirmedPivot rs)
    {
        int side = top ? 1 : -1;
        decimal pLs = ls.Price;
        decimal pRs = rs.Price;
        decimal pHead = head.Price;
        if (pLs <= 0m || pRs <= 0m || pHead <= 0m || v1.Price <= 0m || v2.Price <= 0m)
        {
            return false;
        }

        // Spec 3: durations of the two halves and their symmetry.
        int leftBars = head.Index - ls.Index;
        int rightBars = rs.Index - head.Index;
        if (leftBars < p.MinPeakIntervalBars || leftBars > p.MaxPeakIntervalBars || rightBars < p.MinPeakIntervalBars || rightBars > p.MaxPeakIntervalBars)
        {
            return false;
        }

        decimal symmetry = (decimal)rightBars / leftBars;
        if (symmetry < PatternSpecConstants.HeadAndShouldersTimeSymmetryMin || symmetry > PatternSpecConstants.HeadAndShouldersTimeSymmetryMax)
        {
            return false;
        }

        // Spec 4: the head is the extreme, beyond the shoulders and prominent against the pattern height.
        decimal shoulderExtreme = top ? Math.Max(pLs, pRs) : Math.Min(pLs, pRs);
        decimal headBeyondShoulders = side * (pHead - shoulderExtreme);
        decimal patternHeight = side * (pHead - Neckline(v1, v2, head.Index));
        if (headBeyondShoulders <= 0m || patternHeight <= 0m || headBeyondShoulders / patternHeight < p.MinHeadProminenceRatio)
        {
            return false;
        }

        // Spec 4: both shoulders beyond the neckline at their own bar.
        if (side * (pLs - Neckline(v1, v2, ls.Index)) <= 0m || side * (pRs - Neckline(v1, v2, rs.Index)) <= 0m)
        {
            return false;
        }

        // Spec 4: the shoulders are level (percent of the higher shoulder, or the ATR alternative).
        if (!PatternMath.TryAtrCtx(ctx, rs.Index - 1, PatternSpecConstants.AtrPeriod, out decimal atrAtRightShoulder, out _))
        {
            return false;
        }

        decimal shoulderTolerance = Math.Max(p.ShoulderTolerancePercent / 100m * Math.Max(pLs, pRs), PatternSpecConstants.HeadAndShouldersShoulderAtrMultiplier * atrAtRightShoulder);
        if (Math.Abs(pLs - pRs) > shoulderTolerance)
        {
            return false;
        }

        // Spec 4: neckline depth and slope.
        if (!PatternEngineSupport.NecklineDepthMet(ctx, patternHeight, pHead, p.MinNecklineDepthPercent, head.Index - 1)
            || Math.Abs(v2.Price - v1.Price) / patternHeight > p.MaxNecklineSlopeRatio)
        {
            return false;
        }

        // Spec 2: the head is in the high (low) zone of the last N_zone bars.
        decimal? position = PatternMath.Pos(ctx, pHead, head.Index, p.PriceZoneLookbackBars);
        if (position is null || (top ? position.Value < p.PriceZoneThreshold : position.Value > 1m - p.PriceZoneThreshold))
        {
            return false;
        }

        // Spec 2: the prior trend before the left shoulder (the family text: the SMA(20) against the SMA(50) only).
        if (p.RequirePriorTrend)
        {
            decimal minFraction = p.PriorTrendMinPercent / 100m;
            bool trend = top
                ? PatternMath.MidTermUptrend(ctx, ls.Index, pHead, p.PriorTrendLookbackBars, minFraction, PatternSpecConstants.HeadAndShouldersPriorTrendAtrMultiplier, PatternSpecConstants.AtrPeriod, MidTrendConfirmation.FastAboveSlow)
                : PatternMath.MidTermDowntrend(ctx, ls.Index, pHead, p.PriorTrendLookbackBars, minFraction, PatternSpecConstants.HeadAndShouldersPriorTrendAtrMultiplier, PatternSpecConstants.AtrPeriod, MidTrendConfirmation.FastAboveSlow);
            if (!trend)
            {
                return false;
            }
        }

        return true;
    }

    private static void FireOnBreak(PatternSeriesContext ctx, CoreHeadAndShouldersParameter p, PatternEventTimeline<Candidate> timeline, bool top,
        ConfirmedPivot ls, ConfirmedPivot v1, ConfirmedPivot head, ConfirmedPivot v2, ConfirmedPivot rs)
    {
        decimal pHead = head.Price;
        int confirmedAt = rs.ConfirmedAtIndex;
        int lastBar = Math.Min(ctx.Count - 1, rs.Index + p.MaxBreakoutWaitBars);

        for (int i = rs.Index + PatternSpecConstants.FormationBreakoutMinBarsAfterLastPivot; i <= lastBar; i++)
        {
            // An inconsistent bar between the right shoulder and the break is read by this candidate: it cannot fire (plan R-16, interpretation I-5).
            if (!ctx.IsValid(i))
            {
                return;
            }

            decimal close = ctx[i].Close;

            // Spec 5: a close beyond the head ends the candidate, also on the bars before the right shoulder is confirmed (decision U2-07 A).
            if (top ? close > pHead : close < pHead)
            {
                return;
            }

            if (i < confirmedAt)
            {
                continue;
            }

            int totalBars = i - ls.Index;
            if (totalBars > p.MaxTotalBars)
            {
                return;
            }

            if (totalBars < p.MinTotalBars)
            {
                continue;
            }

            decimal neckline = Neckline(v1, v2, i);
            if (neckline <= 0m)
            {
                continue;
            }

            decimal? margin = PatternMath.BreakMargin(ctx, p.BreakoutMarginPercent, PatternSpecConstants.HeadAndShouldersBreakoutAtrMultiplier, neckline, i);
            if (margin is null || !(top ? close < neckline - margin.Value : close > neckline + margin.Value))
            {
                continue;
            }

            if (!PatternMath.VolumeConfirmed(ctx, ctx[i].Volume, i - 1, p.RequireVolumeConfirmation, p.BreakoutVolumeMultiplier))
            {
                continue;
            }

            int direction = top ? -1 : 1;
            var key = new Candidate(ls.Index, v1.Index, head.Index, v2.Index, rs.Index);
            if (timeline.TryFire(key, direction, i, p.SignalPersistenceBars, null, confirmedAt, out int eventId))
            {
                CutByInvalidation(ctx, timeline, eventId, i, p.SignalPersistenceBars, pHead, top);
            }

            return;
        }
    }

    /// <summary>Spec 5 / 7.2: during the hold period a close beyond the head ends the signal at that bar (the earlier bars keep their value).</summary>
    private static void CutByInvalidation(PatternSeriesContext ctx, PatternEventTimeline<Candidate> timeline, int eventId, int breakBar, int persistenceBars, decimal pHead, bool top)
    {
        int last = (int)Math.Min((long)ctx.Count - 1, (long)breakBar + persistenceBars);
        for (int b = breakBar + 1; b <= last; b++)
        {
            if (ctx.IsValid(b) && (top ? ctx[b].Close > pHead : ctx[b].Close < pHead))
            {
                timeline.InvalidateFrom(eventId, b);
                return;
            }
        }
    }
}
