using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Read-only adapter over <see cref="ParquetMarketDataProvider"/>. Metadata is read with
/// <see cref="ParquetMarketDataProvider.TryLoadStoredMetadataAsync"/> (no cache, tag or file side effects);
/// <c>GetMetadataAsync</c>, which can fetch through Python and write, is never called.
/// </summary>
public sealed class ParquetReadOnlyMarketDataSource : IReadOnlyMarketDataSource
{
    private readonly ParquetMarketDataProvider _provider;

    public ParquetReadOnlyMarketDataSource(ParquetMarketDataProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
        => _provider.GetRegisteredTickersAsync().WaitAsync(cancellationToken);

    public async Task<TickerMetadata?> TryGetStoredMetadataAsync(string canonicalSymbol, CancellationToken cancellationToken = default)
    {
        MetadataDiskLoad load = await _provider.TryLoadStoredMetadataAsync(canonicalSymbol).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (load.ReadFailed)
        {
            throw new DataUnavailableException("Stored ticker metadata could not be read.");
        }

        return load.Meta == TickerMetadata.Unknown ? null : load.Meta;
    }

    public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string canonicalSymbol, CancellationToken cancellationToken = default)
        => _provider.GetTimeSeriesLastUpdatedAsync(canonicalSymbol).WaitAsync(cancellationToken);
}
