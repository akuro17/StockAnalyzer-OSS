using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_calculate_cross_asset_stats holdingPeriodMode through the tool: two markets that trade on weekdays, one of them closed on Monday 2024-01-15, so the date
/// 2024-01-16 combines returns over different holding periods (the open market's return covers 01-15 to 01-16, the other's 01-12 to 01-16).
/// </summary>
public sealed class HoldingPeriodModeToolTests : IDisposable
{
    private const string Tool = "sa_calculate_cross_asset_stats";
    private const string Open = "HPA";
    private const string ClosedOnMonday = "HPB";
    private static readonly DateTime FirstDay = new(2024, 1, 1);
    private static readonly DateTime Holiday = new(2024, 1, 15);
    private static readonly DateTime LastDay = new(2024, 3, 29);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(1);
    private readonly List<McpServerRuntime> _runtimes = new();

    public HoldingPeriodModeToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromDatedCloses(_fixture.DailyDirectory, Open, McpTestSupport.WeekdayCloses(FirstDay, LastDay, i => 100m + 0.9m * i + (i * i % 7)));
        ParquetFixtureBuilder.SeedDailyOhlcvFromDatedCloses(_fixture.DailyDirectory, ClosedOnMonday, McpTestSupport.WeekdayCloses(FirstDay, LastDay, i => 60m + 0.5m * i + (i * 5 % 9), Holiday));
        File.WriteAllText(_fixture.TickerListPath, JsonSerializer.Serialize(new[] { Open, ClosedOnMonday }));
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
        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build(), new LogSink(new StringWriter()), _fixture.DailyDirectory);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static string Args(string extra = "") => $$"""{"symbols":["{{Open}}","{{ClosedOnMonday}}"],"timeFrame":"D1"{{extra}}}""";

    private static async Task<JsonElement> CallAsync(McpMessageDispatcher dispatcher, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static async Task<JsonElement> CallExpectingErrorAsync(McpMessageDispatcher dispatcher, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, arguments))!;
        Assert.True(McpTestSupport.IsError(response), "Expected tool error but call succeeded");
        return McpTestSupport.Structured(response).Clone();
    }

    [Fact]
    public async Task WithoutTheArgument_TheOutputIsTheKnownShape_AndIncludeAllEqualsOmitted()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement omitted = await CallAsync(dispatcher, Args());
        JsonElement explicitIncludeAll = await CallAsync(dispatcher, Args(""","holdingPeriodMode":"IncludeAll" """));

        // 65 weekdays, 64 returns of the open market, 63 of the other (no bar on 01-15): 63 common dates, one of them mixed.
        Assert.Equal(63, omitted.GetProperty("commonDateCount").GetInt32());
        Assert.Equal(1, omitted.GetProperty("multiBarReturnCount").GetInt32());
        Assert.False(omitted.TryGetProperty("holdingPeriodMode", out _));
        Assert.False(omitted.TryGetProperty("excludedMultiBarDateCount", out _));
        Assert.Equal(omitted.GetRawText(), explicitIncludeAll.GetRawText());
    }

    [Fact]
    public async Task StrictEqualHoldingPeriod_LeavesTheMixedDateOut_AndNamesTheModeAndTheCount()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement all = await CallAsync(dispatcher, Args());
        JsonElement strict = await CallAsync(dispatcher, Args(""","holdingPeriodMode":"StrictEqualHoldingPeriod" """));

        Assert.Equal(62, strict.GetProperty("commonDateCount").GetInt32());
        Assert.Equal(1, strict.GetProperty("multiBarReturnCount").GetInt32());
        Assert.Equal("StrictEqualHoldingPeriod", strict.GetProperty("holdingPeriodMode").GetString());
        Assert.Equal(1, strict.GetProperty("excludedMultiBarDateCount").GetInt32());
        Assert.Contains(strict.GetProperty("notes").EnumerateArray().Select(n => n.GetString()), n => n!.Contains("were left out", StringComparison.Ordinal));
        Assert.NotEqual(all.GetProperty("correlation")[0][1].GetDouble(), strict.GetProperty("correlation")[0][1].GetDouble());
    }

    [Fact]
    public async Task AValueThatIsNotAModeNameIsRefusedAtTheArgument()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        foreach (string bad in new[] { "Strict", "strictequalholdingperiod", "1", "" })
        {
            JsonElement error = await CallExpectingErrorAsync(dispatcher, Args($$""","holdingPeriodMode":"{{bad}}" """));
            Assert.Equal(ToolErrorCodes.InvalidArgument, error.GetProperty("code").GetString());
            Assert.Equal("holdingPeriodMode", error.GetProperty("param").GetString());
        }
    }

    [Fact]
    public async Task StrictEqualHoldingPeriod_WithTooFewDatesLeft_FailsAndSaysHowManyWereLeftOut()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        // The newest 5 bars up to 2024-01-17: common return dates 01-12, 01-16, 01-17; 01-16 is mixed, leaving 2 of the 3 required.
        const string window = ""","endDate":"2024-01-17","lookbackBars":5""";

        JsonElement all = await CallAsync(dispatcher, Args(window));
        JsonElement error = await CallExpectingErrorAsync(dispatcher, Args(window + ""","holdingPeriodMode":"StrictEqualHoldingPeriod" """));

        Assert.Equal(3, all.GetProperty("commonDateCount").GetInt32());
        Assert.Equal(ToolErrorCodes.InsufficientMarketData, error.GetProperty("code").GetString());
        Assert.Contains("only 2 were found after leaving out 1", error.ToString());
    }

    [Fact]
    public async Task TheSchemaAndTheDescriptionNameTheArgument()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == Tool);
        JsonElement argument = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("holdingPeriodMode");

        Assert.Equal(new[] { "IncludeAll", "StrictEqualHoldingPeriod" }, argument.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray());
        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("holdingPeriodMode", description);
        Assert.Contains("docs/MCP/tools/sa_calculate_cross_asset_stats.md", description, StringComparison.Ordinal);
        string doc = System.IO.File.ReadAllText(System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "docs", "MCP", "tools", "sa_calculate_cross_asset_stats.md"));
        Assert.Contains("holdingPeriodMode IncludeAll (default)", doc);
        Assert.Contains("excludedMultiBarDateCount", doc);
        Assert.False(tool.TryGetProperty("outputSchema", out _)); // McpOutputSchemaTests: only three tools declare one
    }
}
