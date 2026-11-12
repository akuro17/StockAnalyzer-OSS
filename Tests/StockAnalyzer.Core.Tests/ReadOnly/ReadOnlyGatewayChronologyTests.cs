using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// The contract of <see cref="IReadOnlyStockAnalyzerGateway"/> ("in chronological order"): the bars of both bar APIs are strictly increasing in time for D1, W1 and MN1.
/// The MCP tools (for example sa_calculate_drawing_values) rely on it and do not check it themselves.
/// </summary>
public sealed class ReadOnlyGatewayChronologyTests : IDisposable
{
    private const string Msft = "MSFT";
    private const int Days = 130;
    private const int AllBars = 10000;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, Msft.ToLowerInvariant());
    private readonly DuckDBConnectionManager _db = new();

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private ReadOnlyStockAnalyzerGateway CreateGateway()
    {
        IOptions<MarketDataSettings> options = Options.Create(_fixture.Settings);
        var source = new ParquetReadOnlyMarketDataSource(new ParquetMarketDataProvider(_db, UnavailablePythonService.Instance, options));
        var dataService = new ParquetDataService(_db, options);
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

    private static void AssertStrictlyIncreasing(string what, System.Collections.Generic.IReadOnlyList<CandleData> bars)
    {
        Assert.True(bars.Count >= 1, $"{what}: no bars were returned.");
        for (int i = 1; i < bars.Count; i++)
        {
            Assert.True(bars[i].Timestamp > bars[i - 1].Timestamp, $"{what}: bar {i} ({bars[i].Timestamp:yyyy-MM-dd HH:mm:ss}) is not later than bar {i - 1} ({bars[i - 1].Timestamp:yyyy-MM-dd HH:mm:ss}).");
        }
    }

    [Theory]
    [InlineData(TimeFrame.D1)]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public async Task BothBarApis_ReturnStrictlyIncreasingTimestamps(TimeFrame timeFrame)
    {
        ReadOnlyStockAnalyzerGateway gateway = CreateGateway();
        var daily = await gateway.GetCandlesAsync(Msft, TimeFrame.D1, AllBars);
        DateOnly lastDay = DateOnly.FromDateTime(daily[^1].Timestamp);
        DateOnly middleDay = DateOnly.FromDateTime(daily[daily.Count / 2].Timestamp);

        AssertStrictlyIncreasing($"GetCandlesAsync {timeFrame}", await gateway.GetCandlesAsync(Msft, timeFrame, AllBars));
        AssertStrictlyIncreasing($"GetCandlesUpToAsync {timeFrame} last day", await gateway.GetCandlesUpToAsync(Msft, timeFrame, lastDay, AllBars));
        AssertStrictlyIncreasing($"GetCandlesUpToAsync {timeFrame} middle day", await gateway.GetCandlesUpToAsync(Msft, timeFrame, middleDay, AllBars));
    }
}
