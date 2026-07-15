using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Portfolio;

public readonly record struct AllocationEntry(
    string Category,
    decimal MarketValue,
    decimal Percentage
);

public readonly record struct AllocationResult(
    IReadOnlyList<AllocationEntry> SectorAllocations,
    IReadOnlyList<AllocationEntry> AssetAllocations,
    decimal TotalValue
);
