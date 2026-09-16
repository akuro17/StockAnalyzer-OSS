using System;

namespace StockAnalyzer.Core.Models.Portfolio;

public enum PortfolioSelectionType
{
    Aggregate,
    SingleProfile
}

public sealed record PortfolioSelectedMessage(
    Guid SelectionId,
    Portfolio Portfolio,
    PortfolioSelectionType SelectionType,
    DateTimeOffset GeneratedAtUtc
);
