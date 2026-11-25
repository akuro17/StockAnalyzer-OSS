using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>One causal 5-point candidate <c>(X, A, B, C, D)</c> produced by <see cref="HarmonicExtrema"/> at confirmation bar <see cref="ConfirmedAtIndex"/>.</summary>
public readonly record struct HarmonicCandidatePivots(
    ConfirmedPivot X,
    ConfirmedPivot A,
    ConfirmedPivot B,
    ConfirmedPivot C,
    ConfirmedPivot D,
    int Window)
{
    public int ConfirmedAtIndex => D.ConfirmedAtIndex;
}

/// <summary>
/// Causal local-extrema pivot extractor and alternator for Harmonic patterns (<c>00_COMMON_MATHEMATICAL_DEFINITIONS.md</c> 5.1, plan R-18, R-26, decision U2-12 A).
/// <para>
/// Unlike the legacy <c>HarmonicPatternDetector.ExtractPivotsByLocalExtrema</c> (which excludes equal neighbours on both sides), this extractor applies the v2 tie rule: left <c>&lt;=</c> and
/// right strict <c>&lt;</c> for highs (mirror for lows), so the last bar of an equal plateau is selected uniquely, with <c>ConfirmedAtIndex = i + window</c>. Windows containing an OHLC-inconsistent
/// bar yield no pivot (R-16, R-18).
/// </para>
/// </summary>
public static class HarmonicExtrema
{
    /// <summary>Extracts causal local extrema for a single symmetric <paramref name="window"/> in chronological order of bar index.</summary>
    public static IReadOnlyList<ConfirmedPivot> ExtractWindow(PatternSeriesContext ctx, int window)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (window <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        }

        var pivots = new List<ConfirmedPivot>();
        int lastCenter = ctx.Count - 1 - window;
        for (int i = window; i <= lastCenter; i++)
        {
            if (!ctx.AllValid(i - window, i + window))
            {
                continue;
            }

            decimal centerHigh = ctx[i].High;
            decimal centerLow = ctx[i].Low;
            bool isHigh = true;
            bool isLow = true;

            for (int j = i - window; j < i; j++)
            {
                if (ctx[j].High > centerHigh)
                {
                    isHigh = false;
                }

                if (ctx[j].Low < centerLow)
                {
                    isLow = false;
                }

                if (!isHigh && !isLow)
                {
                    break;
                }
            }

            if (isHigh || isLow)
            {
                for (int j = i + 1; j <= i + window; j++)
                {
                    if (ctx[j].High >= centerHigh)
                    {
                        isHigh = false;
                    }

                    if (ctx[j].Low <= centerLow)
                    {
                        isLow = false;
                    }

                    if (!isHigh && !isLow)
                    {
                        break;
                    }
                }
            }

            int confirmedAt = i + window;
            if (isHigh)
            {
                pivots.Add(new ConfirmedPivot(i, centerHigh, IsHigh: true, confirmedAt));
            }

            if (isLow)
            {
                pivots.Add(new ConfirmedPivot(i, centerLow, IsHigh: false, confirmedAt));
            }
        }

        return pivots;
    }

    /// <summary>
    /// Applies the R-26 alternation rule to an ordered pivot sequence: a consecutive same-side pivot replaces the previous one only when strictly more extreme
    /// (<c>&gt;</c> for highs, <c>&lt;</c> for lows); equal price keeps the earlier pivot.
    /// </summary>
    public static IReadOnlyList<ConfirmedPivot> Alternate(IReadOnlyList<ConfirmedPivot> pivots)
    {
        ArgumentNullException.ThrowIfNull(pivots);
        var alternated = new List<ConfirmedPivot>();
        for (int i = 0; i < pivots.Count; i++)
        {
            AlternateStep(alternated, pivots[i]);
        }

        return alternated;
    }

    /// <summary>
    /// Streams raw pivots of <paramref name="window"/> in causal order, maintaining the alternated sequence incrementally so every 5-tuple formed at <c>ConfirmedAtIndex = D.Index + window</c>
    /// depends strictly on bars <c>0..ConfirmedAtIndex</c> (a later replacement of <c>D</c> never erases an already-confirmed earlier 5-tuple).
    /// </summary>
    public static IReadOnlyList<HarmonicCandidatePivots> ExtractCausalCandidates(PatternSeriesContext ctx, int window)
    {
        IReadOnlyList<ConfirmedPivot> raw = ExtractWindow(ctx, window);
        var alternated = new List<ConfirmedPivot>();
        var candidates = new List<HarmonicCandidatePivots>();

        for (int i = 0; i < raw.Count; i++)
        {
            if (AlternateStep(alternated, raw[i]) && alternated.Count >= 5)
            {
                int n = alternated.Count;
                candidates.Add(new HarmonicCandidatePivots(
                    alternated[n - 5],
                    alternated[n - 4],
                    alternated[n - 3],
                    alternated[n - 2],
                    alternated[n - 1],
                    window));
            }
        }

        return candidates;
    }

    /// <summary>Appends or replaces the trailing pivot in <paramref name="alternated"/> per R-26; returns true when the trailing pivot changed.</summary>
    private static bool AlternateStep(List<ConfirmedPivot> alternated, ConfirmedPivot next)
    {
        if (alternated.Count == 0)
        {
            alternated.Add(next);
            return true;
        }

        ConfirmedPivot last = alternated[^1];
        if (last.IsHigh != next.IsHigh)
        {
            alternated.Add(next);
            return true;
        }

        if (next.IsHigh ? next.Price > last.Price : next.Price < last.Price)
        {
            alternated[^1] = next;
            return true;
        }

        return false;
    }
}
