using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Interface for next-generation market data provider (e.g., Parquet/DuckDB based).
/// Supports high-performance asynchronous data loading and screening.
/// </summary>
public interface IMarketDataProvider
{
    /// <summary>
    /// Gets a list of all available tickers in the data source.
    /// </summary>
    /// <returns>A list of ticker symbols.</returns>
    Task<IReadOnlyList<string>> GetAvailableTickersAsync();

    /// <summary>
    /// Gets the historical data for a specific ticker and timeframe.
    /// </summary>
    /// <param name="symbol">The ticker symbol.</param>
    /// <param name="timeFrame">The requested timeframe.</param>
    /// <returns>A list of candle data.</returns>
    Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame);

    /// <summary>
    /// Screens tickers based on the provided criteria.
    /// </summary>
    /// <param name="criteria">The screening criteria DTO.</param>
    /// <returns>A list of ticker symbols that match the criteria.</returns>
    Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria);

    /// <summary>
    /// Gets the latest available prices for the specified symbols.
    /// </summary>
    /// <param name="symbols">The list of ticker symbols.</param>
    /// <returns>A dictionary mapping ticker symbols to their latest closing prices.</returns>
    Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols);

    /// <summary>
    /// Gets metadata (sector, industry, etc.) for a specific ticker.
    /// Primarily reads from local storage/cache.
    /// </summary>
    /// <param name="ticker">The ticker symbol.</param>
    /// <returns>Metadata for the ticker.</returns>
    ValueTask<TickerMetadata> GetMetadataAsync(string ticker);

    /// <summary>
    /// Explicitly fetches metadata from external sources (Python) and persists it.
    /// Should only be called by manual user triggers.
    /// </summary>
    /// <param name="ticker">The ticker symbol.</param>
    /// <returns>Fetched metadata.</returns>
    Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker);

    /// <summary>
    /// Saves metadata to disk.
    /// </summary>
    Task SaveMetadataAsync(string ticker, TickerMetadata meta);
    
    /// <summary>
    /// Adds a ticker to the master ticker list.
    /// </summary>
    /// <param name="symbol">The ticker symbol to add.</param>
    Task AddTickerAsync(string symbol);

    /// <summary>
    /// Adds multiple tickers to the master ticker list.
    /// </summary>
    /// <param name="symbols">The ticker symbols to add.</param>
    Task AddTickersAsync(IEnumerable<string> symbols);

    /// <summary>
    /// Removes a ticker from the master ticker list.
    /// </summary>
    /// <param name="symbol">The ticker symbol to remove.</param>
    Task RemoveTickerAsync(string symbol);

    /// <summary>
    /// Removes multiple tickers from the master ticker list.
    /// </summary>
    /// <param name="symbols">The ticker symbols to remove.</param>
    Task RemoveTickersAsync(IEnumerable<string> symbols);

    /// <summary>
    /// Invalidates the metadata cache entry for the specified ticker.
    /// </summary>
    /// <param name="ticker">The ticker symbol.</param>
    void InvalidateMetadataCache(string ticker);

    /// <summary>
    /// Gets the last update/write time of the time series data for the specified symbol.
    /// </summary>
    /// <param name="symbol">The ticker symbol.</param>
    /// <returns>The last updated timestamp, or null if it doesn't exist.</returns>
    Task<System.DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol);

    /// <summary>
    /// Permanently deletes all time series rows for the specified symbol dated on or after
    /// <paramref name="cutoffDate"/>, preserving continuity by never leaving a gap: only the
    /// contiguous tail of the series (cutoff date through the latest row) is removed.
    /// </summary>
    /// <param name="symbol">The ticker symbol.</param>
    /// <param name="cutoffDate">The earliest date (inclusive) to delete; all newer rows are also removed.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteTickerDataFromDateAsync(string symbol, System.DateTime cutoffDate);
}
