namespace StockAnalyzer.Core.Models;

public record struct MultiWaveSignal(
    MultiWavePatternType PatternType,
    bool IsBullish,
    int TriggerIndex,
    decimal TriggerPrice,
    decimal SignalLevel,
    double ConfidenceScore = 0.0,
    decimal InvalidationPrice = 0m,
    int StartWaveIndex = -1,
    int EndWaveIndex = -1,
    decimal TargetPriceMin = 0m,
    decimal TargetPriceMax = 0m,
    bool IsInvalidated = false,
    int InvalidationIndex = -1,
    string? DisplayPatternName = null,
    string? DisplayPriceRange = null,
    double WaveStrengthScore = 0.0,
    double PullbackScore = 0.0,
    double BreakoutScore = 0.0
);
