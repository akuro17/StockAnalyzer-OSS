using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// Elliott Wave Impulse (1-2-3-4-5) and Corrective (A-B-C) engines (<c>05_Wave_Theory\01_elliott_wave_impulse_1_5.md</c>, <c>02_elliott_wave_corrective_abc.md</c>;
/// plan ChartPatterns56 v2, run V2-7; ledger <c>docs/sa_pattern_literal_ledger_V2-7_ElliottGranville</c>).
/// <para>
/// Model P: Impulse and Corrective run in independent per-type pools over causal ZigZag pivots (<see cref="CausalZigZag"/>, <c>FractalBars = 0</c>).
/// Impulse evaluates every 6 consecutive pivots <c>(p0..p5)</c>; Corrective evaluates every 4 consecutive pivots <c>(p0..p3)</c> (decision U2-06 A). Confirmed tuples fire at
/// <c>ConfirmedAtIndex &lt;= t</c> (plan R-32), hold for <c>MaxBarsSinceConfirmation</c> bars, and are consumed once.
/// </para>
/// </summary>
public static class ElliottWaveEngine
{
    private readonly record struct ImpulseKey(int P0, int P1, int P2, int P3, int P4, int P5);
    private readonly record struct CorrectiveKey(int P0, int P1, int P2, int P3);

    /// <summary>Runs the 5-wave Impulse engine over <paramref name="ctx"/> (indicator nulls bars before <c>MinBars = 40</c>).</summary>
    public static PatternBarResult[] RunImpulse(PatternSeriesContext ctx, CoreElliottImpulseParameter p, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        var timeline = new PatternEventTimeline<ImpulseKey>(ctx.Count);
        IReadOnlyList<ConfirmedPivot> pivots = CausalZigZag.Extract(ctx, new CausalZigZagOptions(p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, PatternSpecConstants.ElliottFractalBars));

        for (int k = 5; k < pivots.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConfirmedPivot p0 = pivots[k - 5];
            ConfirmedPivot p1 = pivots[k - 4];
            ConfirmedPivot p2 = pivots[k - 3];
            ConfirmedPivot p3 = pivots[k - 2];
            ConfirmedPivot p4 = pivots[k - 1];
            ConfirmedPivot p5 = pivots[k];

            bool bullish = !p0.IsHigh;
            if (bullish ? !PatternEngineSupport.AllowsBullish(p.DirectionMode) : !PatternEngineSupport.AllowsBearish(p.DirectionMode))
            {
                continue;
            }

            if (!ImpulseShapeHolds(ctx, p, bullish, p0, p1, p2, p3, p4, p5, out decimal score))
            {
                continue;
            }

            int confirmedAt = p5.ConfirmedAtIndex;
            if (confirmedAt < ctx.Count && ctx.AllValid(p5.Index, confirmedAt))
            {
                var key = new ImpulseKey(p0.Index, p1.Index, p2.Index, p3.Index, p4.Index, p5.Index);
                timeline.TryFire(key, bullish ? 1 : -1, confirmedAt, p.MaxBarsSinceConfirmation, score, confirmedAt, out _);
            }
        }

        PatternBarResult[] results = timeline.Resolve(PatternEngineSupport.AllowsBullish(p.DirectionMode), PatternEngineSupport.AllowsBearish(p.DirectionMode));
        PatternEngineSupport.ClearInconsistentBars(ctx, results);
        return results;
    }

    /// <summary>Runs the 3-wave Corrective ABC engine over <paramref name="ctx"/> (indicator nulls bars before <c>MinBars = 40</c>).</summary>
    public static PatternBarResult[] RunCorrective(PatternSeriesContext ctx, CoreElliottCorrectiveParameter p, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        var timeline = new PatternEventTimeline<CorrectiveKey>(ctx.Count);
        IReadOnlyList<ConfirmedPivot> pivots = CausalZigZag.Extract(ctx, new CausalZigZagOptions(p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, PatternSpecConstants.ElliottFractalBars));

        for (int k = 3; k < pivots.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConfirmedPivot p0 = pivots[k - 3];
            ConfirmedPivot p1 = pivots[k - 2];
            ConfirmedPivot p2 = pivots[k - 1];
            ConfirmedPivot p3 = pivots[k];

            bool bullish = !p0.IsHigh;
            if (bullish ? !PatternEngineSupport.AllowsBullish(p.DirectionMode) : !PatternEngineSupport.AllowsBearish(p.DirectionMode))
            {
                continue;
            }

            if (!CorrectiveShapeHolds(ctx, p, bullish, p0, p1, p2, p3, out decimal score))
            {
                continue;
            }

            int confirmedAt = p3.ConfirmedAtIndex;
            if (confirmedAt < ctx.Count && ctx.AllValid(p3.Index, confirmedAt))
            {
                var key = new CorrectiveKey(p0.Index, p1.Index, p2.Index, p3.Index);
                timeline.TryFire(key, bullish ? 1 : -1, confirmedAt, p.MaxBarsSinceConfirmation, score, confirmedAt, out _);
            }
        }

        PatternBarResult[] results = timeline.Resolve(PatternEngineSupport.AllowsBullish(p.DirectionMode), PatternEngineSupport.AllowsBearish(p.DirectionMode));
        PatternEngineSupport.ClearInconsistentBars(ctx, results);
        return results;
    }

    /// <summary>
    /// Auxiliary Wave 5 momentum divergence attribute (spec 01 section 5; not an output series):
    /// <c>ROC(i) = (C_i - C_{i-5}) / C_{i-5}</c> when <c>i &gt;= 5</c> and <c>C_{i-5} &gt; 0</c> (returns false outside those bounds or on inconsistent bars).
    /// Bullish: <c>p5 &gt; p3 &amp;&amp; ROC(i5) &lt; ROC(i3)</c>; Bearish: <c>p5 &lt; p3 &amp;&amp; ROC(i5) &gt; ROC(i3)</c>.
    /// </summary>
    public static bool HasWave5RocDivergence(PatternSeriesContext ctx, bool bullish, ConfirmedPivot p3, ConfirmedPivot p5)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        int lookback = PatternSpecConstants.ElliottRocLookbackBars;
        if (p3.Index < lookback || p5.Index < lookback || p5.Index >= ctx.Count)
        {
            return false;
        }

        if (!ctx.IsValid(p3.Index) || !ctx.IsValid(p3.Index - lookback) || !ctx.IsValid(p5.Index) || !ctx.IsValid(p5.Index - lookback))
        {
            return false;
        }

        decimal c3Base = ctx[p3.Index - lookback].Close;
        decimal c5Base = ctx[p5.Index - lookback].Close;
        if (c3Base <= 0m || c5Base <= 0m)
        {
            return false;
        }

        decimal roc3 = (ctx[p3.Index].Close - c3Base) / c3Base;
        decimal roc5 = (ctx[p5.Index].Close - c5Base) / c5Base;
        return bullish
            ? p5.Price > p3.Price && roc5 < roc3
            : p5.Price < p3.Price && roc5 > roc3;
    }

    private static bool ImpulseShapeHolds(
        PatternSeriesContext ctx,
        CoreElliottImpulseParameter p,
        bool bullish,
        ConfirmedPivot p0,
        ConfirmedPivot p1,
        ConfirmedPivot p2,
        ConfirmedPivot p3,
        ConfirmedPivot p4,
        ConfirmedPivot p5,
        out decimal score)
    {
        score = 0m;
        if (p0.Price <= 0m || p1.Price <= 0m || p2.Price <= 0m || p3.Price <= 0m || p4.Price <= 0m || p5.Price <= 0m)
        {
            return false;
        }

        // Spec 3: durations & time symmetry.
        int span = p5.Index - p0.Index;
        int d1 = p1.Index - p0.Index;
        int d2 = p2.Index - p1.Index;
        int d3 = p3.Index - p2.Index;
        int d4 = p4.Index - p3.Index;
        int d5 = p5.Index - p4.Index;
        if (span < p.MinSpanBars || span > p.MaxSpanBars
            || !WaveLegValid(d1, PatternSpecConstants.ElliottImpulseWaveBarsMin, PatternSpecConstants.ElliottImpulseWaveBarsMax)
            || !WaveLegValid(d2, PatternSpecConstants.ElliottImpulseWaveBarsMin, PatternSpecConstants.ElliottImpulseWaveBarsMax)
            || !WaveLegValid(d3, PatternSpecConstants.ElliottImpulseWaveBarsMin, PatternSpecConstants.ElliottImpulseWaveBarsMax)
            || !WaveLegValid(d4, PatternSpecConstants.ElliottImpulseWaveBarsMin, PatternSpecConstants.ElliottImpulseWaveBarsMax)
            || !WaveLegValid(d5, PatternSpecConstants.ElliottImpulseWaveBarsMin, PatternSpecConstants.ElliottImpulseWaveBarsMax))
        {
            return false;
        }

        int minLeg = Math.Min(d1, Math.Min(Math.Min(d2, d3), Math.Min(d4, d5)));
        int maxLeg = Math.Max(d1, Math.Max(Math.Max(d2, d3), Math.Max(d4, d5)));
        decimal symDenom = Math.Max(PatternSpecConstants.ElliottTimeSymmetryFloorBars, minLeg);
        if ((decimal)maxLeg / symDenom > p.MaxTimeSymmetryRatio)
        {
            return false;
        }

        // Spec 4: Three Absolute Principles.
        decimal w1 = Math.Abs(p1.Price - p0.Price);
        decimal w2 = Math.Abs(p2.Price - p1.Price);
        decimal w3 = Math.Abs(p3.Price - p2.Price);
        decimal w4 = Math.Abs(p4.Price - p3.Price);
        decimal w5 = Math.Abs(p5.Price - p4.Price);
        if (w1 <= 0m || w3 <= 0m)
        {
            return false;
        }

        // Principle 1: Wave 2 never retraces beyond Wave 1 origin.
        if (bullish ? p2.Price <= p0.Price : p2.Price >= p0.Price)
        {
            return false;
        }

        // Principle 2: Wave 3 cannot be the shortest of waves 1, 3, 5, and must exceed Wave 1 endpoint.
        if ((w3 <= w1 && w3 <= w5) || (bullish ? p3.Price <= p1.Price : p3.Price >= p1.Price))
        {
            return false;
        }

        // Principle 3: Wave 4 does not overlap Wave 1 territory (strict when OverlapTolerance == 0, else <= OverlapTolerance * w1).
        if (p.OverlapTolerance == 0m)
        {
            if (bullish ? p4.Price <= p1.Price : p4.Price >= p1.Price)
            {
                return false;
            }
        }
        else
        {
            decimal overlap = bullish ? p1.Price - p4.Price : p4.Price - p1.Price;
            if (overlap / w1 > p.OverlapTolerance)
            {
                return false;
            }
        }

        // Wave 5 endpoint: must exceed Wave 3 unless truncation is allowed.
        if (!p.AllowTruncation && (bullish ? p5.Price <= p3.Price : p5.Price >= p3.Price))
        {
            return false;
        }

        // Plan R-16: all constituent bars [p0.Index, p5.Index] must be valid.
        if (!ctx.AllValid(p0.Index, p5.Index))
        {
            return false;
        }

        // Spec 2: Origin p0 price zone.
        if (p.RequireOriginPriceZone && !OriginZoneHolds(ctx, bullish, p0))
        {
            return false;
        }

        // Spec 4: Fibonacci trapezoidal scores (+/- FibonacciTolerance, max 1 zero-score allowed).
        decimal tol = p.FibonacciTolerance;
        decimal s1 = ScoreWithTolerance(w2 / w1, PatternSpecConstants.ElliottImpulseIdealW2W1Min, PatternSpecConstants.ElliottImpulseIdealW2W1Max, tol);
        decimal s2 = ScoreWithTolerance(w3 / w1, PatternSpecConstants.ElliottImpulseIdealW3W1Min, PatternSpecConstants.ElliottImpulseIdealW3W1Max, tol);
        decimal s3 = ScoreWithTolerance(w4 / w3, PatternSpecConstants.ElliottImpulseIdealW4W3Min, PatternSpecConstants.ElliottImpulseIdealW4W3Max, tol);
        decimal s4 = ScoreWithTolerance(w5 / w1, PatternSpecConstants.ElliottImpulseIdealW5W1Min, PatternSpecConstants.ElliottImpulseIdealW5W1Max, tol);

        int zeros = (s1 <= 0m ? 1 : 0) + (s2 <= 0m ? 1 : 0) + (s3 <= 0m ? 1 : 0) + (s4 <= 0m ? 1 : 0);
        if (zeros > PatternSpecConstants.ElliottImpulseMaxZeroRatioScores)
        {
            return false;
        }

        Span<decimal> scores = stackalloc decimal[] { s1, s2, s3, s4 };
        Span<decimal> weights = stackalloc decimal[]
        {
            PatternSpecConstants.ElliottImpulseWeightW2W1,
            PatternSpecConstants.ElliottImpulseWeightW3W1,
            PatternSpecConstants.ElliottImpulseWeightW4W3,
            PatternSpecConstants.ElliottImpulseWeightW5W1,
        };
        score = FibonacciScore.Weighted(scores, weights);
        return score >= p.MinConfidenceScore;
    }

    private static bool CorrectiveShapeHolds(
        PatternSeriesContext ctx,
        CoreElliottCorrectiveParameter p,
        bool bullish,
        ConfirmedPivot p0,
        ConfirmedPivot p1,
        ConfirmedPivot p2,
        ConfirmedPivot p3,
        out decimal score)
    {
        score = 0m;
        if (p0.Price <= 0m || p1.Price <= 0m || p2.Price <= 0m || p3.Price <= 0m)
        {
            return false;
        }

        // Spec 3: durations & time symmetry.
        int span = p3.Index - p0.Index;
        int d1 = p1.Index - p0.Index;
        int d2 = p2.Index - p1.Index;
        int d3 = p3.Index - p2.Index;
        if (span < p.MinSpanBars || span > p.MaxSpanBars
            || !WaveLegValid(d1, PatternSpecConstants.ElliottCorrectiveWaveBarsMin, PatternSpecConstants.ElliottCorrectiveWaveBarsMax)
            || !WaveLegValid(d2, PatternSpecConstants.ElliottCorrectiveWaveBarsMin, PatternSpecConstants.ElliottCorrectiveWaveBarsMax)
            || !WaveLegValid(d3, PatternSpecConstants.ElliottCorrectiveWaveBarsMin, PatternSpecConstants.ElliottCorrectiveWaveBarsMax))
        {
            return false;
        }

        int minLeg = Math.Min(d1, Math.Min(d2, d3));
        int maxLeg = Math.Max(d1, Math.Max(d2, d3));
        decimal symDenom = Math.Max(PatternSpecConstants.ElliottTimeSymmetryFloorBars, minLeg);
        if ((decimal)maxLeg / symDenom > p.MaxTimeSymmetryRatio)
        {
            return false;
        }

        // Spec 4: Wave B cannot retrace beyond Wave A origin; Wave C exceeds Wave A unless option is off.
        if (bullish ? p2.Price <= p0.Price : p2.Price >= p0.Price)
        {
            return false;
        }

        if (p.RequireWaveCExceedWaveA && (bullish ? p3.Price <= p1.Price : p3.Price >= p1.Price))
        {
            return false;
        }

        if (!ctx.AllValid(p0.Index, p3.Index))
        {
            return false;
        }

        if (p.RequireOriginPriceZone && !OriginZoneHolds(ctx, bullish, p0))
        {
            return false;
        }

        decimal wA = Math.Abs(p1.Price - p0.Price);
        decimal wB = Math.Abs(p2.Price - p1.Price);
        decimal wC = Math.Abs(p3.Price - p2.Price);
        if (wA <= 0m)
        {
            return false;
        }

        decimal tol = p.FibonacciTolerance;
        decimal sB = ScoreWithTolerance(wB / wA, PatternSpecConstants.ElliottCorrectiveIdealWBWAMin, PatternSpecConstants.ElliottCorrectiveIdealWBWAMax, tol);
        decimal sC = ScoreWithTolerance(wC / wA, PatternSpecConstants.ElliottCorrectiveIdealWCWAMin, PatternSpecConstants.ElliottCorrectiveIdealWCWAMax, tol);

        // Spec 4: BOTH scores must be strictly positive.
        if (sB <= 0m || sC <= 0m)
        {
            return false;
        }

        Span<decimal> scores = stackalloc decimal[] { sB, sC };
        Span<decimal> weights = stackalloc decimal[]
        {
            PatternSpecConstants.ElliottCorrectiveWeightWBWA,
            PatternSpecConstants.ElliottCorrectiveWeightWCWA,
        };
        score = FibonacciScore.Weighted(scores, weights);
        return score >= p.MinConfidenceScore;
    }

    private static bool WaveLegValid(int legBars, int minBars, int maxBars) => legBars >= minBars && legBars <= maxBars;

    private static bool OriginZoneHolds(PatternSeriesContext ctx, bool bullish, ConfirmedPivot p0)
    {
        decimal? pos = PatternMath.Pos(ctx, p0.Price, p0.Index, PatternSpecConstants.ElliottZoneBars);
        if (pos is null)
        {
            return false;
        }

        return bullish ? pos.Value <= PatternSpecConstants.ElliottOriginZoneThreshold : pos.Value >= 1m - PatternSpecConstants.ElliottOriginZoneThreshold;
    }

    private static decimal ScoreWithTolerance(decimal ratio, decimal idealMin, decimal idealMax, decimal tolerance)
    {
        decimal allowedMin = Math.Max(0m, idealMin - tolerance);
        decimal allowedMax = idealMax + tolerance;
        return FibonacciScore.Trapezoid(ratio, idealMin, idealMax, allowedMin, allowedMax);
    }
}
