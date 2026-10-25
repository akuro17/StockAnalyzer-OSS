using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ParquetMarketDataProviderRegisteredTickersTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(5, "MSFT", "AAPL");
    private readonly DuckDBConnectionManager _db;

    public ParquetMarketDataProviderRegisteredTickersTests()
    {
        _db = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private ParquetMarketDataProvider CreateProvider(MarketDataSettings? settings = null)
    {
        return new ParquetMarketDataProvider(
            _db,
            UnavailablePythonService.Instance,
            Options.Create(settings ?? _fixture.Settings),
            NullLogger<ParquetMarketDataProvider>.Instance);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenTickerListPathNullOrEmpty_ThrowsDataUnavailableException()
    {
        var settings = new MarketDataSettings
        {
            DailyDataPath = _fixture.Settings.DailyDataPath,
            TickerListPath = ""
        };
        var provider = CreateProvider(settings);

        var ex = await Assert.ThrowsAsync<DataUnavailableException>(() => provider.GetRegisteredTickersAsync());
        Assert.Equal("No ticker list is configured.", ex.Message);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenFileDoesNotExist_ThrowsDataUnavailableException()
    {
        var provider = CreateProvider();
        File.Delete(_fixture.TickerListPath);

        var ex = await Assert.ThrowsAsync<DataUnavailableException>(() => provider.GetRegisteredTickersAsync());
        Assert.Equal("The ticker list file is missing.", ex.Message);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenFileIsCorruptJson_ThrowsDataUnavailableException()
    {
        await File.WriteAllTextAsync(_fixture.TickerListPath, "{ this is not valid json }");
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<DataUnavailableException>(() => provider.GetRegisteredTickersAsync());
        Assert.Equal("The ticker list could not be read.", ex.Message);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenJsonIsNullLiteral_ThrowsDataUnavailableException()
    {
        await File.WriteAllTextAsync(_fixture.TickerListPath, "null");
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<DataUnavailableException>(() => provider.GetRegisteredTickersAsync());
        Assert.Equal("The ticker list could not be read.", ex.Message);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenJsonIsNotArray_ThrowsDataUnavailableException()
    {
        await File.WriteAllTextAsync(_fixture.TickerListPath, "{\"tickers\": [\"MSFT\"]}");
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<DataUnavailableException>(() => provider.GetRegisteredTickersAsync());
        Assert.Equal("The ticker list could not be read.", ex.Message);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenArrayIsEmpty_ReturnsEmptyList()
    {
        await File.WriteAllTextAsync(_fixture.TickerListPath, "[]");
        var provider = CreateProvider();

        var result = await provider.GetRegisteredTickersAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenValid_ReturnsCanonicalDeduplicatedListPreservingOrder()
    {
        await File.WriteAllTextAsync(_fixture.TickerListPath, "[\"  msft  \", \"7203.T\", \"MSFT\", \"\", \"  \", \"AAPL\"]");
        var provider = CreateProvider();

        var result = await provider.GetRegisteredTickersAsync();
        Assert.Equal(new[] { "MSFT", "7203-T", "AAPL" }, result);
    }

    [Fact]
    public async Task GetRegisteredTickersAsync_WhenCorrupt_DoesNotFallBackToFolderCrawl()
    {
        await File.WriteAllTextAsync(_fixture.TickerListPath, "{corrupt}");
        var provider = CreateProvider();

        await Assert.ThrowsAsync<DataUnavailableException>(() => provider.GetRegisteredTickersAsync());
    }

    [Fact]
    public async Task GetAvailableTickersAsync_WhenCorrupt_MaintainsLegacyCrawlFallback()
    {
        await File.WriteAllTextAsync(_fixture.TickerListPath, "{corrupt}");
        var provider = CreateProvider();

        var result = await provider.GetAvailableTickersAsync();
        Assert.Contains("MSFT", result);
        Assert.Contains("AAPL", result);
    }
}
