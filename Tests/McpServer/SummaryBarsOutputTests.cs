using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T1 (decision D-1): <c>summaryBars</c> of sa_calculate_indicators and sa_calculate_indicators_batch through the real composition.
/// Omitted, the summary keeps its exact keys and values; sent, the summary covers the last K bars of the calculated window (a null is a bar), reports summaryBars and
/// summaryWindowBars, and pastValues keep reading the whole window.
/// </summary>
public sealed class SummaryBarsOutputTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Sma3 = """{"id":"s3","type":"SMA","parameters":{"Period":3}}""";
    private const string Sma50 = """{"id":"s50","type":"SMA","parameters":{"Period":50}}""";
    private const string AllBars = ""","outputTailCount":500""";
    private static readonly string[] OmittedKeys = { "count", "latest", "mean", "stdDev", "zScoreOfLatest", "percentileRankOfLatest", "trendSlope", "trendBars" };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(Func<IReadOnlyStockAnalyzerGateway, IReadOnlyStockAnalyzerGateway>? gatewayWrapper = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()), gatewayWrapper: gatewayWrapper);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string indicator, string extra)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators", $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":[{{{indicator}}}]{{{extra}}}}"""))!;

    private static JsonElement Result(JsonDocument response, string id) => McpTestSupport.Structured(response).GetProperty("results").GetProperty(id);

    private static JsonElement SummaryOf(JsonDocument response, string id) => Result(response, id).GetProperty("summary").GetProperty("SMA");

    private static decimal?[] Series(JsonDocument response, string id)
        => Result(response, id).GetProperty("series").GetProperty("SMA").EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    private static decimal? Number(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetDecimal();

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private static decimal?[] Tail(decimal?[] window, int count) => window[^count..];

    private static void AssertFieldsEqual(SeriesSummary expected, JsonElement actual)
    {
        Assert.Equal(expected.Count, actual.GetProperty("count").GetInt32());
        Assert.Equal(expected.Latest, Number(actual.GetProperty("latest")));
        Assert.Equal(expected.Mean, Number(actual.GetProperty("mean")));
        Assert.Equal(expected.StdDev, Number(actual.GetProperty("stdDev")));
        Assert.Equal(expected.ZScoreOfLatest, Number(actual.GetProperty("zScoreOfLatest")));
        Assert.Equal(expected.PercentileRankOfLatest, Number(actual.GetProperty("percentileRankOfLatest")));
        Assert.Equal(expected.TrendSlope, Number(actual.GetProperty("trendSlope")));
        Assert.Equal(expected.TrendBars, actual.GetProperty("trendBars").GetInt32());
    }

    // ---- omitted = unchanged ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheArgumentTheSummaryKeepsItsExactKeysAndEqualsTheCoreFunctionOverTheWholeWindow()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, Sma3, AllBars);
        using var summarized = await CallAsync(dispatcher, Sma3, ""","summary":true,"trendBars":7""");

        JsonElement summary = SummaryOf(summarized, "s3");
        Assert.Equal(OmittedKeys, Keys(summary));
        AssertFieldsEqual(SeriesSummaryStatistics.Calculate(Series(full, "s3"), 7), summary);
    }

    [Fact]
    public async Task WithoutTheArgumentTheDistributionSummaryKeepsItsExactKeys()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, Sma3, ""","summary":true,"summaryDistribution":true""");

        Assert.Equal(OmittedKeys.Concat(new[] { "median", "iqr", "mad", "skewness", "excessKurtosis", "robustTrendSlope" }), Keys(SummaryOf(response, "s3")));
    }

    // ---- the last K bars --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSummaryCoversTheLastKBars_AndReportsSummaryBarsAndSummaryWindowBarsAfterTrendBars()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, Sma3, AllBars);
        using var summarized = await CallAsync(dispatcher, Sma3, ""","summary":true,"trendBars":3,"summaryBars":5""");

        decimal?[] tail = Tail(Series(full, "s3"), 5);
        JsonElement summary = SummaryOf(summarized, "s3");

        Assert.Equal(OmittedKeys.Concat(new[] { "summaryBars", "summaryWindowBars" }), Keys(summary));
        AssertFieldsEqual(SeriesSummaryStatistics.Calculate(tail, 3), summary);
        Assert.Equal(5, summary.GetProperty("summaryBars").GetInt32());
        Assert.Equal(5, summary.GetProperty("summaryWindowBars").GetInt32());

        // Independent of the Core function: the mean and the latest value of the five last bars.
        Assert.Equal(5, summary.GetProperty("count").GetInt32());
        Assert.Equal(tail.Sum(v => v!.Value) / 5m, Number(summary.GetProperty("mean")));
        Assert.Equal(tail[^1], Number(summary.GetProperty("latest")));
    }

    [Fact]
    public async Task AValueAboveTheCalculatedBarCountIsClampedAndGivesTheWholeWindowSummary()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, Sma3, AllBars);
        using var omitted = await CallAsync(dispatcher, Sma3, ""","summary":true""");
        using var clamped = await CallAsync(dispatcher, Sma3, ""","summary":true,"summaryBars":10000""");

        int barCount = Series(full, "s3").Length;
        JsonElement expected = SummaryOf(omitted, "s3");
        JsonElement actual = SummaryOf(clamped, "s3");

        Assert.True(barCount < 10000);
        Assert.Equal(10000, actual.GetProperty("summaryBars").GetInt32());
        Assert.Equal(barCount, actual.GetProperty("summaryWindowBars").GetInt32());
        foreach (string key in OmittedKeys)
        {
            Assert.Equal(expected.GetProperty(key).GetRawText(), actual.GetProperty(key).GetRawText());
        }
    }

    [Fact]
    public async Task ANullInsideTheTailCountsAsABar()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, Sma50, AllBars);
        decimal?[] window = Series(full, "s50");
        int barCount = window.Length;
        int barsWithNulls = barCount - 20; // SMA(50) has 49 leading nulls: this tail still holds 29 of them
        using var summarized = await CallAsync(dispatcher, Sma50, $$""","summary":true,"summaryBars":{{barsWithNulls}}""");

        decimal?[] tail = Tail(window, barsWithNulls);
        JsonElement summary = SummaryOf(summarized, "s50");

        Assert.True(tail.Count(v => v is null) > 0);
        Assert.Equal(tail.Count(v => v is not null), summary.GetProperty("count").GetInt32());
        Assert.True(summary.GetProperty("count").GetInt32() < barsWithNulls);
        Assert.Equal(barsWithNulls, summary.GetProperty("summaryWindowBars").GetInt32());
        AssertFieldsEqual(SeriesSummaryStatistics.Calculate(tail, 20), summary);
    }

    [Fact]
    public async Task PastValuesStillReadTheWholeWindowWhenSummaryBarsIsSent()
    {
        var dispatcher = await StartAsync();
        const string Offsets = ""","summary":true,"lookbackOffsets":[0,1,100,300]""";
        using var without = await CallAsync(dispatcher, Sma3, Offsets);
        using var with = await CallAsync(dispatcher, Sma3, Offsets + ""","summaryBars":5""");

        Assert.Equal(Result(without, "s3").GetProperty("pastValues").GetRawText(), Result(with, "s3").GetProperty("pastValues").GetRawText());
        Assert.NotEqual(JsonValueKind.Null, Result(with, "s3").GetProperty("pastValues").GetProperty("SMA").GetProperty("300").ValueKind);
    }

    [Fact]
    public async Task TheDistributionFiguresFollowTheTail()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, Sma3, AllBars);
        using var summarized = await CallAsync(dispatcher, Sma3, ""","summary":true,"summaryDistribution":true,"trendBars":6,"summaryBars":12""");

        SeriesSummary expected = SeriesSummaryStatistics.CalculateDistribution(Tail(Series(full, "s3"), 12), 6);
        JsonElement summary = SummaryOf(summarized, "s3");

        AssertFieldsEqual(expected, summary);
        Assert.Equal(expected.Median, Number(summary.GetProperty("median")));
        Assert.Equal(expected.Iqr, Number(summary.GetProperty("iqr")));
        Assert.Equal(expected.Mad, Number(summary.GetProperty("mad")));
        Assert.Equal(expected.Skewness, Number(summary.GetProperty("skewness")));
        Assert.Equal(expected.ExcessKurtosis, Number(summary.GetProperty("excessKurtosis")));
        Assert.Equal(expected.RobustTrendSlope, Number(summary.GetProperty("robustTrendSlope")));
        Assert.Equal(
            OmittedKeys.Concat(new[] { "summaryBars", "summaryWindowBars", "median", "iqr", "mad", "skewness", "excessKurtosis", "robustTrendSlope" }),
            Keys(summary));
    }

    [Fact]
    public async Task DecimalPlacesRoundsTheTailSummaryLikeAnyOtherSummary()
    {
        var dispatcher = await StartAsync();
        using var plain = await CallAsync(dispatcher, Sma3, ""","summary":true,"summaryBars":9""");
        using var rounded = await CallAsync(dispatcher, Sma3, ""","summary":true,"summaryBars":9,"decimalPlaces":1""");

        Assert.Equal(Math.Round(Number(SummaryOf(plain, "s3").GetProperty("mean"))!.Value, 1, MidpointRounding.AwayFromZero), Number(SummaryOf(rounded, "s3").GetProperty("mean")));
        Assert.Equal(9, SummaryOf(rounded, "s3").GetProperty("summaryWindowBars").GetInt32());
    }

    // ---- refusals ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(""","summaryBars":5""", "INVALID_ARGUMENT")]
    [InlineData(""","summary":false,"summaryBars":5""", "INVALID_ARGUMENT")]
    [InlineData(""","summary":true,"summaryBars":1""", "INVALID_ARGUMENT")]
    [InlineData(""","summary":true,"summaryBars":0""", "INVALID_ARGUMENT")]
    [InlineData(""","summary":true,"summaryBars":-3""", "INVALID_ARGUMENT")]
    [InlineData(""","summary":true,"summaryBars":10001""", "LIMIT_EXCEEDED")]
    public async Task ARefusedValueNamesTheArgument_AndIsRefusedBeforeAnyBarIsLoaded(string extra, string code)
    {
        CalculateIndicatorsBatchToolTests.CountingGateway? counter = null;
        var dispatcher = await StartAsync(inner =>
        {
            (IReadOnlyStockAnalyzerGateway gateway, counter) = CalculateIndicatorsBatchToolTests.CountingGateway.Create(inner);
            return gateway;
        });

        using var response = await CallAsync(dispatcher, Sma3, extra);

        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("summaryBars", McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.NotNull(counter);
        Assert.Empty(counter!.BarReads);
    }

    [Fact]
    public async Task ARefusalWithoutSummaryExplainsHowToFixIt()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, Sma3, ""","summaryBars":5""");

        Assert.Contains("Set summary to true, or omit summaryBars.", McpTestSupport.Structured(response).GetRawText());
    }

    // ---- limit contract and schema ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheLargestSummaryWindowIsTheSameDefinitionAsTheLargestTrendWindow()
    {
        Assert.Equal(10000, McpLimitsContract.EffectiveMaxSummaryBars(new McpLimits()));
        var small = new McpLimits { MaxCandlesPerRequest = 10, DefaultCandleCount = 10, MaxOutputTailBars = 10, DefaultOutputTailBars = 10 };
        Assert.Equal(McpLimitsContract.EffectiveMaxTrendBars(small), McpLimitsContract.EffectiveMaxSummaryBars(small));
        Assert.Equal(10, McpLimitsContract.EffectiveMaxSummaryBars(small));
        var one = new McpLimits { MaxCandlesPerRequest = 1, DefaultCandleCount = 1, MaxOutputTailBars = 1, DefaultOutputTailBars = 1 };
        Assert.Equal(2, McpLimitsContract.EffectiveMaxSummaryBars(one));
    }

    [Theory]
    [InlineData("sa_calculate_indicators")]
    [InlineData("sa_calculate_indicators_batch")]
    public async Task BothToolsAdvertiseTheArgumentWithTheSameDescriptionAndBounds(string tool)
    {
        var dispatcher = await StartAsync();

        string response = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument listing = JsonDocument.Parse(response);
        JsonElement property = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == tool)
            .GetProperty("inputSchema").GetProperty("properties").GetProperty("summaryBars");

        Assert.Equal("integer", property.GetProperty("type").GetString());
        Assert.Equal(2, property.GetProperty("minimum").GetInt32());
        Assert.Equal(10000, property.GetProperty("maximum").GetInt32());
        Assert.Equal(SeriesOutputOptions.SummaryBarsDescription(new McpLimits()), property.GetProperty("description").GetString());
        Assert.False(property.TryGetProperty("default", out _));
    }

    // ---- the batch tool ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBatchToolAcceptsTheArgumentAndEqualsTheSingleSymbolCall()
    {
        var dispatcher = await StartAsync();
        const string Extra = ""","lookbackBars":300,"summary":true,"trendBars":4,"summaryBars":8""";

        using var batch = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators_batch", $$$"""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{{{Sma3}}}]{{{Extra}}}}"""))!;
        using var single = await CallAsync(dispatcher, Sma3, Extra);

        Assert.False(McpTestSupport.IsError(batch), McpTestSupport.Structured(batch).GetRawText());
        JsonElement item = McpTestSupport.Structured(batch).GetProperty("items").EnumerateArray().Single();
        JsonElement summary = item.GetProperty("results").GetProperty("s3").GetProperty("summary").GetProperty("SMA");
        Assert.Equal(SummaryOf(single, "s3").GetRawText(), summary.GetRawText());
        Assert.Equal(8, summary.GetProperty("summaryWindowBars").GetInt32());
    }

    [Fact]
    public async Task TheBatchToolRefusesTheSameWayWithoutSummary()
    {
        var dispatcher = await StartAsync();

        using var batch = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators_batch", $$$"""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{{{Sma3}}}],"summaryBars":5}"""))!;

        Assert.True(McpTestSupport.IsError(batch));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(batch));
        Assert.Equal("summaryBars", McpTestSupport.Structured(batch).GetProperty("param").GetString());
    }
}
