namespace StockAnalyzer.Core.Models;

/// <summary>
/// Represents a single directional wave (a sequence of blocks moving in the same direction).
/// Used by MultiWavePatternEngine to extract context from Point&Figure or Renko blocks.
/// </summary>
public readonly record struct WaveNode(
    bool IsBullish,
    int StartIndex,
    int EndIndex,
    decimal High,
    decimal Low,
    int BlockCount = 0,
    double PurityScore = 1.0,
    double MomentumScore = 0.0
);
