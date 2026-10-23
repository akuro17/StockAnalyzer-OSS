using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// The <see cref="IMarketDataProvider"/> the Screener service needs, backed by the read-only data source: the only member it uses is
/// <see cref="GetMetadataAsync"/>, and that reads the stored metadata without ever fetching, caching or writing (the real provider's
/// <c>GetMetadataAsync</c> can write). Every other member would change or fetch data, so it throws; the Screener never calls them.
/// </summary>
public sealed class ReadOnlyMetadataMarketDataProvider : IMarketDataProvider
{
    private readonly IReadOnlyMarketDataSource _source;

    public ReadOnlyMetadataMarketDataProvider(IReadOnlyMarketDataSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>The stored metadata of <paramref name="ticker"/>; <see cref="TickerMetadata.Unknown"/> when none is stored.</summary>
    public async ValueTask<TickerMetadata> GetMetadataAsync(string ticker)
        => await _source.TryGetStoredMetadataAsync(TickerSymbol.Canonicalize(ticker)).ConfigureAwait(false) ?? TickerMetadata.Unknown;

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => _source.GetAvailableTickersAsync();

    public Task<System.DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol)
        => _source.GetTimeSeriesLastUpdatedAsync(TickerSymbol.Canonicalize(symbol));

    public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => Refuse<IReadOnlyList<CandleData>>();

    public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => Refuse<IReadOnlyList<string>>();

    public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => Refuse<IReadOnlyDictionary<string, decimal>>();

    public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => Refuse<TickerMetadata>();

    public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Refuse<bool>();

    public Task AddTickerAsync(string symbol) => Refuse<bool>();

    public Task AddTickersAsync(IEnumerable<string> symbols) => Refuse<bool>();

    public Task RemoveTickerAsync(string symbol) => Refuse<bool>();

    public Task RemoveTickersAsync(IEnumerable<string> symbols) => Refuse<bool>();

    public void InvalidateMetadataCache(string ticker)
    {
        // Nothing is cached here: a read always goes to the stored file.
    }

    public Task<int> DeleteTickerDataFromDateAsync(string symbol, System.DateTime cutoffDate) => Refuse<int>();

    private static Task<T> Refuse<T>()
        => Task.FromException<T>(new NotSupportedException("The read-only provider only reads stored metadata."));
}
