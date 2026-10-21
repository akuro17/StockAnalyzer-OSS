using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// Shared <see cref="IMarketDataProvider"/> test double for a fixed, in-memory symbol->candles map.
/// Every member outside <see cref="GetTickersDataAsync"/> throws <see cref="NotSupportedException"/>
/// because no test in this project exercises them through this fake. Extracted from the byte-identical
/// private nested classes previously duplicated per test file (a Shared Definitions violation per
/// <c>docs/CODE_REVIEW_GUIDELINES.md</c> lines 234-235); new tests needing a market-data fake should
/// reference this one instead of adding another private copy.
/// </summary>
public sealed class FakeMarketDataProvider : IMarketDataProvider
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CandleData>> _bySymbol;
    private readonly IReadOnlyDictionary<string, TickerMetadata>? _metadataBySymbol;
    private int _metadataRequests;

    /// <param name="bySymbol">The candles served by <see cref="GetTickersDataAsync"/>.</param>
    /// <param name="metadataBySymbol">The metadata served by <see cref="GetMetadataAsync"/> (an unknown symbol gets <see cref="TickerMetadata.Unknown"/>);
    /// when null, <see cref="GetMetadataAsync"/> throws like every other unused member.</param>
    public FakeMarketDataProvider(
        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> bySymbol,
        IReadOnlyDictionary<string, TickerMetadata>? metadataBySymbol = null)
    {
        _bySymbol = bySymbol;
        _metadataBySymbol = metadataBySymbol;
    }

    /// <summary>How many times <see cref="GetMetadataAsync"/> has been called.</summary>
    public int MetadataRequests => System.Threading.Volatile.Read(ref _metadataRequests);

    public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame)
        => _bySymbol.TryGetValue(symbol, out var candles)
            ? Task.FromResult(candles)
            : throw new KeyNotFoundException($"FakeMarketDataProvider: no fixture candles configured for '{symbol}'.");

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
    public ValueTask<TickerMetadata> GetMetadataAsync(string ticker)
    {
        if (_metadataBySymbol is null)
        {
            throw new NotSupportedException();
        }

        System.Threading.Interlocked.Increment(ref _metadataRequests);
        return new ValueTask<TickerMetadata>(_metadataBySymbol.TryGetValue(ticker, out TickerMetadata metadata) ? metadata : TickerMetadata.Unknown);
    }
    public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => throw new NotSupportedException();
    public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => throw new NotSupportedException();
    public Task AddTickerAsync(string symbol) => throw new NotSupportedException();
    public Task AddTickersAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
    public Task RemoveTickerAsync(string symbol) => throw new NotSupportedException();
    public Task RemoveTickersAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
    public void InvalidateMetadataCache(string ticker) => throw new NotSupportedException();
    public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => throw new NotSupportedException();
    public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => throw new NotSupportedException();
}
