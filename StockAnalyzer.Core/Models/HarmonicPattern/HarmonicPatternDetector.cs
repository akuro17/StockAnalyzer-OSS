using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Validation;

namespace StockAnalyzer.Core.Models.HarmonicPattern;

/// <summary>
/// Detects classic harmonic chart patterns (Gartley, Bat, Butterfly, Crab)
/// using ZigZag-based pivot extraction and Fibonacci ratio matching.
/// This is a pure C# implementation with no Python dependency.
/// </summary>
public static class HarmonicPatternDetector
{
    /// <summary>
    /// Detects all valid harmonic patterns in the given candle data using multi-scale analysis.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="zigzagThreshold">ZigZag threshold percentage for pivot extraction.</param>
    /// <param name="zigzagThresholds">Optional array of multi-scale thresholds. If null, uses defaults.</param>
    /// <returns>A list of detected harmonic patterns, ordered by confidence (descending).</returns>
    public static IReadOnlyList<HarmonicPatternResult> Detect(
        IReadOnlyList<CandleData> candles,
        decimal zigzagThreshold = ChartConstants.DefaultHarmonicZigZagThreshold,
        decimal[]? zigzagThresholds = null)
    {
        if (candles == null || candles.Count < ChartConstants.HarmonicMinPivotCount * 2)
            return Array.Empty<HarmonicPatternResult>();

        var thresholds = zigzagThresholds ?? ChartConstants.HarmonicMultiScaleThresholds;
        if (thresholds.Length == 0)
            thresholds = new[] { zigzagThreshold };

        var allPatterns = new List<HarmonicPatternResult>();

        foreach (var threshold in thresholds)
        {
            // Use Local Extrema instead of ZigZag to eliminate left-edge dependency
            int windowSize = (int)Math.Max(1, Math.Round(threshold));
            var pivots = ExtractPivotsByLocalExtrema(candles, windowSize);
            
            if (pivots.Count < ChartConstants.HarmonicMinPivotCount)
                continue;

            var patterns = FindPatternsFromPivots(pivots, candles);
            allPatterns.AddRange(patterns);

            // AB=CD family uses a dedicated 4-point sliding window
            var abcdPatterns = FindAbcdPatterns(pivots, candles);
            allPatterns.AddRange(abcdPatterns);

            // 5-0 pattern uses a dedicated 5-point detection method
            var fiveZeroPatterns = FindFiveZeroPatterns(pivots, candles);
            allPatterns.AddRange(fiveZeroPatterns);
        }

        if (allPatterns.Count == 0)
            return allPatterns;

        // Remove overlapping patterns, keeping the highest-scored
        return FilterOverlappingPatterns(allPatterns);
    }

    /// <summary>
    /// Extracts pivot points by finding local extrema (highest high or lowest low) within a sliding window.
    /// This approach is mathematically stable against shifting the left edge of the dataset.
    /// </summary>
    internal static List<PivotPoint> ExtractPivotsByLocalExtrema(IReadOnlyList<CandleData> candles, int windowSize)
    {
        var pivots = new List<PivotPoint>();

        for (int i = windowSize; i < candles.Count - windowSize; i++)
        {
            bool isSwingHigh = true;
            bool isSwingLow = true;

            decimal centerHigh = candles[i].High;
            decimal centerLow = candles[i].Low;

            // Check neighborhood
            for (int j = 1; j <= windowSize; j++)
            {
                if (candles[i - j].High >= centerHigh || candles[i + j].High >= centerHigh)
                {
                    isSwingHigh = false;
                }
                if (candles[i - j].Low <= centerLow || candles[i + j].Low <= centerLow)
                {
                    isSwingLow = false;
                }
            }

            if (isSwingHigh)
            {
                pivots.Add(new PivotPoint(i, candles[i].Time, centerHigh, isHigh: true));
            }
            if (isSwingLow)
            {
                pivots.Add(new PivotPoint(i, candles[i].Time, centerLow, isHigh: false));
            }
        }

        return pivots;
    }

    /// <summary>
    /// Detects the most recent (highest-confidence) harmonic pattern.
    /// Useful for screening where only the latest state matters.
    /// </summary>
    public static HarmonicPatternResult? DetectLatest(
        IReadOnlyList<CandleData> candles,
        decimal zigzagThreshold = ChartConstants.DefaultHarmonicZigZagThreshold)
    {
        var patterns = Detect(candles, zigzagThreshold);
        return patterns.Count > 0 ? patterns[0] : null;
    }

    /// <summary>
    /// Finds all valid XABCD harmonic patterns from the given pivot sequence.
    /// </summary>
    internal static List<HarmonicPatternResult> FindPatternsFromPivots(
        IReadOnlyList<PivotPoint> pivots,
        IReadOnlyList<CandleData>? candles = null)
    {
        var results = new List<HarmonicPatternResult>();

        // Pre-filter: Ensure strict alternation. MarketStructureDetector may sometimes return adjacent Highs or Lows.
        var alternatingPivots = new List<PivotPoint>();
        if (pivots.Count > 0)
        {
            alternatingPivots.Add(pivots[0]);
            for (int i = 1; i < pivots.Count; i++)
            {
                var prev = alternatingPivots[^1];
                var curr = pivots[i];
                if (curr.IsHigh != prev.IsHigh)
                {
                    alternatingPivots.Add(curr);
                }
                else
                {
                    // If same direction, keep the extreme one
                    if ((curr.IsHigh && curr.Price > prev.Price) ||
                        (!curr.IsHigh && curr.Price < prev.Price))
                    {
                        alternatingPivots[^1] = curr;
                    }
                }
            }
        }

        int count = alternatingPivots.Count;

        // We need at least 5 consecutive pivots to form an XABCD pattern.
        // Iterate through all possible 5-point combinations maintaining alternation.
        for (int i = 0; i <= count - 5; i++)
        {
            var x = alternatingPivots[i];
            var a = alternatingPivots[i + 1];
            var b = alternatingPivots[i + 2];
            var c = alternatingPivots[i + 3];
            var d = alternatingPivots[i + 4];

            // Validate alternation: pivots must alternate between high and low
            if (!IsAlternating(x, a, b, c, d))
                continue;

            // Determine direction: bullish when X->A goes up (X is low, A is high)
            bool isBullish = a.Price > x.Price;

            // Calculate leg ratios
            decimal xa = Math.Abs(a.Price - x.Price);
            if (xa == 0) continue;

            decimal ab = Math.Abs(b.Price - a.Price);
            decimal bc = Math.Abs(c.Price - b.Price);
            decimal cd = Math.Abs(d.Price - c.Price);
            decimal xc = Math.Abs(c.Price - x.Price);

            double abXaRatio = (double)(ab / xa);
            double bcAbRatio = ab > 0 ? (double)(bc / ab) : 0;
            double cdBcRatio = bc > 0 ? (double)(cd / bc) : 0;

            // For D-point: measure retracement/extension relative to XA
            double dXaRatio = (double)(Math.Abs(d.Price - a.Price) / xa);

            // For Cypher-family: measure D as retracement of XC
            double? dXcRatio = xc > 0 ? (double)(cd / xc) : null;

            // Try each pattern type
            foreach (HarmonicPatternType patternType in Enum.GetValues(typeof(HarmonicPatternType)))
            {
                // These pattern types use dedicated detection methods, not the standard XABCD loop
                if (patternType == HarmonicPatternType.ABCD || patternType == HarmonicPatternType.SeaPony ||
                    patternType == HarmonicPatternType.FiveZero || patternType == HarmonicPatternType.SeaHorse ||
                    patternType == HarmonicPatternType.Dragon)
                    continue;
                var definition = GetPatternDefinition(patternType);
                double score = EvaluatePattern(definition, abXaRatio, bcAbRatio, cdBcRatio, dXaRatio, dXcRatio);

                if (score < ChartConstants.HarmonicMinConfidence)
                    continue;

                // Apply multiscale bonus: prefer larger (longer) patterns
                int span = d.Index - x.Index;
                double multiscaleBonus = span > 1
                    ? Math.Log2(span) * ChartConstants.HarmonicMultiscaleWeight
                    : 0;
                double finalScore = Math.Min(1.0, score + multiscaleBonus);

                // Formation Process Validation (FR-60-7-01):
                // Skip patterns that lack sufficient time, volatility, or symmetry.
                if (candles != null)
                {
                    if (!PatternFormationValidator.ValidateMinBars(span, ChartConstants.FormationMinBarsHarmonic))
                        continue;

                    var legs = new List<(int, int)>
                    {
                        (x.Index, a.Index),
                        (a.Index, b.Index),
                        (b.Index, c.Index),
                        (c.Index, d.Index)
                    };
                    if (!PatternFormationValidator.ValidateVolatility(
                            legs, candles, ChartConstants.FormationVolatilityAtrMultiplier))
                        continue;

                    var legDurations = new List<int>
                    {
                        a.Index - x.Index,
                        b.Index - a.Index,
                        c.Index - b.Index,
                        d.Index - c.Index
                    };
                    if (!PatternFormationValidator.ValidateTimeSymmetry(
                            legDurations, ChartConstants.FormationMaxTimeRatio))
                        continue;
                }

                // Navarro 200: apply time-zone constraint (C-D duration vs X-A duration)
                if (patternType == HarmonicPatternType.Navarro200)
                {
                    int xaDuration = a.Index - x.Index;
                    int cdDuration = d.Index - c.Index;
                    if (!ValidateTimeZoneConstraint(xaDuration, cdDuration))
                        continue;
                }

                // Calculate PRZ
                var (przLow, przHigh) = CalculatePrz(x, a, d, patternType, isBullish);

                results.Add(new HarmonicPatternResult(
                    patternType, x, a, b, c, d,
                    finalScore, przLow, przHigh, isBullish));
            }
        }

        return results;
    }

    /// <summary>
    /// Validates that 5 pivots alternate between high and low (or low and high).
    /// </summary>
    internal static bool IsAlternating(PivotPoint x, PivotPoint a, PivotPoint b, PivotPoint c, PivotPoint d)
    {
        // First pivot can be either high or low, subsequent must alternate
        bool expected = !x.IsHigh; // a should be opposite of x
        return a.IsHigh == expected
            && b.IsHigh == !expected
            && c.IsHigh == expected
            && d.IsHigh == !expected;
    }

    /// <summary>
    /// Evaluates how closely the given leg ratios match a specific harmonic pattern definition.
    /// Returns a confidence score from 0.0 to 1.0.
    /// </summary>
    internal static double EvaluatePattern(
        PatternDefinition def,
        double abXaRatio,
        double bcAbRatio,
        double cdBcRatio,
        double dXaRatio,
        double? dXcRatio = null)
    {
        // Score each leg individually (1.0 = perfect match, 0.0 = outside tolerance)
        double abScore = ScoreRatio(abXaRatio, def.AbXaMin, def.AbXaMax, def.IsAbXaFixed);
        double bcScore = ScoreRatio(bcAbRatio, def.BcAbMin, def.BcAbMax, false);
        double cdScore = ScoreRatio(cdBcRatio, def.CdBcMin, def.CdBcMax, false);

        // D-point scoring: use DXc ratio when pattern defines it (e.g. Cypher), otherwise DXa
        double dScore;
        if (def.DXcMin.HasValue && def.DXcMax.HasValue && dXcRatio.HasValue)
        {
            dScore = ScoreRatio(dXcRatio.Value, def.DXcMin.Value, def.DXcMax.Value, def.IsDXaFixed);
        }
        else
        {
            dScore = ScoreRatio(dXaRatio, def.DXaMin, def.DXaMax, def.IsDXaFixed);
        }

        // If any individual score is zero, the pattern is invalid
        if (abScore == 0 || bcScore == 0 || cdScore == 0 || dScore == 0)
            return 0;

        // Weighted average: AB/XA and D ratios are the most critical
        return (abScore * 0.30) + (bcScore * 0.15) + (cdScore * 0.15) + (dScore * 0.40);
    }

    /// <summary>
    /// Scores a single ratio against its expected range, returning 0.0 to 1.0.
    /// </summary>
    internal static double ScoreRatio(double actual, double expectedMin, double expectedMax, bool isFixed)
    {
        double tolerance = isFixed
            ? ChartConstants.HarmonicDefaultTolerance
            : ChartConstants.HarmonicRangeTolerance;

        double lowerBound = expectedMin - tolerance;
        double upperBound = expectedMax + tolerance;

        if (actual < lowerBound || actual > upperBound)
            return 0;

        // Calculate mid-point of the expected range
        double mid = (expectedMin + expectedMax) / 2.0;
        double halfRange = (upperBound - lowerBound) / 2.0;

        if (halfRange <= 0)
            return 1.0;

        // Linear score: 1.0 at mid, decreasing toward 0.0 at bounds
        double deviation = Math.Abs(actual - mid) / halfRange;
        return Math.Max(0, 1.0 - deviation);
    }

    /// <summary>
    /// Calculates the Potential Reversal Zone (PRZ) around the D point.
    /// </summary>
    internal static (decimal Low, decimal High) CalculatePrz(
        PivotPoint x, PivotPoint a, PivotPoint d,
        HarmonicPatternType patternType, bool isBullish)
    {
        decimal dPrice = d.Price;
        decimal xa = Math.Abs(a.Price - x.Price);

        // PRZ expansion: base percentage ± expansion
        decimal expansion = xa * ChartConstants.HarmonicPrzExpansionPercent / 100m;
        if (expansion < 0.01m)
            expansion = Math.Abs(dPrice) * 0.01m; // Fallback 1%

        decimal przLow = dPrice - expansion;
        decimal przHigh = dPrice + expansion;

        // Ensure low <= high
        if (przLow > przHigh)
            (przLow, przHigh) = (przHigh, przLow);

        return (przLow, przHigh);
    }

    /// <summary>
    /// Validates the Navarro 200 time-zone constraint.
    /// The duration of the C-D leg must be within a Fibonacci ratio range of the X-A leg duration.
    /// </summary>
    /// <param name="xaDurationBars">Duration of the X-A leg in bars.</param>
    /// <param name="cdDurationBars">Duration of the C-D leg in bars.</param>
    /// <returns>True if the time ratio falls within the allowed Fibonacci range.</returns>
    internal static bool ValidateTimeZoneConstraint(int xaDurationBars, int cdDurationBars)
    {
        if (xaDurationBars <= 0 || cdDurationBars <= 0)
            return false;

        double timeRatio = (double)cdDurationBars / xaDurationBars;
        return timeRatio >= ChartConstants.Navarro200TimeRatioMin
            && timeRatio <= ChartConstants.Navarro200TimeRatioMax;
    }

    private static IReadOnlyList<HarmonicPatternResult> FilterOverlappingPatterns(List<HarmonicPatternResult> patterns)
    {
        if (patterns.Count <= 1)
            return patterns;

        // Sort by: High Confidence Tier first, then Span descending, then Confidence descending.
        // This ensures large high-confidence patterns are processed first.
        patterns.Sort((a, b) =>
        {
            bool aIsHigh = a.ConfidenceScore >= 0.80;
            bool bIsHigh = b.ConfidenceScore >= 0.80;
            if (aIsHigh != bIsHigh)
                return bIsHigh.CompareTo(aIsHigh);

            int cmp = b.Span.CompareTo(a.Span);
            if (cmp != 0) return cmp;

            return b.ConfidenceScore.CompareTo(a.ConfidenceScore);
        });

        // Greedy selection with enclosure removal:
        // Process patterns from highest priority to lowest.
        // When a new pattern is added:
        //   - If it is fully enclosed by an already-kept pattern, skip it.
        //   - If it fully encloses any already-kept patterns, remove those enclosed patterns.
        //   - If it partially overlaps (>50% of both) with an already-kept pattern, skip it.
        var kept = new List<HarmonicPatternResult>();

        foreach (var pattern in patterns)
        {
            int start = pattern.X.Index;
            int end = pattern.D.Index;
            int span = end - start;

            // Check 1: Is this pattern fully enclosed by any already-kept pattern?
            bool isEnclosed = false;
            foreach (var existing in kept)
            {
                if (existing.X.Index <= start && existing.D.Index >= end &&
                    !(existing.X.Index == start && existing.D.Index == end))
                {
                    isEnclosed = true;
                    break;
                }
            }
            if (isEnclosed) continue;

            // Check 2: Does this pattern partially overlap (>50% mutual) with any already-kept pattern?
            bool partialOverlap = false;
            foreach (var existing in kept)
            {
                int eStart = existing.X.Index;
                int eEnd = existing.D.Index;
                int overlapStart = Math.Max(start, eStart);
                int overlapEnd = Math.Min(end, eEnd);
                if (overlapEnd > overlapStart)
                {
                    int overlapLen = overlapEnd - overlapStart;
                    int existingSpan = eEnd - eStart;
                    if (span > 0 && existingSpan > 0 &&
                        (double)overlapLen / span > 0.5 &&
                        (double)overlapLen / existingSpan > 0.5)
                    {
                        partialOverlap = true;
                        break;
                    }
                }
            }
            if (partialOverlap) continue;

            // Check 3: Does this pattern fully enclose any already-kept patterns? Remove those.
            kept.RemoveAll(existing =>
                start <= existing.X.Index && end >= existing.D.Index &&
                !(start == existing.X.Index && end == existing.D.Index));

            kept.Add(pattern);
        }

        return kept;
    }

    /// <summary>
    /// Gets the Fibonacci ratio definition for a specific harmonic pattern type.
    /// </summary>
    internal static PatternDefinition GetPatternDefinition(HarmonicPatternType type)
    {
        return type switch
        {
            HarmonicPatternType.Gartley => new PatternDefinition(
                AbXaMin: 0.618, AbXaMax: 0.618, IsAbXaFixed: true,
                BcAbMin: 0.382, BcAbMax: 0.886,
                CdBcMin: 1.272, CdBcMax: 1.618,
                DXaMin: 0.786, DXaMax: 0.786, IsDXaFixed: true),

            HarmonicPatternType.Bat => new PatternDefinition(
                AbXaMin: 0.382, AbXaMax: 0.500, IsAbXaFixed: false,
                BcAbMin: 0.382, BcAbMax: 0.886,
                CdBcMin: 1.618, CdBcMax: 2.618,
                DXaMin: 0.886, DXaMax: 0.886, IsDXaFixed: true),

            HarmonicPatternType.Butterfly => new PatternDefinition(
                AbXaMin: 0.786, AbXaMax: 0.786, IsAbXaFixed: true,
                BcAbMin: 0.382, BcAbMax: 0.886,
                CdBcMin: 1.618, CdBcMax: 2.618,
                DXaMin: 1.272, DXaMax: 1.618, IsDXaFixed: false),

            HarmonicPatternType.Crab => new PatternDefinition(
                AbXaMin: 0.382, AbXaMax: 0.618, IsAbXaFixed: false,
                BcAbMin: 0.382, BcAbMax: 0.886,
                CdBcMin: 2.618, CdBcMax: 3.618,
                DXaMin: 1.618, DXaMax: 1.618, IsDXaFixed: true),

            HarmonicPatternType.AlternateBat => new PatternDefinition(
                AbXaMin: 0.236, AbXaMax: 0.382, IsAbXaFixed: false,
                BcAbMin: 0.382, BcAbMax: 0.886,
                CdBcMin: 2.000, CdBcMax: 3.618,
                DXaMin: 1.130, DXaMax: 1.130, IsDXaFixed: true),

            HarmonicPatternType.DeepCrab => new PatternDefinition(
                AbXaMin: 0.886, AbXaMax: 0.886, IsAbXaFixed: true,
                BcAbMin: 0.382, BcAbMax: 0.886,
                CdBcMin: 2.240, CdBcMax: 3.618,
                DXaMin: 1.618, DXaMax: 1.618, IsDXaFixed: true),

            HarmonicPatternType.Leonardo => new PatternDefinition(
                AbXaMin: 0.500, AbXaMax: 0.500, IsAbXaFixed: true,
                BcAbMin: 0.382, BcAbMax: 0.886,
                CdBcMin: 1.128, CdBcMax: 2.618,
                DXaMin: 0.786, DXaMax: 0.786, IsDXaFixed: true),

            HarmonicPatternType.NenStar => new PatternDefinition(
                AbXaMin: 0.382, AbXaMax: 0.618, IsAbXaFixed: false,
                BcAbMin: 1.130, BcAbMax: 1.414,
                CdBcMin: 1.272, CdBcMax: 2.618,
                DXaMin: 1.130, DXaMax: 1.272, IsDXaFixed: false),

            HarmonicPatternType.Cypher => new PatternDefinition(
                AbXaMin: 0.382, AbXaMax: 0.618, IsAbXaFixed: false,
                BcAbMin: 1.272, BcAbMax: 1.414,
                CdBcMin: 1.272, CdBcMax: 2.000,
                DXaMin: 0, DXaMax: 2.0, IsDXaFixed: false,
                DXcMin: 0.786, DXcMax: 0.786),

            HarmonicPatternType.Shark => new PatternDefinition(
                AbXaMin: 0.382, AbXaMax: 0.786, IsAbXaFixed: false,
                BcAbMin: 1.130, BcAbMax: 1.618,
                CdBcMin: 1.130, CdBcMax: 2.236,
                DXaMin: 0.886, DXaMax: 1.130, IsDXaFixed: false),

            HarmonicPatternType.ThreeDrives => new PatternDefinition(
                AbXaMin: 1.272, AbXaMax: 1.618, IsAbXaFixed: false,
                BcAbMin: 0.382, BcAbMax: 0.786,
                CdBcMin: 1.272, CdBcMax: 1.618,
                DXaMin: 1.300, DXaMax: 5.000, IsDXaFixed: false),

            // White Swan: extreme AB extension (panic reversal)
            HarmonicPatternType.WhiteSwan => new PatternDefinition(
                AbXaMin: 1.382, AbXaMax: 2.000, IsAbXaFixed: false,
                BcAbMin: 0.236, BcAbMax: 0.500,
                CdBcMin: 1.128, CdBcMax: 2.000,
                DXaMin: 1.128, DXaMax: 2.618, IsDXaFixed: false),

            // Black Swan: same ratios as White Swan (direction differs via isBullish)
            HarmonicPatternType.BlackSwan => new PatternDefinition(
                AbXaMin: 1.382, AbXaMax: 2.000, IsAbXaFixed: false,
                BcAbMin: 0.236, BcAbMax: 0.500,
                CdBcMin: 1.128, CdBcMax: 2.000,
                DXaMin: 1.128, DXaMax: 2.618, IsDXaFixed: false),

            // Navarro 200: requires additional time-zone constraint (applied in FindPatternsFromPivots)
            HarmonicPatternType.Navarro200 => new PatternDefinition(
                AbXaMin: 0.382, AbXaMax: 0.786, IsAbXaFixed: false,
                BcAbMin: 0.886, BcAbMax: 1.128,
                CdBcMin: 1.128, CdBcMax: 2.618,
                DXaMin: 0.886, DXaMax: 1.128, IsDXaFixed: false),

            // Types that use dedicated detection methods
            HarmonicPatternType.ABCD => throw new InvalidOperationException(
                "ABCD uses FindAbcdPatterns, not GetPatternDefinition."),
            HarmonicPatternType.SeaPony => throw new InvalidOperationException(
                "SeaPony uses FindAbcdPatterns, not GetPatternDefinition."),
            HarmonicPatternType.FiveZero => throw new InvalidOperationException(
                "FiveZero uses FindFiveZeroPatterns, not GetPatternDefinition."),
            HarmonicPatternType.SeaHorse => throw new InvalidOperationException(
                "SeaHorse uses FindAbcdPatterns, not GetPatternDefinition."),
            HarmonicPatternType.Dragon => throw new InvalidOperationException(
                "Dragon uses FindAbcdPatterns, not GetPatternDefinition."),

            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    /// <summary>
    /// Defines the Fibonacci ratio constraints for a harmonic pattern type.
    /// Fixed ratios use the default point tolerance; range ratios use the range tolerance.
    /// </summary>
    internal record PatternDefinition(
        double AbXaMin, double AbXaMax, bool IsAbXaFixed,
        double BcAbMin, double BcAbMax,
        double CdBcMin, double CdBcMax,
        double DXaMin, double DXaMax, bool IsDXaFixed,
        double? DXcMin = null, double? DXcMax = null);

    /// <summary>
    /// Detects AB=CD family patterns using a dedicated 4-point sliding window.
    /// These patterns measure CD relative to AB directly, which the standard
    /// XABCD framework does not support.
    /// </summary>
    internal static List<HarmonicPatternResult> FindAbcdPatterns(
        List<PivotPoint> pivots,
        IReadOnlyList<CandleData>? candles = null)
    {
        var results = new List<HarmonicPatternResult>();

        // Build alternating pivot list (same logic as FindPatternsFromPivots)
        var alternatingPivots = new List<PivotPoint>();
        foreach (var pivot in pivots)
        {
            if (alternatingPivots.Count == 0)
            {
                alternatingPivots.Add(pivot);
            }
            else
            {
                var prev = alternatingPivots[^1];
                if (pivot.IsHigh != prev.IsHigh)
                {
                    alternatingPivots.Add(pivot);
                }
                else
                {
                    if ((pivot.IsHigh && pivot.Price > prev.Price) ||
                        (!pivot.IsHigh && pivot.Price < prev.Price))
                    {
                        alternatingPivots[^1] = pivot;
                    }
                }
            }
        }

        int count = alternatingPivots.Count;

        // 4-point sliding window: A, B, C, D
        for (int i = 0; i <= count - 4; i++)
        {
            var a = alternatingPivots[i];
            var b = alternatingPivots[i + 1];
            var c = alternatingPivots[i + 2];
            var d = alternatingPivots[i + 3];

            // Validate alternation
            if (a.IsHigh == b.IsHigh || b.IsHigh == c.IsHigh || c.IsHigh == d.IsHigh)
                continue;

            bool isBullish = b.Price < a.Price; // AB goes down = bullish (D is buy zone)

            decimal ab = Math.Abs(b.Price - a.Price);
            if (ab == 0) continue;
            decimal bc = Math.Abs(c.Price - b.Price);
            decimal cd = Math.Abs(d.Price - c.Price);

            double bcAbRatio = (double)(bc / ab);
            double cdAbRatio = (double)(cd / ab);

            // Try all AB=CD family patterns
            double abcdScore = ScoreAbcd(bcAbRatio, cdAbRatio, AbcdFamily.Standard);
            double seaPonyScore = ScoreAbcd(bcAbRatio, cdAbRatio, AbcdFamily.SeaPony);
            double seaHorseScore = ScoreAbcd(bcAbRatio, cdAbRatio, AbcdFamily.SeaHorse);
            double dragonScore = ScoreAbcd(bcAbRatio, cdAbRatio, AbcdFamily.Dragon);

            // Pick the best match
            var candidates = new (HarmonicPatternType Type, double Score)[]
            {
                (HarmonicPatternType.ABCD, abcdScore),
                (HarmonicPatternType.SeaPony, seaPonyScore),
                (HarmonicPatternType.SeaHorse, seaHorseScore),
                (HarmonicPatternType.Dragon, dragonScore)
            };

            HarmonicPatternType bestType = HarmonicPatternType.ABCD;
            double bestScore = 0;
            foreach (var (type, score) in candidates)
            {
                if (score > bestScore)
                {
                    bestType = type;
                    bestScore = score;
                }
            }

            if (bestScore < ChartConstants.HarmonicMinConfidence)
                continue;

            // Apply multiscale bonus
            int span = d.Index - a.Index;
            double multiscaleBonus = span > 1
                ? Math.Log2(span) * ChartConstants.HarmonicMultiscaleWeight
                : 0;
            double finalScore = Math.Min(1.0, bestScore + multiscaleBonus);

            // Formation validation (if candles available)
            if (candles != null)
            {
                if (!Validation.PatternFormationValidator.ValidateMinBars(
                        span, ChartConstants.FormationMinBarsHarmonic))
                    continue;
            }

            // For HarmonicPatternResult, set X = A (first point duplicated)
            var (przLow, przHigh) = CalculatePrz(a, a, d, bestType, isBullish);

            results.Add(new HarmonicPatternResult(
                bestType, a, a, b, c, d,
                finalScore, przLow, przHigh, isBullish));
        }

        return results;
    }

    /// <summary>
    /// Identifies the AB=CD family variant for scoring.
    /// </summary>
    internal enum AbcdFamily
    {
        Standard,
        SeaPony,
        SeaHorse,
        Dragon
    }

    /// <summary>
    /// Scores an AB=CD family pattern based on BC/AB and CD/AB ratios.
    /// </summary>
    internal static double ScoreAbcd(double bcAbRatio, double cdAbRatio, AbcdFamily family)
    {
        double bcMin, bcMax, cdMin, cdMax;
        switch (family)
        {
            case AbcdFamily.SeaPony:
                // Sea Pony: shallow retracement, long extension
                bcMin = 0.382; bcMax = 0.500;
                cdMin = 1.618; cdMax = 2.618;
                break;
            case AbcdFamily.SeaHorse:
                // Sea Horse: same shallow retracement, same extension range
                // Differentiated from Sea Pony by stronger momentum continuation context
                bcMin = 0.382; bcMax = 0.500;
                cdMin = 1.618; cdMax = 2.618;
                break;
            case AbcdFamily.Dragon:
                // Dragon (double bottom/top): hump retracement 0.382-0.500,
                // second foot returns near first foot level (0.680-1.000)
                bcMin = 0.382; bcMax = 0.500;
                cdMin = 0.680; cdMax = 1.000;
                break;
            default:
                // Standard AB=CD (including 1.272 and 1.618 alternates)
                bcMin = 0.382; bcMax = 0.886;
                cdMin = 0.886; cdMax = 1.618;
                break;
        }

        double bcScore = ScoreRatio(bcAbRatio, bcMin, bcMax, false);
        double cdScore = ScoreRatio(cdAbRatio, cdMin, cdMax, false);

        if (bcScore == 0 || cdScore == 0)
            return 0;

        // For AB=CD family, the CD/AB ratio is the most important factor
        return (bcScore * 0.35) + (cdScore * 0.65);
    }

    /// <summary>
    /// Detects 5-0 patterns using a dedicated 5-point sliding window (0-X-A-B-C).
    /// The 5-0 pattern captures the first pullback after a Shark completion.
    /// AB extends 1.130-1.618 of XA, BC extends 1.618-2.240 of AB, PRZ at BC × 0.500.
    /// </summary>
    internal static List<HarmonicPatternResult> FindFiveZeroPatterns(
        List<PivotPoint> pivots,
        IReadOnlyList<CandleData>? candles = null)
    {
        var results = new List<HarmonicPatternResult>();

        // Build alternating pivot list
        var alternatingPivots = new List<PivotPoint>();
        foreach (var pivot in pivots)
        {
            if (alternatingPivots.Count == 0)
            {
                alternatingPivots.Add(pivot);
            }
            else
            {
                var prev = alternatingPivots[^1];
                if (pivot.IsHigh != prev.IsHigh)
                {
                    alternatingPivots.Add(pivot);
                }
                else
                {
                    if ((pivot.IsHigh && pivot.Price > prev.Price) ||
                        (!pivot.IsHigh && pivot.Price < prev.Price))
                    {
                        alternatingPivots[^1] = pivot;
                    }
                }
            }
        }

        int count = alternatingPivots.Count;

        // 5-point sliding window: 0, X, A, B, C
        for (int i = 0; i <= count - 5; i++)
        {
            var p0 = alternatingPivots[i];     // Point 0
            var pX = alternatingPivots[i + 1]; // Point X
            var pA = alternatingPivots[i + 2]; // Point A
            var pB = alternatingPivots[i + 3]; // Point B
            var pC = alternatingPivots[i + 4]; // Point C (PRZ)

            // Validate alternation
            if (p0.IsHigh == pX.IsHigh || pX.IsHigh == pA.IsHigh ||
                pA.IsHigh == pB.IsHigh || pB.IsHigh == pC.IsHigh)
                continue;

            decimal xa = Math.Abs(pA.Price - pX.Price);
            if (xa == 0) continue;
            decimal ab = Math.Abs(pB.Price - pA.Price);
            if (ab == 0) continue;
            decimal bc = Math.Abs(pC.Price - pB.Price);

            // AB/XA extension: 1.130 - 1.618
            double abXaRatio = (double)(ab / xa);
            double abXaScore = ScoreRatio(abXaRatio, 1.130, 1.618, false);
            if (abXaScore == 0) continue;

            // BC/AB extension: 1.618 - 2.240
            double bcAbRatio = (double)(bc / ab);
            double bcAbScore = ScoreRatio(bcAbRatio, 1.618, 2.240, false);
            if (bcAbScore == 0) continue;

            // D point (PRZ) = BC × 0.500 retracement
            // Calculate the actual D price at 0.500 of BC
            bool isBullish = pC.Price < pB.Price; // C below B = bullish PRZ
            decimal przPrice = pB.Price + (pC.Price - pB.Price) * 0.5m;

            double score = (abXaScore * 0.40) + (bcAbScore * 0.60);
            if (score < ChartConstants.HarmonicMinConfidence)
                continue;

            // Apply multiscale bonus
            int span = pC.Index - p0.Index;
            double multiscaleBonus = span > 1
                ? Math.Log2(span) * ChartConstants.HarmonicMultiscaleWeight
                : 0;
            double finalScore = Math.Min(1.0, score + multiscaleBonus);

            // Formation validation
            if (candles != null)
            {
                if (!Validation.PatternFormationValidator.ValidateMinBars(
                        span, ChartConstants.FormationMinBarsHarmonic))
                    continue;
            }

            // PRZ zone around the 0.500 retracement level
            decimal expansion = xa * ChartConstants.HarmonicPrzExpansionPercent / 100m;
            if (expansion < 0.01m)
                expansion = Math.Abs(przPrice) * 0.01m;
            decimal przLow = przPrice - expansion;
            decimal przHigh = przPrice + expansion;
            if (przLow > przHigh)
                (przLow, przHigh) = (przHigh, przLow);

            // Map to XABCD result: X=0, A=X, B=A, C=B, D=C
            results.Add(new HarmonicPatternResult(
                HarmonicPatternType.FiveZero, p0, pX, pA, pB, pC,
                finalScore, przLow, przHigh, isBullish));
        }

        return results;
    }
}
