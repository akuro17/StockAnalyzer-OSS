using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// Pins the fact the MCP "allowUnregistered" work relies on: the read-only gateway itself does not consult the ticker list for bars
/// or stored metadata. The registry check of the MCP tools lives only in the MCP layer (SymbolResolver), so a symbol that has a
/// stored daily file but is absent from the ticker list is readable here, and a symbol without any file reads as empty (no exception).
/// </summary>
public sealed class ReadOnlyGatewayUnregisteredSymbolTests : IDisposable
{
    private const string Registered = "MSFT";
    private const string OffList = "OFFLIST";
    private const string NoFile = "NOFILE";
    private const int StoredDays = 20;
    private const int RequestedBars = 5;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, Registered);
    private readonly DuckDBConnectionManager _db = new();

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private IOptions<MarketDataSettings> MarketDataOptions => Options.Create(_fixture.Settings);

    private ReadOnlyStockAnalyzerGateway CreateGateway()
    {
        var source = new ParquetReadOnlyMarketDataSource(new ParquetMarketDataProvider(_db, UnavailablePythonService.Instance, MarketDataOptions));
        var dataService = new ParquetDataService(_db, MarketDataOptions);
        var screener = new ScreenerService(dataService, new PythonIpcTestSettingsBase(), new IMarketDataProvider[] { new ReadOnlyMetadataMarketDataProvider(source) });
        return new ReadOnlyStockAnalyzerGateway(
            source,
            dataService,
            new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default),
            screener,
            new FileReadOnlyTemplateSource(Path.Combine(_fixture.Root, "Templates", "Screener")),
            new ScreenerTemplateEvaluator(screener),
            new BacktestRunPreparationService(dataService),
            new BacktestEvaluationService(new BacktestEngine(IndicatorFactory.Default), new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy()));
    }

    private async Task SeedOffListAsync()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, OffList, StoredDays);
        await _fixture.SeedMetadataAsync(OffList, new TickerMetadata { ShortName = "Off List", Sector = "Technology" });
    }

    [Fact]
    public async Task SymbolMissingFromTheTickerList_IsStillReadableThroughTheGateway()
    {
        await SeedOffListAsync();
        var gateway = CreateGateway();

        var tickers = await gateway.GetAvailableTickersAsync();
        var bars = await gateway.GetCandlesAsync(OffList, TimeFrame.D1, RequestedBars);
        var metadata = await gateway.GetStoredTickerMetadataAsync(OffList);

        Assert.DoesNotContain(OffList, tickers);
        Assert.Equal(RequestedBars, bars.Count);
        Assert.Equal(bars.OrderBy(b => b.Timestamp).ToList(), bars);
        Assert.NotNull(metadata);
        Assert.Equal("Technology", metadata.Value.Sector);
    }

    [Fact]
    public async Task OffListBars_EqualTheBarsOfARegisteredSymbolWithTheSameStoredSeries()
    {
        await SeedOffListAsync();
        var gateway = CreateGateway();

        var offList = await gateway.GetCandlesAsync(OffList, TimeFrame.D1, RequestedBars);
        var registered = await gateway.GetCandlesAsync(Registered, TimeFrame.D1, RequestedBars);

        Assert.Equal(registered, offList);
    }

    [Fact]
    public async Task SymbolWithoutAnyStoredFile_ReadsAsEmptyWithoutAnException()
    {
        var gateway = CreateGateway();

        var bars = await gateway.GetCandlesAsync(NoFile, TimeFrame.D1, RequestedBars);
        var metadata = await gateway.GetStoredTickerMetadataAsync(NoFile);

        Assert.Empty(bars);
        Assert.Null(metadata);
    }
}
