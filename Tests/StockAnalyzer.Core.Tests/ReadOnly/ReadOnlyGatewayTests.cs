using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// Adapter fidelity (the gateway returns what the existing services return) and the read-only guarantee
/// (the multiset of {path, size, last write time, SHA-256} of the data tree is unchanged after a session).
/// Formula correctness stays with the indicator and backtest suites.
/// </summary>
public sealed class ReadOnlyGatewayTests : IDisposable
{
    private const string Msft = "MSFT";
    private const string Toyota = "7203.T";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(20, Msft.ToLowerInvariant(), Toyota);
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
            new FileReadOnlyTemplateSource(TemplateDirectory),
            new ScreenerTemplateEvaluator(screener),
            new BacktestRunPreparationService(dataService),
            CreateEvaluationService());
    }

    private static BacktestEvaluationService CreateEvaluationService()
        => new(new BacktestEngine(IndicatorFactory.Default), new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy());

    private string TemplateDirectory => Path.Combine(_fixture.Root, "Templates", "Screener");

    private static CoreCandleData ToCore(CandleData c) => new(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);

    [Fact]
    public async Task GetAvailableTickers_ReturnsCanonicalSymbolsOfTheTickerList()
    {
        var tickers = await CreateGateway().GetAvailableTickersAsync();

        Assert.Equal(new[] { "MSFT", "7203-T" }, tickers);
    }

    [Theory]
    [InlineData(TimeFrame.D1, 5)]
    [InlineData(TimeFrame.D1, 20)]
    [InlineData(TimeFrame.W1, 2)]
    [InlineData(TimeFrame.MN1, 1)]
    public async Task GetCandles_EqualsTheTimeFrameManagerResultInChronologicalOrder(TimeFrame timeFrame, int count)
    {
        var expected = await new TimeFrameManager(new ParquetDataService(_db, MarketDataOptions)).GetCandlesAsync("7203-T", timeFrame, count);

        var actual = await CreateGateway().GetCandlesAsync("7203.t", timeFrame, count);

        Assert.NotEmpty(actual);
        Assert.Equal(expected, actual);
        Assert.Equal(actual.OrderBy(c => c.Timestamp).ToList(), actual);
    }

    [Fact]
    public async Task GetCandles_ReturnsOnlyTheLatestBars()
    {
        var all = await CreateGateway().GetCandlesAsync(Msft, TimeFrame.D1, 20);

        var latest = await CreateGateway().GetCandlesAsync(Msft, TimeFrame.D1, 5);

        Assert.Equal(all.Skip(15).ToList(), latest);
    }

    [Fact]
    public async Task GetCandles_EachCallStartsWithAnEmptyCache()
    {
        var gateway = CreateGateway();
        var first = await gateway.GetCandlesAsync(Msft, TimeFrame.D1, 5);

        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, Msft, days: 25);
        var second = await gateway.GetCandlesAsync(Msft, TimeFrame.D1, 5);

        Assert.NotEqual(first.Last().Timestamp, second.Last().Timestamp);
        Assert.Equal(new DateTime(2024, 1, 25), second.Last().Timestamp.Date);
    }

    [Theory]
    [InlineData(TimeFrame.M1)]
    [InlineData(TimeFrame.H1)]
    [InlineData(TimeFrame.H4)]
    public async Task GetCandles_RejectsFramesTheDataLayerCannotLoad(TimeFrame timeFrame)
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => CreateGateway().GetCandlesAsync(Msft, timeFrame, 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetCandles_RejectsANonPositiveCount(int count)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateGateway().GetCandlesAsync(Msft, TimeFrame.D1, count));
    }

    public static TheoryData<string> UnsafeSymbols => new()
    {
        "", " ", "../MSFT", "..\\MSFT", "C:\\MSFT", "C:MSFT", "sub/MSFT", "sub\\MSFT", "A\0B", new string('A', TickerSymbol.MaxLength + 1),
    };

    [Theory]
    [MemberData(nameof(UnsafeSymbols))]
    public async Task EverySymbolMember_RejectsMalformedSymbolsBeforeAnyLookup(string symbol)
    {
        var gateway = CreateGateway();

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetCandlesAsync(symbol, TimeFrame.D1, 5));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetStoredTickerMetadataAsync(symbol));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetTimeSeriesLastUpdatedAsync(symbol));
    }

    [Fact]
    public async Task GetStoredTickerMetadata_IsNullWhenNothingIsStored()
    {
        Assert.Null(await CreateGateway().GetStoredTickerMetadataAsync(Msft));
    }

    [Fact]
    public async Task GetStoredTickerMetadata_ReturnsTheStoredRecordAndDoesNotWrite()
    {
        await _fixture.SeedMetadataAsync(Msft, TickerMetadata.Unknown with { ShortName = "Microsoft", Sector = "Technology" } );
        var before = _fixture.Snapshot();

        var metadata = await CreateGateway().GetStoredTickerMetadataAsync("msft");

        Assert.NotNull(metadata);
        Assert.Equal("Microsoft", metadata.Value.ShortName);
        Assert.Equal("Technology", metadata.Value.Sector);
        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task GetStoredTickerMetadata_ReportsAnUnreadableStoredFileAsDataUnavailable()
    {
        File.WriteAllBytes(Path.Combine(_fixture.MetadataDirectory, "MSFT.meta.parquet"), new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsAsync<DataUnavailableException>(() => CreateGateway().GetStoredTickerMetadataAsync(Msft));
    }

    [Fact]
    public async Task GetTimeSeriesLastUpdated_IsSetForAStoredSymbolAndNullOtherwise()
    {
        var gateway = CreateGateway();

        Assert.NotNull(await gateway.GetTimeSeriesLastUpdatedAsync(Msft));
        Assert.Null(await gateway.GetTimeSeriesLastUpdatedAsync("NOSUCH"));
    }

    [Fact]
    public async Task CalculateIndicators_EqualsTheDirectPipelineResult()
    {
        var gateway = CreateGateway();
        var candles = (await gateway.GetCandlesAsync(Msft, TimeFrame.D1, 20)).Select(ToCore).ToList();
        var settings = new[]
        {
            new CoreIndicatorSettings { Id = "sma", IsEnabled = true, TypeEnum = IndicatorType.SMA, ParameterObject = new CoreSmaParameter { Period = 5 } },
        };
        var direct = new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default);

        var expected = await direct.CalculateIndicatorsAsync(candles, settings);
        var actual = await gateway.CalculateIndicatorsAsync(candles, settings);

        Assert.True(actual["sma"].IsSuccessful);
        Assert.Equal(expected["sma"].MainValues, actual["sma"].MainValues);
    }

    [Fact]
    public async Task ASessionOfEveryMemberIncludingFailuresLeavesTheDataTreeUnchanged()
    {
        var before = _fixture.Snapshot();
        var gateway = CreateGateway();

        await gateway.GetAvailableTickersAsync();
        var candles = await gateway.GetCandlesAsync(Msft, TimeFrame.D1, 20);
        await gateway.GetCandlesAsync(Toyota, TimeFrame.W1, 3);
        await gateway.GetStoredTickerMetadataAsync(Msft);
        await gateway.GetTimeSeriesLastUpdatedAsync(Toyota);
        await gateway.CalculateIndicatorsAsync(candles.Select(ToCore).ToList(), new[]
        {
            new CoreIndicatorSettings { Id = "sma", IsEnabled = true, TypeEnum = IndicatorType.SMA, ParameterObject = new CoreSmaParameter { Period = 5 } },
        });
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetCandlesAsync("../x", TimeFrame.D1, 5));
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.GetCandlesAsync(Msft, TimeFrame.H1, 5));
        Assert.Empty(await gateway.GetCandlesAsync("NOSUCH", TimeFrame.D1, 5));
        await gateway.RunScreenerAsync(new[] { Msft, Toyota }, CloseAbove(100m), TimeFrame.D1);
        await gateway.RunScreenerAsync(new[] { Msft }, SectorEquals("Technology"), TimeFrame.D1);
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.RunScreenerAsync(new[] { "../x" }, CloseAbove(100m), TimeFrame.D1));

        Assert.Equal(before, _fixture.Snapshot());
    }

    private static IScreeningCondition CloseAbove(decimal level) => new ScreenerIndicatorEntry
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close" },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = level,
    };

    private static IScreeningCondition SectorEquals(string sector) => new ScreenerIndicatorEntry
    {
        LeftHand = new ScreenerIndicatorSideConfig { OutputName = "Sector", CategoryType = ScreenerItemCategoryType.Column },
        Operator = ComparisonOperator.Equal,
        TargetMode = RightHandTargetMode.StringValue,
        RightStringValue = sector,
        CategoryType = ScreenerItemCategoryType.Column,
    };

    [Fact]
    public async Task RunScreener_EqualsTheScreenerServiceResultAndReturnsCanonicalSymbolsInInputOrder()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, Toyota.Replace('.', '-'), days: 8);
        var source = new ParquetReadOnlyMarketDataSource(new ParquetMarketDataProvider(_db, UnavailablePythonService.Instance, MarketDataOptions));
        var direct = new ScreenerService(new ParquetDataService(_db, MarketDataOptions), new PythonIpcTestSettingsBase(), new IMarketDataProvider[] { new ReadOnlyMetadataMarketDataProvider(source) });
        var condition = CloseAbove(104m);

        var expected = await direct.ScreenAsync(new List<string> { "MSFT", "7203-T" }, condition, TimeFrame.D1, new Progress<int>(), CancellationToken.None);
        var actual = await CreateGateway().RunScreenerAsync(new[] { "msft", "7203.t" }, condition, TimeFrame.D1);

        Assert.Equal(new[] { "MSFT" }, expected); // the 20-bar series closes at 105, the 8-bar one at 102
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RunScreener_ReadsTheStoredMetadataForAColumnConditionAndAMissingOneDoesNotMatch()
    {
        await _fixture.SeedMetadataAsync(Msft, TickerMetadata.Unknown with { ShortName = "Microsoft", Sector = "Technology" });

        var matched = await CreateGateway().RunScreenerAsync(new[] { Msft, Toyota }, SectorEquals("technology"), TimeFrame.D1);

        Assert.Equal(new[] { Msft }, matched);
    }

    [Fact]
    public async Task RunScreener_RejectsAnUnsupportedFrameAndACancelledToken()
    {
        var gateway = CreateGateway();

        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RunScreenerAsync(new[] { Msft }, CloseAbove(1m), TimeFrame.H1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.RunScreenerAsync(new[] { Msft, Toyota }, CloseAbove(1m), TimeFrame.D1, cancelled.Token));
    }

    [Fact]
    public async Task RunScreener_AnOffsetAboveTheApplicationSettingIsRefused()
    {
        var condition = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close", Offset = 1_000_000 },
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 1m,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateGateway().RunScreenerAsync(new[] { Msft }, condition, TimeFrame.D1));
    }

    [Fact]
    public async Task TheMetadataProviderOfTheScreenerRefusesEveryMemberThatWouldWriteOrFetch()
    {
        var source = new ParquetReadOnlyMarketDataSource(new ParquetMarketDataProvider(_db, UnavailablePythonService.Instance, MarketDataOptions));
        IMarketDataProvider provider = new ReadOnlyMetadataMarketDataProvider(source);

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SaveMetadataAsync(Msft, TickerMetadata.Unknown));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.AddTickerAsync("NEW"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.RemoveTickerAsync(Msft));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.FetchMetadataFromPythonAsync(Msft));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteTickerDataFromDateAsync(Msft, DateTime.MinValue));
        Assert.Equal(TickerMetadata.Unknown, await provider.GetMetadataAsync("NOSUCH"));
    }

    private ScreenerIndicatorTemplate ThresholdTemplate(params (decimal Level, LogicalOperator Join)[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "t" };
        template.SetEntries(entries.Select(e => new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close" },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = e.Level,
            LogicalOperator = e.Join,
        }));
        return template;
    }

    [Fact]
    public async Task RunScreenerTemplate_EqualsTheTemplateEvaluatorAndCombinesByThePrecedingEntry()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, Toyota.Replace('.', '-'), days: 8); // closes 102; MSFT (20 bars) closes 105
        var gateway = CreateGateway();
        var symbols = new[] { "MSFT", "7203-T" };

        // MSFT and 7203-T pass "close > 101"; only MSFT passes "close > 104". And intersects, Or unites.
        var and = ThresholdTemplate((101m, LogicalOperator.And), (104m, LogicalOperator.And));
        var or = ThresholdTemplate((104m, LogicalOperator.Or), (101m, LogicalOperator.And));
        var none = ThresholdTemplate((1000m, LogicalOperator.Or), (1001m, LogicalOperator.And));

        Assert.Equal(new[] { "MSFT" }, (await gateway.RunScreenerTemplateAsync(symbols, and, TimeFrame.D1)).OrderBy(s => s).ToArray());
        Assert.Equal(new[] { "7203-T", "MSFT" }, (await gateway.RunScreenerTemplateAsync(symbols, or, TimeFrame.D1)).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Empty(await gateway.RunScreenerTemplateAsync(symbols, none, TimeFrame.D1));
    }

    [Fact]
    public async Task RunScreenerTemplate_RejectsMalformedSymbolsAnUnsupportedFrameAndAnOffsetAboveTheSetting()
    {
        var gateway = CreateGateway();
        var template = ThresholdTemplate((1m, LogicalOperator.And));

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.RunScreenerTemplateAsync(new[] { "../x" }, template, TimeFrame.D1));
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RunScreenerTemplateAsync(new[] { Msft }, template, TimeFrame.H1));
        template.Entries[0].LeftHand.Offset = 1_000_000;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.RunScreenerTemplateAsync(new[] { Msft }, template, TimeFrame.D1));
    }

    [Fact]
    public async Task ListAndGetScreenerTemplates_ReadFromTheTemplateFolderWithoutWritingIt()
    {
        var template = ThresholdTemplate((1m, LogicalOperator.And));
        template.Name = "stored";
        Directory.CreateDirectory(TemplateDirectory);
        File.WriteAllText(Path.Combine(TemplateDirectory, $"{template.Id}.json"), System.Text.Json.JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        var before = _fixture.Snapshot();
        var gateway = CreateGateway();

        var listing = await gateway.ListScreenerTemplatesAsync();
        var loaded = await gateway.GetScreenerTemplateAsync(template.Id);

        Assert.Equal("stored", Assert.Single(listing.Templates).Name);
        Assert.Equal(template.Id, loaded!.Id);
        Assert.Equal(before, _fixture.Snapshot());
    }

    private static BacktestConditionSide ClosePrice(int offset = 0) => new()
    {
        IndicatorType = IndicatorType.Price,
        PriceSource = PriceType.Close,
        OutputName = IndicatorResult.MainSeriesName,
        Offset = offset,
    };

    private static BacktestConditionGroup CloseVersusPreviousClose(ComparisonOperator comparison) => new(
        LogicalOperator.And,
        ImmutableArray.Create<IBacktestConditionNode>(new BacktestConditionLeaf(new BacktestConditionEntry
        {
            Left = ClosePrice(),
            Operator = comparison,
            TargetMode = RightHandTargetMode.Indicator,
            Right = ClosePrice(1),
        })));

    /// <summary>Buy when the close rose against the previous close, sell when it fell: the fixture's alternating closes trade on every other day.</summary>
    private static BacktestRunSnapshot BacktestSnapshot(string symbol = Msft, TimeFrame frame = TimeFrame.D1, ExecutionModel model = ExecutionModel.Legacy) => new(
        symbol,
        frame,
        new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc),
        new BacktestConfiguration { ExecutionModel = model, InitialCapital = 100_000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 10m },
        ImmutableArray<BacktestConditionEntry>.Empty,
        null,
        ImmutableArray<StrategyIndicatorRequest>.Empty,
        BacktestReportDefaults.BuiltIn,
        BacktestDefaults.BootstrapSeed,
        BacktestDefaults.BootstrapIterations,
        new BacktestConditionTree(
            CloseVersusPreviousClose(ComparisonOperator.GreaterThan),
            BacktestConditionGroup.EmptyAnd,
            CloseVersusPreviousClose(ComparisonOperator.LessThan),
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd));

    [Fact]
    public async Task RunBacktest_ReturnsWhatThePreparationAndEvaluationServicesReturnWhenCalledDirectly()
    {
        BacktestEvaluationArtifact viaGateway = await CreateGateway().RunBacktestAsync(BacktestSnapshot());

        PreparedBacktestRun prepared = await new BacktestRunPreparationService(new ParquetDataService(_db, MarketDataOptions))
            .PrepareAsync(BacktestSnapshot(), CancellationToken.None);
        BacktestEvaluationArtifact direct = CreateEvaluationService().Evaluate(
            prepared.Input, prepared.Configuration, prepared.StrategySpecification, prepared.ReportOptions);

        Assert.NotEmpty(viaGateway.Result.Trades);
        Assert.Equal(RunStatus.Completed, viaGateway.RunStatus);
        Assert.True(viaGateway.RunFingerprint.IsAvailable);
        Assert.Equal(direct.RunFingerprint.Sha256, viaGateway.RunFingerprint.Sha256);
        Assert.Equal(direct.Result.ReproducibilityHash, viaGateway.Result.ReproducibilityHash);
        Assert.Equal(direct.Result.Trades.Length, viaGateway.Result.Trades.Length);
        Assert.Equal(direct.Report!.TotalPnL, viaGateway.Report!.TotalPnL);
        Assert.Equal(direct.Report.TotalTrades, viaGateway.Report.TotalTrades);
    }

    [Fact]
    public async Task RunBacktest_CanonicalisesTheSymbolAndChangesNothingInTheDataTree()
    {
        var before = _fixture.Snapshot();

        BacktestEvaluationArtifact artifact = await CreateGateway().RunBacktestAsync(BacktestSnapshot(symbol: " msft "));

        Assert.Equal("MSFT", artifact.Metadata.Symbol);
        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task RunBacktest_StopsWhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateGateway().RunBacktestAsync(BacktestSnapshot(), cancelled.Token));
    }

    [Fact]
    public async Task RunBacktest_RefusesWhatTheReadOnlyHostDoesNotSupport()
    {
        ReadOnlyStockAnalyzerGateway gateway = CreateGateway();

        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RunBacktestAsync(BacktestSnapshot(frame: TimeFrame.W1)));
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RunBacktestAsync(BacktestSnapshot(model: ExecutionModel.StrictEvidence)));
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RunBacktestAsync(BacktestSnapshot(model: ExecutionModel.YFinanceApproximate)));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.RunBacktestAsync(BacktestSnapshot(symbol: "../evil")));
    }

    [Fact]
    public async Task CalculateReverseWatch_EqualsTheAnalysisServiceAndReturnsNullWhenTheCurveCannotBeCalculated()
    {
        ReadOnlyStockAnalyzerGateway gateway = CreateGateway();
        IReadOnlyList<CandleData> bars = await gateway.GetCandlesAsync(Msft, TimeFrame.D1, 20);
        List<CoreCandleData> candles = bars.Select(ToCore).ToList();

        ReverseWatchCurveData? viaGateway = gateway.CalculateReverseWatch(candles, 5, " msft ", isMaBased: false, isLogScaleVolume: true, dataCount: 8);
        ReverseWatchCurveData direct = new ReverseWatchAnalysisService().Calculate(bars, 5, "MSFT", isMaBased: false, isLogScaleVolume: true, dataCount: 8);

        Assert.NotNull(viaGateway);
        Assert.Equal("MSFT", viaGateway!.StockCode);
        Assert.Equal(direct.Points.Select(p => (p.Date, p.Phase, p.PriceAverage, p.VolumeAverage)), viaGateway.Points.Select(p => (p.Date, p.Phase, p.PriceAverage, p.VolumeAverage)));
        Assert.Null(gateway.CalculateReverseWatch(candles, 500, Msft));
        Assert.Throws<ArgumentException>(() => gateway.CalculateReverseWatch(candles, 5, "../evil"));
    }

    [Fact]
    public void TheInterfaceExposesOnlyReadingMembers()
    {
        string[] names = typeof(IReadOnlyStockAnalyzerGateway).GetMethods().Select(m => m.Name).ToArray();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.Matches(new Regex("^(Get|List|Calculate|Run|Evaluate)"), name));
    }

    [Fact]
    public void TheGatewayConstructorTakesOnlyTheNarrowReadingServices()
    {
        Type[] parameters = typeof(ReadOnlyStockAnalyzerGateway).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Equal(
            new[] { typeof(IReadOnlyMarketDataSource), typeof(IDataService), typeof(StockAnalyzer.Core.Services.Analysis.IAnalysisPipelineService), typeof(IScreenerService), typeof(IReadOnlyTemplateSource), typeof(StockAnalyzer.Core.Services.Screener.IScreenerTemplateEvaluator), typeof(IBacktestRunPreparationService), typeof(IBacktestEvaluationService) },
            parameters);
    }

    [Fact]
    public void TheReadingDataSourceHasNoMemberThatWritesOrFetches()
    {
        string[] names = typeof(IReadOnlyMarketDataSource).GetMethods().Select(m => m.Name).ToArray();

        Assert.Equal(new[] { "GetAvailableTickersAsync", "TryGetStoredMetadataAsync", "GetTimeSeriesLastUpdatedAsync" }, names);
    }
}
