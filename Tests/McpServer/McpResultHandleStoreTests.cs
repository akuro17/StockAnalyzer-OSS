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
}
