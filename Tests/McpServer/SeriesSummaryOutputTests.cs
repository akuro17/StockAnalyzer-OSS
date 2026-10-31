using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit.Abstractions;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// summary, trendBars, lookbackOffsets and includeSeries of sa_calculate_indicators through the real composition. The statistics are compared with the Core function over the same
/// series and with the existing ZScore / Regression Slope indicators; the compact output is measured (no pass/fail threshold on the size).
/// </summary>
public sealed class SeriesSummaryOutputTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Sma1 = """{"id":"p","type":"SMA","parameters":{"Period":1}}""";
    private const string Sma3 = """{"id":"s3","type":"SMA","parameters":{"Period":3}}""";
    private const string Sma50 = """{"id":"s50","type":"SMA","parameters":{"Period":50}}""";
    private const string AllBars = ""","outputTailCount":500""";
    private const string Macd = """{"id":"m","type":"MACD"}""";
    private const string Ichimoku = """{"id":"i","type":"Ichimoku"}""";
    private const string Sma20 = """{"id":"sma20","type":"SMA","parameters":{"Period":20}}""";
    private const string ZScore20 = """{"id":"z","type":"ZScore","parameters":{"Period":20}}""";
    private const string Slope20 = """{"id":"rs","type":"RegressionSlope","parameters":{"Period":20}}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "aapl");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly ITestOutputHelper _output;

    public SeriesSummaryOutputTests(ITestOutputHelper output)
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

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string indicatorsJson, string extra = "", string? derivedJson = null)
    {
        string derivedPart = derivedJson is null ? string.Empty : $$$""","derived":{{{derivedJson}}}""";
        string arguments = $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":{{{indicatorsJson}}}{{{derivedPart}}}{{{extra}}}}""";
        return (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators", arguments))!;
    }

    private static JsonElement Result(JsonDocument response, string id) => McpTestSupport.Structured(response).GetProperty("results").GetProperty(id);

    private static decimal?[] Values(JsonElement array)
        => array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    private static decimal? Number(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetDecimal();

    private static decimal?[] Series(JsonDocument response, string id, string seriesName) => Values(Result(response, id).GetProperty("series").GetProperty(seriesName));

    private static JsonElement SummaryOf(JsonDocument response, string id, string seriesName) => Result(response, id).GetProperty("summary").GetProperty(seriesName);

    // ---- summary --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSummaryEqualsTheCoreFunctionOverTheWholeCalculatedWindow_NotOnlyTheReturnedTail()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, $"[{Sma3},{Sma50}]", AllBars);
        using var summarized = await CallAsync(dispatcher, $"[{Sma3},{Sma50}]", ""","outputTailCount":10,"summary":true,"trendBars":30""");

        foreach ((string id, string name) in new[] { ("s3", "SMA"), ("s50", "SMA") })
        {
            SeriesSummary expected = SeriesSummaryStatistics.Calculate(Series(full, id, name), 30);
            JsonElement actual = SummaryOf(summarized, id, name);

            Assert.Equal(expected.Count, actual.GetProperty("count").GetInt32());
            Assert.Equal(expected.Latest, Number(actual.GetProperty("latest")));
            Assert.Equal(expected.Mean, Number(actual.GetProperty("mean")));
            Assert.Equal(expected.StdDev, Number(actual.GetProperty("stdDev")));
            Assert.Equal(expected.ZScoreOfLatest, Number(actual.GetProperty("zScoreOfLatest")));
            Assert.Equal(expected.PercentileRankOfLatest, Number(actual.GetProperty("percentileRankOfLatest")));
            Assert.Equal(expected.TrendSlope, Number(actual.GetProperty("trendSlope")));
            Assert.Equal(30, actual.GetProperty("trendBars").GetInt32());
            Assert.Equal(10, Result(summarized, id).GetProperty("series").GetProperty(name).GetArrayLength());
        }

        Assert.Equal(StoredDays - 2, SummaryOf(summarized, "s3", "SMA").GetProperty("count").GetInt32()); // SMA(3): 2 leading nulls
        Assert.Equal(StoredDays - 49, SummaryOf(summarized, "s50", "SMA").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TheSummaryMeanZScoreAndSlopeEqualTheExistingZScoreAndRegressionSlopeIndicators()
    {
        // lookbackBars 20: the window is the last 20 closes; SMA(1) is the close itself. The indicators of period 20 then give their single value at the last bar.
        string indicators = $"[{Sma1},{Sma20},{ZScore20},{Slope20}]";
        using var response = await CallAsync(await StartAsync(), indicators, ""","lookbackBars":20,"summary":true,"trendBars":20""");

        JsonElement summary = SummaryOf(response, "p", "SMA");
        decimal? zIndicator = Number(Result(response, "z").GetProperty("latestValues").GetProperty("ZScore"));
        decimal? slopeIndicator = Number(Result(response, "rs").GetProperty("latestValues").GetProperty("RegressionSlope"));
        decimal? meanIndicator = Number(Result(response, "sma20").GetProperty("latestValues").GetProperty("SMA"));

        Assert.NotNull(zIndicator);
        Assert.NotNull(slopeIndicator);
        Assert.Equal(20, summary.GetProperty("count").GetInt32());
        Assert.Equal(meanIndicator, Number(summary.GetProperty("mean")));
        Assert.Equal(zIndicator, Number(summary.GetProperty("zScoreOfLatest")));
        Assert.Equal(slopeIndicator, Number(summary.GetProperty("trendSlope")));
    }

    [Fact]
    public async Task EverySeriesOfEveryResultGetsASummary_DerivedSeriesIncluded()
    {
        string derived = """[{"id":"d","op":"Subtract","left":{"ref":"s3"},"right":{"ref":"s50"}}]""";
        using var response = await CallAsync(await StartAsync(), $"[{Sma3},{Macd},{Sma50}]", ""","summary":true""", derived);

        JsonElement macd = Result(response, "m").GetProperty("summary");
        Assert.Equal(new[] { "MacdLine", "Signal", "Histogram" }, macd.EnumerateObject().Select(p => p.Name).ToArray());
        JsonElement fromDerived = SummaryOf(response, "d", "Main");
        Assert.Equal(StoredDays - 49, fromDerived.GetProperty("count").GetInt32());
        Assert.Equal(Result(response, "d").GetProperty("latestValues").GetProperty("Main").GetDecimal(), fromDerived.GetProperty("latest").GetDecimal());
    }

    [Fact]
    public async Task AnUnsuccessfulResultHasNoSummary()
    {
        using var response = await CallAsync(await StartAsync(), """[{"id":"r","type":"RSI"},{"id":"s2","type":"SMA","parameters":{"Period":2}}]""", ""","lookbackBars":5,"summary":true""");

        Assert.False(Result(response, "r").GetProperty("isSuccessful").GetBoolean());
        Assert.False(Result(response, "r").TryGetProperty("summary", out _));
        Assert.True(Result(response, "s2").TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task TheDefaultTrendWindowIsTheLimit_AndAnotherWindowIsEchoed()
    {
        var dispatcher = await StartAsync();
        using var byDefault = await CallAsync(dispatcher, $"[{Sma3}]", ""","summary":true""");
        using var custom = await CallAsync(dispatcher, $"[{Sma3}]", ""","summary":true,"trendBars":5""");

        Assert.Equal(20, SummaryOf(byDefault, "s3", "SMA").GetProperty("trendBars").GetInt32());
        Assert.Equal(5, SummaryOf(custom, "s3", "SMA").GetProperty("trendBars").GetInt32());
        Assert.NotEqual(SummaryOf(byDefault, "s3", "SMA").GetProperty("trendSlope").GetRawText(), SummaryOf(custom, "s3", "SMA").GetProperty("trendSlope").GetRawText());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10000)]
    public async Task TheTrendWindowBoundsThemselvesAreAccepted(int trendBars)
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma1}]", $$$""","summary":true,"trendBars":{{{trendBars}}}""");

        Assert.False(McpTestSupport.IsError(response));
        Assert.Equal(trendBars, SummaryOf(response, "p", "SMA").GetProperty("trendBars").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, SummaryOf(response, "p", "SMA").GetProperty("trendSlope").ValueKind);
    }

    [Fact]
    public async Task ASummaryOfAnIndicatorOnItsOwnTimeFrameCountsTheAlignedValues()
    {
        string weekly = """{"id":"w","type":"SMA","parameters":{"Period":2},"timeFrame":"W1"}""";
        using var response = await CallAsync(await StartAsync(), $"[{weekly}]", ""","summary":true,"outputTailCount":500""");

        decimal?[] aligned = Series(response, "w", "SMA");
        Assert.Equal(aligned.Count(v => v is not null), SummaryOf(response, "w", "SMA").GetProperty("count").GetInt32());
        Assert.Equal(aligned[^1], Number(SummaryOf(response, "w", "SMA").GetProperty("latest")));
    }

    [Fact]
    public async Task ASummaryWithAnEndDateIsTheSummaryOfTheBarsUpToIt()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, $"[{Sma3}]", AllBars);
        using var cut = await CallAsync(dispatcher, $"[{Sma3}]", ""","summary":true,"outputTailCount":500,"endDate":"2024-10-27" """.TrimEnd());

        decimal?[] values = Series(cut, "s3", "SMA");
        SeriesSummary expected = SeriesSummaryStatistics.Calculate(Series(full, "s3", "SMA").Take(values.Length).ToArray(), 20);
        Assert.InRange(values.Length, 2, StoredDays - 1);
        Assert.Equal(expected.Mean, Number(SummaryOf(cut, "s3", "SMA").GetProperty("mean")));
        Assert.Equal(expected.Latest, Number(SummaryOf(cut, "s3", "SMA").GetProperty("latest")));
        Assert.Equal(expected.PercentileRankOfLatest, Number(SummaryOf(cut, "s3", "SMA").GetProperty("percentileRankOfLatest")));
    }

    [Fact]
    public async Task ATrendWindowLongerThanTheCalculatedWindowUsesEveryBar()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma1}]", ""","lookbackBars":10,"summary":true,"trendBars":50""");

        Assert.NotEqual(JsonValueKind.Null, SummaryOf(response, "p", "SMA").GetProperty("trendSlope").ValueKind);
        Assert.Equal(50, SummaryOf(response, "p", "SMA").GetProperty("trendBars").GetInt32());
    }

    // ---- lookbackOffsets ------------------------------------------------------------------------------------------

    [Fact]
    public async Task PastValuesAreTheValuesThatManyBarsBeforeTheLastBar_DuplicatesFoldedAndMissingBarsNull()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, $"[{Sma3},{Sma50}]", AllBars);
        using var past = await CallAsync(dispatcher, $"[{Sma3},{Sma50}]", ""","outputTailCount":3,"lookbackOffsets":[0,1,5,5,350,399,400,100000,2]""");

        decimal?[] s3 = Series(full, "s3", "SMA");
        decimal?[] s50 = Series(full, "s50", "SMA");
        JsonElement pastS50 = Result(past, "s50").GetProperty("pastValues").GetProperty("SMA");
        Assert.Equal(new[] { "0", "1", "5", "350", "399", "400", "100000", "2" }, pastS50.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(s50[StoredDays - 1], Number(pastS50.GetProperty("0")));
        Assert.Equal(s50[StoredDays - 2], Number(pastS50.GetProperty("1")));
        Assert.Equal(s50[StoredDays - 6], Number(pastS50.GetProperty("5")));
        Assert.Equal(s50[StoredDays - 351], Number(pastS50.GetProperty("350")));
        Assert.NotNull(Number(pastS50.GetProperty("350")));
        Assert.Null(Number(pastS50.GetProperty("399"))); // the first bar: SMA(50) has no value yet
        Assert.Null(Number(pastS50.GetProperty("400"))); // before the first calculated bar
        Assert.Null(Number(pastS50.GetProperty("100000")));
        Assert.Equal(s3[StoredDays - 3], Number(Result(past, "s3").GetProperty("pastValues").GetProperty("SMA").GetProperty("2")));
    }

    [Fact]
    public async Task PastValuesReadTheCalculatedWindow_NotTheReturnedTail()
    {
        using var past = await CallAsync(await StartAsync(), $"[{Sma3}]", ""","outputTailCount":2,"lookbackOffsets":[100]""");

        Assert.Equal(2, Result(past, "s3").GetProperty("series").GetProperty("SMA").GetArrayLength());
        Assert.NotNull(Number(Result(past, "s3").GetProperty("pastValues").GetProperty("SMA").GetProperty("100")));
    }

    [Fact]
    public async Task PastValuesOfADerivedSeriesAndOfEveryIndicatorSeriesAreListed()
    {
        string derived = """[{"id":"d","op":"Add","left":{"ref":"s3"},"right":{"constant":1}}]""";
        using var past = await CallAsync(await StartAsync(), $"[{Sma3},{Macd}]", ""","lookbackOffsets":[0,3]""", derived);

        Assert.Equal(new[] { "MacdLine", "Signal", "Histogram" }, Result(past, "m").GetProperty("pastValues").EnumerateObject().Select(p => p.Name).ToArray());
        decimal? last = Number(Result(past, "s3").GetProperty("latestValues").GetProperty("SMA"));
        Assert.Equal(last + 1, Number(Result(past, "d").GetProperty("pastValues").GetProperty("Main").GetProperty("0")));
    }

    // ---- includeSeries --------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutSeriesOnlyTheCompactFiguresAreReturned()
    {
        string derived = """[{"id":"d","op":"Add","left":{"ref":"s3"},"right":{"constant":1}}]""";
        using var response = await CallAsync(await StartAsync(), $"[{Sma3},{Ichimoku}]", ""","includeSeries":false,"summary":true""", derived);

        JsonElement root = McpTestSupport.Structured(response);
        Assert.False(root.TryGetProperty("timestamps", out _));
        foreach (string id in new[] { "s3", "i", "d" })
        {
            JsonElement result = Result(response, id);
            Assert.False(result.TryGetProperty("series", out _), id);
            Assert.False(result.TryGetProperty("projectedValues", out _), id);
            Assert.True(result.TryGetProperty("latestValues", out _), id);
            Assert.True(result.TryGetProperty("leadingNullCount", out _), id);
            Assert.True(result.TryGetProperty("summary", out _), id);
        }

        Assert.Equal(0, root.GetProperty("returnedBarCount").GetInt32()); // no bars are returned, the window size stays in calculatedBarCount
        Assert.True(root.GetProperty("calculatedBarCount").GetInt32() > 0);
    }

    [Theory]
    [InlineData(""","includeSeries":false,"summary":true,"outputTailCount":10""")]
    [InlineData(""","includeSeries":false,"lookbackOffsets":[0],"outputTailCount":200""")]
    public async Task WithoutSeriesAnExplicitTailCountIsRefused(string extra)
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", extra);

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("outputTailCount", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task WithSeriesTheTailCountStillDecidesTheReturnedBars()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", ""","summary":true,"outputTailCount":7""");

        Assert.Equal(7, McpTestSupport.Structured(response).GetProperty("returnedBarCount").GetInt32());
    }

    [Fact]
    public async Task IncludingTheSeriesStillShowsTheProjection()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Ichimoku}]", ""","summary":true""");

        Assert.True(Result(response, "i").TryGetProperty("projectedValues", out _));
        Assert.True(McpTestSupport.Structured(response).TryGetProperty("timestamps", out _));
    }

    [Fact]
    public async Task WithoutSeriesAndWithoutAnythingElseToReadTheCallIsRefused()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", ""","includeSeries":false""");

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("includeSeries", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task WithoutSeriesAnEmptyOffsetListIsNotSomethingToRead()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", ""","includeSeries":false,"lookbackOffsets":[]""");

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
    }

    [Theory]
    [InlineData(""","includeSeries":false,"lookbackOffsets":[0]""")]
    [InlineData(""","includeSeries":false,"summary":true""")]
    public async Task WithoutSeriesSummaryOrOffsetsAreEnough(string extra)
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", extra);

        Assert.False(McpTestSupport.IsError(response));
    }

    // ---- unchanged output and validation --------------------------------------------------------------------------

    [Fact]
    public async Task NamingTheDefaultsExplicitlyGivesTheSameOutputByteForByte()
    {
        var dispatcher = await StartAsync();
        string indicators = $"[{Sma3},{Ichimoku}]";

        using var omitted = await CallAsync(dispatcher, indicators);
        using var explicitDefaults = await CallAsync(dispatcher, indicators, ""","summary":false,"includeSeries":true,"lookbackOffsets":[]""");

        Assert.Equal(McpTestSupport.Structured(omitted).GetRawText(), McpTestSupport.Structured(explicitDefaults).GetRawText());
    }

    [Fact]
    public async Task TheSummaryAndPastValuesComeAfterLeadingNullCountAndNothingElseMoves()
    {
        var dispatcher = await StartAsync();
        using var plain = await CallAsync(dispatcher, $"[{Sma3}]");
        using var rich = await CallAsync(dispatcher, $"[{Sma3}]", ""","summary":true,"lookbackOffsets":[0]""");

        string[] plainKeys = Result(plain, "s3").EnumerateObject().Select(p => p.Name).ToArray();
        string[] richKeys = Result(rich, "s3").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(plainKeys.Concat(new[] { "summary", "pastValues" }).ToArray(), richKeys);
        Assert.Equal(Result(plain, "s3").GetProperty("series").GetRawText(), Result(rich, "s3").GetProperty("series").GetRawText());
    }

    public static TheoryData<string, string, string, string> InvalidOptions => new()
    {
        { "trendBars without summary", ""","trendBars":5""", ToolErrorCodes.InvalidArgument, "trendBars" },
        { "trendBars below two", ""","summary":true,"trendBars":1""", ToolErrorCodes.InvalidArgument, "trendBars" },
        { "trendBars above the calculation window limit", ""","summary":true,"trendBars":10001""", ToolErrorCodes.LimitExceeded, "trendBars" },
        { "negative offset", ""","lookbackOffsets":[0,-1]""", ToolErrorCodes.InvalidArgument, "lookbackOffsets[1]" },
        { "fractional offset", ""","lookbackOffsets":[1.5]""", ToolErrorCodes.InvalidArgument, "lookbackOffsets[0]" },
        { "offset is a string", ""","lookbackOffsets":["1"]""", ToolErrorCodes.InvalidArgument, "lookbackOffsets[0]" },
        { "summary is a string", ""","summary":"yes" """, ToolErrorCodes.InvalidArgument, "summary" },
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task InvalidOptionsAreRefusedNamingTheArgument(string description, string extra, string code, string path)
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", extra);

        Assert.True(McpTestSupport.IsError(response), description);
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(path, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task MoreOffsetsThanTheLimitAreRefused_NotCut()
    {
        using var response = await CallAsync(await StartAsync(("MaxLookbackOffsets", "2")), $"[{Sma3}]", ""","lookbackOffsets":[0,1,2]""");

        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("lookbackOffsets", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task DuplicateOffsetsCountAgainstTheLimitAsWritten()
    {
        using var response = await CallAsync(await StartAsync(("MaxLookbackOffsets", "2")), $"[{Sma3}]", ""","lookbackOffsets":[1,1,1]""");

        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task ARefusalHappensBeforeAnyBarIsLoaded()
    {
        using var response = await CallAsync(await StartAsync(), $"[{Sma3}]", ""","endDate":"1990-01-01","lookbackOffsets":[-1]""");

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task ARequestLimitBelowTwoBarsStillStartsAndTheWindowInForceFollowsIt()
    {
        using var response = await CallAsync(await StartAsync(("MaxCandlesPerRequest", "1"), ("DefaultCandleCount", "1"), ("MaxOutputTailBars", "1"), ("DefaultOutputTailBars", "1")), $"[{Sma1}]", ""","summary":true""");

        Assert.False(McpTestSupport.IsError(response));
        Assert.Equal(2, SummaryOf(response, "p", "SMA").GetProperty("trendBars").GetInt32());
        Assert.Equal(1, SummaryOf(response, "p", "SMA").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TheSchemaNamesTheNewArgumentsWithTheirBounds()
    {
        var dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement properties = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "sa_calculate_indicators").GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal("boolean", properties.GetProperty("summary").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("includeSeries").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("lookbackOffsets").GetProperty("items").GetProperty("type").GetString());
        JsonElement trend = properties.GetProperty("trendBars");
        Assert.Equal(2, trend.GetProperty("minimum").GetInt32());
        Assert.Equal(10000, trend.GetProperty("maximum").GetInt32());
        Assert.Equal(20, trend.GetProperty("default").GetInt32());
    }

    // ---- measurement (T4) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Measurement_ResponseBytesOfTheFullSeriesAgainstTheCompactSummary()
    {
        const string sixteen = """
            [{"id":"a1","type":"SMA","parameters":{"Period":5}},{"id":"a2","type":"SMA","parameters":{"Period":20}},{"id":"a3","type":"SMA","parameters":{"Period":50}},
             {"id":"a4","type":"EMA","parameters":{"Period":12}},{"id":"a5","type":"EMA","parameters":{"Period":26}},{"id":"a6","type":"RSI"},{"id":"a7","type":"MACD"},
             {"id":"a8","type":"ROC"},{"id":"a9","type":"ZScore"},{"id":"a10","type":"RegressionSlope"},{"id":"a11","type":"ATR"},{"id":"a12","type":"OBV"},
             {"id":"a13","type":"ADX"},{"id":"a14","type":"CCI"},{"id":"a15","type":"Stoch"},{"id":"a16","type":"MFI"}]
            """;
        var dispatcher = await StartAsync();

        using var full = await CallAsync(dispatcher, sixteen, ""","outputTailCount":500""");
        using var compact = await CallAsync(dispatcher, sixteen, ""","includeSeries":false,"summary":true""");
        using var compactWithPast = await CallAsync(dispatcher, sixteen, ""","includeSeries":false,"summary":true,"lookbackOffsets":[1,5,20]""");

        int Bytes(JsonDocument d) => Encoding.UTF8.GetByteCount(d.RootElement.GetRawText());
        int structured(JsonDocument d) => Encoding.UTF8.GetByteCount(McpTestSupport.Structured(d).GetRawText());
        Assert.False(McpTestSupport.IsError(full));
        Assert.False(McpTestSupport.IsError(compact));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"MEASURED ({StoredDays} stored bars, 16 indicators, outputTailCount 500): full message {Bytes(full)} bytes (structuredContent {structured(full)}); " +
            $"includeSeries false + summary {Bytes(compact)} bytes (structuredContent {structured(compact)}); + lookbackOffsets [1,5,20] {Bytes(compactWithPast)} bytes; " +
            $"ratio full/compact {(double)Bytes(full) / Bytes(compact):F1}x"));
        Assert.True(Bytes(compact) > 0);
    }
}
