using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// B2: the same Screener condition, once as sa_run_screener and once as the Screener tab runs it, must give the same symbols in the same order,
/// compared exactly. The tab's side is built from explicit values (the entries and sides its registration dialog stores) and run through the
/// service the tab calls (<c>IScreenerService.ScreenAsync</c>, and the template evaluator for a saved template); no view model is created.
/// The six symbols follow a seeded random walk of their own, so every case separates them (a case that matched all or none would prove nothing:
/// that is asserted).
/// </summary>
public sealed class ScreenerGuiMcpEquivalenceTests : IDisposable
{
    private const int Days = 300;
    private const int SmaShort = 5;
    private const int SmaLong = 20;
    private const int LongOffset = 3;
    private const decimal PeLevel = 15m;
    private const string Technology = "Technology";

    private static readonly string[] Symbols = { "AAA", "BBB", "CCC", "DDD", "EEE", "FFF" };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, Symbols);
    private readonly string _templates = Path.Combine(TestDataIsolationBootstrap.NewTempDirectory("McpScreenerEquivalence"), "Screener");
    private readonly Dictionary<string, decimal> _lastClose = new(StringComparer.Ordinal);
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<ServiceProvider> _providers = new();
    private ServiceProvider? _services;
    private McpMessageDispatcher? _dispatcher;

    public ScreenerGuiMcpEquivalenceTests()
    {
        for (int index = 0; index < Symbols.Length; index++)
        {
            Reseed(Symbols[index], seed: 101 + index * 17);
        }
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
        try { Directory.Delete(Path.GetDirectoryName(_templates)!, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    // A random walk per symbol (the fixture's zig-zag gives every symbol the same shape).
    private void Reseed(string symbol, int seed)
    {
        File.Delete(Path.Combine(_fixture.DailyDirectory, $"{symbol}.parquet"));
        var random = new Random(seed);
        var closes = new List<decimal>(Days);
        decimal close = 100m;
        for (int day = 0; day < Days; day++)
        {
            close = Math.Max(5m, close * (1m + (decimal)(random.NextDouble() * 0.06 - 0.03)));
            closes.Add(Math.Round(close, 4));
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, symbol, closes);
        _lastClose[symbol] = closes[^1];
    }

    private decimal MedianLastClose()
    {
        decimal[] sorted = _lastClose.Values.OrderBy(v => v).ToArray();
        return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2m;
    }

    private async Task SeedMetadataAsync()
    {
        await _fixture.SeedMetadataAsync("AAA", TickerMetadata.Unknown with { ShortName = "Alpha", Sector = Technology, TrailingPE = 25m });
        await _fixture.SeedMetadataAsync("BBB", TickerMetadata.Unknown with { ShortName = "Beta", Sector = "Utilities", TrailingPE = 10m });
        await _fixture.SeedMetadataAsync("CCC", TickerMetadata.Unknown with { ShortName = "Gamma", Sector = Technology, TrailingPE = 12m });
        await _fixture.SeedMetadataAsync("DDD", TickerMetadata.Unknown with { ShortName = "Delta", Sector = "Energy", TrailingPE = 30m });
        // EEE and FFF have no stored metadata.
    }

    // ---- hosting -------------------------------------------------------------------------------------------------------------------

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
        ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
        ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
        ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
        ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
    }).Build();

    private async Task StartAsync()
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(), new LogSink(new StringWriter()), _templates);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        _dispatcher = runtime.Dispatcher;

        // The tab's services: built from the same composition, but the condition they evaluate is built here, by hand.
        _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(Configuration()).BuildServiceProvider();
        _providers.Add(_services);
    }

    private async Task<string[]> McpAsync(string request)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(_dispatcher!, "sa_run_screener", request))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(Symbols.Length, result.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        string[] matched = result.GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).ToArray();
        Assert.Equal(matched.Length, result.GetProperty("matchedSymbolCount").GetInt32());
        return matched;
    }

    /// <summary>The tab's call: the universe in ascending order (the Screener passes the symbols it lists), the condition it built, daily bars.</summary>
    private async Task<string[]> TabAsync(IScreeningCondition condition)
    {
        List<string> matched = await _services!.GetRequiredService<IScreenerService>().ScreenAsync(
            Symbols.OrderBy(s => s, StringComparer.Ordinal).ToList(), condition, TimeFrame.D1, new Progress<int>(), CancellationToken.None);
        return matched.ToArray();
    }

    private async Task AssertSameAsync(IScreeningCondition tab, string conditionJson, bool mixed = true)
    {
        string[] fromTab = await TabAsync(tab);
        string[] fromMcp = await McpAsync(Request(conditionJson));

        Assert.Equal(fromTab, fromMcp); // the same symbols, the same order, the same count
        if (mixed)
        {
            Assert.InRange(fromMcp.Length, 1, Symbols.Length - 1);
        }
    }

    // ---- the tab's sides and entries (explicit values) ---------------------------------------------------------------------------------

    private static ScreenerIndicatorSideConfig CloseSide(int offset = 0)
        => new() { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close", Offset = offset };

    private static ScreenerIndicatorSideConfig SmaSide(int period, int offset = 0)
        => new()
        {
            IndicatorType = IndicatorType.SMA,
            OutputName = IndicatorResult.MainSeriesName,
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Period"] = period },
            Offset = offset,
        };

    private static ScreenerIndicatorSideConfig ColumnSide(string name)
        => new() { OutputName = name, CategoryType = ScreenerItemCategoryType.Column };

    private static ScreenerIndicatorEntry Numeric(ScreenerIndicatorSideConfig left, ComparisonOperator op, decimal value) => new()
    {
        LeftHand = left,
        Operator = op,
        CategoryType = left.CategoryType,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
        IsEnabled = true,
    };

    private static ScreenerIndicatorEntry Versus(ScreenerIndicatorSideConfig left, ComparisonOperator op, ScreenerIndicatorSideConfig right) => new()
    {
        LeftHand = left,
        Operator = op,
        CategoryType = left.CategoryType,
        TargetMode = RightHandTargetMode.Indicator,
        RightHand = right,
        IsEnabled = true,
    };

    private static ScreenerIndicatorEntry Text(string column, ComparisonOperator op, string text) => new()
    {
        LeftHand = ColumnSide(column),
        Operator = op,
        CategoryType = ScreenerItemCategoryType.Column,
        TargetMode = RightHandTargetMode.StringValue,
        RightStringValue = text,
        IsEnabled = true,
    };

    private static IScreenerConditionNode Leaf(ScreenerIndicatorEntry entry) => new ScreenerConditionLeaf(entry);

    private static IScreenerConditionNode Group(LogicalOperator op, params IScreenerConditionNode[] children) => new ScreenerConditionGroup(op, children.ToImmutableArray());

    private static IScreeningCondition Tree(params IScreenerConditionNode[] roots) => new ScreenerConditionTreeCondition(new ScreenerConditionGroup(LogicalOperator.And, roots.ToImmutableArray()));

    private static string Request(string conditionJson) => "{\"conditionTree\":" + conditionJson + "}";

    private static string N(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- the five cases ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Case1_AnIndicatorAgainstANumber()
    {
        await StartAsync();
        // Price against the middle of the symbols' last closes.
        decimal level = MedianLastClose();
        await AssertSameAsync(
            Tree(Leaf(Numeric(CloseSide(), ComparisonOperator.GreaterThan, level))),
            $$"""{"left":{"price":"Close"},"comparison":"GreaterThan","right":{{N(level)}}}""");

        // An indicator against a number: RSI(14) above 50.
        ScreenerIndicatorSideConfig rsi = new()
        {
            IndicatorType = IndicatorType.RSI,
            OutputName = IndicatorResult.MainSeriesName,
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Period"] = 14 },
        };
        await AssertSameAsync(
            Tree(Leaf(Numeric(rsi, ComparisonOperator.GreaterThan, 50m))),
            """{"left":{"indicator":"RSI","parameters":{"Period":14}},"comparison":"GreaterThan","right":50}""");
    }

    [Fact]
    public async Task Case2_AnIndicatorAgainstAnotherIndicatorWithAnOffset()
    {
        await StartAsync();

        await AssertSameAsync(
            Tree(Leaf(Versus(SmaSide(SmaShort), ComparisonOperator.GreaterThan, SmaSide(SmaLong, LongOffset)))),
            "{\"left\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":" + SmaShort + "}},\"comparison\":\"GreaterThan\",\"right\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":" + SmaLong + "},\"offset\":" + LongOffset + "}}");
        await AssertSameAsync(
            Tree(Leaf(Versus(CloseSide(), ComparisonOperator.LessThanOrEqual, SmaSide(SmaLong)))),
            "{\"left\":{\"price\":\"Close\"},\"comparison\":\"LessThanOrEqual\",\"right\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":" + SmaLong + "}}}");
    }

    [Fact]
    public async Task Case3_AColumnAgainstANumberAndATextColumnAgainstText()
    {
        await SeedMetadataAsync();
        await StartAsync();

        await AssertSameAsync(
            Tree(Leaf(Numeric(ColumnSide("Trailing P/E"), ComparisonOperator.GreaterThan, PeLevel))),
            $$"""{"left":{"column":"Trailing P/E"},"comparison":"GreaterThan","right":{{N(PeLevel)}}}""");
        await AssertSameAsync(
            Tree(Leaf(Text("Sector", ComparisonOperator.Equal, "technology"))),
            """{"left":{"column":"Sector"},"comparison":"Equal","right":"technology"}""");
        await AssertSameAsync(
            Tree(Leaf(Text("Sector", ComparisonOperator.DoesNotContain, "tech"))),
            """{"left":{"column":"Sector"},"comparison":"DoesNotContain","right":"tech"}""");
    }

    [Fact]
    public async Task Case4_APattern()
    {
        await StartAsync();
        ScreenerIndicatorSideConfig Pattern() => new() { OutputName = "Bullish Engulfing", CustomDisplayName = "Bullish Engulfing", CategoryType = ScreenerItemCategoryType.Criteria };

        // Present and absent are complementary on the same universe, so both are compared (a pattern may be rare on random bars).
        string[] present = await TabAsync(Tree(Leaf(Numeric(Pattern(), ComparisonOperator.Equal, 1m))));
        string[] absent = await TabAsync(Tree(Leaf(Numeric(Pattern(), ComparisonOperator.Equal, 0m))));
        Assert.Equal(Symbols.OrderBy(s => s, StringComparer.Ordinal), present.Concat(absent).OrderBy(s => s, StringComparer.Ordinal));

        await AssertSameAsync(Tree(Leaf(Numeric(Pattern(), ComparisonOperator.Equal, 1m))), """{"left":{"criteria":"Bullish Engulfing"},"comparison":"Equal","right":1}""", mixed: false);
        await AssertSameAsync(Tree(Leaf(Numeric(Pattern(), ComparisonOperator.Equal, 0m))), """{"left":{"criteria":"Bullish Engulfing"},"comparison":"Equal","right":0}""", mixed: false);
    }

    [Fact]
    public async Task Case5_AnAndOrTree()
    {
        await SeedMetadataAsync();
        await StartAsync();
        decimal level = MedianLastClose();

        IScreeningCondition tab = Tree(Group(
            LogicalOperator.And,
            Leaf(Numeric(CloseSide(), ComparisonOperator.GreaterThan, level)),
            Group(
                LogicalOperator.Or,
                Leaf(Text("Sector", ComparisonOperator.Equal, Technology)),
                Leaf(Numeric(ColumnSide("Trailing P/E"), ComparisonOperator.LessThan, PeLevel)))));
        string json = $$"""
            {"operator":"And","children":[
              {"left":{"price":"Close"},"comparison":"GreaterThan","right":{{N(level)}}},
              {"operator":"Or","children":[
                {"left":{"column":"Sector"},"comparison":"Equal","right":"{{Technology}}"},
                {"left":{"column":"Trailing P/E"},"comparison":"LessThan","right":{{N(PeLevel)}}}]}]}
            """;

        await AssertSameAsync(tab, json, mixed: false);
        // The same tree with Or at the root is a different, larger set (a check that the operators are not ignored on either side).
        string[] and = await McpAsync(Request(json));
        string[] or = await McpAsync(Request(
            "{\"operator\":\"Or\",\"children\":[{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":" + N(level) + "},{\"left\":{\"column\":\"Sector\"},\"comparison\":\"Equal\",\"right\":\"" + Technology + "\"}]}"));
        Assert.NotEmpty(or);
        Assert.True(or.Length >= and.Length);
    }

    // ---- a two-symbol indicator (T7) --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Case6_ATwoSymbolIndicatorGivesTheSymbolsOfTheScreenerServiceGivenTheComparisonBars()
    {
        await StartAsync();
        const string comparison = "FFF";
        const int period = 20;
        ScreenerIndicatorSideConfig correlation = new()
        {
            IndicatorType = IndicatorType.Correlation,
            OutputName = IndicatorResult.MainSeriesName,
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Period"] = period, ["ComparisonSymbol"] = comparison },
        };
        IDataService data = _services!.GetRequiredService<IDataService>();
        IScreenerSecondaryCandles secondary = await ScreenerSecondaryCandles.LoadAsync(data, new[] { comparison }, TimeFrame.D1, Days, CancellationToken.None);

        // The level separates the symbols: the middle of the correlations the value extractor reads for them.
        var values = new List<decimal>();
        foreach (string symbol in Symbols)
        {
            values.Add(ScreenerValueExtractor.Default.ExtractValueNullable(correlation, await data.LoadCandlesAsync(symbol, TimeFrame.D1, Days), default, secondary)!.Value);
        }

        decimal[] sorted = values.OrderBy(v => v).ToArray();
        Assert.True(sorted[2] < sorted[3]);
        decimal level = (sorted[2] + sorted[3]) / 2m;
        IScreeningCondition condition = Tree(Leaf(Numeric(correlation, ComparisonOperator.GreaterThan, level)));

        // The Screener service given the comparison bars is the Core path the tool uses; the same service without them is what the tab runs today (D-2 = A).
        List<string> universe = Symbols.OrderBy(s => s, StringComparer.Ordinal).ToList();
        List<string> withBars = await _services!.GetRequiredService<IScreenerService>().ScreenAsync(universe, condition, TimeFrame.D1, secondary, new Progress<int>(), CancellationToken.None);
        List<string> withoutBars = await _services!.GetRequiredService<IScreenerService>().ScreenAsync(universe, condition, TimeFrame.D1, new Progress<int>(), CancellationToken.None);
        string[] fromMcp = await McpAsync(Request(
            "{\"left\":{\"indicator\":\"Correlation\",\"parameters\":{\"Period\":" + period + ",\"ComparisonSymbol\":\"" + comparison + "\"}},\"comparison\":\"GreaterThan\",\"right\":" + N(level) + "}"));

        Assert.Equal(withBars, fromMcp);
        Assert.InRange(fromMcp.Length, 1, Symbols.Length - 1);
        Assert.Empty(withoutBars);
    }

    // ---- sides that read their own time frame (per-side frame plan, T6) ----------------------------------------------------------------------

    private static ScreenerIndicatorSideConfig OnFrame(ScreenerIndicatorSideConfig side, TimeFrame frame)
    {
        side.TimeFrame = frame;
        return side;
    }

    /// <summary>The tab's call when a condition reads a weekly or monthly bar: <c>ScreenWithSideFramesAsync</c> with the selection the tab builds (no date, complete bars only).</summary>
    private async Task<string[]> TabSideFramesAsync(IScreeningCondition condition)
    {
        ScreenerSideFramesResult result = await _services!.GetRequiredService<IScreenerService>().ScreenWithSideFramesAsync(
            Symbols.OrderBy(s => s, StringComparer.Ordinal).ToList(), condition, new ScreenerBarSelection(null, CompleteBarsOnly: true), new Progress<int>(), CancellationToken.None);
        return result.Matched.ToArray();
    }

    [Fact]
    public async Task Case7_ADailySideAgainstAWeeklyIndicatorGivesTheSymbolsOfTheTabsSideFrameScan()
    {
        await StartAsync();

        // Close against the weekly 10-bar average, and the weekly close against the daily 20-bar average: each of the two conditions separates the six random walks.
        IScreeningCondition closeAboveWeeklyAverage = Tree(Leaf(Versus(CloseSide(), ComparisonOperator.GreaterThan, OnFrame(SmaSide(10), TimeFrame.W1))));
        IScreeningCondition weeklyCloseBelowDailyAverage = Tree(Leaf(Versus(OnFrame(CloseSide(), TimeFrame.W1), ComparisonOperator.LessThan, SmaSide(SmaLong))));

        string[] fromTab = await TabSideFramesAsync(closeAboveWeeklyAverage);
        string[] fromMcp = await McpAsync(Request("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":10},"timeFrame":"W1"}}"""));
        Assert.Equal(fromTab, fromMcp);
        Assert.InRange(fromMcp.Length, 1, Symbols.Length - 1);

        string[] fromTabSecond = await TabSideFramesAsync(weeklyCloseBelowDailyAverage);
        string[] fromMcpSecond = await McpAsync(Request("{\"left\":{\"price\":\"Close\",\"timeFrame\":\"W1\"},\"comparison\":\"LessThan\",\"right\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":" + SmaLong + "}}}"));
        Assert.Equal(fromTabSecond, fromMcpSecond);
        Assert.InRange(fromMcpSecond.Length, 1, Symbols.Length - 1);
    }

    [Fact]
    public async Task Case8_ATreeThatMixesFramesGivesTheSymbolsOfTheTabsSideFrameScan()
    {
        await StartAsync();
        decimal level = MedianLastClose();
        IScreeningCondition tab = Tree(Group(
            LogicalOperator.Or,
            Leaf(Numeric(CloseSide(), ComparisonOperator.GreaterThan, level)),
            Leaf(Versus(OnFrame(SmaSide(SmaShort), TimeFrame.MN1), ComparisonOperator.GreaterThan, OnFrame(SmaSide(3), TimeFrame.MN1)))));
        string json = $$$"""
            {"operator":"Or","children":[
              {"left":{"price":"Close"},"comparison":"GreaterThan","right":{{{N(level)}}}},
              {"left":{"indicator":"SMA","parameters":{"Period":{{{SmaShort}}}},"timeFrame":"MN1"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":3},"timeFrame":"MN1"}}]}
            """;

        string[] fromTab = await TabSideFramesAsync(tab);
        string[] fromMcp = await McpAsync(Request(json));

        Assert.Equal(fromTab, fromMcp);
        Assert.NotEmpty(fromMcp);
    }

    [Fact]
    public async Task ASavedTemplateWithAWeeklySideGivesTheSymbolsOfTheTabsTemplateRunOnSideFrames()
    {
        ScreenerIndicatorEntry first = Versus(CloseSide(), ComparisonOperator.GreaterThan, OnFrame(SmaSide(10), TimeFrame.W1));
        first.LogicalOperator = LogicalOperator.And;
        ScreenerIndicatorEntry second = Numeric(OnFrame(CloseSide(), TimeFrame.W1), ComparisonOperator.GreaterThan, MedianLastClose() / 2m);
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "EquivalenceWeekly", Description = "EquivalenceWeekly" };
        template.SetEntries(new[] { first, second });
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        await StartAsync();

        HashSet<string> tabSet = await _services!.GetRequiredService<IScreenerTemplateEvaluator>().EvaluateWithSideFramesAsync(
            Symbols.OrderBy(s => s, StringComparer.Ordinal).ToList(), template, new ScreenerBarSelection(null, CompleteBarsOnly: true), new Progress<int>(), CancellationToken.None);
        string[] viaTemplate = await McpAsync($$"""{"templateId":"{{template.Id}}"}""");

        Assert.Equal(tabSet.OrderBy(s => s, StringComparer.Ordinal), viaTemplate);
        Assert.InRange(viaTemplate.Length, 1, Symbols.Length - 1);
    }

    // ---- a saved template runs as the same conditions given directly ------------------------------------------------------------------------

    [Fact]
    public async Task ASavedTemplateGivesTheSymbolsOfTheTabsTemplateRunAndOfTheSameConditionsGivenDirectly()
    {
        await SeedMetadataAsync();
        decimal level = MedianLastClose();
        ScreenerIndicatorEntry first = Numeric(CloseSide(), ComparisonOperator.GreaterThan, level);
        ScreenerIndicatorEntry second = Text("Sector", ComparisonOperator.Equal, Technology);
        first.LogicalOperator = LogicalOperator.And;
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "Equivalence", Description = "Equivalence" };
        template.SetEntries(new[] { first, second });
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        await StartAsync();

        // The tab runs a template through the template evaluator.
        HashSet<string> tabSet = await _services!.GetRequiredService<IScreenerTemplateEvaluator>().EvaluateAsync(
            Symbols.OrderBy(s => s, StringComparer.Ordinal).ToList(), template, TimeFrame.D1, new Progress<int>(), CancellationToken.None);
        string[] viaTemplate = await McpAsync($$"""{"templateId":"{{template.Id}}"}""");
        string[] direct = await McpAsync(Request(
            "{\"operator\":\"And\",\"children\":[{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":" + N(level) + "},{\"left\":{\"column\":\"Sector\"},\"comparison\":\"Equal\",\"right\":\"" + Technology + "\"}]}"));

        Assert.Equal(tabSet.OrderBy(s => s, StringComparer.Ordinal), viaTemplate);
        Assert.Equal(direct, viaTemplate);
        Assert.InRange(viaTemplate.Length, 1, Symbols.Length - 1);
    }
}
