using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class ValidateConditionTreeToolTests : IDisposable
{
    private const int Days = 100;
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "7203.T");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(McpLimits? limits = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        if (limits is not null)
        {
            values["AiIntegration:MaxSecondarySymbolsPerCall"] = limits.MaxSecondarySymbolsPerCall.ToString();
        }

        McpServerRuntime runtime = McpServerComposition.Create(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            Path.GetTempPath());

        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    [Fact]
    public async Task ValidScreenerConditionTreeReturnsValidTrueAndStructuralMetrics()
    {
        var dispatcher = await StartAsync();
        string validTree = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":100}""";

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"screener","conditionTree":{{validTree}}}"""))!;
        Assert.False(McpTestSupport.IsError(response));

        JsonElement root = McpTestSupport.Structured(response);
        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Equal("screener", root.GetProperty("kind").GetString());
        Assert.Equal(2, root.GetProperty("treeNodeCount").GetInt32());
        Assert.Equal(1, root.GetProperty("treeDepth").GetInt32());

        JsonElement comparisons = root.GetProperty("comparisons");
        Assert.Equal(1, comparisons.GetArrayLength());
        Assert.Equal("L0", comparisons[0].GetProperty("id").GetString());
        Assert.Equal("conditionTree", comparisons[0].GetProperty("path").GetString());
        Assert.Contains("Close > 100", comparisons[0].GetProperty("text").GetString());

        // Proof (b): sa_run_screener does NOT fail binding with this condition
        using JsonDocument runResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{validTree}},"symbols":["MSFT"]}"""))!;
        Assert.False(McpTestSupport.IsError(runResponse));
    }

    [Fact]
    public async Task InvalidScreenerConditionMatchesBindingErrorOfRunScreener_ProofB()
    {
        var dispatcher = await StartAsync();
        // Unknown indicator name
        string invalidTree = """{"left":{"indicator":"NON_EXISTENT_INDICATOR"},"comparison":"GreaterThan","right":100}""";

        // 1. sa_validate_condition_tree
        using JsonDocument valResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"screener","conditionTree":{{invalidTree}}}"""))!;
        Assert.False(McpTestSupport.IsError(valResponse), "Validation tool itself should return a successful JSON payload with valid:false");

        JsonElement valRoot = McpTestSupport.Structured(valResponse);
        Assert.False(valRoot.GetProperty("valid").GetBoolean());
        Assert.Equal("screener", valRoot.GetProperty("kind").GetString());

        JsonElement valError = valRoot.GetProperty("error");
        string valCode = valError.GetProperty("code").GetString()!;
        string valPath = valError.GetProperty("path").GetString()!;
        string valMsg = valError.GetProperty("message").GetString()!;

        // 2. sa_run_screener with identical conditionTree
        using JsonDocument runResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{invalidTree}},"symbols":["MSFT"]}"""))!;
        Assert.True(McpTestSupport.IsError(runResponse));

        JsonElement runError = McpTestSupport.Structured(runResponse);
        string runCode = McpTestSupport.ErrorCodeOf(runResponse);
        string runPath = runError.GetProperty("param").GetString()!;
        string runMsg = runError.GetProperty("message").GetString()!;

        // Parity Proof (b)
        Assert.Equal(runCode, valCode);
        Assert.Equal(runPath, valPath);
        Assert.Equal(runMsg, valMsg);
    }

    [Fact]
    public async Task ScreenerConditionWithUnregisteredComparisonSymbolReturnsSymbolNotFound()
    {
        var dispatcher = await StartAsync();
        // Correlation requires ComparisonSymbol
        string tree = """{"left":{"indicator":"Correlation","parameters":{"Period":14,"ComparisonSymbol":"UNKNOWN_999"}},"comparison":"GreaterThan","right":0.5}""";

        using JsonDocument valResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"screener","conditionTree":{{tree}}}"""))!;
        Assert.False(McpTestSupport.IsError(valResponse));

        JsonElement valRoot = McpTestSupport.Structured(valResponse);
        Assert.False(valRoot.GetProperty("valid").GetBoolean());

        JsonElement valError = valRoot.GetProperty("error");
        Assert.Equal(ToolErrorCodes.SymbolNotFound, valError.GetProperty("code").GetString());
        Assert.Contains("UNKNOWN_999", valError.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ValidBacktestConditionsReturnsValidTrueAndConditionRoots()
    {
        var dispatcher = await StartAsync();
        string validConditions = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":100}}""";

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"backtest","conditions":{{validConditions}}}"""))!;
        Assert.False(McpTestSupport.IsError(response));

        JsonElement root = McpTestSupport.Structured(response);
        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Equal("backtest", root.GetProperty("kind").GetString());

        JsonElement conditionRoots = root.GetProperty("conditionRoots");
        Assert.Equal(1, conditionRoots.GetArrayLength());
        Assert.Equal("EntryLong", conditionRoots[0].GetProperty("root").GetString());
        Assert.Equal(1, conditionRoots[0].GetProperty("comparisonCount").GetInt32());

        JsonElement comparisons = root.GetProperty("comparisons");
        Assert.Equal(1, comparisons.GetArrayLength());
        Assert.Equal("C0", comparisons[0].GetProperty("id").GetString());
        Assert.Contains("Close > 100", comparisons[0].GetProperty("text").GetString());

        // Proof (b): sa_run_backtest does NOT fail binding with this condition
        using JsonDocument runResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{validConditions}}}"""))!;
        Assert.False(McpTestSupport.IsError(runResponse));
    }

    [Fact]
    public async Task InvalidBacktestConditionMatchesBindingErrorOfRunBacktest_ProofB()
    {
        var dispatcher = await StartAsync();
        // Empty condition group (invalid)
        string invalidConditions = """{"entryLong":{"operator":"And","children":[]}}""";

        // 1. sa_validate_condition_tree
        using JsonDocument valResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"backtest","conditions":{{invalidConditions}}}"""))!;
        Assert.False(McpTestSupport.IsError(valResponse));

        JsonElement valRoot = McpTestSupport.Structured(valResponse);
        Assert.False(valRoot.GetProperty("valid").GetBoolean());
        Assert.Equal("backtest", valRoot.GetProperty("kind").GetString());

        JsonElement valError = valRoot.GetProperty("error");
        string valCode = valError.GetProperty("code").GetString()!;
        string valPath = valError.GetProperty("path").GetString()!;
        string valMsg = valError.GetProperty("message").GetString()!;

        // 2. sa_run_backtest with identical conditions
        using JsonDocument runResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{invalidConditions}}}"""))!;
        Assert.True(McpTestSupport.IsError(runResponse));

        JsonElement runError = McpTestSupport.Structured(runResponse);
        string runCode = McpTestSupport.ErrorCodeOf(runResponse);
        string runPath = runError.GetProperty("param").GetString()!;
        string runMsg = runError.GetProperty("message").GetString()!;

        // Parity Proof (b)
        Assert.Equal(runCode, valCode);
        Assert.Equal(runPath, valPath);
        Assert.Equal(runMsg, valMsg);
    }

    [Fact]
    public async Task InvalidKindOrMissingPayloadFailsToolWithInvalidArgument()
    {
        var dispatcher = await StartAsync();

        // Unknown kind
        using JsonDocument res1 = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", """{"kind":"unknown"}"""))!;
        Assert.True(McpTestSupport.IsError(res1));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(res1));

        // kind=screener without conditionTree
        using JsonDocument res2 = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", """{"kind":"screener"}"""))!;
        Assert.True(McpTestSupport.IsError(res2));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(res2));

        // kind=backtest without conditions
        using JsonDocument res3 = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", """{"kind":"backtest"}"""))!;
        Assert.True(McpTestSupport.IsError(res3));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(res3));
    }

    [Fact]
    public async Task ScreenerWithSideTimeFrameAndCrossTickerIndicatorFailsBindingParity_ProofB()
    {
        var dispatcher = await StartAsync();
        // Cross-ticker indicator with a side timeFrame is rejected by ApplyLiteralFrames
        string tree = """{"left":{"indicator":"Correlation","timeFrame":"W1","parameters":{"Period":14,"ComparisonSymbol":"MSFT"}},"comparison":"GreaterThan","right":0.5}""";

        // 1. sa_validate_condition_tree
        using JsonDocument valResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"screener","conditionTree":{{tree}}}"""))!;
        Assert.False(McpTestSupport.IsError(valResponse));

        JsonElement valRoot = McpTestSupport.Structured(valResponse);
        Assert.False(valRoot.GetProperty("valid").GetBoolean());
        JsonElement valError = valRoot.GetProperty("error");
        string valCode = valError.GetProperty("code").GetString()!;
        string valPath = valError.GetProperty("path").GetString()!;

        // 2. sa_run_screener
        using JsonDocument runResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{tree}},"symbols":["MSFT"]}"""))!;
        Assert.True(McpTestSupport.IsError(runResponse));
        JsonElement runError = McpTestSupport.Structured(runResponse);
        string runCode = McpTestSupport.ErrorCodeOf(runResponse);
        string runPath = runError.GetProperty("param").GetString()!;

        Assert.Equal(runCode, valCode);
        Assert.Equal(runPath, valPath);
        Assert.Equal(ToolErrorCodes.InvalidArgument, valCode);
    }

    [Fact]
    public async Task ScreenerWithExcessiveSecondarySymbolsFailsWithLimitExceeded()
    {
        var dispatcher = await StartAsync(new McpLimits { MaxSecondarySymbolsPerCall = 1 });
        // 2 comparison symbols exceeds limit of 1
        string tree = """
        {
            "operator": "And",
            "children": [
                {"left":{"indicator":"Correlation","parameters":{"Period":14,"ComparisonSymbol":"MSFT"}},"comparison":"GreaterThan","right":0},
                {"left":{"indicator":"Correlation","parameters":{"Period":14,"ComparisonSymbol":"7203-T"}},"comparison":"GreaterThan","right":0}
            ]
        }
        """;

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"screener","conditionTree":{{tree}}}"""))!;
        Assert.False(McpTestSupport.IsError(response));

        JsonElement root = McpTestSupport.Structured(response);
        Assert.False(root.GetProperty("valid").GetBoolean());
        JsonElement error = root.GetProperty("error");
        Assert.Equal(ToolErrorCodes.LimitExceeded, error.GetProperty("code").GetString());
        Assert.Equal("conditionTree", error.GetProperty("path").GetString());
    }

    [Fact]
    public async Task CrossKindArgumentsRejectedWithInvalidArgument()
    {
        var dispatcher = await StartAsync();
        string validScreenerTree = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":100}""";
        string validBacktestConditions = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":100}}""";

        // kind=screener with conditions
        using JsonDocument res1 = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"screener","conditionTree":{{validScreenerTree}},"conditions":{{validBacktestConditions}}}"""))!;
        Assert.True(McpTestSupport.IsError(res1));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(res1));
        Assert.Equal("conditions", McpTestSupport.Structured(res1).GetProperty("param").GetString());

        // kind=screener with timeFrame
        using JsonDocument res2 = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"screener","conditionTree":{{validScreenerTree}},"timeFrame":"D1"}"""))!;
        Assert.True(McpTestSupport.IsError(res2));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(res2));
        Assert.Equal("timeFrame", McpTestSupport.Structured(res2).GetProperty("param").GetString());

        // kind=backtest with conditionTree
        using JsonDocument res3 = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"backtest","conditions":{{validBacktestConditions}},"conditionTree":{{validScreenerTree}}}"""))!;
        Assert.True(McpTestSupport.IsError(res3));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(res3));
        Assert.Equal("conditionTree", McpTestSupport.Structured(res3).GetProperty("param").GetString());
    }

    [Fact]
    public async Task BacktestWithUnsupportedTimeFrameReturnsValidFalseWithUnsupportedTimeFrameCode()
    {
        var dispatcher = await StartAsync();
        string validConditions = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":100}}""";

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_validate_condition_tree", $$"""{"kind":"backtest","conditions":{{validConditions}},"timeFrame":"M5"}"""))!;
        Assert.False(McpTestSupport.IsError(response));

        JsonElement root = McpTestSupport.Structured(response);
        Assert.False(root.GetProperty("valid").GetBoolean());
        Assert.Equal("backtest", root.GetProperty("kind").GetString());

        JsonElement error = root.GetProperty("error");
        Assert.Equal(ToolErrorCodes.UnsupportedTimeFrame, error.GetProperty("code").GetString());
        Assert.Equal("timeFrame", error.GetProperty("path").GetString());
    }
}
