using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The allowUnregistered argument of sa_filter_tickers, sa_run_screener, sa_calculate_indicators and sa_run_backtest: a symbol that is not in the
/// ticker list but has stored daily bars is accepted (and named in the warnings); one without stored bars is SYMBOL_NOT_FOUND; and with the
/// argument left out, or false, nothing differs from before (the registered path is byte-identical with and without the flag).
/// </summary>
public sealed class AllowUnregisteredTests : IDisposable
{
    private const string Registered = "MSFT";
    private const string OffList = "OFFLIST";
    private const string NoFile = "NOFILE";
    private const int Days = 120;

    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Exit = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}""";
    private const string CloseAboveZero = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""";
    private const string Indicators = """[{"id":"r","type":"RSI"}]""";
    private const string Correlation = """[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"OFFLIST","Period":20}}]""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, Registered);
    private readonly List<McpServerRuntime> _runtimes = new();

    public AllowUnregisteredTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, OffList, Days);
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
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string arguments)
        => (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

    private static string[] Warnings(JsonDocument response)
        => McpTestSupport.Structured(response).TryGetProperty("warnings", out JsonElement warnings)
            ? warnings.EnumerateArray().Select(w => w.GetString()!).ToArray()
            : Array.Empty<string>();

    private static void AssertNotFound(JsonDocument response, string parameter)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal("SYMBOL_NOT_FOUND", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    private static string Screener(string symbol, string flag = "")
        => $$$"""{"conditionTree":{{{CloseAboveZero}}},"symbols":["{{{symbol}}}"]{{{flag}}}}""";

    private static string Indicator(string symbol, string flag = "")
        => $$$"""{"symbol":"{{{symbol}}}","timeFrame":"D1","indicators":{{{Indicators}}}{{{flag}}}}""";

    private static string Backtest(string symbol, string flag = "")
        => $$$"""{"symbol":"{{{symbol}}}","conditions":{"entryLong":{{{Entry}}},"exitLong":{{{Exit}}}}{{{flag}}}}""";

    private static string Filter(string symbol, string flag = "")
        => $$$"""{"symbols":["{{{symbol}}}"],"rules":[{"field":"Close","operator":">","value":"0"}]{{{flag}}}}""";

    private const string Allow = ",\"allowUnregistered\":true";
    private const string Deny = ",\"allowUnregistered\":false";

    // ---- default: nothing changes ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheArgument_AnOffListSymbolIsRefusedByEveryTool()
    {
        var dispatcher = await StartAsync();

        using var screener = await CallAsync(dispatcher, "sa_run_screener", Screener(OffList));
        using var indicators = await CallAsync(dispatcher, "sa_calculate_indicators", Indicator(OffList));
        using var backtest = await CallAsync(dispatcher, "sa_run_backtest", Backtest(OffList));
        using var crossTicker = await CallAsync(dispatcher, "sa_calculate_indicators", $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":{{{Correlation}}}}""");

        AssertNotFound(screener, "symbols[0]");
        AssertNotFound(indicators, "symbol");
        AssertNotFound(backtest, "symbol");
        AssertNotFound(crossTicker, "indicators[0].parameters.ComparisonSymbol");
    }

    [Fact]
    public async Task WithFalse_ItIsTheSameAsWithoutTheArgument()
    {
        var dispatcher = await StartAsync();

        using var screener = await CallAsync(dispatcher, "sa_run_screener", Screener(OffList, Deny));
        using var indicators = await CallAsync(dispatcher, "sa_calculate_indicators", Indicator(OffList, Deny));
        using var backtest = await CallAsync(dispatcher, "sa_run_backtest", Backtest(OffList, Deny));

        AssertNotFound(screener, "symbols[0]");
        AssertNotFound(indicators, "symbol");
        AssertNotFound(backtest, "symbol");
    }

    [Fact]
    public async Task ForARegisteredSymbol_TheFlagChangesNothingInTheOutput()
    {
        var dispatcher = await StartAsync();

        foreach ((string tool, Func<string, string, string> build) in new (string, Func<string, string, string>)[]
        {
            ("sa_run_screener", Screener),
            ("sa_calculate_indicators", Indicator),
            ("sa_run_backtest", Backtest),
        })
        {
            using var plain = await CallAsync(dispatcher, tool, build(Registered, string.Empty));
            using var allowed = await CallAsync(dispatcher, tool, build(Registered, Allow));

            Assert.False(McpTestSupport.IsError(plain), $"{tool}: {McpTestSupport.Structured(plain)}");
            Assert.Equal(McpTestSupport.Structured(plain).GetRawText(), McpTestSupport.Structured(allowed).GetRawText());
        }
    }

    [Fact]
    public async Task TheFilterToolLeavesAnOffListSymbolOutWithoutTheFlag_AndCountsIt()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_filter_tickers", Filter(OffList));

        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(McpTestSupport.IsError(response));
        Assert.Equal(1, result.GetProperty("universe").GetProperty("unregisteredCount").GetInt32());
        Assert.Equal(0, result.GetProperty("universe").GetProperty("evaluatedCount").GetInt32());
        Assert.Empty(result.GetProperty("matches").EnumerateArray().ToArray());
    }

    // ---- with the flag and stored bars ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheScreenerAcceptsAnOffListSymbolThatHasBars_AndNamesItInTheWarnings()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_run_screener", Screener(OffList, Allow));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { OffList }, result.GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()).ToArray());
        string warning = Assert.Single(Warnings(response));
        Assert.Contains(OffList, warning);
        Assert.Contains("allowUnregistered", warning);
    }

    [Fact]
    public async Task TheScreenerAcceptsAnOffListComparisonSymbol()
    {
        var dispatcher = await StartAsync();
        string condition = """{"left":{"indicator":"Correlation","parameters":{"Period":20,"ComparisonSymbol":"OFFLIST"}},"comparison":"GreaterThan","right":-2}""";

        using var refused = await CallAsync(dispatcher, "sa_run_screener", $$$"""{"conditionTree":{{{condition}}},"symbols":["MSFT"]}""");
        using var accepted = await CallAsync(dispatcher, "sa_run_screener", $$$"""{"conditionTree":{{{condition}}},"symbols":["MSFT"]{{{Allow}}}}""");

        AssertNotFound(refused, "conditionTree.left.parameters.ComparisonSymbol");
        Assert.False(McpTestSupport.IsError(accepted), McpTestSupport.Structured(accepted).ToString());
        Assert.Contains(Warnings(accepted), warning => warning.Contains(OffList, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheIndicatorToolAcceptsAnOffListSymbol_AndGivesTheValuesOfItsBars()
    {
        var dispatcher = await StartAsync();

        using var offList = await CallAsync(dispatcher, "sa_calculate_indicators", Indicator(OffList, Allow));
        using var registered = await CallAsync(dispatcher, "sa_calculate_indicators", Indicator(Registered));

        Assert.False(McpTestSupport.IsError(offList), McpTestSupport.Structured(offList).ToString());
        JsonElement result = McpTestSupport.Structured(offList);
        Assert.Equal(OffList, result.GetProperty("symbol").GetString());
        Assert.Contains(OffList, Assert.Single(Warnings(offList)));
        // Same stored series, same indicator: the numbers equal those of the registered symbol.
        Assert.Equal(
            McpTestSupport.Structured(registered).GetProperty("results").GetRawText(),
            result.GetProperty("results").GetRawText());
    }

    [Fact]
    public async Task TheIndicatorToolAcceptsAnOffListComparisonSymbol()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_calculate_indicators", $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":{{{Correlation}}}{{{Allow}}}}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Contains(Warnings(response), warning => warning.Contains(OffList, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBacktestToolAcceptsAnOffListSymbol_AndNamesItInTheWarnings()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_run_backtest", Backtest(OffList, Allow));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal(OffList, McpTestSupport.Structured(response).GetProperty("symbol").GetString());
        Assert.Contains(Warnings(response), warning => warning.Contains(OffList, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFilterToolEvaluatesAnOffListSymbolThatHasBars()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_filter_tickers", Filter(OffList, Allow));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(1, result.GetProperty("universe").GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("universe").GetProperty("unregisteredCount").GetInt32());
        Assert.Equal(new[] { OffList }, result.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("symbol").GetString()).ToArray());
        Assert.Contains(OffList, Assert.Single(Warnings(response)));
    }

    [Fact]
    public async Task TheFilterToolAppliesAScreenerToAnOffListSymbol()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_filter_tickers", $$$"""{"symbols":["OFFLIST"],"screener":{"conditionTree":{{{CloseAboveZero}}}}{{{Allow}}}}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal(new[] { OffList }, McpTestSupport.Structured(response).GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("symbol").GetString()).ToArray());
    }

    [Fact]
    public async Task TheFilterToolAcceptsAnOffListScreenerComparisonSymbol_AlsoWhenTheRulesLeaveNothing()
    {
        var dispatcher = await StartAsync();
        string condition = """{"left":{"indicator":"Correlation","parameters":{"Period":20,"ComparisonSymbol":"OFFLIST"}},"comparison":"GreaterThan","right":-2}""";
        string noneKept = """[{"field":"Close","operator":">","value":"1000000"}]""";
        string call = $$$"""{"symbols":["MSFT"],"rules":{{{noneKept}}},"screener":{"conditionTree":{{{condition}}}}""";

        using var refused = await CallAsync(dispatcher, "sa_filter_tickers", call + "}");
        using var accepted = await CallAsync(dispatcher, "sa_filter_tickers", call + Allow + "}");

        AssertNotFound(refused, "screener.conditionTree.left.parameters.ComparisonSymbol");
        Assert.False(McpTestSupport.IsError(accepted), McpTestSupport.Structured(accepted).ToString());
        Assert.Equal(0, McpTestSupport.Structured(accepted).GetProperty("totalMatchCount").GetInt32());
        Assert.Contains(Warnings(accepted), warning => warning.Contains(OffList, StringComparison.Ordinal));
    }

    // ---- with the flag and no stored bars --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASymbolWithoutStoredBarsIsSymbolNotFoundEvenWithTheFlag()
    {
        var dispatcher = await StartAsync();

        using var screener = await CallAsync(dispatcher, "sa_run_screener", Screener(NoFile, Allow));
        using var indicators = await CallAsync(dispatcher, "sa_calculate_indicators", Indicator(NoFile, Allow));
        using var backtest = await CallAsync(dispatcher, "sa_run_backtest", Backtest(NoFile, Allow));
        using var filter = await CallAsync(dispatcher, "sa_filter_tickers", Filter(NoFile, Allow));
        using var crossTicker = await CallAsync(dispatcher, "sa_calculate_indicators",
            $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"NOFILE"}}]{{{Allow}}}}""");

        AssertNotFound(screener, "symbols[0]");
        AssertNotFound(indicators, "symbol");
        AssertNotFound(backtest, "symbol");
        AssertNotFound(filter, "symbols[0]");
        AssertNotFound(crossTicker, "indicators[0].parameters.ComparisonSymbol");
        Assert.Contains("no stored daily bars", McpTestSupport.Structured(screener).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("../MSFT")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("MSFT\\u0000")]
    [InlineData("")]
    public async Task TheFlagNeverLetsPathLikeTextReachTheFileSystem(string symbol)
    {
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();
        string escaped = JsonSerializer.Serialize(symbol)[1..^1];

        foreach ((string tool, string arguments) in new[]
        {
            ("sa_run_screener", Screener(escaped, Allow)),
            ("sa_calculate_indicators", Indicator(escaped, Allow)),
            ("sa_run_backtest", Backtest(escaped, Allow)),
            ("sa_filter_tickers", Filter(escaped, Allow)),
        })
        {
            using var response = await CallAsync(dispatcher, tool, arguments);
            Assert.True(McpTestSupport.IsError(response), $"{tool} accepted '{symbol}'");
            Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
            Assert.DoesNotContain(_fixture.Root, McpTestSupport.Structured(response).GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task ASessionWithTheFlagLeavesTheDataTreeUnchanged()
    {
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        (await CallAsync(dispatcher, "sa_run_screener", Screener(OffList, Allow))).Dispose();
        (await CallAsync(dispatcher, "sa_calculate_indicators", Indicator(OffList, Allow))).Dispose();
        (await CallAsync(dispatcher, "sa_run_backtest", Backtest(OffList, Allow))).Dispose();
        (await CallAsync(dispatcher, "sa_filter_tickers", Filter(NoFile, Allow))).Dispose();

        Assert.Equal(before, _fixture.Snapshot());
    }
}
