using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>Used when the data tree is incomplete (see <see cref="ReadOnlyDataTree"/>): reports no tickers and no stored data.</summary>
public sealed class EmptyReadOnlyMarketDataSource : IReadOnlyMarketDataSource
{
    public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<TickerMetadata?> TryGetStoredMetadataAsync(string canonicalSymbol, CancellationToken cancellationToken = default)
        => Task.FromResult<TickerMetadata?>(null);

    public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string canonicalSymbol, CancellationToken cancellationToken = default)
        => Task.FromResult<DateTimeOffset?>(null);
}
