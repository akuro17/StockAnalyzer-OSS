using System;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Core.Models.HarmonicPattern;

/// <summary>
/// Represents a detected harmonic pattern with its XABCD pivot points,
/// pattern classification, confidence score, and Potential Reversal Zone (PRZ).
/// </summary>
public class HarmonicPatternResult
{
    /// <summary>The classified harmonic pattern type.</summary>
    public HarmonicPatternType PatternType { get; }

    /// <summary>The X pivot point (pattern start).</summary>
    public PivotPoint X { get; }

    /// <summary>The A pivot point (first major swing).</summary>
    public PivotPoint A { get; }

    /// <summary>The B pivot point (first retracement).</summary>
    public PivotPoint B { get; }

    /// <summary>The C pivot point (second swing).</summary>
    public PivotPoint C { get; }

    /// <summary>The D pivot point (completion / PRZ).</summary>
    public PivotPoint D { get; }

    /// <summary>
    /// The confidence score (0.0 to 1.0) based on how closely the leg ratios
    /// match the ideal Fibonacci ratios for the detected pattern type.
    /// </summary>
    public double ConfidenceScore { get; }

    /// <summary>The lower bound of the Potential Reversal Zone at D.</summary>
    public decimal PrzLow { get; }

    /// <summary>The upper bound of the Potential Reversal Zone at D.</summary>
    public decimal PrzHigh { get; }

    /// <summary>
    /// True if this is a bullish harmonic pattern (X-A moves up, D is a buying zone).
    /// False if bearish (X-A moves down, D is a selling zone).
    /// </summary>
    public bool IsBullish { get; }

    /// <summary>The span of candles covered by this pattern (D.Index - X.Index).</summary>
    public int Span => D.Index - X.Index;

    public HarmonicPatternResult(
        HarmonicPatternType patternType,
        PivotPoint x,
        PivotPoint a,
        PivotPoint b,
        PivotPoint c,
        PivotPoint d,
        double confidenceScore,
        decimal przLow,
        decimal przHigh,
        bool isBullish)
    {
        PatternType = patternType;
        X = x;
        A = a;
        B = b;
        C = c;
        D = d;
        ConfidenceScore = Math.Clamp(confidenceScore, 0.0, 1.0);
        PrzLow = przLow;
        PrzHigh = przHigh;
        IsBullish = isBullish;
    }

    public override string ToString()
        => $"{PatternType} [{X.Index}-{D.Index}] Confidence={ConfidenceScore:F2} PRZ=[{PrzLow:F2}-{PrzHigh:F2}] {(IsBullish ? "Bullish" : "Bearish")}";
}
