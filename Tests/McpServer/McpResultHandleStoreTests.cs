using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Master Task 3 tests: <see cref="McpResultHandleStore"/> LRU cache, <c>storeResult</c> on producer tools,
/// and <c>symbolsFromResultId</c> on consumer tools.
/// </summary>
public sealed class McpResultHandleStoreTests : IDisposable
{
    private const int Days = 120;
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "MSFT", "AAPL", "GOOGL");
    private readonly List<McpServerRuntime> _runtimes = new();

    public McpResultHandleStoreTests()
    {
        decimal[] msft = Enumerable.Range(0, Days).Select(i => 100m + i * 0.5m).ToArray();
        decimal[] aapl = Enumerable.Range(0, Days).Select(i => 150m + (decimal)(4 * Math.Sin(i / 5.0))).ToArray();
        decimal[] googl = Enumerable.Range(0, Days).Select(i => 200m + i * 0.3m).ToArray();
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", msft);
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "AAPL", aapl);
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "GOOGL", googl);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture))
            .Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string arguments)
        => (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

    private static JsonElement Ok(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    // ---- McpResultHandleStore LRU & validation unit tests ------------------------------------------------------------------------

    [Fact]
    public void Store_EvictsLeastRecentlyUsedAfterMaxHandlesAndUpdatesRecencyOnTryGet()
    {
        var store = new McpResultHandleStore();
        for (int i = 1; i <= McpResultHandleStore.MaxHandles; i++)
        {
            SymbolListHandleEntry entry = store.Store("sa_run_screener", new[] { $"SYM{i}" });
            Assert.Equal($"res_{i:D4}", entry.ResultId);
        }

        Assert.Equal(McpResultHandleStore.MaxHandles, store.Count);

        // Refresh res_0001 so res_0002 becomes LRU
        Assert.True(store.TryGet("res_0001", out SymbolListHandleEntry? refreshed));
        Assert.NotNull(refreshed);

        SymbolListHandleEntry overflow = store.Store("sa_filter_tickers", new[] { "NEW" });
        Assert.Equal("res_0033", overflow.ResultId);
        Assert.Equal(McpResultHandleStore.MaxHandles, store.Count);

        Assert.True(store.TryGet("res_0001", out _));
        Assert.False(store.TryGet("res_0002", out _));
        Assert.True(store.TryGet("res_0033", out _));
    }

    [Fact]
    public void ResolveSymbols_EnforcesNotFoundMinCountAndMaxCount()
    {
        var store = new McpResultHandleStore();
        string oneSymbolId = store.Store("sa_run_screener", new[] { "MSFT" }).ResultId;
        string threeSymbolId = store.Store("sa_filter_tickers", new[] { "AAPL", "GOOGL", "MSFT" }).ResultId;

        ToolException missing = Assert.Throws<ToolException>(() => store.ResolveSymbols("res_9999", 1, 10));
        Assert.Equal(ToolErrorCodes.InvalidArgument, missing.Code);
        Assert.Equal(McpResultHandleStore.SymbolsFromResultIdArgument, missing.Parameter);

        ToolException tooFew = Assert.Throws<ToolException>(() => store.ResolveSymbols(oneSymbolId, 2, 10));
        Assert.Equal(ToolErrorCodes.InvalidArgument, tooFew.Code);
        Assert.Equal(McpResultHandleStore.SymbolsFromResultIdArgument, tooFew.Parameter);

        ToolException tooMany = Assert.Throws<ToolException>(() => store.ResolveSymbols(threeSymbolId, 1, 2));
        Assert.Equal(ToolErrorCodes.LimitExceeded, tooMany.Code);
        Assert.Equal(McpResultHandleStore.SymbolsFromResultIdArgument, tooMany.Parameter);

        Assert.Equal(new[] { "AAPL", "GOOGL", "MSFT" }, store.ResolveSymbols(threeSymbolId, 2, 5));
    }

    // ---- End-to-end producer & consumer pipeline tests ---------------------------------------------------------------------------

    [Fact]
    public async Task RunScreener_And_FilterTickers_StoreResultEmitsResultIdAndOmittedIsByteIdentical()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string screenerCondition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":10}""";

        using JsonDocument screenerOmitted = await CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{screenerCondition}}}""");
        using JsonDocument screenerFalse = await CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{screenerCondition}},"storeResult":false}""");
        using JsonDocument screenerStored = await CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{screenerCondition}},"storeResult":true}""");

        Assert.Equal(Ok(screenerOmitted).GetRawText(), Ok(screenerFalse).GetRawText());
        Assert.False(Ok(screenerOmitted).TryGetProperty("resultId", out _));
        string screenerHandle = Ok(screenerStored).GetProperty("resultId").GetString()!;
        Assert.StartsWith("res_", screenerHandle, StringComparison.Ordinal);

        using JsonDocument filterOmitted = await CallAsync(dispatcher, "sa_filter_tickers", "{}");
        using JsonDocument filterFalse = await CallAsync(dispatcher, "sa_filter_tickers", """{"storeResult":false}""");
        using JsonDocument filterStored = await CallAsync(dispatcher, "sa_filter_tickers", """{"storeResult":true}""");

        Assert.Equal(Ok(filterOmitted).GetRawText(), Ok(filterFalse).GetRawText());
        Assert.False(Ok(filterOmitted).TryGetProperty("resultId", out _));
        string filterHandle = Ok(filterStored).GetProperty("resultId").GetString()!;
        Assert.NotEqual(screenerHandle, filterHandle);
    }

    [Fact]
    public async Task ProducerTools_RemainMarkedReadOnlyInToolsList()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string listJson = await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None);
        using JsonDocument doc = JsonDocument.Parse(listJson);
        foreach (JsonElement tool in doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            if (name is "sa_run_screener" or "sa_filter_tickers")
            {
                Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), $"{name} must stay readOnlyHint: true.");
            }
        }
    }

    [Fact]
    public async Task ScreenerHandle_FeedsAllFourConsumerToolsEquivalently()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string screenerCondition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":10}""";
        using JsonDocument stored = await CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{screenerCondition}},"storeResult":true}""");
        JsonElement storedBody = Ok(stored);
        string handleId = storedBody.GetProperty("resultId").GetString()!;
        string symbolsJson = JsonSerializer.Serialize(storedBody.GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).ToArray());

        // 1. sa_calculate_indicators_batch
        using JsonDocument batchViaSymbols = await CallAsync(dispatcher, "sa_calculate_indicators_batch", $$$"""{"symbols":{{{symbolsJson}}},"timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}]}""");
        using JsonDocument batchViaHandle = await CallAsync(dispatcher, "sa_calculate_indicators_batch", $$$"""{"symbolsFromResultId":"{{{handleId}}}","timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}]}""");
        Assert.Equal(Ok(batchViaSymbols).GetRawText(), Ok(batchViaHandle).GetRawText());

        // 2. sa_run_portfolio_backtest
        string backtestConds = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}}""";
        using JsonDocument portfolioViaSymbols = await CallAsync(dispatcher, "sa_run_portfolio_backtest", $$"""{"symbols":{{symbolsJson}},"conditions":{{backtestConds}}}""");
        using JsonDocument portfolioViaHandle = await CallAsync(dispatcher, "sa_run_portfolio_backtest", $$"""{"symbolsFromResultId":"{{handleId}}","conditions":{{backtestConds}}}""");
        Assert.Equal(Ok(portfolioViaSymbols).GetRawText(), Ok(portfolioViaHandle).GetRawText());

        // 3. sa_calculate_cross_asset_stats
        using JsonDocument crossViaSymbols = await CallAsync(dispatcher, "sa_calculate_cross_asset_stats", $$"""{"symbols":{{symbolsJson}},"timeFrame":"D1","lookbackBars":60}""");
        using JsonDocument crossViaHandle = await CallAsync(dispatcher, "sa_calculate_cross_asset_stats", $$"""{"symbolsFromResultId":"{{handleId}}","timeFrame":"D1","lookbackBars":60}""");
        Assert.Equal(Ok(crossViaSymbols).GetRawText(), Ok(crossViaHandle).GetRawText());

        // 4. sa_calculate_market_breadth
        using JsonDocument breadthViaSymbols = await CallAsync(dispatcher, "sa_calculate_market_breadth", $$"""{"symbols":{{symbolsJson}},"timeFrame":"D1","lookbackBars":60,"outputTailCount":10}""");
        using JsonDocument breadthViaHandle = await CallAsync(dispatcher, "sa_calculate_market_breadth", $$"""{"symbolsFromResultId":"{{handleId}}","timeFrame":"D1","lookbackBars":60,"outputTailCount":10}""");
        Assert.Equal(Ok(breadthViaSymbols).GetRawText(), Ok(breadthViaHandle).GetRawText());
    }

    [Fact]
    public async Task ConsumerTools_RejectMutuallyExclusiveAndInvalidHandles()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument oneSymbol = await CallAsync(dispatcher, "sa_filter_tickers", """{"symbols":["MSFT"],"storeResult":true}""");
        string oneId = Ok(oneSymbol).GetProperty("resultId").GetString()!;

        // Mutual exclusivity on all 4 consumer tools
        (string Tool, string Extra)[] conflicts =
        {
            ("sa_calculate_indicators_batch", $$$"""{"symbols":["MSFT"],"symbolsFromResultId":"{{{oneId}}}","timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}]}"""),
            ("sa_run_portfolio_backtest", $$$$"""{"symbols":["MSFT"],"symbolsFromResultId":"{{{{oneId}}}}","conditions":{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":10}}}"""),
            ("sa_calculate_cross_asset_stats", $$"""{"symbols":["MSFT","AAPL"],"symbolsFromResultId":"{{oneId}}"}"""),
            ("sa_calculate_market_breadth", $$"""{"symbols":["MSFT"],"symbolsFromResultId":"{{oneId}}"}"""),
        };

        foreach ((string tool, string args) in conflicts)
        {
            using JsonDocument conflict = await CallAsync(dispatcher, tool, args);
            Assert.True(McpTestSupport.IsError(conflict), $"{tool} must reject symbols + symbolsFromResultId");
            Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(conflict));
            Assert.Equal(McpResultHandleStore.SymbolsFromResultIdArgument, McpTestSupport.Structured(conflict).GetProperty("param").GetString());
        }

        // Cross-asset requires >= 2 symbols even via handle
        using JsonDocument tooFew = await CallAsync(dispatcher, "sa_calculate_cross_asset_stats", $$"""{"symbolsFromResultId":"{{oneId}}"}""");
        Assert.True(McpTestSupport.IsError(tooFew));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(tooFew));

        // Unknown handle
        using JsonDocument unknown = await CallAsync(dispatcher, "sa_calculate_market_breadth", """{"symbolsFromResultId":"res_9999"}""");
        Assert.True(McpTestSupport.IsError(unknown));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(unknown));
        Assert.Equal(McpResultHandleStore.SymbolsFromResultIdArgument, McpTestSupport.Structured(unknown).GetProperty("param").GetString());
    }

    [Fact]
    public void ResolveSymbolsSlice_SlicesAndEnforcesBoundsWithoutSilentTruncation()
    {
        var store = new McpResultHandleStore();
        string[] all = Enumerable.Range(1, 55).Select(i => $"SYM{i:D2}").ToArray();
        string id = store.Store("sa_run_screener", all).ResultId;

        // Omitting limit when remaining > maxCount throws LIMIT_EXCEEDED with slice hint
        ToolException unpaged = Assert.Throws<ToolException>(() => store.ResolveSymbolsSlice(id, 1, 50));
        Assert.Equal(ToolErrorCodes.LimitExceeded, unpaged.Code);
        Assert.Contains(McpResultHandleStore.ResultOffsetArgument, unpaged.Hint!, StringComparison.Ordinal);
        Assert.Contains(McpResultHandleStore.ResultLimitArgument, unpaged.Hint!, StringComparison.Ordinal);

        // Explicit slice 0..49 and 50..54
        Assert.Equal(all[..50], store.ResolveSymbolsSlice(id, 1, 50, offset: 0, limit: 50));
        Assert.Equal(all[50..], store.ResolveSymbolsSlice(id, 1, 50, offset: 50, limit: 50));

        // Omitted limit when offset brings remaining <= maxCount succeeds
        Assert.Equal(all[50..], store.ResolveSymbolsSlice(id, 1, 50, offset: 50, limit: null));

        // Offset beyond count -> INVALID_ARGUMENT at resultOffset
        ToolException outOfRange = Assert.Throws<ToolException>(() => store.ResolveSymbolsSlice(id, 1, 50, offset: 55, limit: 10));
        Assert.Equal(ToolErrorCodes.InvalidArgument, outOfRange.Code);
        Assert.Equal(McpResultHandleStore.ResultOffsetArgument, outOfRange.Parameter);

        // Limit above maxCount -> LIMIT_EXCEEDED at resultLimit
        ToolException limitTooHigh = Assert.Throws<ToolException>(() => store.ResolveSymbolsSlice(id, 1, 50, offset: 0, limit: 51));
        Assert.Equal(ToolErrorCodes.LimitExceeded, limitTooHigh.Code);
        Assert.Equal(McpResultHandleStore.ResultLimitArgument, limitTooHigh.Parameter);

        // Trailing slice smaller than minCount (e.g. 54..54 = 1 symbol for cross-asset min=2) -> INVALID_ARGUMENT
        ToolException tooFewRemaining = Assert.Throws<ToolException>(() => store.ResolveSymbolsSlice(id, 2, 50, offset: 54, limit: 50));
        Assert.Equal(ToolErrorCodes.InvalidArgument, tooFewRemaining.Code);
        Assert.Equal(McpResultHandleStore.SymbolsFromResultIdArgument, tooFewRemaining.Parameter);
    }

    [Fact]
    public async Task RunScreener_And_FilterTickers_StoreFullResultsAndSupportPreviewSuppression()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string screenerCondition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":10}""";

        // 1. Preview truncated (maxReturnedMatches: 1) still stores ALL 3 symbols in the handle
        using JsonDocument previewDoc = await CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{screenerCondition}},"maxReturnedMatches":1,"storeResult":true}""");
        JsonElement preview = Ok(previewDoc);
        Assert.Equal(3, preview.GetProperty("matchedSymbolCount").GetInt32());
        Assert.Equal(1, preview.GetProperty("returnedMatchCount").GetInt32());
        Assert.Single(preview.GetProperty("matchedSymbols").EnumerateArray());
        string previewHandle = preview.GetProperty("resultId").GetString()!;

        using JsonDocument metaFromPreview = await CallAsync(dispatcher, "sa_get_ticker_metadata", $$"""{"symbolsFromResultId":"{{previewHandle}}"}""");
        Assert.Equal(3, Ok(metaFromPreview).GetProperty("uniqueCount").GetInt32());

        // 2. includeMatchedSymbols: false suppresses matchedSymbols & ranking while storing all 3 symbols
        using JsonDocument suppressedScreener = await CallAsync(
            dispatcher,
            "sa_run_screener",
            $$"""{"conditionTree":{{screenerCondition}},"sortBy":{"price":"Close"},"storeResult":true,"includeMatchedSymbols":false}""");
        JsonElement suppressedBody = Ok(suppressedScreener);
        Assert.Equal(3, suppressedBody.GetProperty("matchedSymbolCount").GetInt32());
        Assert.Equal(0, suppressedBody.GetProperty("returnedMatchCount").GetInt32());
        Assert.False(suppressedBody.GetProperty("truncated").GetBoolean());
        Assert.Empty(suppressedBody.GetProperty("matchedSymbols").EnumerateArray());
        Assert.False(suppressedBody.TryGetProperty("ranking", out _));
        string suppressedHandle = suppressedBody.GetProperty("resultId").GetString()!;

        using JsonDocument batchFromSuppressed = await CallAsync(
            dispatcher,
            "sa_calculate_indicators_batch",
            $$$"""{"symbolsFromResultId":"{{{suppressedHandle}}}","timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}]}""");
        Assert.Equal(3, Ok(batchFromSuppressed).GetProperty("symbolCount").GetInt32());

        // 3. sa_filter_tickers: limit: 1 stores all 3 symbols; includeMatches: false suppresses matches while storing all 3
        using JsonDocument pagedFilter = await CallAsync(dispatcher, "sa_filter_tickers", """{"limit":1,"storeResult":true}""");
        JsonElement pagedBody = Ok(pagedFilter);
        Assert.Equal(3, pagedBody.GetProperty("totalMatchCount").GetInt32());
        Assert.Equal(1, pagedBody.GetProperty("returnedCount").GetInt32());
        string pagedFilterHandle = pagedBody.GetProperty("resultId").GetString()!;

        using JsonDocument metaFromPagedFilter = await CallAsync(dispatcher, "sa_get_ticker_metadata", $$"""{"symbolsFromResultId":"{{pagedFilterHandle}}"}""");
        Assert.Equal(3, Ok(metaFromPagedFilter).GetProperty("uniqueCount").GetInt32());

        using JsonDocument suppressedFilter = await CallAsync(dispatcher, "sa_filter_tickers", """{"storeResult":true,"includeMatches":false}""");
        JsonElement suppressedFilterBody = Ok(suppressedFilter);
        Assert.Equal(3, suppressedFilterBody.GetProperty("totalMatchCount").GetInt32());
        Assert.Equal(0, suppressedFilterBody.GetProperty("returnedCount").GetInt32());
        Assert.False(suppressedFilterBody.GetProperty("hasMore").GetBoolean());
        Assert.Empty(suppressedFilterBody.GetProperty("matches").EnumerateArray());
        string suppressedFilterHandle = suppressedFilterBody.GetProperty("resultId").GetString()!;

        using JsonDocument metaFromSuppressedFilter = await CallAsync(dispatcher, "sa_get_ticker_metadata", $$"""{"symbolsFromResultId":"{{suppressedFilterHandle}}"}""");
        Assert.Equal(3, Ok(metaFromSuppressedFilter).GetProperty("uniqueCount").GetInt32());

        // 4. Safeguard 5: suppressing without storeResult: true must fail with INVALID_ARGUMENT
        using JsonDocument noStoreScreener = await CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{screenerCondition}},"includeMatchedSymbols":false}""");
        Assert.True(McpTestSupport.IsError(noStoreScreener));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(noStoreScreener));
        Assert.Equal("includeMatchedSymbols", McpTestSupport.Structured(noStoreScreener).GetProperty("param").GetString());

        using JsonDocument noStoreFilter = await CallAsync(dispatcher, "sa_filter_tickers", """{"includeMatches":false}""");
        Assert.True(McpTestSupport.IsError(noStoreFilter));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(noStoreFilter));
        Assert.Equal("includeMatches", McpTestSupport.Structured(noStoreFilter).GetProperty("param").GetString());
    }

    [Fact]
    public async Task ConsumerTools_PaginateStoredHandleWithResultOffsetAndResultLimit_AndMetadataSupportsHandles()
    {
        // Seed a 55-symbol fixture to verify >50 handle pagination across all 5 consumer tools
        string[] fiftyFive = Enumerable.Range(1, 55).Select(i => $"T{i:D2}").ToArray();
        using ReadOnlyDataTreeFixture largeFixture = ReadOnlyDataTreeFixture.Create(60, fiftyFive);
        decimal[] closes = Enumerable.Range(0, 60).Select(i => 100m + i * 0.5m).ToArray();
        foreach (string sym in fiftyFive)
        {
            StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(largeFixture.DailyDirectory, sym, closes);
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(largeFixture))
            .Build();
        using McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        string screenerCondition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":10}""";
        using JsonDocument stored = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_run_screener",
            $$"""{"conditionTree":{{screenerCondition}},"storeResult":true,"includeMatchedSymbols":false}"""))!;
        JsonElement storedBody = Ok(stored);
        Assert.Equal(55, storedBody.GetProperty("matchedSymbolCount").GetInt32());
        Assert.False(storedBody.GetProperty("truncated").GetBoolean());
        Assert.Empty(storedBody.GetProperty("matchedSymbols").EnumerateArray());
        string handleId = storedBody.GetProperty("resultId").GetString()!;

        // Without resultLimit, a >50 handle must still fail with LIMIT_EXCEEDED (no silent truncation)
        using JsonDocument batchUnpaged = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_calculate_indicators_batch",
            $$$"""{"symbolsFromResultId":"{{{handleId}}}","timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}]}"""))!;
        Assert.True(McpTestSupport.IsError(batchUnpaged));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(batchUnpaged));

        // Page 1 (0..49 = 50) & Page 2 (50..54 = 5) on sa_calculate_indicators_batch
        using JsonDocument batchPage1 = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_calculate_indicators_batch",
            $$$"""{"symbolsFromResultId":"{{{handleId}}}","resultOffset":0,"resultLimit":50,"timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}]}"""))!;
        using JsonDocument batchPage2 = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_calculate_indicators_batch",
            $$$"""{"symbolsFromResultId":"{{{handleId}}}","resultOffset":50,"resultLimit":50,"timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}]}"""))!;
        Assert.Equal(50, Ok(batchPage1).GetProperty("symbolCount").GetInt32());
        Assert.Equal(5, Ok(batchPage2).GetProperty("symbolCount").GetInt32());

        // Cross-asset stats slice
        using JsonDocument crossPage2 = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_calculate_cross_asset_stats",
            $$"""{"symbolsFromResultId":"{{handleId}}","resultOffset":50,"resultLimit":5,"timeFrame":"D1","lookbackBars":40}"""))!;
        Assert.Equal(5, Ok(crossPage2).GetProperty("symbols").GetArrayLength());

        // Portfolio backtest slice
        string backtestConds = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}}""";
        using JsonDocument portfolioPage2 = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_run_portfolio_backtest",
            $$"""{"symbolsFromResultId":"{{handleId}}","resultOffset":50,"resultLimit":5,"conditions":{{backtestConds}}}"""))!;
        Assert.Equal(5, Ok(portfolioPage2).GetProperty("symbolCount").GetInt32());

        // Market breadth slice
        using JsonDocument breadthPage1 = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_calculate_market_breadth",
            $$"""{"symbolsFromResultId":"{{handleId}}","resultOffset":0,"resultLimit":25,"timeFrame":"D1","lookbackBars":40,"outputTailCount":5}"""))!;
        Assert.Equal(25, Ok(breadthPage1).GetProperty("symbolCount").GetInt32());

        // sa_get_ticker_metadata handle consumption + slice + mutual exclusivity + resultOffset without handle
        using JsonDocument metaSlice = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_get_ticker_metadata",
            $$"""{"symbolsFromResultId":"{{handleId}}","resultOffset":50,"resultLimit":5}"""))!;
        Assert.Equal(5, Ok(metaSlice).GetProperty("uniqueCount").GetInt32());
        Assert.Equal("T51", Ok(metaSlice).GetProperty("results")[0].GetProperty("symbol").GetString());

        using JsonDocument metaConflict = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_get_ticker_metadata",
            $$"""{"symbols":["T01"],"symbolsFromResultId":"{{handleId}}"}"""))!;
        Assert.True(McpTestSupport.IsError(metaConflict));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(metaConflict));
        Assert.Equal(McpResultHandleStore.SymbolsFromResultIdArgument, McpTestSupport.Structured(metaConflict).GetProperty("param").GetString());

        using JsonDocument offsetWithoutHandle = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_get_ticker_metadata",
            """{"symbols":["T01"],"resultOffset":0}"""))!;
        Assert.True(McpTestSupport.IsError(offsetWithoutHandle));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(offsetWithoutHandle));
        Assert.Equal(McpResultHandleStore.ResultOffsetArgument, McpTestSupport.Structured(offsetWithoutHandle).GetProperty("param").GetString());
    }
}
