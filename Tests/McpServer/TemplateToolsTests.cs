using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_list_templates and the templateId mode of sa_run_screener through the real composition, with the Screener templates in a temporary folder.
/// Bars as in <see cref="RunScreenerToolTests"/>: the latest close tells the symbols apart (MSFT and 7203-T about 200, AAPL about 125).
/// </summary>
public sealed class TemplateToolsTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(400, "msft", "7203.T", "aapl");
    private readonly string _templates = Path.Combine(TestDataIsolationBootstrap.NewTempDirectory("McpTemplates"), "Screener");
    private readonly List<McpServerRuntime> _runtimes = new();

    public TemplateToolsTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, "AAPL", 100);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_templates)!, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in overrides)
        {
            values[key] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()), _templates);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static ScreenerIndicatorEntry CloseAbove(decimal level, LogicalOperator join = LogicalOperator.And, bool enabled = true) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close" },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = level,
        LogicalOperator = join,
        IsEnabled = enabled,
    };

    private Guid Save(string name, bool favorite, params ScreenerIndicatorEntry[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = name, IsFavorite = favorite, Description = name + " description" };
        template.SetEntries(entries);
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        return template.Id;
    }

    private static async Task<JsonDocument> ListAsync(McpMessageDispatcher dispatcher)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_list_templates"))!;

    private static async Task<JsonDocument> RunTemplateAsync(McpMessageDispatcher dispatcher, string templateId, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"templateId":"{{templateId}}"{{extra}}}"""))!;

    private static string[] Matched(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).ToArray();

    private static string Code(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("code").GetString()!;

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    // ---- sa_list_templates ----------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutATemplateFolderTheListIsEmptyAndNothingIsCreated()
    {
        var dispatcher = await StartAsync();

        using var response = await ListAsync(dispatcher);

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(0, result.GetProperty("templateCount").GetInt32());
        Assert.Equal("Screener", result.GetProperty("templateType").GetString());
        Assert.False(Directory.Exists(_templates));
    }

    [Fact]
    public async Task TemplatesAreListedFavouritesFirstWithTheirEntriesAsText()
    {
        Guid plain = Save("Beta", false, CloseAbove(150m, LogicalOperator.Or), CloseAbove(120m));
        Guid favourite = Save("Zulu", true, CloseAbove(100m));
        var dispatcher = await StartAsync();

        using var response = await ListAsync(dispatcher);

        JsonElement[] templates = McpTestSupport.Structured(response).GetProperty("templates").EnumerateArray().ToArray();
        Assert.Equal(new[] { favourite.ToString(), plain.ToString() }, templates.Select(t => t.GetProperty("id").GetString()).ToArray());
        JsonElement beta = templates[1];
        Assert.Equal("Beta", beta.GetProperty("name").GetString());
        Assert.Equal("Beta description", beta.GetProperty("description").GetString());
        Assert.False(beta.GetProperty("isFavorite").GetBoolean());
        Assert.Equal(2, beta.GetProperty("entryCount").GetInt32());
        Assert.Equal(new[] { "Close > 150", "Close > 120" }, beta.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToArray());
        Assert.Equal(new[] { "Or", "And" }, beta.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("combinedWithNext").GetString()).ToArray());
        Assert.False(beta.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task EntriesThatCanNeverMatchAreReportedAsWarnings()
    {
        // A weekly side is evaluated now (each side reads its own frame): it is no problem. AnchoredVWAP has no definition on a weekly side.
        var weekly = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.RSI, OutputName = "RSI", TimeFrame = TimeFrame.W1 },
            Operator = ComparisonOperator.LessThan,
            RightNumericValue = 30m,
        };
        var anchoredWeekly = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.AnchoredVWAP, OutputName = "Main", TimeFrame = TimeFrame.W1 },
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 1m,
        };
        var lookAhead = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.ZigZag, OutputName = "Main" },
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 1m,
        };
        Save("Problems", false, weekly, anchoredWeekly, lookAhead, CloseAbove(100m, enabled: false), CloseAbove(100m));
        var dispatcher = await StartAsync();

        using var response = await ListAsync(dispatcher);

        string[] warnings = McpTestSupport.Structured(response).GetProperty("templates")[0].GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.DoesNotContain(warnings, warning => warning.Contains("Entry 0", StringComparison.Ordinal));
        Assert.Contains("W1", warnings[0]);
        Assert.Contains("Entry 1", warnings[0]);
        Assert.Contains("AnchoredVWAP", warnings[0]);
        Assert.Contains("ZigZag", warnings[1]);
        Assert.Contains("never matches", warnings[1]);
    }

    [Fact]
    public async Task AnUnreadableTemplateFileIsCountedNotListed()
    {
        Save("Good", false, CloseAbove(100m));
        File.WriteAllText(Path.Combine(_templates, $"{Guid.NewGuid()}.json"), "{ broken");
        var dispatcher = await StartAsync();

        using var response = await ListAsync(dispatcher);

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(1, result.GetProperty("templateCount").GetInt32());
        Assert.Equal(1, result.GetProperty("unreadableFileCount").GetInt32());
    }

    // A hand-edited template whose entry has no left side: the file is valid JSON, but the template type rejects the value while it is built.
    private Guid SaveWithNullLeftHand(string name)
    {
        Guid id = Save(name, false, CloseAbove(100m));
        string file = Path.Combine(_templates, $"{id}.json");
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        node["Entries"]![0]!["LeftHand"] = null;
        File.WriteAllText(file, node.ToJsonString());
        return id;
    }

    [Fact]
    public async Task RunningATemplateThatCannotBeBuiltIsTemplateNotFound()
    {
        Guid broken = SaveWithNullLeftHand("Broken");
        var dispatcher = await StartAsync();

        using var response = await RunTemplateAsync(dispatcher, broken.ToString());

        Assert.Equal("TEMPLATE_NOT_FOUND", Code(response));
        Assert.Equal("templateId", Param(response));
    }

    [Fact]
    public async Task ATemplateThatCannotBeRenderedIsCountedAsUnreadableAndTheListContinues()
    {
        Guid good = Save("Good", false, CloseAbove(100m));
        SaveWithNullLeftHand("Broken");
        var dispatcher = await StartAsync();

        using var response = await ListAsync(dispatcher);

        JsonElement result = McpTestSupport.Structured(response);
        Assert.True(result.TryGetProperty("templateCount", out _), result.GetRawText());
        Assert.Equal(1, result.GetProperty("templateCount").GetInt32());
        Assert.Equal(1, result.GetProperty("unreadableFileCount").GetInt32());
        Assert.Equal(good.ToString(), result.GetProperty("templates")[0].GetProperty("id").GetString());
    }

    // ---- sa_run_screener with templateId --------------------------------------------------------------------------

    [Fact]
    public async Task ATemplateGivesTheSameSetAsTheEquivalentConditionTree()
    {
        Guid id = Save("High close", false, CloseAbove(150m));
        var dispatcher = await StartAsync();

        using var byTemplate = await RunTemplateAsync(dispatcher, id.ToString());
        using var byTree = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", """{"conditionTree":{"left":{"price":"Close"},"comparison":"GreaterThan","right":150}}"""))!;

        Assert.Equal(new[] { "7203-T", "MSFT" }, Matched(byTemplate));
        Assert.Equal(Matched(byTree), Matched(byTemplate));
        JsonElement result = McpTestSupport.Structured(byTemplate);
        Assert.Equal(id.ToString(), result.GetProperty("template").GetProperty("id").GetString());
        Assert.Equal("High close", result.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal(3, result.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.False(result.TryGetProperty("treeNodeCount", out _));
    }

    [Fact]
    public async Task EntriesAreCombinedByTheOperatorOfThePrecedingEntryAndADisabledEntryIsIgnored()
    {
        // 100 < close of all three; 150 < close of MSFT and 7203-T only.
        Guid and = Save("And", false, CloseAbove(100m, LogicalOperator.And), CloseAbove(150m));
        Guid or = Save("Or", false, CloseAbove(150m, LogicalOperator.Or), CloseAbove(100m));
        Guid disabled = Save("Disabled", false, CloseAbove(100m, LogicalOperator.And), CloseAbove(100000m, enabled: false));
        var dispatcher = await StartAsync();

        using var andResponse = await RunTemplateAsync(dispatcher, and.ToString());
        using var orResponse = await RunTemplateAsync(dispatcher, or.ToString());
        using var disabledResponse = await RunTemplateAsync(dispatcher, disabled.ToString());

        Assert.Equal(new[] { "7203-T", "MSFT" }, Matched(andResponse));
        Assert.Equal(new[] { "7203-T", "AAPL", "MSFT" }, Matched(orResponse));
        Assert.Equal(new[] { "7203-T", "AAPL", "MSFT" }, Matched(disabledResponse));
    }

    [Fact]
    public async Task ASymbolListAndTheMatchLimitApplyToATemplateToo()
    {
        Guid id = Save("All", false, CloseAbove(100m));
        var dispatcher = await StartAsync();

        using var response = await RunTemplateAsync(dispatcher, id.ToString(), ""","symbols":["msft","aapl"],"maxReturnedMatches":1""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("Custom", result.GetProperty("universe").GetString());
        Assert.Equal(2, result.GetProperty("matchedSymbolCount").GetInt32());
        Assert.Equal(new[] { "AAPL" }, Matched(response));
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task MatchedValuesNameTheEnabledEntriesOfTheTemplate()
    {
        Guid id = Save("Values", false, CloseAbove(100000m, enabled: false), CloseAbove(150m));
        var dispatcher = await StartAsync();

        using var response = await RunTemplateAsync(dispatcher, id.ToString(), ""","includeMatchedValues":true,"maxReturnedMatches":1""");

        JsonElement result = McpTestSupport.Structured(response);
        JsonElement comparison = Assert.Single(result.GetProperty("comparisons").EnumerateArray());
        Assert.Equal("L0", comparison.GetProperty("id").GetString());
        Assert.Equal("entries[1]", comparison.GetProperty("path").GetString());
        Assert.Equal("Close > 150", comparison.GetProperty("text").GetString());
        JsonElement value = result.GetProperty("matchedValues").GetProperty("7203-T").GetProperty("L0");
        Assert.True(value.GetProperty("left").GetDecimal() > 150m);
        Assert.Equal(150m, value.GetProperty("right").GetDecimal());
    }

    [Fact]
    public async Task ATemplateWithNoEnabledEntryMatchesNothingAndSaysSo()
    {
        Guid id = Save("Off", false, CloseAbove(100m, enabled: false));
        var dispatcher = await StartAsync();

        using var response = await RunTemplateAsync(dispatcher, id.ToString());

        Assert.Empty(Matched(response));
        Assert.Contains("no enabled entry", McpTestSupport.Structured(response).GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task WarningsOfATemplateAreReturnedWithTheResult()
    {
        // A weekly side is evaluated (each side reads its own frame); AnchoredVWAP has no definition on one, so this entry never matches and says so.
        var weekly = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.AnchoredVWAP, OutputName = "Main", TimeFrame = TimeFrame.W1 },
            Operator = ComparisonOperator.LessThan,
            RightNumericValue = 30m,
        };
        Guid id = Save("Weekly", false, weekly);
        var dispatcher = await StartAsync();

        using var response = await RunTemplateAsync(dispatcher, id.ToString());

        Assert.Empty(Matched(response));
        Assert.Contains("W1", McpTestSupport.Structured(response).GetProperty("warnings")[0].GetString());
    }

    [Theory]
    [InlineData("""{}""", "INVALID_ARGUMENT", "conditionTree")]
    [InlineData("""{"templateId":"not-a-guid"}""", "INVALID_ARGUMENT", "templateId")]
    [InlineData("""{"templateId":"00000000-0000-0000-0000-000000000001"}""", "TEMPLATE_NOT_FOUND", "templateId")]
    [InlineData("""{"templateId":"00000000-0000-0000-0000-000000000001","conditionTree":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "templateId")]
    [InlineData("""{"templateId":"..\\..\\x"}""", "INVALID_ARGUMENT", "templateId")]
    public async Task ABadTemplateRequestIsRejectedWithTheCodeAndTheArgument(string argumentsJson, string code, string parameter)
    {
        var dispatcher = await StartAsync();

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", argumentsJson))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, Code(response));
        Assert.Equal(parameter, Param(response));
    }

    [Fact]
    public async Task ATemplateWithAnOffsetAboveTheApplicationSettingIsConditionLimitExceeded()
    {
        ScreenerIndicatorEntry entry = CloseAbove(100m);
        entry.LeftHand.Offset = 11;
        Guid id = Save("Deep", false, entry);
        var dispatcher = await StartAsync(("Screener:MaxConditionOffset", "10"));

        using var response = await RunTemplateAsync(dispatcher, id.ToString());

        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(response));
        Assert.Equal("templateId", Param(response));
    }

    [Fact]
    public async Task ATemplateColumnEntryReadsTheStoredMetadata()
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with { ShortName = "Microsoft", Sector = "Technology" });
        var sector = new ScreenerIndicatorEntry
        {
            // As the registration dialog stores a text column: the description first, the short name in OutputName.
            LeftHand = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Sector", CustomDisplayName = "Company sector" },
            Operator = ComparisonOperator.Equal,
            TargetMode = RightHandTargetMode.StringValue,
            RightStringValue = "technology",
            CategoryType = ScreenerItemCategoryType.Column,
        };
        Guid id = Save("Tech", false, sector);
        var dispatcher = await StartAsync();

        using var response = await RunTemplateAsync(dispatcher, id.ToString());

        Assert.Equal(new[] { "MSFT" }, Matched(response));
    }

    [Fact]
    public async Task ListingAndRunningTemplatesLeavesTheDataTreeAndTheTemplateFolderUnchanged()
    {
        Guid id = Save("Kept", true, CloseAbove(150m));
        File.WriteAllText(Path.Combine(_templates, "broken.json"), "{");
        var dataBefore = _fixture.Snapshot();
        var templatesBefore = ReadOnlyDataTreeFixture.SnapshotOf(Path.GetDirectoryName(_templates)!);
        var dispatcher = await StartAsync();

        (await ListAsync(dispatcher)).Dispose();
        (await RunTemplateAsync(dispatcher, id.ToString(), ""","includeMatchedValues":true""")).Dispose();
        (await RunTemplateAsync(dispatcher, Guid.NewGuid().ToString())).Dispose();

        Assert.Equal(dataBefore, _fixture.Snapshot());
        Assert.Equal(templatesBefore, ReadOnlyDataTreeFixture.SnapshotOf(Path.GetDirectoryName(_templates)!));
    }

    [Fact]
    public async Task TheListToolIsReadOnlyHasOnlyTheTemplateTypeArgumentAndTheRunToolDocumentsBothModes()
    {
        var dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement[] tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        JsonElement listTool = tools.Single(t => t.GetProperty("name").GetString() == "sa_list_templates");
        JsonElement runTool = tools.Single(t => t.GetProperty("name").GetString() == "sa_run_screener");

        Assert.Equal(new[] { "templateType" }, listTool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(listTool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        Assert.True(listTool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(runTool.GetProperty("inputSchema").TryGetProperty("required", out _));
        Assert.Contains("templateId", runTool.GetProperty("description").GetString());
        Assert.Contains("sa_list_templates", runTool.GetProperty("description").GetString());
    }
}
