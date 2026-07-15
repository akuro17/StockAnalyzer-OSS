using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// A lightweight, non-blocking mock implementation of IMarketDataProvider.
/// Used to completely bypass native DuckDB/Parquet initialization in unit tests and design-time.
/// </summary>
public class MockMarketDataProvider : IMarketDataProvider
{
    private static readonly IReadOnlyList<string> _availableTickers = new[] { "AAPL", "MSFT", "GOOG" };
    private static readonly MockDataService _mockDataService = new();
    private static readonly IReadOnlyDictionary<string, decimal> _emptyPrices = new Dictionary<string, decimal>();
    private static readonly IReadOnlyList<string> _emptyTickers = Array.Empty<string>();
    private static readonly TickerMetadata _defaultMetadata = new TickerMetadata("DUMMY", "DUMMY", "US", "Technology", "Software", "USD");

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync()
    {
        return Task.FromResult(_availableTickers);
    }

    public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame)
    {
        return _mockDataService.LoadCandlesAsync(symbol, timeFrame);
    }

    public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria)
    {
        return Task.FromResult(_emptyTickers);
    }

    public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols)
    {
        return Task.FromResult(_emptyPrices);
    }

    public ValueTask<TickerMetadata> GetMetadataAsync(string ticker)
    {
        if (ticker == "DUMMY")
        {
            return ValueTask.FromResult(_defaultMetadata);
        }
        return ValueTask.FromResult(new TickerMetadata(ticker, ticker, "US", "Technology", "Software", "USD"));
    }

    public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker)
    {
        if (ticker == "DUMMY")
        {
            return Task.FromResult(_defaultMetadata);
        }
        return Task.FromResult(new TickerMetadata(ticker, ticker, "US", "Technology", "Software", "USD"));
    }

    public Task AddTickerAsync(string symbol) => Task.CompletedTask;
    public Task AddTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
    public Task RemoveTickerAsync(string symbol) => Task.CompletedTask;
    public Task RemoveTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;

    public void InvalidateMetadataCache(string ticker)
    {
        // No-op for mock
    }

    public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Task.CompletedTask;
}
