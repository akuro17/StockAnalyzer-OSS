using System.Collections.Generic;
using StockAnalyzer.Core.Models.HarmonicPattern;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition based on harmonic chart patterns (Gartley, Bat, Butterfly, Crab).
/// Filters stocks by their most recently detected harmonic formation.
/// </summary>
public class HarmonicPatternCondition : IScreeningCondition
{
    private readonly HarmonicPatternType _targetType;
    private readonly decimal _zigzagThreshold;

    /// <summary>
    /// Creates a new harmonic pattern screening condition.
    /// </summary>
    /// <param name="targetType">The harmonic pattern type to screen for.</param>
    /// <param name="zigzagThreshold">
    /// The minimum percentage swing to constitute a pivot point (default: 3.0%).
    /// </param>
    public HarmonicPatternCondition(
        HarmonicPatternType targetType,
        decimal zigzagThreshold = ChartConstants.DefaultHarmonicZigZagThreshold)
    {
        _targetType = targetType;
        _zigzagThreshold = zigzagThreshold;
    }

    public override string ToString()
    {
        return $"Harmonic Pattern ({_targetType})";
    }

    /// <summary>
    /// Checks if the latest detected harmonic pattern matches the target type.
    /// Uses the pure C# <see cref="HarmonicPatternDetector"/> (no Python dependency).
    /// </summary>
    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < ChartConstants.HarmonicMinPivotCount * 2)
            return false;

        var latest = HarmonicPatternDetector.DetectLatest(candles, _zigzagThreshold);
        if (latest == null)
            return false;

        return latest.PatternType == _targetType;
    }
}
