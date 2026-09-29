using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Validation;

namespace StockAnalyzer.Core.Models.ElliottWave;

/// <summary>
/// Detects Elliott Wave patterns (impulse 5-wave and corrective 3-wave) from candle data.
/// Uses ZigZag-based pivot extraction, 3 absolute rule validation, Fibonacci ratio scoring,
/// and optional RSI divergence confluence verification.
/// Reuses <see cref="MarketStructureDetector.ExtractPivots"/> for pivot extraction
/// and <see cref="DivergenceCrossDetector"/> for confluence checks.
/// </summary>
public static class ElliottWaveDetector
{
    /// <summary>
    /// Detects all valid Elliott Wave patterns in the given candle data using multi-scale analysis.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="zigzagThresholds">Optional multi-scale thresholds. If null, uses defaults from ChartConstants.</param>
    /// <returns>A list of detected Elliott Wave patterns, ordered by confidence (descending).</returns>
    public static IReadOnlyList<ElliottWaveResult> Detect(
        IReadOnlyList<CandleData> candles,
        decimal[]? zigzagThresholds = null)
    {
        if (candles == null || candles.Count < ChartConstants.ElliottMinCandleCount)
            return Array.Empty<ElliottWaveResult>();

        var thresholds = zigzagThresholds ?? ChartConstants.ElliottMultiScaleThresholds;
        var allResults = new List<ElliottWaveResult>();

        foreach (var threshold in thresholds)
        {
            var pivots = MarketStructureDetector.ExtractPivots(candles, threshold);
            if (pivots.Count < ChartConstants.ElliottMinPivotCountImpulse)
                continue;

            // Try impulse waves (6 consecutive pivots = 5 waves)
            var impulseResults = FindImpulseWaves(pivots, candles);
            allResults.AddRange(impulseResults);

            // Try corrective waves (4 consecutive pivots = 3 waves A-B-C)
            var correctiveResults = FindCorrectiveWaves(pivots, candles);
            allResults.AddRange(correctiveResults);
        }

        if (allResults.Count == 0)
            return Array.Empty<ElliottWaveResult>();

        // Apply multiscale weight and filter overlapping patterns
        return FilterOverlappingPatterns(allResults);
    }

    /// <summary>
    /// Detects the most recent (highest-confidence) Elliott Wave pattern.
    /// Useful for screening where only the latest state matters.
    /// </summary>
    public static ElliottWaveResult? DetectLatest(
        IReadOnlyList<CandleData> candles,
        decimal zigzagThreshold = ChartConstants.DefaultElliottZigZagThreshold)
    {
        var results = Detect(candles, new[] { zigzagThreshold });
        return results.Count > 0 ? results[0] : null;
    }

    /// <summary>
    /// Finds all valid impulse wave patterns (5 waves: 1-2-3-4-5) from pivot sequences.
    /// An impulse wave requires 6 consecutive pivot points forming alternating highs and lows.
    /// </summary>
    internal static List<ElliottWaveResult> FindImpulseWaves(
        IReadOnlyList<PivotPoint> pivots,
        IReadOnlyList<CandleData> candles)
    {
        var results = new List<ElliottWaveResult>();

        if (pivots.Count < ChartConstants.ElliottMinPivotCountImpulse)
            return results;

        // Slide a window of 6 pivots
        for (int i = 0; i <= pivots.Count - ChartConstants.ElliottMinPivotCountImpulse; i++)
        {
            var window = new PivotPoint[ChartConstants.ElliottMinPivotCountImpulse];
            for (int j = 0; j < ChartConstants.ElliottMinPivotCountImpulse; j++)
                window[j] = pivots[i + j];

            // Check alternation (must alternate between high and low)
            if (!IsAlternating(window))
                continue;

            // Determine direction: bullish starts with Low, bearish starts with High
            bool isBullish = !window[0].IsHigh;

            // Validate the 3 absolute rules
            if (!ValidateImpulseRules(window, isBullish))
                continue;

            // Score based on Fibonacci ratios
            double score = ScoreImpulseWave(window, isBullish);
            if (score < ChartConstants.ElliottMinConfidence)
                continue;

            // Apply multiscale weight (prefer larger patterns)
            int span = window[5].Index - window[0].Index;
            if (span > 1)
            {
                score += Math.Log2(span) * ChartConstants.ElliottMultiscaleWeight;
                score = Math.Min(score, 1.0);
            }

            // Formation Process Validation (FR-60-7-01):
            // Skip patterns that lack sufficient time or temporal symmetry.
            if (!PatternFormationValidator.ValidateMinBars(span, ChartConstants.FormationMinBarsElliott))
                continue;

            var legDurations = new List<int>
            {
                window[1].Index - window[0].Index,
                window[2].Index - window[1].Index,
                window[3].Index - window[2].Index,
                window[4].Index - window[3].Index,
                window[5].Index - window[4].Index
            };
            if (!PatternFormationValidator.ValidateTimeSymmetry(
                    legDurations, ChartConstants.FormationMaxTimeRatio))
                continue;

            // Determine current phase
            var phase = DetermineImpulsePhase(window, candles, isBullish);

            var wavePoints = new List<PivotPoint>(window);
            results.Add(new ElliottWaveResult(
                isImpulse: true,
                isBullish: isBullish,
                wavePoints: wavePoints,
                confidenceScore: score,
                currentPhase: phase));
        }

        return results;
    }

    /// <summary>
    /// Finds all valid corrective wave patterns (3 waves: A-B-C) from pivot sequences.
    /// A corrective wave requires 4 consecutive pivot points.
    /// </summary>
    internal static List<ElliottWaveResult> FindCorrectiveWaves(
        IReadOnlyList<PivotPoint> pivots,
        IReadOnlyList<CandleData> candles)
    {
        var results = new List<ElliottWaveResult>();

        if (pivots.Count < ChartConstants.ElliottMinPivotCountCorrective)
            return results;

        for (int i = 0; i <= pivots.Count - ChartConstants.ElliottMinPivotCountCorrective; i++)
        {
            var window = new PivotPoint[ChartConstants.ElliottMinPivotCountCorrective];
            for (int j = 0; j < ChartConstants.ElliottMinPivotCountCorrective; j++)
                window[j] = pivots[i + j];

            if (!IsAlternating(window))
                continue;

            // Corrective bearish: starts from High (A goes down)
            // Corrective bullish: starts from Low (A goes up)
            bool isBullish = !window[0].IsHigh;

            // Validate corrective rules
            if (!ValidateCorrectiveRules(window, isBullish))
                continue;

            double score = ScoreCorrectiveWave(window, isBullish);
            if (score < ChartConstants.ElliottMinConfidence)
                continue;

            int span = window[3].Index - window[0].Index;
            if (span > 1)
            {
                score += Math.Log2(span) * ChartConstants.ElliottMultiscaleWeight;
                score = Math.Min(score, 1.0);
            }

            // Formation Process Validation (FR-60-7-01):
            // Use a reduced minimum for corrective waves (3 waves vs 5)
            int correctiveMinBars = ChartConstants.FormationMinBarsElliott * 3 / 5;
            if (!PatternFormationValidator.ValidateMinBars(span, correctiveMinBars))
                continue;

            var legDurations = new List<int>
            {
                window[1].Index - window[0].Index,
                window[2].Index - window[1].Index,
                window[3].Index - window[2].Index
            };
            if (!PatternFormationValidator.ValidateTimeSymmetry(
                    legDurations, ChartConstants.FormationMaxTimeRatio))
                continue;

            var phase = DetermineCorrectivePhase(window);

            var wavePoints = new List<PivotPoint>(window);
            results.Add(new ElliottWaveResult(
                isImpulse: false,
                isBullish: isBullish,
                wavePoints: wavePoints,
                confidenceScore: score,
                currentPhase: phase));
        }

        return results;
    }

    /// <summary>
    /// Validates that pivots alternate between high and low (or vice versa).
    /// </summary>
    internal static bool IsAlternating(PivotPoint[] pivots)
    {
        for (int i = 1; i < pivots.Length; i++)
        {
            if (pivots[i].IsHigh == pivots[i - 1].IsHigh)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Validates the 3 absolute rules of Elliott Wave impulse patterns:
    /// Rule 1: Wave 2 must not retrace beyond the starting point of Wave 1.
    /// Rule 2: Wave 3 must not be the shortest among Waves 1, 3, and 5.
    /// Rule 3: Wave 4 must not overlap the price territory of Wave 1.
    /// </summary>
    /// <param name="pivots">6 pivot points: [0]=W1 start, [1]=W1 end/W2 start, [2]=W2 end/W3 start,
    /// [3]=W3 end/W4 start, [4]=W4 end/W5 start, [5]=W5 end.</param>
    /// <param name="isBullish">True if the impulse is upward (bullish).</param>
    internal static bool ValidateImpulseRules(PivotPoint[] pivots, bool isBullish)
    {
        // Points: p0=start, p1=end W1, p2=end W2, p3=end W3, p4=end W4, p5=end W5
        decimal p0 = pivots[0].Price;
        decimal p1 = pivots[1].Price;
        decimal p2 = pivots[2].Price;
        decimal p3 = pivots[3].Price;
        decimal p4 = pivots[4].Price;
        decimal p5 = pivots[5].Price;

        if (isBullish)
        {
            // Bullish impulse: p0(Low) -> p1(High) -> p2(Low) -> p3(High) -> p4(Low) -> p5(High)

            // Rule 1: Wave 2 cannot retrace below Wave 1 start
            if (p2 <= p0)
                return false;

            // Rule 3: Wave 4 cannot overlap Wave 1 price territory (p4 must stay above p1)
            // Use robust tolerance: allow minor wick violations (within ElliottOverlapTolerance)
            decimal w1Range = Math.Abs(p1 - p0);
            if (w1Range > 0 && (p1 - p4) / w1Range > ChartConstants.ElliottOverlapTolerance)
                return false;

            // Rule 2: Wave 3 cannot be the shortest
            decimal w1Len = p1 - p0;
            decimal w3Len = p3 - p2;
            decimal w5Len = p5 - p4;
            if (w3Len <= w1Len && w3Len <= w5Len)
                return false;
        }
        else
        {
            // Bearish impulse: p0(High) -> p1(Low) -> p2(High) -> p3(Low) -> p4(High) -> p5(Low)

            // Rule 1: Wave 2 cannot retrace above Wave 1 start
            if (p2 >= p0)
                return false;

            // Rule 3: Wave 4 cannot overlap Wave 1 price territory (p4 must stay below p1)
            decimal w1Range = Math.Abs(p0 - p1);
            if (w1Range > 0 && (p4 - p1) / w1Range > ChartConstants.ElliottOverlapTolerance)
                return false;

            // Rule 2: Wave 3 cannot be the shortest
            decimal w1Len = p0 - p1;
            decimal w3Len = p2 - p3;
            decimal w5Len = p4 - p5;
            if (w3Len <= w1Len && w3Len <= w5Len)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates corrective wave rules (A-B-C):
    /// Wave B must not retrace beyond Wave A start.
    /// Wave C should extend beyond Wave A end.
    /// </summary>
    internal static bool ValidateCorrectiveRules(PivotPoint[] pivots, bool isBullish)
    {
        decimal p0 = pivots[0].Price; // start
        decimal p1 = pivots[1].Price; // end A
        decimal p2 = pivots[2].Price; // end B
        decimal p3 = pivots[3].Price; // end C

        if (isBullish)
        {
            // Bullish corrective: p0(Low)->p1(High)->p2(Low)->p3(High)
            // Wave B should not retrace below Wave A start
            if (p2 <= p0)
                return false;
        }
        else
        {
            // Bearish corrective: p0(High)->p1(Low)->p2(High)->p3(Low)
            // Wave B should not retrace above Wave A start
            if (p2 >= p0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Scores an impulse wave based on Fibonacci ratio matching.
    /// Ideal ratios: W2 retraces 38.2%-61.8% of W1, W3 extends 161.8% of W1,
    /// W4 retraces 23.6%-50% of W3, W5 equals or extends W1.
    /// </summary>
    internal static double ScoreImpulseWave(PivotPoint[] pivots, bool isBullish)
    {
        decimal p0 = pivots[0].Price;
        decimal p1 = pivots[1].Price;
        decimal p2 = pivots[2].Price;
        decimal p3 = pivots[3].Price;
        decimal p4 = pivots[4].Price;
        decimal p5 = pivots[5].Price;

        decimal w1, w2Retrace, w3, w4Retrace, w5;

        if (isBullish)
        {
            w1 = p1 - p0;
            w2Retrace = p1 - p2;
            w3 = p3 - p2;
            w4Retrace = p3 - p4;
            w5 = p5 - p4;
        }
        else
        {
            w1 = p0 - p1;
            w2Retrace = p2 - p1;
            w3 = p2 - p3;
            w4Retrace = p4 - p3;
            w5 = p4 - p5;
        }

        // Avoid division by zero
        if (w1 <= 0 || w3 <= 0)
            return 0;

        // Fibonacci ratios to score
        double w2Ratio = (double)(w2Retrace / w1);   // Ideal: 0.382 - 0.618
        double w3Ratio = (double)(w3 / w1);           // Ideal: 1.618 (extension)
        double w4Ratio = (double)(w4Retrace / w3);    // Ideal: 0.236 - 0.500
        double w5Ratio = (double)(w5 / w1);           // Ideal: 0.618 - 1.000 (equality)

        // Score each ratio using linear deviation scoring
        double s1 = ScoreRatio(w2Ratio, 0.382, 0.618);
        double s2 = ScoreRatio(w3Ratio, 1.272, 1.618); // W3 extension range
        double s3 = ScoreRatio(w4Ratio, 0.236, 0.500);
        double s4 = ScoreRatio(w5Ratio, 0.618, 1.000);

        // If any single ratio is completely outside tolerance, return 0
        int zeroCount = (s1 == 0 ? 1 : 0) + (s2 == 0 ? 1 : 0) + (s3 == 0 ? 1 : 0) + (s4 == 0 ? 1 : 0);
        if (zeroCount >= 2)
            return 0;

        // Weighted average: W3 extension is the most important
        return s1 * 0.20 + s2 * 0.35 + s3 * 0.20 + s4 * 0.25;
    }

    /// <summary>
    /// Scores a corrective wave based on Fibonacci ratio matching.
    /// Ideal ratios: B retraces 38.2%-78.6% of A, C extends 61.8%-161.8% of A.
    /// </summary>
    internal static double ScoreCorrectiveWave(PivotPoint[] pivots, bool isBullish)
    {
        decimal p0 = pivots[0].Price;
        decimal p1 = pivots[1].Price;
        decimal p2 = pivots[2].Price;
        decimal p3 = pivots[3].Price;

        decimal wA, wBRetrace, wC;

        if (isBullish)
        {
            wA = p1 - p0;
            wBRetrace = p1 - p2;
            wC = p3 - p2;
        }
        else
        {
            wA = p0 - p1;
            wBRetrace = p2 - p1;
            wC = p2 - p3;
        }

        if (wA <= 0)
            return 0;

        double bRatio = (double)(wBRetrace / wA);  // Ideal: 0.382 - 0.786
        double cRatio = (double)(wC / wA);          // Ideal: 0.618 - 1.618

        double s1 = ScoreRatio(bRatio, 0.382, 0.786);
        double s2 = ScoreRatio(cRatio, 0.618, 1.618);

        if (s1 == 0 && s2 == 0)
            return 0;

        return s1 * 0.40 + s2 * 0.60;
    }

    /// <summary>
    /// Scores a single ratio against its expected range, returning 0.0 to 1.0.
    /// Uses the Elliott-specific tolerance from ChartConstants.
    /// Pattern inspired by <see cref="Models.HarmonicPattern.HarmonicPatternDetector.ScoreRatio"/>.
    /// </summary>
    internal static double ScoreRatio(double actual, double expectedMin, double expectedMax)
    {
        double tolerance = ChartConstants.ElliottFibonacciTolerance;

        double lowerBound = expectedMin - tolerance;
        double upperBound = expectedMax + tolerance;

        if (actual < lowerBound || actual > upperBound)
            return 0;

        double mid = (expectedMin + expectedMax) / 2.0;
        double halfRange = (upperBound - lowerBound) / 2.0;

        if (halfRange <= 0)
            return 1.0;

        double deviation = Math.Abs(actual - mid) / halfRange;
        return Math.Max(0, 1.0 - deviation);
    }

    /// <summary>
    /// Determines the current phase of a detected impulse wave pattern
    /// by checking the last pivot position relative to the candle data end,
    /// and optionally verifying RSI divergence for Wave 5.
    /// </summary>
    private static ElliottWavePhase DetermineImpulsePhase(
        PivotPoint[] pivots, IReadOnlyList<CandleData> candles, bool isBullish)
    {
        int lastCandleIndex = candles.Count - 1;

        // Wave 5 is complete - check for divergence
        if (pivots[5].Index <= lastCandleIndex)
        {
            if (HasWave5Divergence(pivots, candles, isBullish))
                return ElliottWavePhase.Wave5Divergence;
            return ElliottWavePhase.Wave5;
        }

        // Check which wave is still forming
        if (pivots[4].Index <= lastCandleIndex)
            return ElliottWavePhase.Wave5;
        if (pivots[3].Index <= lastCandleIndex)
            return ElliottWavePhase.Wave4;
        if (pivots[2].Index <= lastCandleIndex)
            return ElliottWavePhase.Wave3;
        if (pivots[1].Index <= lastCandleIndex)
            return ElliottWavePhase.Wave2;

        return ElliottWavePhase.Wave1;
    }

    /// <summary>
    /// Determines the current phase of a detected corrective wave pattern.
    /// </summary>
    private static ElliottWavePhase DetermineCorrectivePhase(PivotPoint[] pivots)
    {
        // Since we already have all 4 pivots validated, the phase is WaveC completed
        return ElliottWavePhase.WaveC;
    }

    /// <summary>
    /// Checks if Wave 5 exhibits RSI divergence (price makes new extreme but RSI does not).
    /// Uses <see cref="DivergenceCrossDetector"/> for robust detection.
    /// </summary>
    private static bool HasWave5Divergence(
        PivotPoint[] pivots, IReadOnlyList<CandleData> candles, bool isBullish)
    {
        // Only check if we have enough data around the wave 3 and wave 5 endpoints
        int w3EndIdx = pivots[3].Index;
        int w5EndIdx = pivots[5].Index;

        if (w3EndIdx >= candles.Count || w5EndIdx >= candles.Count)
            return false;

        // Simple RSI divergence check:
        // Bullish impulse: W5 High > W3 High but closing momentum weakens
        // Bearish impulse: W5 Low < W3 Low but closing momentum weakens
        // Use close-based momentum as a simple proxy (proper RSI would require the indicator pipeline)
        if (isBullish)
        {
            // Wave 5 high should exceed Wave 3 high for a valid impulse
            if (pivots[5].Price <= pivots[3].Price)
                return false;

            // Check if the rate of change at Wave 5 is less than Wave 3
            // (simple momentum-based divergence proxy)
            decimal w3Momentum = CalculateLocalMomentum(candles, w3EndIdx);
            decimal w5Momentum = CalculateLocalMomentum(candles, w5EndIdx);

            return w5Momentum < w3Momentum;
        }
        else
        {
            if (pivots[5].Price >= pivots[3].Price)
                return false;

            decimal w3Momentum = CalculateLocalMomentum(candles, w3EndIdx);
            decimal w5Momentum = CalculateLocalMomentum(candles, w5EndIdx);

            // For bearish, momentum should be less negative at W5 (divergence)
            return w5Momentum > w3Momentum;
        }
    }

    /// <summary>
    /// Calculates simple local momentum (rate of change over a small window) around a given index.
    /// Used as a lightweight proxy for RSI divergence detection.
    /// </summary>
    private static decimal CalculateLocalMomentum(IReadOnlyList<CandleData> candles, int index)
    {
        int lookback = Math.Min(ChartConstants.ElliottMomentumLookback, index);
        if (lookback <= 0)
            return 0;

        decimal startPrice = candles[index - lookback].Close;
        decimal endPrice = candles[index].Close;

        if (startPrice == 0)
            return 0;

        return (endPrice - startPrice) / startPrice;
    }

    /// <summary>
    /// Filters overlapping Elliott Wave patterns using greedy selection.
    /// Prefers higher confidence and larger span patterns.
    /// Pattern inspired by <see cref="Models.HarmonicPattern.HarmonicPatternDetector"/>.
    /// </summary>
    private static IReadOnlyList<ElliottWaveResult> FilterOverlappingPatterns(List<ElliottWaveResult> patterns)
    {
        if (patterns.Count <= 1)
            return patterns;

        // Sort: High confidence first, then larger span, then confidence descending
        patterns.Sort((a, b) =>
        {
            bool aIsHigh = a.ConfidenceScore >= 0.70;
            bool bIsHigh = b.ConfidenceScore >= 0.70;
            if (aIsHigh != bIsHigh)
                return bIsHigh.CompareTo(aIsHigh);

            int cmp = b.Span.CompareTo(a.Span);
            if (cmp != 0) return cmp;

            return b.ConfidenceScore.CompareTo(a.ConfidenceScore);
        });

        var kept = new List<ElliottWaveResult>();

        foreach (var pattern in patterns)
        {
            int start = pattern.StartIndex;
            int end = pattern.EndIndex;
            int span = end - start;

            // Check if enclosed by any kept pattern
            bool isEnclosed = false;
            foreach (var existing in kept)
            {
                if (existing.StartIndex <= start && existing.EndIndex >= end &&
                    !(existing.StartIndex == start && existing.EndIndex == end))
                {
                    isEnclosed = true;
                    break;
                }
            }
            if (isEnclosed) continue;

            // Check partial overlap (>50% mutual)
            bool partialOverlap = false;
            foreach (var existing in kept)
            {
                int overlapStart = Math.Max(start, existing.StartIndex);
                int overlapEnd = Math.Min(end, existing.EndIndex);
                if (overlapEnd > overlapStart)
                {
                    int overlapLen = overlapEnd - overlapStart;
                    int existingSpan = existing.Span;
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

            // Remove enclosed patterns
            kept.RemoveAll(existing =>
                start <= existing.StartIndex && end >= existing.EndIndex &&
                !(start == existing.StartIndex && end == existing.EndIndex));

            kept.Add(pattern);
        }

        return kept;
    }
}
