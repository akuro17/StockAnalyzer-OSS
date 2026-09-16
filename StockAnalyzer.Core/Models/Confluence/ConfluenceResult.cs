namespace StockAnalyzer.Core.Models.Confluence;

/// <summary>
/// The result of a signal orchestration operation.
/// </summary>
public readonly record struct ConfluenceResult(
    int Index,
    int Score, // 0-100
    SignalDirection FinalDirection,
    int ConfluenceCount,
    string? Reason = null
);
