using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>The three Harmonic pattern kinds evaluated in independent per-type pools (spec 03/01..03).</summary>
public enum HarmonicPatternKind
{
    Gartley,
    Butterfly,
    Bat,
}

/// <summary>
/// Gartley, Butterfly, and Bat Harmonic engines (<c>03_Harmonic\01_gartley_pattern.md</c>, <c>02_butterfly_pattern.md</c>, <c>03_bat_pattern.md</c>; plan ChartPatterns56 v2, run V2-6;
/// ledger <c>docs/sa_pattern_literal_ledger_V2-6_Harmonic</c>).
/// <para>
/// Model P: each pattern type runs independently over multi-scale causal local extrema (<see cref="HarmonicExtrema"/>, <c>w in {2,3,5,8}</c>) with causal alternation (R-26) and <c>w_min</c> deduplication
/// (the smallest window confirming a 5-tuple <c>(X,A,B,C,D)</c> claims its origin <c>t_conf = i_D + w_min</c>). Every candidate checks price zone, initial leg move, span/leg/symmetry durations,
/// minimum leg swings, strict structure constraints, intermediate bar cleanliness, trapezoidal Fibonacci scoring (0 in any ratio rejects; total &gt;= <c>MinConfidenceScore</c>), optional reversal candle within
/// <c>MaxBarsSinceConfirmation</c>, and close invalidation during pre-trigger and hold.
/// </para>
/// </summary>
public static class HarmonicPatternEngine
{
    private readonly record struct HarmonicTupleKey(int X, int A, int B, int C, int D);

    private readonly record struct RatioSpec(decimal IdealMin, decimal IdealMax, decimal AllowedMin, decimal AllowedMax, decimal Weight);

    /// <summary>Evaluates <paramref name="kind"/> across the entire series held by <paramref name="ctx"/> (indicator sets bars before <c>MinBars</c> to null).</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, CoreHarmonicPatternParameter p, HarmonicPatternKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        var timeline = new PatternEventTimeline<HarmonicTupleKey>(ctx.Count);
        var seenTuples = new HashSet<HarmonicTupleKey>();

        foreach (int window in PatternSpecConstants.HarmonicWindows)
        {
            IReadOnlyList<HarmonicCandidatePivots> candidates = HarmonicExtrema.ExtractCausalCandidates(ctx, window);
            for (int c = 0; c < candidates.Count; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                HarmonicCandidatePivots cand = candidates[c];
                var key = new HarmonicTupleKey(cand.X.Index, cand.A.Index, cand.B.Index, cand.C.Index, cand.D.Index);

                // Spec 5: multi-scale w_min deduplication — only the smallest confirming window w_min claims this 5-tuple.
                if (!seenTuples.Add(key))
                {
                    continue;
                }

                // Bullish starts with a Low at X; Bearish starts with a High at X.
                bool bullish = !cand.X.IsHigh;
                if (bullish ? !PatternEngineSupport.AllowsBullish(p.DirectionMode) : !PatternEngineSupport.AllowsBearish(p.DirectionMode))
                {
                    continue;
                }

                if (!TryEvaluateShape(ctx, p, kind, bullish, cand, out decimal score))
                {
                    continue;
                }

                TriggerAndHold(ctx, p, kind, timeline, key, bullish, cand, score);
            }
        }

        PatternBarResult[] results = timeline.Resolve(PatternEngineSupport.AllowsBullish(p.DirectionMode), PatternEngineSupport.AllowsBearish(p.DirectionMode));
        PatternEngineSupport.ClearInconsistentBars(ctx, results);
        return results;
    }

    private static bool TryEvaluateShape(
        PatternSeriesContext ctx,
        CoreHarmonicPatternParameter p,
        HarmonicPatternKind kind,
        bool bullish,
        in HarmonicCandidatePivots cand,
        out decimal score)
    {
        score = 0m;

        decimal px = cand.X.Price;
        decimal pa = cand.A.Price;
        decimal pb = cand.B.Price;
        decimal pc = cand.C.Price;
        decimal pd = cand.D.Price;
        if (px <= 0m || pa <= 0m || pb <= 0m || pc <= 0m || pd <= 0m)
        {
            return false;
        }

        // Spec 3: total span, leg durations, and time symmetry.
        int span = cand.D.Index - cand.X.Index;
        int d1 = cand.A.Index - cand.X.Index;
        int d2 = cand.B.Index - cand.A.Index;
        int d3 = cand.C.Index - cand.B.Index;
        int d4 = cand.D.Index - cand.C.Index;
        if (span < p.MinSpanBars || span > p.MaxSpanBars
            || d1 < p.MinLegBars || d1 > p.MaxLegBars
            || d2 < p.MinLegBars || d2 > p.MaxLegBars
            || d3 < p.MinLegBars || d3 > p.MaxLegBars
            || d4 < p.MinLegBars || d4 > p.MaxLegBars)
        {
            return false;
        }

        int minLeg = Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
        int maxLeg = Math.Max(Math.Max(d1, d2), Math.Max(d3, d4));
        decimal denom = Math.Max(PatternSpecConstants.HarmonicTimeSymmetryFloorBars, minLeg);
        if ((decimal)maxLeg / denom > p.MaxTimeSymmetryRatio)
        {
            return false;
        }

        // Decision U2-13 A: AB/CD time symmetry applies to Gartley only.
        if (kind == HarmonicPatternKind.Gartley)
        {
            decimal abCd = (decimal)d4 / d2;
            if (abCd < PatternSpecConstants.HarmonicGartleyAbCdSymmetryMin || abCd > PatternSpecConstants.HarmonicGartleyAbCdSymmetryMax)
            {
                return false;
            }
        }

        // Spec 4: strict structure ordering.
        if (!StructureHolds(kind, bullish, px, pa, pb, pc, pd))
        {
            return false;
        }

        // Plan R-16: all bars spanned by the pattern [i_X, i_D] must be OHLC-consistent.
        if (!ctx.AllValid(cand.X.Index, cand.D.Index))
        {
            return false;
        }

        // Spec 2: X->A initial impulse leg (>= 3.0% AND >= 2.5 ATR_ctx(i_X - 1, 14)).
        decimal xa = Math.Abs(pa - px);
        if (xa / px < PatternSpecConstants.HarmonicInitialLegMinFraction
            || !PatternMath.TryAtrCtx(ctx, cand.X.Index - 1, PatternSpecConstants.AtrPeriod, out decimal atrAtX, out _)
            || xa < PatternSpecConstants.HarmonicInitialLegAtrMultiplier * atrAtX)
        {
            return false;
        }

        // Spec 2: price zone (Point X for Gartley/Bat, Point D for Butterfly).
        if (!ZoneHolds(ctx, kind, bullish, cand.X.Index, px, cand.D.Index, pd))
        {
            return false;
        }

        // Spec 4: minimum swing across all 4 legs.
        if (!LegMinimumMet(ctx, p, cand.X.Index, px, pa)
            || !LegMinimumMet(ctx, p, cand.A.Index, pa, pb)
            || !LegMinimumMet(ctx, p, cand.B.Index, pb, pc)
            || !LegMinimumMet(ctx, p, cand.C.Index, pc, pd))
        {
            return false;
        }

        // Spec 4: intermediate bar cleanliness.
        if (p.RequireIntermediateCleanliness && !CleanlinessHolds(ctx, kind, bullish, cand, px, pa))
        {
            return false;
        }

        // Spec 4 & Common 6: Fibonacci ratios + trapezoidal score (any 0 ratio rejects).
        decimal ab = Math.Abs(pb - pa);
        decimal bc = Math.Abs(pc - pb);
        decimal cd = Math.Abs(pd - pc);
        decimal ad = Math.Abs(pd - pa);
        if (xa <= 0m || ab <= 0m || bc <= 0m)
        {
            return false;
        }

        return TryComputeScore(p, kind, ab / xa, bc / ab, cd / bc, ad / xa, out score);
    }

    private static bool StructureHolds(HarmonicPatternKind kind, bool bullish, decimal px, decimal pa, decimal pb, decimal pc, decimal pd)
    {
        if (kind == HarmonicPatternKind.Butterfly)
        {
            return bullish
                ? pd < px && px < pb && pb < pc && pc < pa
                : pd > px && px > pb && pb > pc && pc > pa;
        }

        // Gartley & Bat: D must NOT break X.
        return bullish
            ? px < pd && pd < pb && pb < pc && pc < pa
            : px > pd && pd > pb && pb > pc && pc > pa;
    }

    private static bool ZoneHolds(PatternSeriesContext ctx, HarmonicPatternKind kind, bool bullish, int xIdx, decimal px, int dIdx, decimal pd)
    {
        int bar = kind == HarmonicPatternKind.Butterfly ? dIdx : xIdx;
        decimal price = kind == HarmonicPatternKind.Butterfly ? pd : px;
        decimal threshold = kind == HarmonicPatternKind.Butterfly
            ? PatternSpecConstants.HarmonicButterflyPointDZoneThreshold
            : PatternSpecConstants.HarmonicPointXZoneThreshold;

        decimal? pos = PatternMath.Pos(ctx, price, bar, PatternSpecConstants.HarmonicZoneBars);
        if (pos is null)
        {
            return false;
        }

        return bullish ? pos.Value <= threshold : pos.Value >= 1m - threshold;
    }

    private static bool LegMinimumMet(PatternSeriesContext ctx, CoreHarmonicPatternParameter p, int startIdx, decimal startPrice, decimal endPrice)
    {
        decimal move = Math.Abs(endPrice - startPrice);
        if (move / startPrice < p.MinLegSwingPercent / 100m)
        {
            return false;
        }

        if (p.MinLegAtrMultiplier == 0m)
        {
            return true;
        }

        return PatternMath.TryAtrCtx(ctx, startIdx - 1, PatternSpecConstants.AtrPeriod, out decimal atr, out _)
               && move >= p.MinLegAtrMultiplier * atr;
    }

    private static bool CleanlinessHolds(PatternSeriesContext ctx, HarmonicPatternKind kind, bool bullish, in HarmonicCandidatePivots cand, decimal px, decimal pa)
    {
        // Butterfly checks (i_A, i_C) for A and (i_X, i_B) for X; Gartley/Bat check (i_A, i_D) for A and (i_X, i_D) for X.
        int aCleanEnd = kind == HarmonicPatternKind.Butterfly ? cand.C.Index : cand.D.Index;
        int xCleanEnd = kind == HarmonicPatternKind.Butterfly ? cand.B.Index : cand.D.Index;

        for (int i = cand.A.Index + 1; i < aCleanEnd; i++)
        {
            if (bullish ? ctx[i].High > pa : ctx[i].Low < pa)
            {
                return false;
            }
        }

        for (int i = cand.X.Index + 1; i < xCleanEnd; i++)
        {
            if (bullish ? ctx[i].Low < px : ctx[i].High > px)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryComputeScore(
        CoreHarmonicPatternParameter p,
        HarmonicPatternKind kind,
        decimal rAbXa,
        decimal rBcAb,
        decimal rCdBc,
        decimal rAdXa,
        out decimal score)
    {
        score = 0m;
        RatioSpec abSpec;
        RatioSpec bcSpec;
        RatioSpec cdSpec;
        RatioSpec adSpec;

        switch (kind)
        {
            case HarmonicPatternKind.Gartley:
                abSpec = new(PatternSpecConstants.HarmonicGartleyIdealAbXa, PatternSpecConstants.HarmonicGartleyIdealAbXa, p.AbXaMin, p.AbXaMax, PatternSpecConstants.HarmonicWeightAbXa);
                bcSpec = new(PatternSpecConstants.HarmonicGartleyIdealBcAbMin, PatternSpecConstants.HarmonicGartleyIdealBcAbMax, p.BcAbMin, p.BcAbMax, PatternSpecConstants.HarmonicWeightBcAb);
                cdSpec = new(PatternSpecConstants.HarmonicGartleyIdealCdBcMin, PatternSpecConstants.HarmonicGartleyIdealCdBcMax, p.CdBcMin, p.CdBcMax, PatternSpecConstants.HarmonicWeightCdBc);
                adSpec = new(PatternSpecConstants.HarmonicGartleyIdealAdXa, PatternSpecConstants.HarmonicGartleyIdealAdXa, p.AdXaMin, p.AdXaMax, PatternSpecConstants.HarmonicWeightAdXa);
                break;

            case HarmonicPatternKind.Butterfly:
                abSpec = new(PatternSpecConstants.HarmonicButterflyIdealAbXa, PatternSpecConstants.HarmonicButterflyIdealAbXa, p.AbXaMin, p.AbXaMax, PatternSpecConstants.HarmonicWeightAbXa);
                bcSpec = new(PatternSpecConstants.HarmonicButterflyIdealBcAbMin, PatternSpecConstants.HarmonicButterflyIdealBcAbMax, p.BcAbMin, p.BcAbMax, PatternSpecConstants.HarmonicWeightBcAb);
                cdSpec = new(PatternSpecConstants.HarmonicButterflyIdealCdBcMin, PatternSpecConstants.HarmonicButterflyIdealCdBcMax, p.CdBcMin, p.CdBcMax, PatternSpecConstants.HarmonicWeightCdBc);
                adSpec = new(PatternSpecConstants.HarmonicButterflyIdealAdXaMin, PatternSpecConstants.HarmonicButterflyIdealAdXaMax, p.AdXaMin, p.AdXaMax, PatternSpecConstants.HarmonicWeightAdXa);
                break;

            case HarmonicPatternKind.Bat:
                abSpec = new(PatternSpecConstants.HarmonicBatIdealAbXaMin, PatternSpecConstants.HarmonicBatIdealAbXaMax, p.AbXaMin, p.AbXaMax, PatternSpecConstants.HarmonicWeightAbXa);
                bcSpec = new(PatternSpecConstants.HarmonicBatIdealBcAbMin, PatternSpecConstants.HarmonicBatIdealBcAbMax, p.BcAbMin, p.BcAbMax, PatternSpecConstants.HarmonicWeightBcAb);
                cdSpec = new(PatternSpecConstants.HarmonicBatIdealCdBcMin, PatternSpecConstants.HarmonicBatIdealCdBcMax, p.CdBcMin, p.CdBcMax, PatternSpecConstants.HarmonicWeightCdBc);
                adSpec = new(PatternSpecConstants.HarmonicBatIdealAdXa, PatternSpecConstants.HarmonicBatIdealAdXa, p.AdXaMin, p.AdXaMax, PatternSpecConstants.HarmonicWeightAdXa);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        decimal s1 = ScoreRatio(rAbXa, abSpec);
        decimal s2 = ScoreRatio(rBcAb, bcSpec);
        decimal s3 = ScoreRatio(rCdBc, cdSpec);
        decimal s4 = ScoreRatio(rAdXa, adSpec);

        // Spec 4: any single ratio scoring 0 rejects the pattern.
        if (s1 <= 0m || s2 <= 0m || s3 <= 0m || s4 <= 0m)
        {
            return false;
        }

        Span<decimal> scores = stackalloc decimal[] { s1, s2, s3, s4 };
        Span<decimal> weights = stackalloc decimal[] { abSpec.Weight, bcSpec.Weight, cdSpec.Weight, adSpec.Weight };
        score = FibonacciScore.Weighted(scores, weights);
        return score >= p.MinConfidenceScore;
    }

    private static decimal ScoreRatio(decimal ratio, in RatioSpec spec)
    {
        decimal idealMin = Math.Clamp(spec.IdealMin, spec.AllowedMin, spec.AllowedMax);
        decimal idealMax = Math.Clamp(spec.IdealMax, idealMin, spec.AllowedMax);
        return FibonacciScore.Trapezoid(ratio, idealMin, idealMax, spec.AllowedMin, spec.AllowedMax);
    }

    private static void TriggerAndHold(
        PatternSeriesContext ctx,
        CoreHarmonicPatternParameter p,
        HarmonicPatternKind kind,
        PatternEventTimeline<HarmonicTupleKey> timeline,
        HarmonicTupleKey key,
        bool bullish,
        in HarmonicCandidatePivots cand,
        decimal score)
    {
        int confirmedAt = cand.ConfirmedAtIndex;
        decimal px = cand.X.Price;
        decimal pa = cand.A.Price;
        decimal pd = cand.D.Price;
        decimal xa = Math.Abs(pa - px);
        int direction = bullish ? 1 : -1;

        // Pre-confirmation bars (i_D + 1 .. t_conf - 1): an invalid bar or invalidation kills the candidate.
        for (int b = cand.D.Index + 1; b < confirmedAt; b++)
        {
            if (!ctx.IsValid(b) || IsInvalidated(kind, bullish, ctx[b].Close, px, pa, xa))
            {
                return;
            }
        }

        int lastTriggerBar = Math.Min(ctx.Count - 1, confirmedAt + p.MaxBarsSinceConfirmation);
        for (int t = confirmedAt; t <= lastTriggerBar; t++)
        {
            if (!ctx.IsValid(t) || IsInvalidated(kind, bullish, ctx[t].Close, px, pa, xa))
            {
                return;
            }

            if (p.RequireReversalCandle)
            {
                decimal close = ctx[t].Close;
                decimal open = ctx[t].Open;
                bool reversal = bullish ? (close > open && close > pd) : (close < open && close < pd);
                if (!reversal)
                {
                    continue;
                }
            }

            if (timeline.TryFire(key, direction, t, p.MaxBarsSinceConfirmation, score, confirmedAt, out int eventId))
            {
                CutByInvalidation(ctx, kind, bullish, timeline, eventId, t, p.MaxBarsSinceConfirmation, px, pa, xa);
            }

            return;
        }
    }

    private static void CutByInvalidation(
        PatternSeriesContext ctx,
        HarmonicPatternKind kind,
        bool bullish,
        PatternEventTimeline<HarmonicTupleKey> timeline,
        int eventId,
        int triggerBar,
        int persistenceBars,
        decimal px,
        decimal pa,
        decimal xa)
    {
        int last = (int)Math.Min((long)ctx.Count - 1, (long)triggerBar + persistenceBars);
        for (int b = triggerBar + 1; b <= last; b++)
        {
            if (ctx.IsValid(b) && IsInvalidated(kind, bullish, ctx[b].Close, px, pa, xa))
            {
                timeline.InvalidateFrom(eventId, b);
                return;
            }
        }
    }

    private static bool IsInvalidated(HarmonicPatternKind kind, bool bullish, decimal close, decimal px, decimal pa, decimal xa)
    {
        if (kind == HarmonicPatternKind.Butterfly)
        {
            decimal ad = bullish ? pa - close : close - pa;
            return xa > 0m && ad / xa > PatternSpecConstants.HarmonicButterflyInvalidationExtensionMax;
        }

        return bullish ? close < px : close > px;
    }
}
