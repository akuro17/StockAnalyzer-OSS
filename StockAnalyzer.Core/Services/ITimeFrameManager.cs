using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Manages multi-timeframe candle data with caching and local aggregation.
/// Provides lazy loading, LRU cache, and cancellation support for
/// efficient timeframe switching.
/// </summary>
public interface ITimeFrameManager
{
    /// <summary>
    /// Gets candle data for the specified symbol and timeframe.
    /// Uses cached data when available. For weekly/monthly timeframes,
    /// aggregates from cached daily data when possible.
    /// </summary>
    /// <param name="symbol">Stock symbol.</param>
    /// <param name="timeFrame">Target timeframe.</param>
    /// <param name="count">Number of candles to request from the data source.</param>
    /// <param name="cancellationToken">Cancellation token for aborting the operation.</param>
    /// <returns>Candle data for the requested timeframe.</returns>
    Task<IReadOnlyList<CandleData>> GetCandlesAsync(
        string symbol,
        TimeFrame timeFrame,
        int count = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cached data. If symbol is null, clears all cache entries.
    /// </summary>
    /// <param name="symbol">Optional symbol to invalidate. Null clears everything.</param>
    void InvalidateCache(string? symbol = null);
}
