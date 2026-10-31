using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit.Abstractions;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The <c>derived</c> argument of sa_calculate_indicators through the real composition: every operation against hand-computed values (the expectation is
/// computed in the test from the indicator series of the same response), null propagation, zero divisors, references, validation and the unchanged output when omitted.
/// </summary>
public sealed class DerivedSeriesTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Sma3 = """{"id":"s3","type":"SMA","parameters":{"Period":3}}""";
    private const string Sma10 = """{"id":"s10","type":"SMA","parameters":{"Period":10}}""";
    private const string Sma50 = """{"id":"s50","type":"SMA","parameters":{"Period":50}}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "aapl");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly ITestOutputHelper _output;

    public DerivedSeriesTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string indicatorsJson, string? derivedJson, string extra = "")
    {
        string derivedPart = derivedJson is null ? string.Empty : $$$""","derived":{{{derivedJson}}}""";
        string arguments = $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":{{{indicatorsJson}}}{{{derivedPart}}}{{{extra}}}}""";
        return (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators", arguments))!;
    }

    private static JsonElement Result(JsonDocument response, string id) => McpTestSupport.Structured(response).GetProperty("results").GetProperty(id);

    private static decimal?[] Values(JsonElement array)
        => array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    private static decimal?[] Main(JsonDocument response, string derivedId) => Values(Result(response, derivedId).GetProperty("series").GetProperty("Main"));

    private static decimal?[] Series(JsonDocument response, string indicatorId, string seriesName) => Values(Result(response, indicatorId).GetProperty("series").GetProperty(seriesName));

    private static string Derived(string id, string op, string left, string right) => $$$"""{"id":"{{{id}}}","op":"{{{op}}}","left":{{{left}}},"right":{{{right}}}}""";

    private static string Ref(string id) => $$$"""{"ref":"{{{id}}}"}""";

    private static string Constant(string number) => $$$"""{"constant":{{{number}}}}""";

    private const string AllBars = ""","outputTailCount":500""";

    // ---- operations -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Add")]
    [InlineData("Subtract")]
    [InlineData("Multiply")]
    [InlineData("Divide")]
    [InlineData("Max")]
    [InlineData("Min")]
    public async Task EachOperationOnTwoIndicatorSeriesEqualsTheHandComputedValueAtEveryBar(string op)
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3},{Sma10}]", $"[{Derived("d", op, Ref("s3"), Ref("s10"))}]", AllBars);

        decimal?[] left = Series(response, "s3", "SMA");
        decimal?[] right = Series(response, "s10", "SMA");
        decimal?[] expected = left.Zip(right, (l, r) => l is null || r is null ? (decimal?)null : op switch
        {
            "Add" => l + r,
            "Subtract" => l - r,
            "Multiply" => l * r,
            "Divide" => l / r,
            "Max" => l > r ? l : r,
            "Min" => l < r ? l : r,
            _ => throw new InvalidOperationException(op),
        }).ToArray();

        JsonElement result = Result(response, "d");
        Assert.True(result.GetProperty("isSuccessful").GetBoolean());
        Assert.Equal("Derived", result.GetProperty("type").GetString());
        Assert.Equal(op, result.GetProperty("op").GetString());
        Assert.Equal(expected, Main(response, "d"));
        Assert.Equal(0, result.GetProperty("divisionByZeroCount").GetInt32());
    }

    [Theory]
    [InlineData("Add", "2.5")]
    [InlineData("Subtract", "2.5")]
    [InlineData("Multiply", "2")]
    [InlineData("Divide", "4")]
    [InlineData("Max", "110")]
    [InlineData("Min", "110")]
    public async Task AConstantOnTheRightSideIsAppliedAtEveryBar(string op, string constant)
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma10}]", $"[{Derived("d", op, Ref("s10"), Constant(constant))}]", AllBars);

        decimal c = decimal.Parse(constant, System.Globalization.CultureInfo.InvariantCulture);
        decimal?[] expected = Series(response, "s10", "SMA").Select(v => v is not { } x ? (decimal?)null : op switch
        {
            "Add" => x + c,
            "Subtract" => x - c,
            "Multiply" => x * c,
            "Divide" => x / c,
            "Max" => Math.Max(x, c),
            "Min" => Math.Min(x, c),
            _ => throw new InvalidOperationException(op),
        }).ToArray();

        Assert.Equal(expected, Main(response, "d"));
    }

    [Fact]
    public async Task AConstantOnTheLeftSideKeepsTheOrderOfTheOperands()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma10}]", $"[{Derived("d", "Subtract", Constant("1000"), Ref("s10"))}]", AllBars);

        decimal?[] expected = Series(response, "s10", "SMA").Select(v => v is { } x ? 1000m - x : (decimal?)null).ToArray();
        Assert.Equal(expected, Main(response, "d"));
    }

    [Fact]
    public async Task ABarWithoutAValueOnEitherSideHasNoValue_AndTheLeadingNullsAreCounted()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3},{Sma50}]", $"[{Derived("d", "Subtract", Ref("s3"), Ref("s50"))}]", AllBars);

        JsonElement result = Result(response, "d");
        Assert.Equal(49, result.GetProperty("leadingNullCount").GetProperty("Main").GetInt32()); // SMA(50) has no value for the first 49 bars
        decimal?[] values = Main(response, "d");
        Assert.All(values.Take(49), Assert.Null);
        Assert.All(values.Skip(49), v => Assert.NotNull(v));
        Assert.Equal(values[^1], decimal.Parse(result.GetProperty("latestValues").GetProperty("Main").GetRawText(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ADerivedSeriesCanReadAnEarlierDerivedSeries()
    {
        string derived = $"[{Derived("spread", "Subtract", Ref("s3"), Ref("s10"))},{Derived("scaled", "Multiply", Ref("spread"), Constant("100"))},{Derived("both", "Add", Ref("scaled"), Ref("spread"))}]";
        using var response = await CallAsync(await StartAsync(), $"[{Sma3},{Sma10}]", derived, AllBars);

        decimal?[] spread = Main(response, "spread");
        Assert.Equal(spread.Select(v => v * 100).ToArray(), Main(response, "scaled"));
        Assert.Equal(spread.Select(v => v * 100 + v).ToArray(), Main(response, "both"));
    }

    [Fact]
    public async Task TheTailLimitsTheReturnedBarsButNotTheCalculation()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3},{Sma50}]", $"[{Derived("d", "Subtract", Ref("s3"), Ref("s50"))}]", ""","outputTailCount":10""");

        JsonElement result = Result(response, "d");
        Assert.Equal(10, result.GetProperty("series").GetProperty("Main").GetArrayLength());
        Assert.Equal(49, result.GetProperty("leadingNullCount").GetProperty("Main").GetInt32()); // counted over the whole calculated history
        decimal?[] tail = Main(response, "d");
        decimal?[] expected = Series(response, "s3", "SMA").Zip(Series(response, "s50", "SMA"), (l, r) => l - r).ToArray();
        Assert.Equal(expected, tail);
    }

    // ---- zero divisors --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADivisionByZeroEverywhereIsAnUnsuccessfulResultThatCountsTheBars_NotAnError()
    {
        string derived = $"[{Derived("zero", "Subtract", Ref("s10"), Ref("s10"))},{Derived("d", "Divide", Ref("s3"), Ref("zero"))}]";
        using var response = await CallAsync(await StartAsync(), $"[{Sma3},{Sma10}]", derived, AllBars);

        Assert.False(McpTestSupport.IsError(response));
        Assert.True(Result(response, "s3").GetProperty("isSuccessful").GetBoolean());
        JsonElement result = Result(response, "d");
        Assert.False(result.GetProperty("isSuccessful").GetBoolean());
        Assert.Equal(StoredDays - 9, result.GetProperty("divisionByZeroCount").GetInt32()); // bars where both s3 and zero have a value
        Assert.Contains("zero divisor", result.GetProperty("errorMessage").GetString());
        Assert.False(result.TryGetProperty("series", out _));
    }

    [Fact]
    public async Task ADivisionByZeroAtSomeBarsGivesNullThereAndCountsThem()
    {
        // ROC(1) of the zig-zag closes is positive on up bars and negative on down bars, so Max(roc, 0) is zero on the down bars only.
        const string rocOne = """{"id":"roc","type":"ROC","parameters":{"Period":1}}""";
        string indicators = $"[{Sma3},{rocOne}]";
        string derived = $"[{Derived("up", "Max", Ref("roc"), Constant("0"))},{Derived("d", "Divide", Ref("s3"), Ref("up"))}]";
        using var response = await CallAsync(await StartAsync(), indicators, derived, AllBars);

        decimal?[] s3 = Series(response, "s3", "SMA");
        decimal?[] roc = Series(response, "roc", "ROC");
        decimal?[] up = roc.Select(v => v is { } x ? Math.Max(x, 0m) : (decimal?)null).ToArray();
        int expectedZeroDivisors = s3.Zip(up, (l, r) => l is not null && r == 0m).Count(b => b);
        decimal?[] expected = s3.Zip(up, (l, r) => l is null || r is null || r == 0m ? (decimal?)null : l / r).ToArray();

        JsonElement result = Result(response, "d");
        Assert.InRange(expectedZeroDivisors, 1, StoredDays - 1);
        Assert.True(result.GetProperty("isSuccessful").GetBoolean());
        Assert.Equal(expectedZeroDivisors, result.GetProperty("divisionByZeroCount").GetInt32());
        Assert.Equal(expected, Main(response, "d"));
    }

    // ---- references -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnIndicatorWithSeveralOutputSeriesIsReadByName_CaseInsensitively()
    {
        string left = """{"ref":"m","outputSeriesName":"signal"}""";
        using var response = await CallAsync(await StartAsync(), """[{"id":"m","type":"MACD"}]""", $"[{Derived("d", "Add", left, Constant("1"))}]", AllBars);

        decimal?[] expected = Series(response, "m", "Signal").Select(v => v + 1).ToArray();
        Assert.Equal(expected, Main(response, "d"));
    }

    [Fact]
    public async Task ADerivedSeriesCanBeReadWithTheNameMain()
    {
        string derived = $"[{Derived("a", "Add", Ref("s3"), Constant("1"))},{Derived("b", "Add", """{"ref":"a","outputSeriesName":"main"}""", Constant("1"))}]";
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", derived, AllBars);

        Assert.Equal(Series(response, "s3", "SMA").Select(v => v + 2).ToArray(), Main(response, "b"));
    }

    [Fact]
    public async Task ADerivedSeriesOfAFailedIndicatorFailsAndNamesIt_AndTheCallSucceeds()
    {
        string indicators = """[{"id":"r","type":"RSI"},{"id":"s2","type":"SMA","parameters":{"Period":2}}]""";
        string derived = $"[{Derived("fromRsi", "Add", Ref("r"), Constant("1"))},{Derived("chained", "Add", Ref("fromRsi"), Constant("1"))},{Derived("fromSma", "Add", Ref("s2"), Constant("1"))}]";
        using var response = await CallAsync(await StartAsync(), indicators, derived, ""","lookbackBars":5""");

        Assert.False(McpTestSupport.IsError(response));
        Assert.False(Result(response, "r").GetProperty("isSuccessful").GetBoolean());
        JsonElement failed = Result(response, "fromRsi");
        Assert.False(failed.GetProperty("isSuccessful").GetBoolean());
        Assert.Contains("'r'", failed.GetProperty("errorMessage").GetString());
        Assert.False(failed.TryGetProperty("series", out _));
        JsonElement chained = Result(response, "chained");
        Assert.False(chained.GetProperty("isSuccessful").GetBoolean());
        Assert.Contains("'fromRsi'", chained.GetProperty("errorMessage").GetString());
        Assert.True(Result(response, "fromSma").GetProperty("isSuccessful").GetBoolean());
    }

    [Fact]
    public async Task ALookAheadInputMarksTheDerivedSeriesNonCausal_AndItIsInheritedAlongTheChain()
    {
        string indicators = $$"""[{{Sma3}},{"id":"i","type":"Ichimoku"}]""";
        string chikou = """{"ref":"i","outputSeriesName":"ChikouSpan"}""";
        string derived = $"[{Derived("ahead", "Add", chikou, Constant("0"))},{Derived("inherited", "Add", Ref("ahead"), Constant("1"))},{Derived("causal", "Add", Ref("s3"), Constant("1"))}]";
        using var response = await CallAsync(await StartAsync(), indicators, derived, AllBars);

        Assert.Equal(new[] { "Main" }, Result(response, "ahead").GetProperty("nonCausalSeries").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "Main" }, Result(response, "inherited").GetProperty("nonCausalSeries").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Empty(Result(response, "causal").GetProperty("nonCausalSeries").EnumerateArray());
    }

    [Fact]
    public async Task ADerivedResultStatesExplicitlyThatItsUnitIsNull()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", $"[{Derived("d", "Add", Ref("s3"), Constant("1"))}]");

        JsonElement result = Result(response, "d");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("unit").ValueKind); // the same expression as a sa_get_capabilities entry without a stored unit
        Assert.False(result.TryGetProperty("unitUndefined", out _));
        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
    }

    // ---- own time frame, end date, main series --------------------------------------------------------------------

    [Fact]
    public async Task ADerivedSeriesReadsAnIndicatorOnItsOwnTimeFrameAsAlignedToTheCallsBars()
    {
        string weekly = """{"id":"w","type":"SMA","parameters":{"Period":2},"timeFrame":"W1"}""";
        using var response = await CallAsync(await StartAsync(), $"[{weekly}]", $"[{Derived("d", "Add", Ref("w"), Constant("1"))}]", AllBars);

        decimal?[] aligned = Series(response, "w", "SMA");
        Assert.Equal(StoredDays, aligned.Length);
        Assert.Contains(aligned, v => v is null); // before the first closed weekly period, and the warm-up
        Assert.Contains(aligned, v => v is not null);
        Assert.Equal(aligned.Select(v => v + 1).ToArray(), Main(response, "d"));
    }

    [Fact]
    public async Task WithAnEndDateTheDerivedSeriesEqualsThePrefixOfTheSameCallWithoutOne()
    {
        var dispatcher = await StartAsync();
        string derived = $"[{Derived("d", "Subtract", Ref("s3"), Ref("s10"))}]";
        using var full = await CallAsync(dispatcher, $"[{Sma3},{Sma10}]", derived, AllBars);
        using var cut = await CallAsync(dispatcher, $"[{Sma3},{Sma10}]", derived, $$"""{{AllBars}},"endDate":"2024-10-27" """.TrimEnd());

        decimal?[] fromCut = Main(cut, "d");
        Assert.InRange(fromCut.Length, 2, StoredDays - 1);
        Assert.Equal(Main(full, "d").Take(fromCut.Length).ToArray(), fromCut); // no bar after the end date is read
        Assert.Equal("2024-10-27", McpTestSupport.Structured(cut).GetProperty("endDate").GetString());
    }

    [Theory]
    [InlineData("Ichimoku")]
    [InlineData("Stoch")]
    [InlineData("ADX")]
    public async Task AnIndicatorWithoutAMainSeriesNeedsTheSeriesNamed(string type)
    {
        // These types list no series that is their main one, so "the main series" would be a guess: the refusal names the series to choose from.
        string indicator = $$$"""{"id":"x","type":"{{{type}}}"}""";
        using var response = await CallAsync(await StartAsync(), $"[{indicator}]", $"[{Derived("d", "Add", Ref("x"), Constant("1"))}]");

        Assert.Equal(ToolErrorCodes.InvalidOutputSeries, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("derived[0].left.outputSeriesName", McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.Contains("Name one of", McpTestSupport.Structured(response).GetProperty("hint").GetString());
    }

    [Fact]
    public void EveryRegisteredIndicatorNamesAMainSeriesOfItsListedSeriesOrNone()
    {
        var factory = new IndicatorFactory();
        var provider = new ScreenerCatalogProvider();
        var catalog = new IndicatorCatalog(factory, provider);
        var chain = new IndicatorChainPlan(factory);
        int named = 0;
        var withoutMain = new List<string>();
        foreach (IndicatorCatalogEntry entry in catalog.Entries.Where(e => e.IsRegistered))
        {
            CoreIndicatorSettings? settings = provider.GetDefaultSettings(Enum.Parse<IndicatorType>(entry.Type), factory);
            if (settings is null)
            {
                continue; // the catalogue lists such a type without parameters; a request cannot be built for it either
            }

            var request = new RequestedIndicator(0, "x", entry, settings, null, null, null, null);
            string? main = chain.MainSeriesDisplayName(request);
            if (main is null)
            {
                withoutMain.Add(entry.Type);
                continue;
            }

            Assert.Contains(main, entry.OutputSeriesNames);
            named++;
        }

        Assert.True(named > 0);
        _output.WriteLine($"registered indicators with a listed Main series: {named}; without: {withoutMain.Count} [{string.Join(", ", withoutMain)}]");
    }

    // ---- unchanged output when omitted ----------------------------------------------------------------------------

    [Fact]
    public async Task OmittingDerivedOrSendingAnEmptyListGivesTheSameOutputByteForByte()
    {
        var dispatcher = await StartAsync();
        string indicators = $"[{Sma3},{{\"id\":\"m\",\"type\":\"MACD\"}}]";

        using var omitted = await CallAsync(dispatcher, indicators, null);
        using var empty = await CallAsync(dispatcher, indicators, "[]");

        Assert.Equal(McpTestSupport.Structured(omitted).GetRawText(), McpTestSupport.Structured(empty).GetRawText());
        Assert.Equal(new[] { "s3", "m" }, McpTestSupport.Structured(omitted).GetProperty("results").EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task TheDerivedResultsComeAfterTheIndicatorsInDeclarationOrder_AndTheIndicatorsAreUnchanged()
    {
        var dispatcher = await StartAsync();
        string indicators = $"[{Sma3},{Sma10}]";
        string derived = $"[{Derived("b", "Add", Ref("s3"), Constant("1"))},{Derived("a", "Add", Ref("s10"), Constant("1"))}]";

        using var plain = await CallAsync(dispatcher, indicators, null);
        using var extended = await CallAsync(dispatcher, indicators, derived);

        Assert.Equal(new[] { "s3", "s10", "b", "a" }, McpTestSupport.Structured(extended).GetProperty("results").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Result(plain, "s3").GetRawText(), Result(extended, "s3").GetRawText());
        Assert.Equal(Result(plain, "s10").GetRawText(), Result(extended, "s10").GetRawText());
    }

    // ---- validation -----------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> InvalidDerived => new()
    {
        // description, derived array, expected path
        { "forward reference", $"[{Derived("a", "Add", Ref("b"), Constant("1"))},{Derived("b", "Add", Ref("s3"), Constant("1"))}]", "derived[0].left" },
        { "self reference", $"[{Derived("a", "Add", Ref("s3"), Ref("a"))}]", "derived[0].right" },
        { "unknown id", $"[{Derived("a", "Add", Ref("nope"), Constant("1"))}]", "derived[0].left" },
        { "id used by an indicator", $"[{Derived("s3", "Add", Ref("s3"), Constant("1"))}]", "derived[0].id" },
        { "id used twice", $"[{Derived("a", "Add", Ref("s3"), Constant("1"))},{Derived("a", "Add", Ref("s3"), Constant("2"))}]", "derived[1].id" },
        { "empty id", $"[{Derived(" ", "Add", Ref("s3"), Constant("1"))}]", "derived[0].id" },
        { "Ratio is not an operation", $"[{Derived("a", "Ratio", Ref("s3"), Constant("1"))}]", "derived[0].op" },
        { "operation names are case-sensitive", $"[{Derived("a", "add", Ref("s3"), Constant("1"))}]", "derived[0].op" },
        { "ref and constant together", """[{"id":"a","op":"Add","left":{"ref":"s3","constant":1},"right":{"constant":1}}]""", "derived[0].left" },
        { "neither ref nor constant", """[{"id":"a","op":"Add","left":{},"right":{"constant":1}}]""", "derived[0].left" },
        { "unknown operand key", """[{"id":"a","op":"Add","left":{"ref":"s3"},"right":{"value":1}}]""", "derived[0].right.value" },
        { "series name on a constant", """[{"id":"a","op":"Add","left":{"constant":1,"outputSeriesName":"SMA"},"right":{"constant":1}}]""", "derived[0].left.outputSeriesName" },
        { "constant is a string", """[{"id":"a","op":"Add","left":{"constant":"1"},"right":{"constant":1}}]""", "derived[0].left.constant" },
        { "constant beyond decimal range", """[{"id":"a","op":"Add","left":{"constant":1e40},"right":{"constant":1}}]""", "derived[0].left.constant" },
        { "ref is a number", """[{"id":"a","op":"Add","left":{"ref":3},"right":{"constant":1}}]""", "derived[0].left.ref" },
        { "unknown output series", """[{"id":"a","op":"Add","left":{"ref":"s3","outputSeriesName":"Nope"},"right":{"constant":1}}]""", "derived[0].left.outputSeriesName" },
        { "a derived series has only Main", $"[{Derived("a", "Add", Ref("s3"), Constant("1"))},{Derived("b", "Add", """{"ref":"a","outputSeriesName":"Upper"}""", Constant("1"))}]", "derived[1].left.outputSeriesName" },
    };

    [Theory]
    [MemberData(nameof(InvalidDerived))]
    public async Task AnInvalidDerivedEntryIsRefusedNamingThePath(string description, string derivedJson, string expectedPath)
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", derivedJson);

        Assert.True(McpTestSupport.IsError(response), description);
        string code = McpTestSupport.ErrorCodeOf(response);
        Assert.True(code is ToolErrorCodes.InvalidArgument or ToolErrorCodes.InvalidOutputSeries, $"{description}: {code}");
        Assert.Equal(expectedPath, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task AnUnknownOutputSeriesIsInvalidOutputSeries()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", """[{"id":"a","op":"Add","left":{"ref":"s3","outputSeriesName":"Nope"},"right":{"constant":1}}]""");

        Assert.Equal(ToolErrorCodes.InvalidOutputSeries, McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task MoreDerivedSeriesThanTheLimitAreRefused_NotCut()
    {
        string derived = $"[{Derived("a", "Add", Ref("s3"), Constant("1"))},{Derived("b", "Add", Ref("s3"), Constant("2"))},{Derived("c", "Add", Ref("s3"), Constant("3"))}]";
        using var response = await CallAsync(await StartAsync(("MaxDerivedSeriesPerCall", "2")), $"[{Sma3}]", derived);

        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("derived", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task ARefusalHappensBeforeAnyBarIsLoaded()
    {
        // An endDate before the first stored bar would be INSUFFICIENT_MARKET_DATA once the bars are loaded; the bad derived entry must be reported instead.
        using var response = await CallAsync(
            await StartAsync(),
            $"[{Sma3}]",
            """[{"id":"a","op":"Add","left":{"ref":"zzz"},"right":{"constant":1}}]""",
            ""","endDate":"1990-01-01" """.TrimEnd());

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("derived[0].left", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task WithAValidDerivedListTheSameEndDateIsInsufficientMarketData()
    {
        using var response = await CallAsync(
            await StartAsync(),
            $"[{Sma3}]",
            $"[{Derived("a", "Add", Ref("s3"), Constant("1"))}]",
            ""","endDate":"1990-01-01" """.TrimEnd());

        Assert.Equal(ToolErrorCodes.InsufficientMarketData, McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task TheOperationNamesInTheSchemaAreTheCoreEnumNames()
    {
        var dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == "sa_calculate_indicators");
        JsonElement op = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("derived").GetProperty("items").GetProperty("properties").GetProperty("op");
        Assert.Equal(Enum.GetNames<SeriesOperation>(), op.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.DoesNotContain("Ratio", op.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }
}
