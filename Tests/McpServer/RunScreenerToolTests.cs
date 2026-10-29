using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_screener through the real composition on a throw-away data tree. The stored series are the fixture's zig-zag with a different
/// number of days per symbol, so the latest close tells the symbols apart (about 100 + 0.25 per day): MSFT and 7203-T have 400 days,
/// AAPL 100 and ZZZ 5. Results are compared with the Screener service called directly on conditions built by hand.
/// </summary>
public sealed class RunScreenerToolTests : IDisposable
{
    private const int LongDays = 400;
    private const int ShortDays = 100;
    private const int TinyDays = 5;
    private const string HighClose = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":150}""";

    private static readonly IndicatorFactory Factory = new();

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(LongDays, "msft", "7203.T", "aapl", "zzz");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<ServiceProvider> _providers = new();

    public RunScreenerToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, "AAPL", ShortDays);
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, "ZZZ", TinyDays);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        foreach (ServiceProvider provider in _providers)
        {
            provider.Dispose();
        }

        _fixture.Dispose();
    }

    private IConfiguration Configuration(params (string Key, string Value)[] limitOverrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in limitOverrides)
        {
            values[key.Contains(':') ? key : $"AiIntegration:{key}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(overrides), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string conditionJson, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{conditionJson}}{{extra}}}"""))!;

    private static string[] Matched(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).ToArray();

    private static string Code(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("code").GetString()!;

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    private static IStockAnalyzerSettings Settings()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        return new ServiceCollection()
            .AddSingleton(configuration)
            .AddStockAnalyzerSettingsOptions(configuration)
            .AddSingleton<IStockAnalyzerSettings, StockAnalyzerSettings>()
            .BuildServiceProvider()
            .GetRequiredService<IStockAnalyzerSettings>();
    }

    private static string Group(string op, params string[] children) => $$"""{"operator":"{{op}}","children":[{{string.Join(",", children)}}]}""";

    private static string SectorIs(string comparison, string text) => $$"""{"left":{"column":"Sector"},"comparison":"{{comparison}}","right":"{{text}}"}""";

    private async Task SeedSectorsAsync()
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with { ShortName = "Microsoft", Sector = "Technology", TrailingPE = 25m });
        await _fixture.SeedMetadataAsync("7203-T", TickerMetadata.Unknown with { ShortName = "Toyota", Sector = "Consumer Cyclical", TrailingPE = 10m });
    }

    private IScreenerService DirectScreener()
    {
        ServiceProvider provider = new ServiceCollection().AddStockAnalyzerReadOnlyCore(Configuration()).BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IScreenerService>();
    }

    // ---- parity with the Screener service -------------------------------------------------------------------------

    [Fact]
    public async Task APriceComparisonGivesTheSetTheScreenerServiceGivesForTheSameCondition()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close" },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 150m,
        };
        var direct = await DirectScreener().ScreenAsync(
            new List<string> { "7203-T", "AAPL", "MSFT", "ZZZ" },
            new ScreenerConditionTreeCondition(new ScreenerConditionGroup(LogicalOperator.And, System.Collections.Immutable.ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(entry)))),
            TimeFrame.D1, new Progress<int>(), CancellationToken.None);

        using var response = await RunAsync(await StartAsync(), HighClose);

        Assert.Equal(new[] { "7203-T", "MSFT" }, direct.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(direct.OrderBy(s => s, StringComparer.Ordinal).ToArray(), Matched(response));
    }

    [Fact]
    public async Task AnIndicatorComparisonReadsTheValueTheValueExtractorGives()
    {
        const string Tree = """{"left":{"indicator":"RSI","parameters":{"Period":9}},"comparison":"GreaterThan","right":0}""";
        using var response = await RunAsync(await StartAsync(), Tree, ""","includeMatchedValues":true""");

        var side = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.RSI,
            OutputName = "RSI",
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Period"] = 9 },
        };
        var provider = new ServiceCollection().AddStockAnalyzerReadOnlyCore(Configuration()).BuildServiceProvider();
        _providers.Add(provider);
        var candles = await provider.GetRequiredService<IReadOnlyStockAnalyzerGateway>().GetCandlesAsync("MSFT", TimeFrame.D1, 10000);
        decimal expected = ScreenerValueExtractor.Default.ExtractValueNullable(side, candles)!.Value;

        JsonElement value = McpTestSupport.Structured(response).GetProperty("matchedValues").GetProperty("MSFT").GetProperty("L0");
        Assert.Equal(expected, value.GetProperty("left").GetDecimal());
        Assert.Equal(0m, value.GetProperty("right").GetDecimal());
        Assert.Contains("MSFT", Matched(response));
    }

    [Fact]
    public async Task ASingleComparisonEqualsTheSameComparisonInsideAnAndGroup()
    {
        var dispatcher = await StartAsync();
        using var single = await RunAsync(dispatcher, HighClose);
        using var grouped = await RunAsync(dispatcher, Group("And", HighClose));

        Assert.Equal(Matched(grouped), Matched(single));
        Assert.Equal(2, McpTestSupport.Structured(single).GetProperty("treeNodeCount").GetInt32()); // the And root and the comparison
        Assert.Equal(2, McpTestSupport.Structured(grouped).GetProperty("treeNodeCount").GetInt32());
    }

    // ---- logic, offset, universe ----------------------------------------------------------------------------------

    [Fact]
    public async Task AndIntersectsAndOrUnitesTheChildren()
    {
        await SeedSectorsAsync();
        var dispatcher = await StartAsync();
        string technology = SectorIs("Equal", "Technology");

        using var and = await RunAsync(dispatcher, Group("And", HighClose, technology));
        using var or = await RunAsync(dispatcher, Group("Or", HighClose, technology));
        using var nested = await RunAsync(dispatcher, Group("And", Group("Or", HighClose, technology), SectorIs("NotEqual", "Technology")));

        Assert.Equal(new[] { "MSFT" }, Matched(and));
        Assert.Equal(new[] { "7203-T", "MSFT" }, Matched(or));
        Assert.Equal(new[] { "7203-T" }, Matched(nested));
        Assert.Equal(3, McpTestSupport.Structured(nested).GetProperty("treeDepth").GetInt32() + 1);
    }

    [Fact]
    public async Task TheOffsetReadsAnEarlierBarOfTheSeries()
    {
        var dispatcher = await StartAsync();
        const string Back250 = """{"left":{"price":"Close","offset":250},"comparison":"GreaterThan","right":150}""";
        const string Back100 = """{"left":{"price":"Close","offset":100},"comparison":"GreaterThan","right":150}""";

        using var farBack = await RunAsync(dispatcher, Back250);
        using var back = await RunAsync(dispatcher, Back100);

        Assert.Empty(Matched(farBack)); // 150 bars into the series the close is still below 150
        Assert.Equal(new[] { "7203-T", "MSFT" }, Matched(back));
    }

    [Fact]
    public async Task ASymbolListIsCanonicalisedDeduplicatedAndSortedAndOnlyThoseAreEvaluated()
    {
        using var response = await RunAsync(await StartAsync(), HighClose, ""","symbols":["msft","7203.t","MSFT","aapl"]""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("Custom", result.GetProperty("universe").GetString());
        Assert.Equal(3, result.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(new[] { "7203-T", "MSFT" }, Matched(response));
    }

    [Fact]
    public async Task WithoutSymbolsEveryAvailableSymbolIsEvaluated()
    {
        using var response = await RunAsync(await StartAsync(), HighClose);

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("AllAvailable", result.GetProperty("universe").GetString());
        Assert.Equal(4, result.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(2, result.GetProperty("matchedSymbolCount").GetInt32());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
    }

    [Fact]
    public async Task ASymbolWithTooFewBarsForTheIndicatorDoesNotMatch()
    {
        const string Tree = """{"left":{"indicator":"SMA","parameters":{"Period":50}},"comparison":"GreaterThan","right":0}""";
        using var response = await RunAsync(await StartAsync(), Tree);

        Assert.Equal(new[] { "7203-T", "AAPL", "MSFT" }.OrderBy(s => s, StringComparer.Ordinal).ToArray(), Matched(response)); // ZZZ has 5 bars
    }

    // ---- text and column conditions -------------------------------------------------------------------------------

    [Fact]
    public async Task ATextColumnIsComparedCaseInsensitivelyAndASymbolWithoutMetadataDoesNotMatch()
    {
        await SeedSectorsAsync();
        var dispatcher = await StartAsync();

        using var equal = await RunAsync(dispatcher, SectorIs("Equal", "technology"));
        using var contains = await RunAsync(dispatcher, SectorIs("Contains", "CYCLICAL"));
        using var notEqual = await RunAsync(dispatcher, SectorIs("NotEqual", "Technology"));
        using var doesNotContain = await RunAsync(dispatcher, SectorIs("DoesNotContain", "tech"));

        Assert.Equal(new[] { "MSFT" }, Matched(equal));
        Assert.Equal(new[] { "7203-T" }, Matched(contains));
        Assert.Equal(new[] { "7203-T" }, Matched(notEqual)); // AAPL and ZZZ have no stored sector: not matched, not even by NotEqual
        Assert.Equal(new[] { "7203-T" }, Matched(doesNotContain));
    }

    [Fact]
    public async Task ANumericColumnReadsTheStoredMetadata()
    {
        await SeedSectorsAsync();
        const string Tree = """{"left":{"column":"Trailing P/E"},"comparison":"GreaterThan","right":20}""";
        using var response = await RunAsync(await StartAsync(), Tree, ""","includeMatchedValues":true""");

        Assert.Equal(new[] { "MSFT" }, Matched(response));
        Assert.Equal("Trailing P/E > 20", McpTestSupport.Structured(response).GetProperty("comparisons")[0].GetProperty("text").GetString());
        JsonElement value = McpTestSupport.Structured(response).GetProperty("matchedValues").GetProperty("MSFT").GetProperty("L0");
        Assert.Equal(25m, value.GetProperty("left").GetDecimal());
        Assert.Equal(20m, value.GetProperty("right").GetDecimal());
    }

    [Fact]
    public async Task APatternSideIsOneWhenPresentAndZeroOtherwise()
    {
        using var absent = await RunAsync(await StartAsync(), """{"left":{"criteria":"Bullish Engulfing"},"comparison":"Equal","right":1}""");
        using var none = await RunAsync(await StartAsync(), """{"left":{"criteria":"Bullish Engulfing"},"comparison":"Equal","right":0}""");

        Assert.Empty(Matched(absent)); // the zig-zag has no engulfing pattern
        Assert.Equal(new[] { "7203-T", "AAPL", "MSFT", "ZZZ" }.OrderBy(s => s, StringComparer.Ordinal).ToArray(), Matched(none));
    }

    [Fact]
    public async Task TheRightSideCanBeAnotherIndicatorWithItsOwnParameters()
    {
        const string Tree = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":20}}}""";
        using var response = await RunAsync(await StartAsync(), Tree, ""","includeMatchedValues":true,"maxReturnedMatches":1""");

        JsonElement result = McpTestSupport.Structured(response);
        string text = result.GetProperty("comparisons")[0].GetProperty("text").GetString()!;
        Assert.Contains(">", text);
        Assert.Contains("SMA", text);
        string symbol = Matched(response).Single();
        JsonElement value = result.GetProperty("matchedValues").GetProperty(symbol).GetProperty("L0");
        Assert.True(value.GetProperty("left").GetDecimal() > value.GetProperty("right").GetDecimal());
    }

    [Fact]
    public async Task AColumnReadsTheMetricItNamesEvenWhereTheScreenerTabsDescriptionKeyReadsAnother()
    {
        // The tab's description key resolves "Gross Margin" to total revenue and "Trailing P/E" to nothing (measured 2026-10-22); the short name does not.
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with { ShortName = "Microsoft", GrossMargins = 0.3m, TotalRevenue = 999m, TrailingPE = 25m, Ebitda = 777m, EnterpriseToEbitda = 12m });
        var dispatcher = await StartAsync();

        using var margin = await RunAsync(dispatcher, """{"left":{"column":"Gross Margin"},"comparison":"LessThan","right":1}""", ""","includeMatchedValues":true""");
        using var pe = await RunAsync(dispatcher, """{"left":{"column":"Trailing P/E"},"comparison":"Equal","right":25}""");
        using var evEbitda = await RunAsync(dispatcher, """{"left":{"column":"EV/EBITDA"},"comparison":"Equal","right":12}""");

        Assert.Equal(new[] { "MSFT" }, Matched(margin));
        Assert.Equal(0.3m, McpTestSupport.Structured(margin).GetProperty("matchedValues").GetProperty("MSFT").GetProperty("L0").GetProperty("left").GetDecimal());
        Assert.Equal(new[] { "MSFT" }, Matched(pe));
        Assert.Equal(new[] { "MSFT" }, Matched(evEbitda));
    }

    [Fact]
    public void EveryOfferedColumnReadsExactlyThePropertyItNamesAndTheOthersAreListedAsUnavailable()
    {
        var provider = new ScreenerCatalogProvider();
        var catalog = new ScreenerCatalog(provider, Factory);

        string[] offered = catalog.Columns.Select(c => c.Name).ToArray();
        string[] unavailable = catalog.UnavailableColumns.ToArray();

        Assert.Contains("Gross Margin", offered);
        Assert.Contains("Trailing P/E", offered);
        Assert.Contains("Close", offered);
        Assert.Contains("Sector", offered);
        Assert.Contains("Business Summary", unavailable);
        Assert.Contains("Notes", unavailable);
        Assert.Empty(offered.Intersect(unavailable, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(provider.GetCatalogItems(Factory).Count(i => i.CategoryType == ScreenerItemCategoryType.Column), offered.Length + unavailable.Length);
        Assert.True(offered.Length > 60);
    }

    // ---- result shape ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMatchListIsCutAtMaxReturnedMatchesWhileTheCountStaysTrue()
    {
        using var response = await RunAsync(await StartAsync(), HighClose, ""","maxReturnedMatches":1""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { "7203-T" }, Matched(response));
        Assert.Equal(2, result.GetProperty("matchedSymbolCount").GetInt32());
        Assert.Equal(1, result.GetProperty("returnedMatchCount").GetInt32());
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ValuesAreAddedOnlyForTheReturnedSymbolsAndOnlyWhenAsked()
    {
        var dispatcher = await StartAsync();
        using var without = await RunAsync(dispatcher, HighClose);
        using var with = await RunAsync(dispatcher, HighClose, ""","includeMatchedValues":true,"maxReturnedMatches":1""");

        Assert.False(McpTestSupport.Structured(without).TryGetProperty("matchedValues", out _));
        JsonElement values = McpTestSupport.Structured(with).GetProperty("matchedValues");
        Assert.Equal(new[] { "7203-T" }, values.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("L0", McpTestSupport.Structured(with).GetProperty("comparisons")[0].GetProperty("id").GetString());
        Assert.True(values.GetProperty("7203-T").GetProperty("L0").GetProperty("left").GetDecimal() > 150m);
    }

    [Fact]
    public async Task AnEmptyUniverseIsASuccessWithZeroCounts()
    {
        var runtimeFixture = ReadOnlyDataTreeFixture.Create(5);
        try
        {
            McpServerRuntime runtime = McpServerComposition.Create(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MarketDataSettings:DailyDataPath"] = runtimeFixture.Settings.DailyDataPath,
                    ["MarketDataSettings:WeeklyDataPath"] = runtimeFixture.Settings.WeeklyDataPath,
                    ["MarketDataSettings:MonthlyDataPath"] = runtimeFixture.Settings.MonthlyDataPath,
                    ["MarketDataSettings:MetadataPath"] = runtimeFixture.Settings.MetadataPath,
                    ["MarketDataSettings:TickerListPath"] = runtimeFixture.Settings.TickerListPath,
                }).Build(),
                new LogSink(new StringWriter()));
            _runtimes.Add(runtime);
            await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

            using var response = await RunAsync(runtime.Dispatcher, HighClose);

            JsonElement result = McpTestSupport.Structured(response);
            Assert.False(McpTestSupport.IsError(response));
            Assert.Equal(0, result.GetProperty("evaluatedSymbolCount").GetInt32());
            Assert.Equal(0, result.GetProperty("matchedSymbolCount").GetInt32());
        }
        finally
        {
            runtimeFixture.Dispose();
        }
    }

    // ---- limits ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MoreSymbolsThanTheLimitIsLimitExceededAndTheLimitIsConfigurable()
    {
        var dispatcher = await StartAsync(("MaxScreenerSymbols", "2"));

        using var response = await RunAsync(dispatcher, HighClose, ""","symbols":["MSFT","AAPL","ZZZ"]""");

        Assert.Equal("LIMIT_EXCEEDED", Code(response));
        Assert.Equal("symbols", Param(response));
    }

    [Fact]
    public async Task MaxReturnedMatchesAboveTheLimitIsLimitExceeded()
    {
        var dispatcher = await StartAsync(("MaxReturnedMatches", "5"));

        using var response = await RunAsync(dispatcher, HighClose, ""","maxReturnedMatches":6""");

        Assert.Equal("LIMIT_EXCEEDED", Code(response));
    }

    [Fact]
    public async Task ATreeDeeperThanTheApplicationSettingIsConditionLimitExceededNamingTheSetting()
    {
        var dispatcher = await StartAsync(("Screener:MaxFilterTreeDepth", "2"));

        using var response = await RunAsync(dispatcher, Group("And", Group("And", Group("And", HighClose))));

        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(response));
        Assert.Contains("Screener:MaxFilterTreeDepth", McpTestSupport.Structured(response).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task ATreeWithMoreNodesThanTheApplicationSettingIsConditionLimitExceeded()
    {
        var dispatcher = await StartAsync(("Screener:MaxFilterTreeNodes", "4"), ("Screener:MaxFilterTreeDepth", "2"));

        using var response = await RunAsync(dispatcher, Group("And", HighClose, HighClose, HighClose, HighClose));

        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(response));
        Assert.Contains("Screener:MaxFilterTreeNodes", McpTestSupport.Structured(response).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task AnOffsetAboveTheApplicationSettingIsConditionLimitExceeded()
    {
        var dispatcher = await StartAsync(("Screener:MaxConditionOffset", "10"));

        using var response = await RunAsync(dispatcher, """{"left":{"price":"Close","offset":11},"comparison":"GreaterThan","right":1}""");

        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(response));
        Assert.Equal("conditionTree.left.offset", Param(response));
    }

    // ---- strict input ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":1,"extra":true}""", "INVALID_ARGUMENT", "conditionTree.extra")]
    [InlineData("""{"left":{"column":"Business Summary"},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.column")]
    [InlineData("""{"left":{"price":"Close","indicator":"RSI"},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left")]
    [InlineData("""{"left":{"offset":1},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left")]
    [InlineData("""{"left":{"price":"Close","parameters":{"Period":1}},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left")]
    [InlineData("""{"left":{"price":"Close","unknown":1},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.unknown")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"Above","right":1}""", "INVALID_ARGUMENT", "conditionTree.comparison")]
    [InlineData("""{"left":{"price":"Close"},"right":1}""", "INVALID_ARGUMENT", "conditionTree.comparison")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"GreaterThan"}""", "INVALID_ARGUMENT", "conditionTree.right")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":"high"}""", "INVALID_ARGUMENT", "conditionTree.right")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":true}""", "INVALID_ARGUMENT", "conditionTree.right")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"Contains","right":1}""", "INVALID_ARGUMENT", "conditionTree.comparison")]
    [InlineData("""{"left":{"column":"Sector"},"comparison":"GreaterThan","right":"Tech"}""", "INVALID_ARGUMENT", "conditionTree.comparison")]
    [InlineData("""{"left":{"column":"Sector"},"comparison":"Equal","right":5}""", "INVALID_ARGUMENT", "conditionTree.right")]
    [InlineData("""{"left":{"column":"Sector"},"comparison":"Equal","right":" "}""", "INVALID_ARGUMENT", "conditionTree.right")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"Equal","right":{"column":"Sector"}}""", "INVALID_ARGUMENT", "conditionTree.right")]
    [InlineData("""{"left":{"price":"Nope"},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.price")]
    [InlineData("""{"left":{"column":"Nope"},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.column")]
    [InlineData("""{"left":{"criteria":"Nope"},"comparison":"Equal","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.criteria")]
    [InlineData("""{"left":{"price":"Close","offset":-1},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.offset")]
    [InlineData("""{"left":{"price":"Close","offset":1.5},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.offset")]
    [InlineData("""{"left":{"indicator":"NotAnIndicator"},"comparison":"GreaterThan","right":1}""", "UNKNOWN_INDICATOR", "conditionTree.left.indicator")]
    [InlineData("""{"left":{"indicator":"Entropy"},"comparison":"GreaterThan","right":1}""", "UNKNOWN_INDICATOR", "conditionTree.left.indicator")]
    [InlineData("""{"left":{"indicator":"ZigZag"},"comparison":"GreaterThan","right":1}""", "INVALID_ARGUMENT", "conditionTree.left.indicator")]
    [InlineData("""{"left":{"indicator":"Correlation"},"comparison":"GreaterThan","right":0}""", "INVALID_ARGUMENT", "conditionTree.left.parameters.ComparisonSymbol")]
    [InlineData("""{"left":{"indicator":"RSI","parameters":{"Period":0}},"comparison":"LessThan","right":30}""", "INVALID_INDICATOR_PARAMETER", "conditionTree.left.parameters.Period")]
    [InlineData("""{"left":{"indicator":"RSI","parameters":{"Nope":1}},"comparison":"LessThan","right":30}""", "INVALID_INDICATOR_PARAMETER", "conditionTree.left.parameters.Nope")]
    [InlineData("""{"left":{"indicator":"RSI","outputSeriesName":"Nope"},"comparison":"LessThan","right":30}""", "INVALID_OUTPUT_SERIES", "conditionTree.left.outputSeriesName")]
    [InlineData("""{"operator":"And","children":[]}""", "INVALID_ARGUMENT", "conditionTree.children")]
    [InlineData("""{"operator":"Xor","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}]}""", "INVALID_ARGUMENT", "conditionTree.operator")]
    [InlineData("""{"children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}]}""", "INVALID_ARGUMENT", "conditionTree.operator")]
    [InlineData("""{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":1},{"left":{"price":"Close"},"comparison":"Nope","right":1}]}""", "INVALID_ARGUMENT", "conditionTree.children[1].comparison")]
    [InlineData("""{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}],"name":"x"}""", "INVALID_ARGUMENT", "conditionTree.name")]
    [InlineData("""[1]""", "INVALID_ARGUMENT", "conditionTree")]
    public async Task ABadConditionIsRejectedWithTheCodeAndThePathOfTheFault(string conditionJson, string code, string parameter)
    {
        using var response = await RunAsync(await StartAsync(), conditionJson);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, Code(response));
        Assert.Equal(parameter, Param(response));
    }

    [Theory]
    [InlineData(",\"timeFrame\":\"M5\"", "UNSUPPORTED_TIMEFRAME", "timeFrame")]
    [InlineData(""","symbols":["NOPE"]""", "SYMBOL_NOT_FOUND", "symbols[0]")]
    [InlineData(""","symbols":["../x"]""", "INVALID_ARGUMENT", "symbols[0]")]
    [InlineData(""","maxReturnedMatches":0""", "INVALID_ARGUMENT", "maxReturnedMatches")]
    [InlineData(""","symbols":[]""", "INVALID_ARGUMENT", "symbols")]
    [InlineData(""","unknownArgument":1""", "INVALID_ARGUMENT", "unknownArgument")]
    public async Task ABadArgumentIsRejectedWithTheCodeAndTheArgumentName(string extra, string code, string parameter)
    {
        using var response = await RunAsync(await StartAsync(), HighClose, extra);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, Code(response));
        Assert.Equal(parameter, Param(response));
    }

    [Fact]
    public async Task TheConditionTreeIsRequired()
    {
        using var response = (await McpTestSupport.CallAsync(await StartAsync(), "sa_run_screener", "{}"))!;

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal("conditionTree", Param(response));
    }

    // ---- read-only guarantee and tool contract --------------------------------------------------------------------

    [Fact]
    public async Task AScreeningSessionIncludingMetadataConditionsAndFailuresLeavesTheDataTreeUnchanged()
    {
        await SeedSectorsAsync();
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        (await RunAsync(dispatcher, Group("And", HighClose, SectorIs("Equal", "Technology")), ""","includeMatchedValues":true""")).Dispose();
        (await RunAsync(dispatcher, """{"left":{"criteria":"Bullish Engulfing"},"comparison":"Equal","right":0}""")).Dispose();
        (await RunAsync(dispatcher, """{"left":{"price":"Nope"},"comparison":"GreaterThan","right":1}""")).Dispose();

        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task TheToolIsHeavyHasAStrictSchemaAndTheCatalogueNamesTheTool()
    {
        var dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_run_screener");
        JsonElement schema = tool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.TryGetProperty("required", out _)); // exactly one of conditionTree and templateId is checked by the tool
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Contains("sa_get_capabilities", tool.GetProperty("description").GetString());
    }

    // ---- the catalogue the screener names come from ---------------------------------------------------------------

    [Fact]
    public async Task CapabilitiesListsTheScreenerNamesOnRequestOnly()
    {
        var dispatcher = await StartAsync();
        using var plain = (await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", """{"indicatorTypeFilter":["RSI"]}"""))!;
        using var withScreener = (await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", """{"indicatorTypeFilter":["RSI"],"includeScreenerCatalog":true}"""))!;

        Assert.False(McpTestSupport.Structured(plain).TryGetProperty("screener", out _));
        JsonElement screener = McpTestSupport.Structured(withScreener).GetProperty("screener");
        Assert.Equal("D1", screener.GetProperty("timeFrame").GetString());
        Assert.Contains("GreaterThan", screener.GetProperty("comparisons").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(new[] { "And", "Or" }, screener.GetProperty("groupOperators").EnumerateArray().Select(c => c.GetString()!).OrderBy(c => c, StringComparer.Ordinal).ToArray());
        JsonElement sector = screener.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Sector");
        Assert.True(sector.GetProperty("isText").GetBoolean());
        Assert.Contains(screener.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "Trailing P/E" && !c.TryGetProperty("isText", out _));
        Assert.Contains("Business Summary", screener.GetProperty("unavailableColumns").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains(screener.GetProperty("criteria").EnumerateArray(), c => c.GetProperty("name").GetString() == "Bullish Engulfing");
        Assert.Contains(screener.GetProperty("prices").EnumerateArray(), c => c.GetProperty("name").GetString() == "Close");
    }

    [Fact]
    public void EveryCatalogueNameCanBeBoundAsASideOfACondition()
    {
        var provider = new ScreenerCatalogProvider();
        var screenerCatalog = new ScreenerCatalog(provider, Factory);
        var binder = new ScreenerConditionBinder(
            new IndicatorCatalog(Factory, provider), new IndicatorSettingsBinder(provider, Factory), screenerCatalog, Settings());

        foreach (ScreenerCatalogName column in screenerCatalog.Columns)
        {
            string leaf = column.IsText
                ? $$"""{"left":{"column":"{{column.Name}}"},"comparison":"Contains","right":"x"}"""
                : $$"""{"left":{"column":"{{column.Name}}"},"comparison":"GreaterThan","right":0}""";
            Assert.Single(binder.Bind(JsonDocument.Parse(leaf).RootElement, "t").Leaves);
        }

        foreach (ScreenerCatalogName criteria in screenerCatalog.Criteria)
        {
            Assert.Single(binder.Bind(JsonDocument.Parse($$"""{"left":{"criteria":"{{criteria.Name}}"},"comparison":"Equal","right":1}""").RootElement, "t").Leaves);
        }

        foreach (ScreenerCatalogName price in screenerCatalog.Prices)
        {
            Assert.Single(binder.Bind(JsonDocument.Parse($$"""{"left":{"price":"{{price.Name}}"},"comparison":"GreaterThan","right":0}""").RootElement, "t").Leaves);
        }

        Assert.NotEmpty(screenerCatalog.Columns);
        Assert.NotEmpty(screenerCatalog.Criteria);
        Assert.NotEmpty(screenerCatalog.Prices);
    }

    [Fact]
    public void EveryUsableIndicatorTypeCanBeBoundWithItsDefaultParametersAndItsFirstSeries()
    {
        var provider = new ScreenerCatalogProvider();
        var catalog = new IndicatorCatalog(Factory, provider);
        var binder = new ScreenerConditionBinder(
            catalog, new IndicatorSettingsBinder(provider, Factory), new ScreenerCatalog(provider, Factory), Settings());

        int bound = 0;
        foreach (IndicatorCatalogEntry entry in catalog.Entries.Where(e => e.IsRegistered && !e.IsCrossTicker && e.OutputSeriesNames.Except(e.NonCausalSeries).Any()
                     && e.BacktestEligibility == IndicatorCatalog.AllowedEligibility))
        {
            var tree = JsonDocument.Parse($$"""{"left":{"indicator":"{{entry.Type}}"},"comparison":"GreaterThan","right":0}""").RootElement;
            BoundScreenerCondition result = binder.Bind(tree, "t");
            Assert.Equal(Enum.Parse<IndicatorType>(entry.Type), result.Leaves.Single().Entry.LeftHand.IndicatorType);
            bound++;
        }

        Assert.True(bound > 100);
    }
}
