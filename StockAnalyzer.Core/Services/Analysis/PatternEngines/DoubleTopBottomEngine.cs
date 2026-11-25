using System;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// Double Top (bearish) / Double Bottom (bullish) of <c>01_Pattern_Recognition\02_double_top_bottom.md</c> (plan ChartPatterns56 v2, run V2-3; ledger <c>sa_pattern_literal_ledger_V2-3_HeadShouldersDouble.md</c>).
/// Same life cycle as <see cref="HeadAndShouldersEngine"/>: model P, every run of three consecutive causal ZigZag pivots (first peak, valley, second peak) is a candidate, the shape is checked once, and the break
/// (the first bar that is not before the confirmation of the second peak, within the wait limit, whose own close passes the valley by the margin, with the volume and total-length conditions) fires it once.
/// <para>
/// This engine is the direct cure of the two reported false positives: the peaks must be in the high zone of the last 60 bars (a peak in the middle of the range fails), at least 10 and at most 65 bars apart (80 bars fail),
/// the valley deep enough, the prior move present, and the pattern is only reported on the break of the valley by a close, not while it forms.
/// </para>
/// Two points the specification leaves open are single functions, easy to change after the owner's answer: <see cref="DepthReferencePrice"/> (the <c>P_ref</c> of the depth rule) and <see cref="TryPriorMoveWidth"/>
/// (the prior-trend width of the maximum retracement).
/// </summary>
public static class DoubleTopBottomEngine
{
    /// <summary>The pivots of a candidate: the identity that is consumed.</summary>
    private readonly record struct Candidate(int FirstPeak, int Valley, int SecondPeak);

    /// <summary>Runs the engine over the series of <paramref name="ctx"/>; one result per bar (the indicator makes the bars before the minimum history null).</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, CoreDoubleTopBottomParameter p, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        var timeline = new PatternEventTimeline<Candidate>(ctx.Count);
        var options = new CausalZigZagOptions(p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, p.FractalWindowBars);
        System.Collections.Generic.IReadOnlyList<ConfirmedPivot> pivots = CausalZigZag.Extract(ctx, options);

        for (int k = 2; k < pivots.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConfirmedPivot first = pivots[k - 2];
            ConfirmedPivot valley = pivots[k - 1];
            ConfirmedPivot second = pivots[k];

            bool top = first.IsHigh;
            if (top ? !PatternEngineSupport.AllowsBearish(p.DirectionMode) : !PatternEngineSupport.AllowsBullish(p.DirectionMode))
            {
                continue;
            }

            if (!ShapeHolds(ctx, p, top, first, valley, second))
            {
                continue;
            }

            FireOnBreak(ctx, p, timeline, top, first, valley, second);
        }

        PatternBarResult[] results = timeline.Resolve(PatternEngineSupport.AllowsBullish(p.DirectionMode), PatternEngineSupport.AllowsBearish(p.DirectionMode));
        PatternEngineSupport.ClearInconsistentBars(ctx, results);
        return results;
    }

    /// <summary>
    /// The price <c>P_ref</c> the depth <c>D_neck</c> is divided by in spec 02 section 4. The specification does not define it (open question Q-V2-2a); this is the price <c>D_neck</c> is measured from: the lower peak of
    /// a top, the higher bottom of a bottom (owner decision Q-V2-2a = A). A non-positive price gives a non-positive reference, which <see cref="PatternEngineSupport.NecklineDepthMet"/> treats as "not met" (no division).
    /// </summary>
    public static decimal DepthReferencePrice(bool top, decimal price1, decimal price2) => top ? Math.Min(price1, price2) : Math.Max(price1, price2);

    /// <summary>
    /// The width of the prior move the maximum retracement is measured against (spec 02 section 4: "the pull-back depth relative to the prior trend width"; the formula is not given, open question Q-V2-2b):
    /// the first peak minus the lowest low (a top), or the highest high minus the first bottom (a bottom), over the lookback that ends the bar before the first peak. False when the window cannot be read or has no width.
    /// </summary>
    public static bool TryPriorMoveWidth(PatternSeriesContext ctx, int firstPeakIndex, decimal firstPeakPrice, int lookbackBars, bool top, out decimal width)
    {
        width = 0m;
        int from = firstPeakIndex - lookbackBars;
        int last = firstPeakIndex - 1;
        if (lookbackBars <= 0 || from < 0 || !ctx.AllValid(from, last))
        {
            return false;
        }

        decimal extreme = top ? ctx[from].Low : ctx[from].High;
        for (int i = from + 1; i <= last; i++)
        {
            extreme = top ? Math.Min(extreme, ctx[i].Low) : Math.Max(extreme, ctx[i].High);
        }

        width = top ? firstPeakPrice - extreme : extreme - firstPeakPrice;
        return width > 0m;
    }

    /// <summary>
    /// Spec 02 section 4 (cleanliness): no close strictly between the two peaks is above the higher peak (top) or below the lower bottom (bottom). False when the window cannot be read (plan R-16). With the pivots of
    /// the causal ZigZag this holds for every candidate by construction (a bar beyond the peaks between them would be a pivot itself); the rule is kept as the specification states it and is tested on its own.
    /// </summary>
    public static bool HasCleanInterval(PatternSeriesContext ctx, int firstPeakIndex, int secondPeakIndex, bool top, decimal higherPeak, decimal lowerPeak)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (secondPeakIndex - firstPeakIndex < 2)
        {
            return true;
        }

        if (!ctx.AllValid(firstPeakIndex + 1, secondPeakIndex - 1))
        {
            return false;
        }

        for (int i = firstPeakIndex + 1; i < secondPeakIndex; i++)
        {
            if (top ? ctx[i].Close > higherPeak : ctx[i].Close < lowerPeak)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShapeHolds(PatternSeriesContext ctx, CoreDoubleTopBottomParameter p, bool top, ConfirmedPivot first, ConfirmedPivot valley, ConfirmedPivot second)
    {
        decimal price1 = first.Price;
        decimal price2 = second.Price;
        decimal priceValley = valley.Price;
        if (price1 <= 0m || price2 <= 0m || priceValley <= 0m)
        {
            return false;
        }

        // Spec 3: the interval of the peaks and the two legs.
        int interval = second.Index - first.Index;
        int leg1 = valley.Index - first.Index;
        int leg2 = second.Index - valley.Index;
        if (interval < p.MinPeakIntervalBars || interval > p.MaxPeakIntervalBars
            || leg1 < PatternSpecConstants.DoubleLegBarsMin || leg1 > PatternSpecConstants.DoubleLegBarsMax
            || leg2 < PatternSpecConstants.DoubleLegBarsMin || leg2 > PatternSpecConstants.DoubleLegBarsMax)
        {
            return false;
        }

        // Spec 4: the peaks are level (percent of the higher peak, or the ATR alternative).
        if (!PatternMath.TryAtrCtx(ctx, second.Index - 1, PatternSpecConstants.AtrPeriod, out decimal atrAtSecondPeak, out _))
        {
            return false;
        }

        decimal peakTolerance = Math.Max(p.PeakTolerancePercent / 100m * Math.Max(price1, price2), PatternSpecConstants.DoublePeakAtrMultiplier * atrAtSecondPeak);
        if (Math.Abs(price1 - price2) > peakTolerance)
        {
            return false;
        }

        // Spec 4: depth of the valley.
        decimal depth = top ? Math.Min(price1, price2) - priceValley : priceValley - Math.Max(price1, price2);
        if (!PatternEngineSupport.NecklineDepthMet(ctx, depth, DepthReferencePrice(top, price1, price2), p.MinNecklineDepthPercent, valley.Index - 1))
        {
            return false;
        }

        // Spec 4: the valley does not give back more than the allowed part of the prior move. Without the prior-trend requirement there is no prior move to measure against, so the rule is off with it.
        if (p.RequirePriorTrend && (!TryPriorMoveWidth(ctx, first.Index, price1, p.PriorTrendLookbackBars, top, out decimal priorWidth) || depth > p.MaxNecklineRetraceRatio * priorWidth))
        {
            return false;
        }

        // Spec 4: no close between the peaks beyond the peaks.
        if (!HasCleanInterval(ctx, first.Index, second.Index, top, Math.Max(price1, price2), Math.Min(price1, price2)))
        {
            return false;
        }

        // Spec 2: both peaks in the high (low) zone of the last N_zone bars, each at its own bar.
        decimal? position1 = PatternMath.Pos(ctx, price1, first.Index, p.PriceZoneLookbackBars);
        decimal? position2 = PatternMath.Pos(ctx, price2, second.Index, p.PriceZoneLookbackBars);
        if (position1 is null || position2 is null
            || (top
                ? position1.Value < p.Peak1ZoneThreshold || position2.Value < p.Peak2ZoneThreshold
                : position1.Value > 1m - p.Peak1ZoneThreshold || position2.Value > 1m - p.Peak2ZoneThreshold))
        {
            return false;
        }

        // Spec 2: the prior trend before the first peak (the family text: the fast SMA against itself only).
        if (p.RequirePriorTrend)
        {
            decimal minFraction = p.PriorTrendMinPercent / 100m;
            bool trend = top
                ? PatternMath.MidTermUptrend(ctx, first.Index, price1, p.PriorTrendLookbackBars, minFraction, PatternSpecConstants.MidTrendAtrMultiplier, PatternSpecConstants.AtrPeriod, MidTrendConfirmation.FastRising)
                : PatternMath.MidTermDowntrend(ctx, first.Index, price1, p.PriorTrendLookbackBars, minFraction, PatternSpecConstants.MidTrendAtrMultiplier, PatternSpecConstants.AtrPeriod, MidTrendConfirmation.FastRising);
            if (!trend)
            {
                return false;
            }
        }

        return true;
    }

    private static void FireOnBreak(PatternSeriesContext ctx, CoreDoubleTopBottomParameter p, PatternEventTimeline<Candidate> timeline, bool top, ConfirmedPivot first, ConfirmedPivot valley, ConfirmedPivot second)
    {
        decimal invalidationLevel = InvalidationLevel(top, first.Price, second.Price);
        int confirmedAt = second.ConfirmedAtIndex;
        int lastBar = Math.Min(ctx.Count - 1, second.Index + p.MaxBreakoutWaitBars);

        for (int i = second.Index + PatternSpecConstants.FormationBreakoutMinBarsAfterLastPivot; i <= lastBar; i++)
        {
            // An inconsistent bar between the second peak and the break is read by this candidate: it cannot fire (plan R-16, interpretation I-5).
            if (!ctx.IsValid(i))
            {
                return;
            }

            decimal close = ctx[i].Close;

            // Spec 5: a close beyond the peaks by one percent ends the candidate, also before the second peak is confirmed (decision U2-07 A).
            if (top ? close > invalidationLevel : close < invalidationLevel)
            {
                return;
            }

            if (i < confirmedAt)
            {
                continue;
            }

            int totalBars = i - first.Index;
            if (totalBars > PatternSpecConstants.DoubleTotalBarsMax)
            {
                return;
            }

            if (totalBars < PatternSpecConstants.DoubleTotalBarsMin)
            {
                continue;
            }

            decimal? margin = PatternMath.BreakMargin(ctx, p.BreakoutMarginPercent, PatternSpecConstants.DoubleBreakoutAtrMultiplier, valley.Price, i);
            if (margin is null || !(top ? close < valley.Price - margin.Value : close > valley.Price + margin.Value))
            {
                continue;
            }

            if (!PatternMath.VolumeConfirmed(ctx, ctx[i].Volume, i - 1, p.RequireVolumeConfirmation, p.BreakoutVolumeMultiplier))
            {
                continue;
            }

            int direction = top ? -1 : 1;
            var key = new Candidate(first.Index, valley.Index, second.Index);
            if (timeline.TryFire(key, direction, i, p.SignalPersistenceBars, null, confirmedAt, out int eventId))
            {
                CutByInvalidation(ctx, timeline, eventId, i, p.SignalPersistenceBars, invalidationLevel, top);
            }

            return;
        }
    }

    /// <summary>Spec 5: a top is invalid above the higher peak by 1 percent, a bottom below the lower bottom by 1 percent.</summary>
    private static decimal InvalidationLevel(bool top, decimal price1, decimal price2)
        => top
            ? Math.Max(price1, price2) * (1m + PatternSpecConstants.DoubleInvalidationFraction)
            : Math.Min(price1, price2) * (1m - PatternSpecConstants.DoubleInvalidationFraction);

    /// <summary>Spec 5 / 7.2: during the hold period a close beyond the invalidation level ends the signal at that bar (the earlier bars keep their value).</summary>
    private static void CutByInvalidation(PatternSeriesContext ctx, PatternEventTimeline<Candidate> timeline, int eventId, int breakBar, int persistenceBars, decimal invalidationLevel, bool top)
    {
        int last = (int)Math.Min((long)ctx.Count - 1, (long)breakBar + persistenceBars);
        for (int b = breakBar + 1; b <= last; b++)
        {
            if (ctx.IsValid(b) && (top ? ctx[b].Close > invalidationLevel : ctx[b].Close < invalidationLevel))
            {
                timeline.InvalidateFrom(eventId, b);
                return;
            }
        }
    }
}
