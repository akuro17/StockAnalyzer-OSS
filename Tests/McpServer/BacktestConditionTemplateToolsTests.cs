using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_list_templates (templateType backtestCondition) and the templateId mode of sa_run_backtest through the real composition, with the
/// backtest-condition templates in a temporary folder. Bars as in <see cref="TemplateToolsTests"/>: AAPL has 100 daily bars.
/// </summary>
public sealed class BacktestConditionTemplateToolsTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(400, "msft", "7203.T", "aapl");
    private readonly string _root = TestDataIsolationBootstrap.NewTempDirectory("McpBacktestTemplates");
    private readonly List<McpServerRuntime> _runtimes = new();

    public BacktestConditionTemplateToolsTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, "AAPL", 100);
    }

    private string Folder => Path.Combine(_root, "BacktestCondition");

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            templatesDirectory: Path.Combine(_root, "Screener"),
            backtestTemplatesDirectory: Folder);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static BacktestConditionEntryDto CloseBelow(decimal level, TimeFrame? frame = null) => new()
    {
        Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close, Frame = frame },
        Operator = ComparisonOperator.LessThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = level,
    };

    private static BacktestConditionNodeDto Group(params BacktestConditionEntryDto[] comparisons) => new()
    {
        Kind = BacktestConditionNodeKind.Group,
        Operator = LogicalOperator.And,
        Children = comparisons.Select(c => new BacktestConditionNodeDto { Kind = BacktestConditionNodeKind.Leaf, Comparison = c }).ToList(),
    };

    private static BacktestConditionTreeDto EntryOnly(BacktestConditionEntryDto entry) => new() { EntryLong = Group(entry) };

    private Guid Save(string name, BacktestConditionTreeDto tree, BacktestRiskManagementSettingsDto? risk = null)
    {
        var template = new BacktestConditionTemplate { Id = Guid.NewGuid(), Name = name, ConditionTree = tree, RiskManagement = risk };
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        return template.Id;
    }

    private static Task<JsonDocument> ListAsync(McpMessageDispatcher dispatcher)
        => McpTestSupport.CallAsync(dispatcher, "sa_list_templates", """{"templateType":"backtestCondition"}""")!;

    private static Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string argumentsBody)
        => McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", "{\"symbol\":\"AAPL\"," + argumentsBody + "}")!;

    private static string Code(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("code").GetString()!;

    // ---- sa_list_templates with templateType backtestCondition --------------------------------------------------------

    [Fact]
    public async Task WithoutAFolderTheBacktestListingIsEmptyAndNothingIsCreated()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await ListAsync(dispatcher);

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("BacktestCondition", result.GetProperty("templateType").GetString());
        Assert.Equal(0, result.GetProperty("templateCount").GetInt32());
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public async Task TheListingShowsTheRootsTheRiskAndNoWarningsForAValidTemplate()
    {
        Guid id = Save("Dip buyer", new BacktestConditionTreeDto { EntryLong = Group(CloseBelow(1000m), CloseBelow(900m)) }, new BacktestRiskManagementSettingsDto { StopLossPercent = 0.05m });
        var dispatcher = await StartAsync();

        using JsonDocument response = await ListAsync(dispatcher);

        JsonElement template = McpTestSupport.Structured(response).GetProperty("templates")[0];
        Assert.Equal(id.ToString(), template.GetProperty("id").GetString());
        Assert.Equal("Dip buyer", template.GetProperty("name").GetString());
        JsonElement root = template.GetProperty("roots")[0];
        Assert.Equal("EntryLong", root.GetProperty("root").GetString());
        Assert.Equal(2, root.GetProperty("comparisonCount").GetInt32());
        Assert.Equal(0.05m, template.GetProperty("riskManagement").GetProperty("stopLossRatio").GetDecimal());
        Assert.False(template.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task AnUnreadableFileIsCountedAndNotListed()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, $"{Guid.NewGuid()}.json"), "{ not json");
        var dispatcher = await StartAsync();

        using JsonDocument response = await ListAsync(dispatcher);

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(0, result.GetProperty("templateCount").GetInt32());
        Assert.Equal(1, result.GetProperty("unreadableFileCount").GetInt32());
    }

    [Fact]
    public async Task ATemplateWithAWeeklySideIsListedWithoutAWarningAndTheTextNamesTheFrame()
    {
        Save("Weekly", EntryOnly(CloseBelow(1000m, TimeFrame.W1)));
        var dispatcher = await StartAsync();

        using JsonDocument response = await ListAsync(dispatcher);

        JsonElement template = McpTestSupport.Structured(response).GetProperty("templates")[0];
        Assert.False(template.TryGetProperty("warnings", out _));
        Assert.Contains("Close@W1", template.GetProperty("roots")[0].GetProperty("text").GetString());
    }

    // ---- sa_run_backtest with templateId ----------------------------------------------------------------------------

    [Fact]
    public async Task TemplateIdRunsTheTemplatesConditionsAndItsRiskSettings()
    {
        Guid id = Save("Dip buyer", EntryOnly(CloseBelow(1000m)), new BacktestRiskManagementSettingsDto { StopLossPercent = 0.05m });
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\"");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("template", result.GetProperty("conditionSource").GetString());
        Assert.Equal(id.ToString(), result.GetProperty("template").GetProperty("id").GetString());
        Assert.Equal("Dip buyer", result.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal(0.05m, result.GetProperty("effectiveSettings").GetProperty("riskManagement").GetProperty("stopLossRatio").GetDecimal());
        Assert.Equal("EntryLong", result.GetProperty("conditionRoots")[0].GetProperty("root").GetString());
    }

    [Fact]
    public async Task ConditionsAreReportedAsTheSourceWhenNoTemplateIsGiven()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, "\"conditions\":{\"entryLong\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"LessThan\",\"right\":1000}}");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("conditions", result.GetProperty("conditionSource").GetString());
        Assert.False(result.TryGetProperty("template", out _));
    }

    [Fact]
    public async Task ConditionsAndTemplateIdTogetherAreRefused()
    {
        Guid id = Save("Dip buyer", EntryOnly(CloseBelow(1000m)));
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\",\"conditions\":{{\"entryLong\":{{\"left\":{{\"price\":\"Close\"}},\"comparison\":\"LessThan\",\"right\":1000}}}}");

        Assert.Equal("INVALID_ARGUMENT", Code(response));
    }

    [Fact]
    public async Task NeitherConditionsNorTemplateIdIsRefused()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, "\"startDate\":\"2026-01-01\"");

        Assert.Equal("INVALID_ARGUMENT", Code(response));
    }

    [Fact]
    public async Task RiskManagementWithTemplateIdIsRefused()
    {
        Guid id = Save("Dip buyer", EntryOnly(CloseBelow(1000m)));
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\",\"riskManagement\":{{\"stopLossRatio\":0.02}}");

        Assert.Equal("INVALID_ARGUMENT", Code(response));
    }

    [Fact]
    public async Task AnUnknownTemplateIdIsTemplateNotFound()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{Guid.NewGuid()}\"");

        Assert.Equal("TEMPLATE_NOT_FOUND", Code(response));
    }

    [Fact]
    public async Task ANonGuidTemplateIdIsRefused()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, "\"templateId\":\"not-a-guid\"");

        Assert.Equal("INVALID_ARGUMENT", Code(response));
    }

    [Fact]
    public async Task ATemplateWithAWeeklySideRunsOnDailyBarsAndEchoesTheFrame()
    {
        Guid id = Save("Weekly", EntryOnly(CloseBelow(1000m, TimeFrame.W1)));
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\"");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(McpTestSupport.IsError(response), result.ToString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Contains("Close@W1", result.GetProperty("conditionRoots")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ATemplateWithADailySideIsRefusedOnAWeeklyRunWithUnsupportedTimeFrame()
    {
        Guid id = Save("Daily", EntryOnly(CloseBelow(1000m, TimeFrame.D1)));
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\",\"timeFrame\":\"W1\"");

        Assert.Equal("UNSUPPORTED_TIMEFRAME", Code(response));
    }

    [Fact]
    public async Task ATemplateWithAWeeklySideRunsOnWeeklyBars()
    {
        Guid id = Save("Weekly on weekly", EntryOnly(CloseBelow(1000m, TimeFrame.W1)));
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\",\"timeFrame\":\"W1\"");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
    }

    // ---- indicator parameter ranges of a template (D-20 A) -----------------------------------------------------------

    private static BacktestConditionSideDto Sma(int period) => new() { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = period } };

    private static BacktestConditionEntryDto CloseAbove(BacktestConditionSideDto right) => new()
    {
        Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.Indicator,
        Right = right,
    };

    private static BacktestConditionEntryDto SmaAbove(int period, decimal level) => new()
    {
        Left = Sma(period),
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = level,
    };

    [Fact]
    public async Task ATemplateWithAnOutOfRangeIndicatorParameterIsRefusedAtItsPosition()
    {
        Guid id = Save("Bad period", new BacktestConditionTreeDto { EntryLong = Group(CloseBelow(1000m), SmaAbove(20000, 1m)) });
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\"");

        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("INVALID_INDICATOR_PARAMETER", Code(response));
        Assert.Equal("templateId", error.GetProperty("param").GetString());
        Assert.Contains("entryLong.children[1].left.parameters.Period", error.GetProperty("hint").GetString());
        Assert.DoesNotContain(_root, error.GetRawText());
    }

    [Fact]
    public async Task TheRightSideOfATemplateComparisonIsCheckedToo()
    {
        Guid id = Save("Bad right", new BacktestConditionTreeDto { EntryLong = Group(CloseAbove(Sma(20000))) });
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\"");

        Assert.Equal("INVALID_INDICATOR_PARAMETER", Code(response));
        Assert.Contains("entryLong.children[0].right.parameters.Period", McpTestSupport.Structured(response).GetProperty("hint").GetString());
    }

    // W-11: the same condition gives the same code and message whether it comes from conditions or from a template.
    [Fact]
    public async Task TheSameOutOfRangeConditionGivesTheSameErrorFromConditionsAndFromATemplate()
    {
        Guid id = Save("Bad period", new BacktestConditionTreeDto { EntryLong = Group(SmaAbove(20000, 1m)) });
        var dispatcher = await StartAsync();

        using JsonDocument fromTemplate = await RunAsync(dispatcher, $"\"templateId\":\"{id}\"");
        using JsonDocument fromConditions = await RunAsync(dispatcher,
            "\"conditions\":{\"entryLong\":{\"left\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":20000}},\"comparison\":\"GreaterThan\",\"right\":1}}");

        JsonElement templateError = McpTestSupport.Structured(fromTemplate);
        JsonElement conditionsError = McpTestSupport.Structured(fromConditions);
        Assert.Equal("INVALID_INDICATOR_PARAMETER", conditionsError.GetProperty("code").GetString());
        Assert.Equal(conditionsError.GetProperty("code").GetString(), templateError.GetProperty("code").GetString());
        Assert.Equal(conditionsError.GetProperty("message").GetString(), templateError.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ATemplateWithInRangeIndicatorParametersStillRuns()
    {
        Guid id = Save("Good period", new BacktestConditionTreeDto { EntryLong = Group(CloseAbove(Sma(10)), SmaAbove(50, 1m)) });
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, $"\"templateId\":\"{id}\"");

        Assert.Equal("template", McpTestSupport.Structured(response).GetProperty("conditionSource").GetString());
    }

    [Fact]
    public async Task TheListingWarnsAboutATemplateWithAnOutOfRangeParameter()
    {
        Save("Bad period", new BacktestConditionTreeDto { EntryLong = Group(SmaAbove(20000, 1m)) });
        var dispatcher = await StartAsync();

        using JsonDocument response = await ListAsync(dispatcher);

        JsonElement template = McpTestSupport.Structured(response).GetProperty("templates")[0];
        Assert.Contains("Period", template.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task ListingAndRunningNeverChangeTheTemplateFolder()
    {
        Guid id = Save("Dip buyer", EntryOnly(CloseBelow(1000m)), new BacktestRiskManagementSettingsDto { StopLossPercent = 0.05m });
        string file = Path.Combine(Folder, $"{id}.json");
        string before = File.ReadAllText(file);
        var dispatcher = await StartAsync();

        using (await ListAsync(dispatcher)) { }
        using (await RunAsync(dispatcher, $"\"templateId\":\"{id}\"")) { }

        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(new[] { Path.GetFileName(file) }, Directory.GetFiles(Folder).Select(Path.GetFileName).ToArray());
    }
}
