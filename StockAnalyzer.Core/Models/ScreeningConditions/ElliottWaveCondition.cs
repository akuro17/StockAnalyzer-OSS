using System.Collections.Generic;
using StockAnalyzer.Core.Models.ElliottWave;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition based on Elliott Wave pattern detection.
/// Filters stocks by their most recently detected wave pattern phase.
/// Uses the pure C# <see cref="ElliottWaveDetector"/> (no Python dependency).
/// </summary>
public class ElliottWaveCondition : IScreeningCondition
{
    private readonly ElliottWaveConditionType _conditionType;
    private readonly decimal _zigzagThreshold;

    /// <summary>
    /// Creates a new Elliott Wave screening condition.
    /// </summary>
    /// <param name="conditionType">The wave condition type to screen for.</param>
    /// <param name="zigzagThreshold">
    /// The minimum percentage swing to constitute a pivot point (default: 5.0%).
    /// </param>
    public ElliottWaveCondition(
        ElliottWaveConditionType conditionType,
        decimal zigzagThreshold = ChartConstants.DefaultElliottZigZagThreshold)
    {
        _conditionType = conditionType;
        _zigzagThreshold = zigzagThreshold;
    }

    public override string ToString()
    {
        return $"Elliott Wave ({_conditionType})";
    }

    /// <summary>
    /// Checks if the latest detected Elliott Wave pattern matches the target condition.
    /// </summary>
    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < ChartConstants.ElliottMinCandleCount)
            return false;

        var latest = ElliottWaveDetector.DetectLatest(candles, _zigzagThreshold);
        if (latest == null)
            return false;

        return _conditionType switch
        {
            ElliottWaveConditionType.AnyImpulse => latest.IsImpulse,
            ElliottWaveConditionType.AnyCorrective => !latest.IsImpulse,
            ElliottWaveConditionType.Wave3Start =>
                latest.IsImpulse && latest.CurrentPhase == ElliottWavePhase.Wave3Start,
            ElliottWaveConditionType.Wave5Divergence =>
                latest.IsImpulse && latest.CurrentPhase == ElliottWavePhase.Wave5Divergence,
            ElliottWaveConditionType.WaveCComplete =>
                !latest.IsImpulse && latest.CurrentPhase == ElliottWavePhase.WaveC,
            _ => false,
        };
    }
}
