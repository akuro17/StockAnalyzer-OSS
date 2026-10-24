using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
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
/// T7 (MP-D) through <c>sa_run_screener</c>: a side may name an indicator that compares two symbols when it gives the ComparisonSymbol parameter. The
/// expected symbols are computed independently, by the value extractor called per symbol with the comparison symbol's bars loaded on its own.
/// Four symbols follow seeded random walks, so the compared values differ and every threshold below separates them (asserted).
/// </summary>
public sealed class ScreenerCrossTickerToolTests : IDisposable
{
    private const int Days = 300;
    private const int Period = 20;

    private static readonly string[] Symbols = { "AAA", "BBB", "CCC", "DDD" };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, Symbols);
    private readonly string _templates = Path.Combine(TestDataIsolationBootstrap.NewTempDirectory("McpScreenerCrossTicker"), "Screener");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<ServiceProvider> _providers = new();

    public ScreenerCrossTickerToolTests()
    {
        for (int index = 0; index < Symbols.Length; index++)
        {
            Reseed(Symbols[index], seed: 211 + index * 29);
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
            values[$"AiIntegration:{key}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(overrides), new LogSink(new StringWriter()), _templates);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private ServiceProvider Services()
    {
        ServiceProvider provider = new ServiceCollection().AddStockAnalyzerReadOnlyCore(Configuration()).BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string conditionJson, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"conditionTree":{{conditionJson}}{{extra}}}"""))!;

    private static string[] Matched(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).ToArray();
    }

    private static string Correlation(string comparison, int? offset = null)
        => "{\"indicator\":\"Correlation\",\"parameters\":{\"Period\":" + Period + ",\"ComparisonSymbol\":\"" + comparison + "\"}"
           + (offset is null ? string.Empty : ",\"offset\":" + offset) + "}";

    private static string Compare(string left, string comparison, string right) => $$"""{"left":{{left}},"comparison":"{{comparison}}","right":{{right}}}""";

    private static string N(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- the expected values, computed on their own ----------------------------------------------------------------------------------

    private static ScreenerIndicatorSideConfig CorrelationSide(string comparison)
        => new()
        {
            IndicatorType = IndicatorType.Correlation,
            OutputName = IndicatorResult.MainSeriesName,
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Period"] = Period, ["ComparisonSymbol"] = comparison },
        };

    /// <summary>The correlation of every symbol with <paramref name="comparison"/>, read from the value extractor with the comparison bars loaded here.</summary>
    private async Task<Dictionary<string, decimal>> CorrelationsAsync(string comparison)
    {
        ServiceProvider services = Services();
        IDataService data = services.GetRequiredService<IDataService>();
        IScreenerSecondaryCandles secondary = await ScreenerSecondaryCandles.LoadAsync(data, new[] { comparison }, TimeFrame.D1, Days, CancellationToken.None);
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (string symbol in Symbols)
        {
            IReadOnlyList<CandleData> bars = await data.LoadCandlesAsync(symbol, TimeFrame.D1, Days);
            decimal? value = ScreenerValueExtractor.Default.ExtractValueNullable(CorrelationSide(comparison), bars, default, secondary);
            Assert.NotNull(value);
            values[symbol] = value!.Value;
        }

        return values;
    }

    /// <summary>A level strictly between the two middle values, so the symbols above it and below it are both present.</summary>
    private static decimal Median(IEnumerable<decimal> values)
    {
        decimal[] sorted = values.OrderBy(v => v).ToArray();
        Assert.True(sorted[sorted.Length / 2 - 1] < sorted[sorted.Length / 2], "the test data must separate the middle two symbols");
        return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2m;
    }

    // ---- AT-D.3 / AT-D.6: a two-symbol indicator through the tool ----------------------------------------------------------------------

    [Fact]
    public async Task ACorrelationWithAComparisonSymbolMatchesTheSymbolsTheExtractorSelects()
    {
        Dictionary<string, decimal> values = await CorrelationsAsync("DDD");
        decimal level = Median(values.Values);
        string[] expected = Symbols.Where(s => values[s] > level).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Compare(Correlation("DDD"), "GreaterThan", N(level)));

        Assert.Equal(expected, Matched(response));
        Assert.InRange(expected.Length, 1, Symbols.Length - 1);
    }

    [Fact]
    public async Task AnOffsetOnATwoSymbolSideReadsAnEarlierBar()
    {
        ServiceProvider services = Services();
        IDataService data = services.GetRequiredService<IDataService>();
        IScreenerSecondaryCandles secondary = await ScreenerSecondaryCandles.LoadAsync(data, new[] { "DDD" }, TimeFrame.D1, Days, CancellationToken.None);
        ScreenerIndicatorSideConfig side = CorrelationSide("DDD");
        side.Offset = 5;
        var values = new Dictionary<string, decimal>();
        foreach (string symbol in Symbols)
        {
            values[symbol] = ScreenerValueExtractor.Default.ExtractValueNullable(side, await data.LoadCandlesAsync(symbol, TimeFrame.D1, Days), default, secondary)!.Value;
        }

        decimal level = Median(values.Values);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Compare(Correlation("DDD", offset: 5), "GreaterThan", N(level)));

        Assert.Equal(Symbols.Where(s => values[s] > level).OrderBy(s => s, StringComparer.Ordinal), Matched(response));
    }

    [Fact]
    public async Task BothSidesOfAComparisonMayBeTwoSymbolIndicators()
    {
        Dictionary<string, decimal> withC = await CorrelationsAsync("CCC");
        Dictionary<string, decimal> withD = await CorrelationsAsync("DDD");
        string[] expected = Symbols.Where(s => withC[s] > withD[s]).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.InRange(expected.Length, 1, Symbols.Length - 1);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Compare(Correlation("CCC"), "GreaterThan", Correlation("DDD")));

        Assert.Equal(expected, Matched(response));
    }

    [Fact]
    public async Task ASymbolIsComparedWithItselfWhenItIsAlsoScreened()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Compare(Correlation("BBB"), "GreaterThan", "0.999"));

        Assert.Equal(new[] { "BBB" }, Matched(response)); // only BBB correlates perfectly with BBB
    }

    [Fact]
    public async Task TheComparisonSymbolIsCaseInsensitiveAndTheOtherConditionsStillWorkInTheSameTree()
    {
        Dictionary<string, decimal> values = await CorrelationsAsync("DDD");
        decimal level = Median(values.Values);
        McpMessageDispatcher dispatcher = await StartAsync();
        string tree = $$"""{"operator":"And","children":[{{Compare(Correlation("ddd"), "GreaterThan", N(level))}},{{Compare("""{"price":"Close"}""", "GreaterThan", "0")}}]}""";

        using JsonDocument response = await RunAsync(dispatcher, tree);

        Assert.Equal(Symbols.Where(s => values[s] > level).OrderBy(s => s, StringComparer.Ordinal), Matched(response));
    }

    [Fact]
    public async Task TheMatchedValuesCarryTheTwoSymbolValue()
    {
        Dictionary<string, decimal> values = await CorrelationsAsync("DDD");
        decimal level = Median(values.Values);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Compare(Correlation("DDD"), "GreaterThan", N(level)), ",\"includeMatchedValues\":true");

        JsonElement matched = McpTestSupport.Structured(response).GetProperty("matchedValues");
        string[] symbols = Matched(response);
        Assert.NotEmpty(symbols);
        foreach (string symbol in symbols)
        {
            decimal shown = matched.GetProperty(symbol).GetProperty("L0").GetProperty("left").GetDecimal();
            Assert.Equal(values[symbol], shown);
        }
    }

    // ---- AT-D.3: what is refused ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"left":{"indicator":"Correlation"},"comparison":"GreaterThan","right":0}""", "INVALID_ARGUMENT", "conditionTree.left.parameters.ComparisonSymbol")]
    [InlineData("""{"left":{"indicator":"Correlation","parameters":{"ComparisonSymbol":"  "}},"comparison":"GreaterThan","right":0}""", "INVALID_ARGUMENT", "conditionTree.left.parameters.ComparisonSymbol")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"CrossCorrelation"}}""", "INVALID_ARGUMENT", "conditionTree.right.parameters.ComparisonSymbol")]
    [InlineData("""{"left":{"indicator":"Correlation","parameters":{"ComparisonSymbol":"a/b"}},"comparison":"GreaterThan","right":0}""", "INVALID_ARGUMENT", "conditionTree.left.parameters.ComparisonSymbol")]
    [InlineData("""{"left":{"indicator":"Correlation","parameters":{"ComparisonSymbol":"NOPE"}},"comparison":"GreaterThan","right":0}""", "SYMBOL_NOT_FOUND", "conditionTree.left.parameters.ComparisonSymbol")]
    [InlineData("""{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":0},{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"Correlation","parameters":{"ComparisonSymbol":"NOPE"}}}]}""", "SYMBOL_NOT_FOUND", "conditionTree.children[1].right.parameters.ComparisonSymbol")]
    [InlineData("""{"left":{"indicator":"Ratiocator","parameters":{"ComparisonSymbol":"DDD"}},"comparison":"GreaterThan","right":0}""", "INVALID_ARGUMENT", "conditionTree.left.indicator")]
    public async Task AFaultyTwoSymbolSideIsRejectedWithTheCodeAndThePathOfTheFault(string conditionJson, string code, string parameter)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, conditionJson);

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(parameter, error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task MoreDifferentComparisonSymbolsThanTheLimitAreRefusedAndRepeatsAreNotCounted()
    {
        McpMessageDispatcher dispatcher = await StartAsync(("MaxSecondarySymbolsPerCall", "1"));

        using JsonDocument two = await RunAsync(dispatcher, Compare(Correlation("CCC"), "GreaterThan", Correlation("DDD")));
        using JsonDocument same = await RunAsync(dispatcher, Compare(Correlation("CCC"), "GreaterThan", Correlation("ccc")));

        Assert.True(McpTestSupport.IsError(two));
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.Structured(two).GetProperty("code").GetString());
        Assert.Equal("conditionTree", McpTestSupport.Structured(two).GetProperty("param").GetString());
        Assert.False(McpTestSupport.IsError(same));
    }

    // ---- a saved template ----------------------------------------------------------------------------------------------------------------

    private Guid SaveTemplate(params ScreenerIndicatorEntry[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "CrossTicker", Description = "CrossTicker" };
        template.SetEntries(entries);
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        return template.Id;
    }

    private static ScreenerIndicatorEntry CorrelationEntry(string? comparison, decimal level) => new()
    {
        LeftHand = comparison is null
            ? new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Correlation, OutputName = IndicatorResult.MainSeriesName, Parameters = new Dictionary<string, object> { ["Period"] = Period } }
            : CorrelationSide(comparison),
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = level,
        IsEnabled = true,
    };

    [Fact]
    public async Task ASavedTemplateWithATwoSymbolEntryRunsLikeTheSameConditionGivenDirectlyAndLikeTheTabsEvaluatorWithTheBars()
    {
        Dictionary<string, decimal> values = await CorrelationsAsync("DDD");
        decimal level = Median(values.Values);
        Guid id = SaveTemplate(CorrelationEntry("DDD", level));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument viaTemplate = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"templateId":"{{id}}"}"""))!;
        using JsonDocument direct = await RunAsync(dispatcher, Compare(Correlation("DDD"), "GreaterThan", N(level)));

        string[] expected = Symbols.Where(s => values[s] > level).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, Matched(viaTemplate));
        Assert.Equal(expected, Matched(direct));
        Assert.False(McpTestSupport.Structured(viaTemplate).TryGetProperty("warnings", out _), "an entry that names its comparison symbol needs no warning");

        // The evaluator the Screener tab calls, given the comparison bars, selects the same symbols.
        ServiceProvider services = Services();
        IScreenerSecondaryCandles secondary = await ScreenerSecondaryCandles.LoadAsync(services.GetRequiredService<IDataService>(), new[] { "DDD" }, TimeFrame.D1, Days, CancellationToken.None);
        var template = new ScreenerIndicatorTemplate { Id = id, Name = "CrossTicker" };
        template.SetEntries(new[] { CorrelationEntry("DDD", level) });
        HashSet<string> tab = await services.GetRequiredService<IScreenerTemplateEvaluator>().EvaluateAsync(
            Symbols.OrderBy(s => s, StringComparer.Ordinal).ToList(), template, TimeFrame.D1, secondary, new Progress<int>(), CancellationToken.None);
        Assert.Equal(expected, tab.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ATemplateEntryThatNamesNoComparisonSymbolNeverMatchesAndIsWarnedAbout()
    {
        Guid id = SaveTemplate(CorrelationEntry(comparison: null, level: -2m));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"templateId":"{{id}}"}"""))!;

        Assert.Empty(Matched(response));
        string warnings = McpTestSupport.Structured(response).GetProperty("warnings").ToString();
        Assert.Contains("names no ComparisonSymbol", warnings);
    }

    [Fact]
    public async Task ATemplateEntryWithAnUnknownComparisonSymbolIsRefusedWithThePathOfTheEntry()
    {
        Guid id = SaveTemplate(CorrelationEntry("NOPE", level: 0m));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"templateId":"{{id}}"}"""))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("SYMBOL_NOT_FOUND", McpTestSupport.Structured(response).GetProperty("code").GetString());
        Assert.Equal("entries[0].leftHand", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }
}
