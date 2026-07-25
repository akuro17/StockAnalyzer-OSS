using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Core.Models.ElliottWave;

/// <summary>
/// Represents a detected Elliott Wave pattern (impulse 5-wave or corrective 3-wave)
/// with pivot points, confidence score, and current phase identification.
/// </summary>
public class ElliottWaveResult
{
    /// <summary>True if this is an impulse wave (5 waves: 1-2-3-4-5), false for corrective (3 waves: A-B-C).</summary>
    public bool IsImpulse { get; }

    /// <summary>True if the pattern is bullish (upward impulse), false if bearish.</summary>
    public bool IsBullish { get; }

    /// <summary>
    /// The ordered pivot points defining the wave structure.
    /// For impulse: 6 points (start, end of W1, end of W2, end of W3, end of W4, end of W5).
    /// For corrective: 4 points (start, end of A, end of B, end of C).
    /// </summary>
    public IReadOnlyList<PivotPoint> WavePoints { get; }

    /// <summary>
    /// The confidence score (0.0 to 1.0) based on how closely the wave ratios
    /// match ideal Fibonacci proportions.
    /// </summary>
    public double ConfidenceScore { get; }

    /// <summary>
    /// The detected current phase of the wave pattern.
    /// Used for screening (e.g., Wave3Start for entry signals).
    /// </summary>
    public ElliottWavePhase CurrentPhase { get; }

    /// <summary>The span of candles covered by this pattern.</summary>
    public int Span => WavePoints.Count >= 2
        ? WavePoints[WavePoints.Count - 1].Index - WavePoints[0].Index
        : 0;

    /// <summary>The start index of the pattern in the candle array.</summary>
    public int StartIndex => WavePoints.Count > 0 ? WavePoints[0].Index : 0;

    /// <summary>The end index of the pattern in the candle array.</summary>
    public int EndIndex => WavePoints.Count > 0 ? WavePoints[WavePoints.Count - 1].Index : 0;

    public ElliottWaveResult(
        bool isImpulse,
        bool isBullish,
        IReadOnlyList<PivotPoint> wavePoints,
        double confidenceScore,
        ElliottWavePhase currentPhase)
    {
        IsImpulse = isImpulse;
        IsBullish = isBullish;
        WavePoints = wavePoints;
        ConfidenceScore = Math.Clamp(confidenceScore, 0.0, 1.0);
        CurrentPhase = currentPhase;
    }

    public override string ToString()
    {
        string type = IsImpulse ? "Impulse" : "Corrective";
        string direction = IsBullish ? "Bullish" : "Bearish";
        return $"{type} {direction} [{StartIndex}-{EndIndex}] Phase={CurrentPhase} Confidence={ConfidenceScore:F2}";
    }
}
