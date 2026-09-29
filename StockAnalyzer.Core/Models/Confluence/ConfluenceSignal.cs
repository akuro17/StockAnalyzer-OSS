using StockAnalyzer.Core.Models.DivergenceCross;

namespace StockAnalyzer.Core.Models.Confluence;

/// <summary>
/// A standardized signal record used for confluence and orchestration.
/// </summary>
public readonly record struct ConfluenceSignal(
    int Index,
    SignalType IndicatorType,
    SignalDirection Direction,
    DecorrelationGroup Group,
    double Strength = 1.0,
    double Weight = 1.0
);
