using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Validation outcome for manual Impulse 1-2-3-4-5 Elliott Wave pivots.
/// Segment mappings when violated:
/// R1 -&gt; segment P1-P2 (terminal point 2),
/// R2 -&gt; segment P2-P3 (terminal point 3),
/// R3 -&gt; segment P3-P4 (terminal point 4).
/// </summary>
public readonly record struct ElliottWaveValidationResult(
    bool IsEvaluated,
    bool Rule1Violated,
    bool Rule2Violated,
    bool Rule3Violated,
    string? RuleStatus)
{
    public bool HasAnyViolation => Rule1Violated || Rule2Violated || Rule3Violated;

    public bool IsSegmentViolated(int segmentStartIndex) => segmentStartIndex switch
    {
        1 => Rule1Violated,
        2 => Rule2Violated,
        3 => Rule3Violated,
        _ => false
    };

    public bool IsTerminalPointViolated(int pointIndex) => pointIndex switch
    {
        2 => Rule1Violated,
        3 => Rule2Violated,
        4 => Rule3Violated,
        _ => false
    };
}

/// <summary>
/// Pure price-only rule validation and calculated metrics for manual Elliott Wave drawings.
/// Evaluates only active displayed pivots; <see cref="Calculate"/> always returns a fixed 4-element array.
/// </summary>
public static class ElliottWaveValues
{
    public static ElliottWaveValidationResult ValidateImpulse(
        ReadOnlySpan<decimal> activePrices,
        bool isImpulse = true)
    {
        if (!isImpulse || activePrices.Length < 3)
        {
            return new ElliottWaveValidationResult(false, false, false, false, null);
        }

        decimal p0 = activePrices[0];
        decimal p1 = activePrices[1];
        if (p1 == p0)
        {
            return new ElliottWaveValidationResult(false, false, false, false, null);
        }

        bool isUp = p1 > p0;
        decimal p2 = activePrices[2];

        // R1 (3+ points): Wave 2 must not retrace beyond Wave 1 origin (p0).
        bool r1Violated = isUp ? p2 <= p0 : p2 >= p0;

        // R3 (5+ points): Wave 4 must not enter Wave 1 price territory (beyond p1).
        bool r3Violated = false;
        if (activePrices.Length >= 5)
        {
            decimal p4 = activePrices[4];
            r3Violated = isUp ? p4 <= p1 : p4 >= p1;
        }

        // R2 (6 points): Wave 3 must not be strictly shorter than both Wave 1 and Wave 5.
        bool r2Violated = false;
        if (activePrices.Length >= 6)
        {
            decimal len1 = Math.Abs(p1 - p0);
            decimal len3 = Math.Abs(activePrices[3] - p2);
            decimal len5 = Math.Abs(activePrices[5] - activePrices[4]);
            r2Violated = len3 < len1 && len3 < len5;
        }

        string status = BuildRuleStatusString(r1Violated, r2Violated, r3Violated);
        return new ElliottWaveValidationResult(true, r1Violated, r2Violated, r3Violated, status);
    }

    public static DrawingCalculatedValue[] Calculate(
        ReadOnlySpan<decimal> activePrices,
        bool isImpulse,
        IndicatorColor color)
    {
        decimal? wave2Retracement = null;
        decimal? wave3ToWave1Ratio = null;
        decimal? wave4Retracement = null;

        if (isImpulse && activePrices.Length >= 3)
        {
            decimal len1 = Math.Abs(activePrices[1] - activePrices[0]);
            if (len1 != 0m)
            {
                decimal w2Move = Math.Abs(activePrices[2] - activePrices[1]);
                wave2Retracement = (w2Move / len1) * 100m;
            }

            if (activePrices.Length >= 4)
            {
                decimal len3 = Math.Abs(activePrices[3] - activePrices[2]);
                if (len1 != 0m)
                {
                    wave3ToWave1Ratio = len3 / len1;
                }

                if (activePrices.Length >= 5 && len3 != 0m)
                {
                    decimal w4Move = Math.Abs(activePrices[4] - activePrices[3]);
                    wave4Retracement = (w4Move / len3) * 100m;
                }
            }
        }

        ElliottWaveValidationResult validation = ValidateImpulse(activePrices, isImpulse);
        IndicatorColor statusColor = !validation.IsEvaluated
            ? IndicatorColor.Gray
            : validation.HasAnyViolation
                ? IndicatorColor.Bearish
                : IndicatorColor.Bullish;

        return new[]
        {
            new DrawingCalculatedValue(
                "Wave2Retracement",
                "Wave 2 Retracement",
                wave2Retracement,
                wave2Retracement.HasValue
                    ? string.Create(DrawingValueFormat.Culture, $"{wave2Retracement.Value:F2}%")
                    : "-",
                color),
            new DrawingCalculatedValue(
                "Wave3ToWave1Ratio",
                "Wave 3 / Wave 1",
                wave3ToWave1Ratio,
                wave3ToWave1Ratio.HasValue
                    ? string.Create(DrawingValueFormat.Culture, $"{wave3ToWave1Ratio.Value:F2}")
                    : "-",
                color),
            new DrawingCalculatedValue(
                "Wave4Retracement",
                "Wave 4 Retracement",
                wave4Retracement,
                wave4Retracement.HasValue
                    ? string.Create(DrawingValueFormat.Culture, $"{wave4Retracement.Value:F2}%")
                    : "-",
                color),
            new DrawingCalculatedValue(
                "RuleStatus",
                "Rule Status",
                null,
                validation.RuleStatus ?? "-",
                statusColor)
        };
    }

    private static string BuildRuleStatusString(bool r1, bool r2, bool r3)
    {
        if (!r1 && !r2 && !r3)
        {
            return "OK";
        }

        // Direct lookup covers all 7 non-empty subsets in ascending order without allocations.
        return (r1, r2, r3) switch
        {
            (true, false, false) => "R1",
            (false, true, false) => "R2",
            (false, false, true) => "R3",
            (true, true, false) => "R1,R2",
            (true, false, true) => "R1,R3",
            (false, true, true) => "R2,R3",
            (true, true, true) => "R1,R2,R3",
            _ => "OK"
        };
    }
}
