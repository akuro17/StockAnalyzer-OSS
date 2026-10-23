using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// The only view of the ticker list and the stored ticker metadata that the read-only gateway uses.
/// It has no member that writes, deletes or fetches from an external source.
/// </summary>
public interface IReadOnlyMarketDataSource
{
    /// <summary>Canonical symbols of all available tickers.</summary>
    Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default);

    /// <summary>Metadata exactly as stored on disk, or null when nothing is stored. Never fetches.</summary>
    /// <exception cref="DataUnavailableException">The stored file exists but could not be read.</exception>
    Task<TickerMetadata?> TryGetStoredMetadataAsync(string canonicalSymbol, CancellationToken cancellationToken = default);

    /// <summary>Last write time of the stored time series of <paramref name="canonicalSymbol"/>, or null.</summary>
    Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string canonicalSymbol, CancellationToken cancellationToken = default);
}
