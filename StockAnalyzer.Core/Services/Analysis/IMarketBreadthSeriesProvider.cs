using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Resolves a workspace ticker group, loads historical candles for its members,
/// and computes a full-history <see cref="MarketBreadthResult"/> for charting.
/// </summary>
public interface IMarketBreadthSeriesProvider
{
    /// <summary>
    /// Computes or retrieves a cached market breadth series for <paramref name="groupId"/>
    /// at <paramref name="timeFrame"/> using <paramref name="parameters"/>.
    /// Returns <c>null</c> when the group has no members, exceeds the symbol limit,
    /// or encounters a non-cancellation load or calculation failure.
    /// </summary>
    Task<MarketBreadthResult?> GetAsync(
        Guid groupId,
        TimeFrame timeFrame,
        MarketBreadthParameters parameters,
        CancellationToken ct = default);

    /// <summary>
    /// Clears all cached breadth calculation results (for example after market data sync).
    /// </summary>
    void ClearCache();
}
