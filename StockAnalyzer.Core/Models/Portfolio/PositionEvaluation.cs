namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// Represents the evaluation result of a specific position, used primarily for UI binding.
/// </summary>
public readonly record struct PositionEvaluation(
    string Symbol,
    decimal Quantity,
    decimal AveragePrice,
    decimal CurrentPrice,
    decimal MarketValue,
    decimal UnrealizedPnL,
    decimal PnLRate,
    bool IsShort,
    string DisplaySymbol = ""
);
