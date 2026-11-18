using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Statistical functions plan, Run R8 (M1): <c>summaryDistribution</c> of sa_calculate_indicators and sa_calculate_indicators_batch through the real composition.
/// The default output must not change; the six added fields equal the Core function over the same series; robustTrendSlope is null above the one measured bound
/// (never an error); the two tools share the rule and the description.
/// </summary>
public sealed class SummaryDistributionOutputTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Sma3 = """{"id":"s3","type":"SMA","parameters":{"Period":3}}""";
    private const string AllBars = ""","outputTailCount":500""";
    private static readonly string[] NewFields = { "median", "iqr", "mad", "skewness", "excessKurtosis", "robustTrendSlope" };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "aapl");
    private readonly List<McpServerRuntime> _runtimes = new();

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

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators", $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":[{{{Sma3}}}]{{{extra}}}}"""))!;

    private static JsonElement Summary(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("results").GetProperty("s3").GetProperty("summary").GetProperty("SMA");

    private static decimal?[] Values(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("results").GetProperty("s3").GetProperty("series").GetProperty("SMA").EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    private static decimal? Number(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetDecimal();

    [Fact]
    public async Task WithoutTheArgumentTheSummaryIsUnchanged_AndFalseEqualsOmitted()
    {
        var dispatcher = await StartAsync();
        using var omitted = await CallAsync(dispatcher, ""","summary":true""");
        using var explicitFalse = await CallAsync(dispatcher, ""","summary":true,"summaryDistribution":false""");

        string[] keys = Summary(omitted).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "count", "latest", "mean", "stdDev", "zScoreOfLatest", "percentileRankOfLatest", "trendSlope", "trendBars" }, keys);
        Assert.Equal(Summary(omitted).GetRawText(), Summary(explicitFalse).GetRawText());
    }

    [Fact]
    public async Task TheSixFieldsFollowTrendBars_AndEqualTheCoreFunctionOverTheWholeWindow()
    {
        var dispatcher = await StartAsync();
        using var full = await CallAsync(dispatcher, AllBars);
        using var response = await CallAsync(dispatcher, ""","outputTailCount":10,"summary":true,"summaryDistribution":true,"trendBars":30""");

        SeriesSummary expected = SeriesSummaryStatistics.CalculateDistribution(Values(full), 30);
        JsonElement actual = Summary(response);

        Assert.Equal(new[] { "count", "latest", "mean", "stdDev", "zScoreOfLatest", "percentileRankOfLatest", "trendSlope", "trendBars" }.Concat(NewFields).ToArray(),
            actual.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(expected.Median, Number(actual.GetProperty("median")));
        Assert.Equal(expected.Iqr, Number(actual.GetProperty("iqr")));
        Assert.Equal(expected.Mad, Number(actual.GetProperty("mad")));
        Assert.Equal(expected.Skewness, Number(actual.GetProperty("skewness")));
        Assert.Equal(expected.ExcessKurtosis, Number(actual.GetProperty("excessKurtosis")));
        Assert.Equal(expected.RobustTrendSlope, Number(actual.GetProperty("robustTrendSlope")));
        Assert.NotNull(expected.RobustTrendSlope);
        Assert.NotNull(expected.Skewness);
        // The existing fields are the same as without the argument.
        using var plain = await CallAsync(dispatcher, ""","outputTailCount":10,"summary":true,"trendBars":30""");
        foreach (string key in new[] { "count", "latest", "mean", "stdDev", "zScoreOfLatest", "percentileRankOfLatest", "trendSlope", "trendBars" })
        {
            Assert.Equal(Summary(plain).GetProperty(key).GetRawText(), actual.GetProperty(key).GetRawText());
        }
    }

    [Fact]
    public async Task ARobustSlopeAboveTheMeasuredBoundIsNull_NotAnError_AndEveryOtherFieldIsKept()
    {
        var dispatcher = await StartAsync();
        int bound = SeriesSummaryStatistics.MaxRobustTrendBars;
        using var atBound = await CallAsync(dispatcher, $",\"summary\":true,\"summaryDistribution\":true,\"trendBars\":{bound}");
        using var above = await CallAsync(dispatcher, $",\"summary\":true,\"summaryDistribution\":true,\"trendBars\":{bound + 1}");
        using var farAbove = await CallAsync(dispatcher, ""","summary":true,"summaryDistribution":true,"trendBars":5000""");

        Assert.False(McpTestSupport.IsError(atBound));
        Assert.False(McpTestSupport.IsError(above));
        Assert.False(McpTestSupport.IsError(farAbove));
        Assert.NotEqual(JsonValueKind.Null, Summary(atBound).GetProperty("robustTrendSlope").ValueKind);
        Assert.Equal(JsonValueKind.Null, Summary(above).GetProperty("robustTrendSlope").ValueKind);
        Assert.Equal(JsonValueKind.Null, Summary(farAbove).GetProperty("robustTrendSlope").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, Summary(above).GetProperty("trendSlope").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, Summary(above).GetProperty("median").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, Summary(above).GetProperty("skewness").ValueKind);
    }

    [Fact]
    public async Task TheMinimumTrendWindowGivesARobustSlope()
    {
        using var response = await CallAsync(await StartAsync(), ""","summary":true,"summaryDistribution":true,"trendBars":2""");

        Assert.NotEqual(JsonValueKind.Null, Summary(response).GetProperty("robustTrendSlope").ValueKind);
        Assert.Equal(Number(Summary(response).GetProperty("trendSlope")), Number(Summary(response).GetProperty("robustTrendSlope"))); // two points: both are their slope
    }

    [Fact]
    public async Task TheSixFieldsAreRoundedByDecimalPlaces()
    {
        var dispatcher = await StartAsync();
        using var raw = await CallAsync(dispatcher, ""","summary":true,"summaryDistribution":true""");
        using var rounded = await CallAsync(dispatcher, ""","summary":true,"summaryDistribution":true,"decimalPlaces":2""");

        foreach (string field in NewFields)
        {
            decimal? value = Number(Summary(raw).GetProperty(field));
            Assert.NotNull(value);
            Assert.Equal(SeriesOutputOptions.Round(value!.Value, 2), Number(Summary(rounded).GetProperty(field)));
        }
    }

    [Fact]
    public async Task WithoutSummaryTheArgumentIsRefused()
    {
        using var response = await CallAsync(await StartAsync(), ""","lookbackOffsets":[0],"summaryDistribution":true""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("summaryDistribution", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task TheBatchToolGivesTheSameSummaryAsTheSingleCall()
    {
        var dispatcher = await StartAsync();
        const string Extra = ""","lookbackBars":300,"summary":true,"summaryDistribution":true,"trendBars":25""";
        using var single = await CallAsync(dispatcher, Extra);
        using var batch = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators_batch",
            $$$"""{"symbols":["msft"],"timeFrame":"D1","indicators":[{{{Sma3}}}]{{{Extra}}}}"""))!;

        Assert.False(McpTestSupport.IsError(batch), McpTestSupport.Structured(batch).GetRawText());
        JsonElement item = McpTestSupport.Structured(batch).GetProperty("items")[0];
        string batchSummary = item.GetProperty("results").GetProperty("s3").GetProperty("summary").GetProperty("SMA").GetRawText();
        Assert.Equal(Summary(single).GetRawText(), batchSummary);
        Assert.Contains("robustTrendSlope", batchSummary);

        using var batchWithout = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators_batch",
            $$$"""{"symbols":["msft"],"timeFrame":"D1","indicators":[{{{Sma3}}}],"summaryDistribution":true}"""))!;
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(batchWithout));
    }

    [Theory]
    [InlineData("sa_calculate_indicators")]
    [InlineData("sa_calculate_indicators_batch")]
    public async Task BothSchemasDescribeTheArgumentAndTheNullRuleOfTheRobustSlope(string tool)
    {
        var dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement properties = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == tool).GetProperty("inputSchema").GetProperty("properties");
        JsonElement argument = properties.GetProperty("summaryDistribution");

        Assert.Equal("boolean", argument.GetProperty("type").GetString());
        string description = argument.GetProperty("description").GetString()!;
        Assert.Equal(SeriesOutputOptions.SummaryDistributionDescription, description);
        Assert.Contains($"{SeriesSummaryStatistics.MaxRobustTrendBars}", description);
        Assert.Contains("null", description);
        Assert.Contains("not an error", description);
        Assert.Equal(30, SeriesSummaryStatistics.MaxRobustTrendBars);
    }
}
