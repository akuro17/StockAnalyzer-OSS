using System.Collections.Generic;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Portfolio;

public readonly record struct PortfolioMetrics(
    decimal TotalValue,
    decimal TotalUnrealizedPL,
    decimal TotalRealizedPL,
    decimal CashBalance,
    decimal CashRatio
);

public readonly record struct PortfolioEvaluationResult(
    PortfolioMetrics Metrics,
    IReadOnlyDictionary<string, decimal> PositionValues,
    IReadOnlyDictionary<string, decimal> PositionPLs
);
