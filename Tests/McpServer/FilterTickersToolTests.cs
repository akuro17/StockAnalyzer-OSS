using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>sa_filter_tickers through the real composition on a throw-away data tree: universe, rules, paging, fields, refusals and parity with a saved filter.</summary>
public sealed class FilterTickersToolTests : IDisposable
{
    private const string Msft = "MSFT";
    private const string Aapl = "AAPL";
    private const string Nvda = "NVDA";
    private const string NoBars = "NOBARS";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(5, Msft, Aapl, Nvda, NoBars);
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly string _workspacePath;

    public FilterTickersToolTests()
    {
        string configDir = Path.Combine(_fixture.Root, "Config");
        Directory.CreateDirectory(configDir);
        _workspacePath = Path.Combine(configDir, LayoutConstants.DefaultWorkspaceFileName);

        // Last two closes: MSFT 100 -> 110 (+10 %), AAPL 200 -> 190 (-5 %), NVDA 50 -> 55 (+10 %). NOBARS has a ticker-list entry and no file.
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Msft, new[] { 100m, 110m });
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Aapl, new[] { 200m, 190m });
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Nvda, new[] { 50m, 55m });
        File.Delete(Path.Combine(_fixture.DailyDirectory, NoBars + ".parquet"));
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task SeedMetadataAsync()
    {
        await _fixture.SeedMetadataAsync(Msft, new TickerMetadata { ShortName = "Microsoft", Sector = "Technology", Tag = "AI, Cloud" });
        await _fixture.SeedMetadataAsync(Aapl, new TickerMetadata { ShortName = "Apple", Sector = "Technology", Tag = "Hardware" });
        await _fixture.SeedMetadataAsync(Nvda, new TickerMetadata { ShortName = "Nvidia", Sector = "Semiconductors", Tag = "AI" });
        await _fixture.SeedMetadataAsync(NoBars, new TickerMetadata { ShortName = "No Bars", Sector = "Technology" });
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()), workspaceSource: new FileReadOnlyWorkspaceSource(_workspacePath));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_filter_tickers", argumentsJson))!;

    private static string[] Symbols(JsonElement result)
        => result.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("symbol").GetString()!).ToArray();

    private static void AssertError(JsonDocument response, string code, string parameter)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    private const string CloseAbove100 = """{"field":"Close","operator":">","value":"100"}""";

    // ---- universe and rules ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RulesOverASymbolList_ReturnTheMatchesSortedAndDescribeTheUniverse()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, $$"""{"symbols":["NVDA","MSFT","AAPL"],"rules":[{{CloseAbove100}}]}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { Aapl, Msft }, Symbols(result));
        Assert.Equal(2, result.GetProperty("totalMatchCount").GetInt32());
        Assert.Equal(1, result.GetProperty("ruleCount").GetInt32());
        JsonElement universe = result.GetProperty("universe");
        Assert.Equal("symbols", universe.GetProperty("kind").GetString());
        Assert.Equal(3, universe.GetProperty("inputCount").GetInt32());
        Assert.Equal(0, universe.GetProperty("unregisteredCount").GetInt32());
        Assert.Equal(3, universe.GetProperty("evaluatedCount").GetInt32());
        Assert.False(result.GetProperty("hasMore").GetBoolean());
        Assert.Empty(result.GetProperty("warnings").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task AllRulesMustHold()
    {
        await SeedMetadataAsync();
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher,
            $$"""{"symbols":["MSFT","AAPL","NVDA"],"rules":[{{CloseAbove100}},{"field":"Tag","operator":"ContainsTag","value":"AI"}]}""");

        Assert.Equal(new[] { Msft }, Symbols(McpTestSupport.Structured(response)));
    }

    [Fact]
    public async Task NoSymbolsAndNoGroup_FiltersEveryRegisteredSymbol_AndASymbolWithoutBarsMatchesNoPriceRule()
    {
        await SeedMetadataAsync();
        var dispatcher = await StartAsync();

        using var priceRule = await CallAsync(dispatcher, """{"rules":[{"field":"Close","operator":">","value":"0"}]}""");
        using var metadataRule = await CallAsync(dispatcher, """{"rules":[{"field":"Sector","operator":"==","value":"Technology"}]}""");

        JsonElement price = McpTestSupport.Structured(priceRule);
        Assert.Equal("all", price.GetProperty("universe").GetProperty("kind").GetString());
        Assert.Equal(4, price.GetProperty("universe").GetProperty("inputCount").GetInt32());
        Assert.Equal(new[] { Aapl, Msft, Nvda }, Symbols(price));
        Assert.Equal(new[] { Aapl, Msft, NoBars }, Symbols(McpTestSupport.Structured(metadataRule)));
    }

    [Fact]
    public async Task WithoutRules_TheWholeUniverseIsReturned()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["NVDA","AAPL"]}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { Aapl, Nvda }, Symbols(result));
        Assert.Equal(0, result.GetProperty("ruleCount").GetInt32());
    }

    [Fact]
    public async Task AnEmptyRuleList_IsTheSameAsNoRules()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["NVDA","AAPL"],"rules":[]}""");

        Assert.Equal(new[] { Aapl, Nvda }, Symbols(McpTestSupport.Structured(response)));
    }

    [Fact]
    public async Task ARuleThatMatchesNothing_IsAnEmptyResultNotAnError()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"],"rules":[{"field":"Close","operator":">","value":"1000000"}]}""");

        Assert.False(McpTestSupport.IsError(response));
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(0, result.GetProperty("totalMatchCount").GetInt32());
        Assert.Equal(0, result.GetProperty("returnedCount").GetInt32());
        Assert.False(result.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task ComparingTwoFields_Works()
    {
        var dispatcher = await StartAsync();

        // Close above Open: the seeded bars open 0.2 below their close.
        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT","AAPL"],"rules":[{"field":"Close","operator":">","value":"Open","isCompareToField":true}]}""");

        Assert.Equal(new[] { Aapl, Msft }, Symbols(McpTestSupport.Structured(response)));
    }

    [Fact]
    public async Task SymbolsAreCanonicalisedDeduplicatedAndUnregisteredOnesAreCountedAndLeftOut()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["msft","MSFT","nosuch","ALSONO"],"rules":[{"field":"Close","operator":">","value":"0"}]}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        JsonElement universe = result.GetProperty("universe");
        Assert.Equal(4, universe.GetProperty("inputCount").GetInt32());
        Assert.Equal(2, universe.GetProperty("unregisteredCount").GetInt32());
        Assert.Equal(1, universe.GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(new[] { Msft }, Symbols(result));
        string warning = Assert.Single(result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray());
        Assert.Contains("ALSONO", warning);
        Assert.Contains("NOSUCH", warning);
    }

    [Fact]
    public async Task ASymbolListOfOnlyUnregisteredSymbols_IsAnEmptyUniverseNotAnError()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["NOSUCH"],"rules":[{"field":"Close","operator":">","value":"1"}]}""");

        Assert.False(McpTestSupport.IsError(response));
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(0, result.GetProperty("universe").GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("totalMatchCount").GetInt32());
    }

    // ---- groups, parity with a saved filter -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGroupIsTheUniverse_AndItsUnregisteredMembersAreReported()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Mine", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("NVDA", DateTimeOffset.UtcNow),
                    new WatchlistItem("GONE", DateTimeOffset.UtcNow),
                }),
            },
        };
        await new WorkspaceSerializationService().SaveAsync(workspace, _workspacePath);
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, $$"""{"groupId":"{{watchlistId}}","rules":[{{CloseAbove100}}]}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        JsonElement universe = result.GetProperty("universe");
        Assert.Equal("group", universe.GetProperty("kind").GetString());
        Assert.Equal(watchlistId.ToString("D").ToLowerInvariant(), universe.GetProperty("groupId").GetString());
        Assert.Equal(1, universe.GetProperty("unregisteredCount").GetInt32());
        Assert.Equal(2, universe.GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(new[] { Msft }, Symbols(result));
        Assert.Single(result.GetProperty("warnings").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task InlineRulesGiveTheSameSymbolsAsTheSameRulesSavedAsATickersTabFilter()
    {
        await SeedMetadataAsync();
        var filterId = Guid.NewGuid();
        var rules = new List<FilterRule>
        {
            new() { Field = "Close", Operator = ">", Value = "100" },
            new() { Field = "Sector", Operator = "==", Value = "Technology" },
        };
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings> { new() { Id = filterId, ParentId = TickerGroupIds.AllTickers, Name = "Saved", Rules = rules } },
        };
        await new WorkspaceSerializationService().SaveAsync(workspace, _workspacePath);
        var dispatcher = await StartAsync();

        using var saved = (await McpTestSupport.CallAsync(dispatcher, "sa_list_tickers", $$"""{"groupId":"{{filterId}}"}"""))!;
        using var inline = await CallAsync(dispatcher,
            """{"rules":[{"field":"Close","operator":">","value":"100"},{"field":"Sector","operator":"==","value":"Technology"}]}""");

        string[] expected = McpTestSupport.Structured(saved).GetProperty("tickers").EnumerateArray().Select(t => t.GetProperty("symbol").GetString()!).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Symbols(McpTestSupport.Structured(inline)));
    }

    [Fact]
    public async Task SymbolsAndGroupIdTogether_AreRefused()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, $$"""{"symbols":["MSFT"],"groupId":"{{Guid.NewGuid()}}"}""");

        AssertError(response, "INVALID_ARGUMENT", "groupId");
    }

    [Fact]
    public async Task AnInvalidOrUnknownGroupId_IsRefusedWithTheGroupCodes()
    {
        var dispatcher = await StartAsync();

        using var invalid = await CallAsync(dispatcher, """{"groupId":"not-a-guid"}""");
        using var unknown = await CallAsync(dispatcher, $$"""{"groupId":"{{Guid.NewGuid()}}"}""");

        AssertError(invalid, "INVALID_ARGUMENT", "groupId");
        AssertError(unknown, "GROUP_NOT_FOUND", "groupId");
    }

    // ---- fields and paging ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IncludeFields_AddsTheColumnsInTheRequestedOrderWithTheirUnits()
    {
        await SeedMetadataAsync();
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher,
            """{"symbols":["AAPL","NOBARS"],"includeFields":["ChangePercent","Close","Sector","Tag","Volume","LastUpdatedUtc","IsLong"]}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement matches = McpTestSupport.Structured(response).GetProperty("matches");
        JsonElement apple = matches[0].GetProperty("fields");
        Assert.Equal(new[] { "ChangePercent", "Close", "Sector", "Tag", "Volume", "LastUpdatedUtc", "IsLong" }, apple.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(-5.0, apple.GetProperty("ChangePercent").GetDouble(), 6);
        Assert.Equal(190m, apple.GetProperty("Close").GetDecimal());
        Assert.Equal("Technology", apple.GetProperty("Sector").GetString());
        Assert.Equal("Hardware", apple.GetProperty("Tag").GetString());
        Assert.Equal(JsonValueKind.Number, apple.GetProperty("Volume").ValueKind);
        Assert.Equal(JsonValueKind.String, apple.GetProperty("LastUpdatedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, apple.GetProperty("IsLong").ValueKind);

        // A symbol without stored bars: price columns are the row's zero values or null, never an error.
        JsonElement empty = matches[1].GetProperty("fields");
        Assert.Equal("Technology", empty.GetProperty("Sector").GetString());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("LastUpdatedUtc").ValueKind);
    }

    [Fact]
    public async Task IncludeFields_AcceptsAliasesAndReportsADuplicateOnce()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"],"includeFields":["Change %","Close","close"]}""");

        JsonElement fields = McpTestSupport.Structured(response).GetProperty("matches")[0].GetProperty("fields");
        Assert.Equal(new[] { "Change %", "Close" }, fields.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(10.0, fields.GetProperty("Change %").GetDouble(), 6);
    }

    [Fact]
    public async Task WithoutIncludeFields_MatchesCarryOnlyTheSymbol()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"]}""");

        JsonElement match = McpTestSupport.Structured(response).GetProperty("matches")[0];
        Assert.False(match.TryGetProperty("fields", out _));
    }

    [Fact]
    public async Task Paging_SkipsAndLimitsAndSaysWhetherThereIsMore()
    {
        var dispatcher = await StartAsync();

        using var first = await CallAsync(dispatcher, """{"symbols":["MSFT","AAPL","NVDA"],"limit":2}""");
        using var second = await CallAsync(dispatcher, """{"symbols":["MSFT","AAPL","NVDA"],"limit":2,"offset":2}""");
        using var beyond = await CallAsync(dispatcher, """{"symbols":["MSFT","AAPL","NVDA"],"offset":10}""");

        JsonElement a = McpTestSupport.Structured(first);
        Assert.Equal(new[] { Aapl, Msft }, Symbols(a));
        Assert.Equal(3, a.GetProperty("totalMatchCount").GetInt32());
        Assert.True(a.GetProperty("hasMore").GetBoolean());
        JsonElement b = McpTestSupport.Structured(second);
        Assert.Equal(new[] { Nvda }, Symbols(b));
        Assert.False(b.GetProperty("hasMore").GetBoolean());
        JsonElement c = McpTestSupport.Structured(beyond);
        Assert.Empty(Symbols(c));
        Assert.Equal(3, c.GetProperty("totalMatchCount").GetInt32());
        Assert.False(c.GetProperty("hasMore").GetBoolean());
    }

    // ---- screener condition (T3) ------------------------------------------------------------------------------------------------------

    private const string CloseAbove150 = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":150}""";
    private const string CloseAbovePreviousClose = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";

    private static string ScreenerCall(string symbolsJson, string condition, string rulesJson = "[]")
        => $$$"""{"symbols":{{{symbolsJson}}},"rules":{{{rulesJson}}},"screener":{"conditionTree":{{{condition}}}}}""";

    private static string[] ScreenedSymbols(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).ToArray();

    [Fact]
    public async Task AScreenerConditionKeepsOnlyTheSymbolsThatMeetIt()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ScreenerCall("""["MSFT","AAPL","NVDA"]""", CloseAbove150));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { Aapl }, Symbols(result));
        Assert.True(result.GetProperty("screenerApplied").GetBoolean());
        Assert.Equal(3, result.GetProperty("screener").GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(1, result.GetProperty("totalMatchCount").GetInt32());
    }

    [Fact]
    public async Task TheScreenerRunsOnTheSymbolsTheRulesKept()
    {
        await SeedMetadataAsync();
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher,
            ScreenerCall("""["MSFT","AAPL","NVDA"]""", CloseAbovePreviousClose, """[{"field":"Sector","operator":"==","value":"Technology"}]"""));

        // Rules keep MSFT and AAPL; of those only MSFT closed above its previous close (110 > 100; AAPL 190 < 200).
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { Msft }, Symbols(result));
        Assert.Equal(2, result.GetProperty("screener").GetProperty("evaluatedSymbolCount").GetInt32());
    }

    [Theory]
    [InlineData(CloseAbove150)]
    [InlineData(CloseAbovePreviousClose)]
    [InlineData("""{"operator":"Or","children":[{"left":{"price":"Close"},"comparison":"LessThan","right":60},{"left":{"price":"Close"},"comparison":"GreaterThan","right":150}]}""")]
    [InlineData("""{"left":{"indicator":"SMA","parameters":{"Period":2}},"comparison":"GreaterThan","right":100}""")]
    public async Task TheResultEqualsWhatSaRunScreenerReturnsForTheSameConditionAndSymbols(string condition)
    {
        var dispatcher = await StartAsync();

        using var inline = await CallAsync(dispatcher, ScreenerCall("""["MSFT","AAPL","NVDA"]""", condition));
        using var screener = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{condition}},"symbols":["MSFT","AAPL","NVDA"]}"""))!;

        Assert.False(McpTestSupport.IsError(inline), McpTestSupport.Structured(inline).ToString());
        Assert.False(McpTestSupport.IsError(screener), McpTestSupport.Structured(screener).ToString());
        Assert.Equal(ScreenedSymbols(screener), Symbols(McpTestSupport.Structured(inline)));
        JsonElement screenerBlock = McpTestSupport.Structured(inline).GetProperty("screener");
        Assert.Equal(McpTestSupport.Structured(screener).GetProperty("treeNodeCount").GetInt32(), screenerBlock.GetProperty("treeNodeCount").GetInt32());
        Assert.Equal(McpTestSupport.Structured(screener).GetProperty("treeDepth").GetInt32(), screenerBlock.GetProperty("treeDepth").GetInt32());
    }

    [Fact]
    public async Task AScreenerWithoutRulesRunsOnTheWholeUniverse_AndOnAGroup()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Mine", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[]
                {
                    new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
                    new WatchlistItem("NVDA", DateTimeOffset.UtcNow),
                }),
            },
        };
        await new WorkspaceSerializationService().SaveAsync(workspace, _workspacePath);
        var dispatcher = await StartAsync();

        using var all = await CallAsync(dispatcher, $$$"""{"screener":{"conditionTree":{{{CloseAbovePreviousClose}}}}}""");
        using var group = await CallAsync(dispatcher, $$$"""{"groupId":"{{{watchlistId}}}","screener":{"conditionTree":{{{CloseAbovePreviousClose}}}}}""");

        Assert.Equal(new[] { Msft, Nvda }, Symbols(McpTestSupport.Structured(all)));
        Assert.Equal(new[] { Nvda }, Symbols(McpTestSupport.Structured(group)));
    }

    [Fact]
    public async Task WhenTheRulesKeepNothing_TheScreenerIsAppliedToZeroSymbolsWithoutError()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher,
            ScreenerCall("""["MSFT"]""", CloseAbove150, """[{"field":"Close","operator":">","value":"1000000"}]"""));

        Assert.False(McpTestSupport.IsError(response));
        JsonElement result = McpTestSupport.Structured(response);
        Assert.True(result.GetProperty("screenerApplied").GetBoolean());
        Assert.Equal(0, result.GetProperty("screener").GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(0, result.GetProperty("totalMatchCount").GetInt32());
    }

    [Fact]
    public async Task WithoutAScreener_ScreenerAppliedIsFalseAndThereIsNoScreenerBlock()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"]}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(result.GetProperty("screenerApplied").GetBoolean());
        Assert.False(result.TryGetProperty("screener", out _));
    }

    [Fact]
    public async Task ScreenerMatchesCarryTheRequestedFieldsAndPageLikeAnyOtherMatch()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher,
            $$"""{"symbols":["MSFT","AAPL","NVDA"],"screener":{"conditionTree":{{CloseAbovePreviousClose}}},"includeFields":["Close"],"limit":1,"offset":1}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(2, result.GetProperty("totalMatchCount").GetInt32());
        Assert.Equal(new[] { Nvda }, Symbols(result));
        Assert.Equal(55m, result.GetProperty("matches")[0].GetProperty("fields").GetProperty("Close").GetDecimal());
        Assert.False(result.GetProperty("hasMore").GetBoolean());
    }

    private const string CorrelationWithAapl = """{"left":{"indicator":"Correlation","parameters":{"Period":2,"ComparisonSymbol":"AAPL"}},"comparison":"GreaterThan","right":-2}""";
    private const string CorrelationWithGhost = """{"left":{"indicator":"Correlation","parameters":{"Period":2,"ComparisonSymbol":"GHOST"}},"comparison":"GreaterThan","right":-2}""";
    private const string GhostPath = "screener.conditionTree.left.parameters.ComparisonSymbol";

    [Fact]
    public async Task AScreenerComparisonSymbolThatIsInTheTickerListIsAccepted()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ScreenerCall("""["MSFT"]""", CorrelationWithAapl));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.True(McpTestSupport.Structured(response).GetProperty("screenerApplied").GetBoolean());
    }

    [Fact]
    public async Task AnUnknownScreenerComparisonSymbolIsRefused()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ScreenerCall("""["MSFT"]""", CorrelationWithGhost));

        AssertError(response, "SYMBOL_NOT_FOUND", GhostPath);
    }

    [Fact]
    public async Task AnUnknownScreenerComparisonSymbolIsRefused_EvenWhenTheRulesLeaveNoSymbolForTheScreener()
    {
        var dispatcher = await StartAsync();

        // Close > 1000 keeps nothing, so the screener would never run; the answer must not depend on that.
        using var response = await CallAsync(dispatcher,
            ScreenerCall("""["MSFT","AAPL"]""", CorrelationWithGhost, """[{"field":"Close","operator":">","value":"1000"}]"""));

        AssertError(response, "SYMBOL_NOT_FOUND", GhostPath);
    }

    [Theory]
    [InlineData("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":150,"extra":1}""")]
    [InlineData("""{"left":{"indicator":"NoSuchIndicator"},"comparison":"GreaterThan","right":1}""")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"Sideways","right":1}""")]
    public async Task ABadConditionIsRefusedWithAPathInsideScreenerConditionTree(string condition)
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ScreenerCall("""["MSFT"]""", condition));

        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.StartsWith("screener.conditionTree", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task ABadConditionIsRefusedBeforeAnySymbolIsLookedAt()
    {
        var dispatcher = await StartAsync();

        // The symbol does not exist; the condition error must win, because everything the client sent is checked before data is read.
        using var response = await CallAsync(dispatcher, ScreenerCall("""["NOSUCH"]""", """{"left":{"price":"Close"},"comparison":"Sideways","right":1}"""));

        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task MoreSymbolsThanTheScreenerLimitReachingTheScreener_AreRefusedNotCut()
    {
        var dispatcher = await StartAsync(("MaxScreenerSymbols", "2"));

        using var response = await CallAsync(dispatcher, ScreenerCall("""["MSFT","AAPL","NVDA"]""", CloseAbove150));

        AssertError(response, "LIMIT_EXCEEDED", "screener.conditionTree");
    }

    [Fact]
    public async Task RulesThatNarrowTheUniverseBelowTheScreenerLimit_LetTheCallThrough()
    {
        var dispatcher = await StartAsync(("MaxScreenerSymbols", "2"));

        using var response = await CallAsync(dispatcher,
            ScreenerCall("""["MSFT","AAPL","NVDA"]""", CloseAbove150, """[{"field":"Close","operator":">","value":"100"}]"""));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal(new[] { Aapl }, Symbols(McpTestSupport.Structured(response)));
    }

    // ---- refusals ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownField_IsRefusedNamingTheRuleNotAnEmptyResult()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, $$"""{"symbols":["MSFT"],"rules":[{{CloseAbove100}},{"field":"NoSuchColumn","operator":">","value":"1"}]}""");

        AssertError(response, "INVALID_ARGUMENT", "rules[1].field");
    }

    [Fact]
    public async Task AnOperatorTheFieldKindCannotUse_IsRefusedNamingTheOperator()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"],"rules":[{"field":"Close","operator":"Contains","value":"1"}]}""");

        AssertError(response, "INVALID_ARGUMENT", "rules[0].operator");
        Assert.Contains("Valid:", McpTestSupport.Structured(response).GetProperty("message").GetString());
    }

    [Fact]
    public async Task ANonNumericValueForANumericField_IsRefusedNamingTheValue()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"],"rules":[{"field":"Close","operator":">","value":"high"}]}""");

        AssertError(response, "INVALID_ARGUMENT", "rules[0].value");
    }

    [Fact]
    public async Task ComparingFieldsOfDifferentKinds_IsRefusedNamingTheValue()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"],"rules":[{"field":"Close","operator":">","value":"Sector","isCompareToField":true}]}""");

        AssertError(response, "INVALID_ARGUMENT", "rules[0].value");
    }

    [Theory]
    [InlineData("""{"symbols":["MSFT"],"rules":[{"field":"Close","value":"1"}]}""", "rules[0].operator")]
    [InlineData("""{"symbols":["MSFT"],"rules":[{"operator":">","value":"1"}]}""", "rules[0].field")]
    [InlineData("""{"symbols":["MSFT"],"rules":[{"field":"Close","operator":">"}]}""", "rules[0].value")]
    [InlineData("""{"symbols":["MSFT"],"rules":[{"field":"Close","operator":">","value":1}]}""", "rules[0].value")]
    [InlineData("""{"symbols":["MSFT"],"rules":[{"field":"Close","operator":">","value":"1","compareMode":"Numeric"}]}""", "rules[0].compareMode")]
    [InlineData("""{"symbols":["MSFT"],"rules":{"field":"Close"}}""", "rules")]
    [InlineData("""{"symbols":["MSFT"],"screener":{}}""", "screener.conditionTree")]
    [InlineData("""{"symbols":["MSFT"],"screener":{"conditionTree":{},"templateId":"x"}}""", "screener.templateId")]
    [InlineData("""{"symbols":["MSFT"],"screener":[]}""", "screener")]
    [InlineData("""{"symbols":["MSFT"],"allowUnregistered":"yes"}""", "allowUnregistered")]
    [InlineData("""{"symbols":[]}""", "symbols")]
    [InlineData("""{"symbols":"MSFT"}""", "symbols")]
    [InlineData("""{"offset":-1}""", "offset")]
    [InlineData("""{"limit":0}""", "limit")]
    public async Task AMalformedCall_IsRefusedNamingTheArgument(string arguments, string parameter)
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, arguments);

        AssertError(response, "INVALID_ARGUMENT", parameter);
    }

    [Fact]
    public async Task AnUnknownIncludedField_IsRefusedNamingTheItem()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, """{"symbols":["MSFT"],"includeFields":["Close","NoSuchColumn"]}""");

        AssertError(response, "INVALID_ARGUMENT", "includeFields[1]");
    }

    [Theory]
    [InlineData("../MSFT")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    public async Task ASymbolThatIsNotWellFormed_IsRefused(string symbol)
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, $$"""{"symbols":[{{JsonSerializer.Serialize(symbol)}}]}""");

        AssertError(response, "INVALID_ARGUMENT", "symbols[0]");
    }

    // ---- limits -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MoreRulesThanTheLimit_AreRefusedNotCut()
    {
        var dispatcher = await StartAsync(("MaxInlineFilterRules", "2"));

        using var response = await CallAsync(dispatcher, $$"""{"symbols":["MSFT"],"rules":[{{CloseAbove100}},{{CloseAbove100}},{{CloseAbove100}}]}""");

        AssertError(response, "LIMIT_EXCEEDED", "rules");
    }

    [Fact]
    public async Task ExactlyTheLimitOfRules_IsAccepted()
    {
        var dispatcher = await StartAsync(("MaxInlineFilterRules", "2"));

        using var response = await CallAsync(dispatcher, $$"""{"symbols":["MSFT"],"rules":[{{CloseAbove100}},{{CloseAbove100}}]}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal(2, McpTestSupport.Structured(response).GetProperty("ruleCount").GetInt32());
    }

    [Fact]
    public async Task AUniverseLargerThanTheEvaluationLimit_IsRefusedForEveryWayOfNamingIt()
    {
        var dispatcher = await StartAsync(("MaxFilterEvaluationSymbols", "3"));

        using var all = await CallAsync(dispatcher, """{"rules":[{"field":"Close","operator":">","value":"0"}]}""");
        using var listed = await CallAsync(dispatcher, """{"symbols":["MSFT","AAPL","NVDA","NOBARS"]}""");

        AssertError(all, "LIMIT_EXCEEDED", "symbols");
        AssertError(listed, "LIMIT_EXCEEDED", "symbols");
    }

    [Fact]
    public async Task AGroupLargerThanTheEvaluationLimit_IsRefused()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Big", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
                    new WatchlistItem("NVDA", DateTimeOffset.UtcNow),
                }),
            },
        };
        await new WorkspaceSerializationService().SaveAsync(workspace, _workspacePath);
        var dispatcher = await StartAsync(("MaxFilterEvaluationSymbols", "2"));

        using var response = await CallAsync(dispatcher, $$"""{"groupId":"{{watchlistId}}"}""");

        AssertError(response, "LIMIT_EXCEEDED", "groupId");
    }

    [Fact]
    public async Task APageSizeAboveTheLimit_IsRefused()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, $$"""{"limit":{{new StockAnalyzer.McpServer.Limits.McpLimits().MaxTickersPerPage + 1}}}""");

        AssertError(response, "LIMIT_EXCEEDED", "limit");
    }

    // ---- read-only --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASessionOfCallsLeavesTheDataTreeUnchanged()
    {
        await SeedMetadataAsync();
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        foreach (string arguments in new[]
        {
            $$"""{"rules":[{{CloseAbove100}}],"includeFields":["Close","Sector"]}""",
            """{"symbols":["MSFT","NOSUCH"],"rules":[{"field":"Sector","operator":"==","value":"Technology"}]}""",
            """{"symbols":["MSFT"],"rules":[{"field":"Bogus","operator":">","value":"1"}]}""",
        })
        {
            (await CallAsync(dispatcher, arguments)).Dispose();
        }

        Assert.Equal(before, _fixture.Snapshot());
    }
}
