using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T2a (decision D-2a): the optional <c>decimalPlaces</c> of sa_calculate_relative_performance, sa_calculate_cross_asset_stats and sa_get_seasonality through the
/// real composition on a throw-away data tree. The rule under test: with the argument, every written non-integer number equals the number of the call WITHOUT it rounded by the test
/// (away from zero at the midpoint); everything else (counts, dates, text, nulls, key order) is untouched; without the argument the answer is the calculated numbers.
/// </summary>
public sealed class DecimalPlacesAnalysisToolsTests : IDisposable
{
    private const int StoredDays = 400;
    private const string RelativeTool = "sa_calculate_relative_performance";
    private const string CrossAssetTool = "sa_calculate_cross_asset_stats";
    private const string SeasonalityTool = "sa_get_seasonality";

    private const string RelativeArguments = """{"symbol":"MSFT","timeFrame":"D1","comparisonSymbols":["AAPL","GOOGL"],"outputTailCount":60""";
    private const string CrossAssetArguments = """{"symbols":["MSFT","AAPL","GOOGL"],"benchmark":"MSFT","timeFrame":"D1","includeCovariance":true,"includeDistributionStats":true""";
    private const string SeasonalityArguments = """{"symbol":"MSFT","timeFrame":"D1","years":3""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "aapl", "googl");
    private readonly List<McpServerRuntime> _runtimes = new();

    public DecimalPlacesAnalysisToolsTests()
    {
        // Three different series, so that ratios, spreads, correlations and betas are not the trivial 1 / 0 of identical fixtures.
        Seed("MSFT", i => 100m + (decimal)(10 * Math.Sin(i / 6.0)) + (i * 0.037m));
        Seed("AAPL", i => 150m + (decimal)(15 * Math.Cos(i / 8.0)) + (i * 0.011m));
        Seed("GOOGL", i => 80m + (decimal)(7 * Math.Sin(i / 11.0)) - (i * 0.023m));
    }

    private void Seed(string symbol, Func<int, decimal> close)
        => StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(
            _fixture.DailyDirectory, symbol, Enumerable.Range(0, StoredDays).Select(i => Math.Round(close(i), 4)).ToList());

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
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonElement> OkAsync(McpMessageDispatcher dispatcher, string tool, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static string WithPlaces(string openArguments, string places) => openArguments + (places.Length == 0 ? string.Empty : $",\"decimalPlaces\":{places}") + "}";

    internal static bool IsFractional(JsonElement number)
    {
        string raw = number.GetRawText();
        return raw.Contains('.', StringComparison.Ordinal) || raw.Contains('e', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks the answer without the argument (<paramref name="original"/>) and the answer with it (<paramref name="actual"/>) in step: same shape and key order, every fractional number is
    /// the test's own rounding of the original, everything else is identical. Returns how many numbers the rounding actually changed.
    /// </summary>
    internal static int AssertRoundedCopy(JsonElement original, JsonElement actual, int places, bool asDouble, string path = "$", IReadOnlySet<string>? echoedAsSent = null)
    {
        Assert.Equal(original.ValueKind, actual.ValueKind);
        if (echoedAsSent is not null && echoedAsSent.Contains(path))
        {
            // An echo of the caller's own argument is written as sent, never rounded.
            Assert.Equal(original.GetRawText(), actual.GetRawText());
            return 0;
        }

        switch (original.ValueKind)
        {
            case JsonValueKind.Object:
                string[] names = original.EnumerateObject().Select(p => p.Name).ToArray();
                Assert.Equal(names, actual.EnumerateObject().Select(p => p.Name).ToArray());
                return names.Sum(name => AssertRoundedCopy(original.GetProperty(name), actual.GetProperty(name), places, asDouble, $"{path}.{name}", echoedAsSent));
            case JsonValueKind.Array:
                Assert.Equal(original.GetArrayLength(), actual.GetArrayLength());
                return Enumerable.Range(0, original.GetArrayLength()).Sum(i => AssertRoundedCopy(original[i], actual[i], places, asDouble, $"{path}[{i}]", echoedAsSent));
            case JsonValueKind.Number when IsFractional(original):
                if (asDouble)
                {
                    double expected = Math.Round(original.GetDouble(), places, MidpointRounding.AwayFromZero);
                    Assert.True(expected == actual.GetDouble(), $"{path}: expected {expected} but the answer has {actual.GetRawText()} (calculated {original.GetRawText()})");
                    return expected == original.GetDouble() ? 0 : 1;
                }
                else
                {
                    decimal expected = Math.Round(original.GetDecimal(), places, MidpointRounding.AwayFromZero);
                    Assert.True(expected == actual.GetDecimal(), $"{path}: expected {expected} but the answer has {actual.GetRawText()} (calculated {original.GetRawText()})");
                    return expected == original.GetDecimal() ? 0 : 1;
                }

            case JsonValueKind.Number when original.GetRawText() == "-0":
                // A calculated negative zero: with decimalPlaces a result of zero is a plain zero.
                Assert.Equal("0", actual.GetRawText());
                return 1;
            default:
                Assert.Equal(original.GetRawText(), actual.GetRawText());
                return 0;
        }
    }

    // ---- the helper itself ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2.5, 0, 3.0)]
    [InlineData(-2.5, 0, -3.0)]
    [InlineData(0.125, 2, 0.13)]
    [InlineData(-0.125, 2, -0.13)]
    [InlineData(1234.5678, 2, 1234.57)]
    public void RoundingADoubleGoesAwayFromZeroAtTheMidpoint(double value, int places, double expected)
        => Assert.Equal(expected, SeriesOutputOptions.Round(value, places));

    [Fact]
    public void RoundingADoubleKeepsNonFiniteValuesAndNeverReturnsNegativeZero()
    {
        Assert.True(double.IsNaN(SeriesOutputOptions.Round(double.NaN, 2)));
        Assert.True(double.IsPositiveInfinity(SeriesOutputOptions.Round(double.PositiveInfinity, 2)));
        Assert.True(double.IsNegativeInfinity(SeriesOutputOptions.Round(double.NegativeInfinity, 2)));

        double zero = SeriesOutputOptions.Round(-0.0004, 2);
        Assert.Equal(0d, zero);
        Assert.False(double.IsNegative(zero), "a result of zero is a plain zero");
        Assert.False(double.IsNegative(SeriesOutputOptions.Round(-0.0, 2)));
    }

    [Fact]
    public void RoundingADoubleFollowsTheBinaryValueNotDecimalIntuition()
    {
        // 1.005 is stored as 1.00499999999999989...; whatever Math.Round makes of it is what the tool writes.
        Assert.Equal(Math.Round(1.005, 2, MidpointRounding.AwayFromZero), SeriesOutputOptions.Round(1.005, 2));
    }

    [Fact]
    public void RoundIfRequestedLeavesTheValueAloneWithoutPlacesAndRoundsWithThem()
    {
        Assert.Equal(1.23456789d, SeriesOutputOptions.RoundIfRequested(1.23456789d, null));
        Assert.Equal(1.23456789m, SeriesOutputOptions.RoundIfRequested(1.23456789m, null));
        Assert.Equal(1.23d, SeriesOutputOptions.RoundIfRequested(1.23456789d, 2));
        Assert.Equal(1.23m, SeriesOutputOptions.RoundIfRequested(1.23456789m, 2));
    }

    // ---- sa_calculate_relative_performance --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Performance", "0")]
    [InlineData("Performance", "2")]
    [InlineData("Ratio", "3")]
    [InlineData("Spread", "2")]
    [InlineData("ZScore", "4")]
    public async Task RelativePerformance_DecimalPlacesIsTheCallWithoutItRoundedByTheTest(string mode, string places)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string open = RelativeArguments + $",\"mode\":\"{mode}\"";

        JsonElement calculated = await OkAsync(dispatcher, RelativeTool, WithPlaces(open, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, RelativeTool, WithPlaces(open, places));

        int changed = AssertRoundedCopy(calculated, rounded, int.Parse(places), asDouble: false);
        Assert.True(changed > 0, "the fixture must hold numbers that the rounding changes, or the test proves nothing");
    }

    [Fact]
    public async Task RelativePerformance_WithoutDecimalPlacesTheAnswerIsTheSameAsBefore()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement first = await OkAsync(dispatcher, RelativeTool, WithPlaces(RelativeArguments, string.Empty));
        JsonElement second = await OkAsync(dispatcher, RelativeTool, WithPlaces(RelativeArguments, string.Empty));

        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.False(first.GetRawText().Contains("decimalPlaces", StringComparison.Ordinal));
    }

    // ---- sa_calculate_cross_asset_stats -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("6")]
    public async Task CrossAssetStats_DecimalPlacesIsTheCallWithoutItRoundedByTheTest(string places)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement calculated = await OkAsync(dispatcher, CrossAssetTool, WithPlaces(CrossAssetArguments, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, CrossAssetTool, WithPlaces(CrossAssetArguments, places));

        int changed = AssertRoundedCopy(calculated, rounded, int.Parse(places), asDouble: true);
        Assert.True(changed > 0);
        Assert.Equal(calculated.GetProperty("commonDateCount").GetRawText(), rounded.GetProperty("commonDateCount").GetRawText());
    }

    [Fact]
    public async Task CrossAssetStats_TheDiagonalAndTheBenchmarkRowStayExactAndAReportedSymmetryIsKept()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement rounded = await OkAsync(dispatcher, CrossAssetTool, WithPlaces(CrossAssetArguments, "2"));

        JsonElement correlation = rounded.GetProperty("correlation");
        for (int i = 0; i < correlation.GetArrayLength(); i++)
        {
            Assert.Equal(1.0, correlation[i][i].GetDouble());
            for (int j = 0; j < i; j++)
            {
                Assert.Equal(correlation[i][j].GetDouble(), correlation[j][i].GetDouble());
            }
        }

        JsonElement benchmarkRow = rounded.GetProperty("betaAlpha")[0];
        Assert.Equal("MSFT", benchmarkRow.GetProperty("symbol").GetString());
        Assert.Equal(1.0, benchmarkRow.GetProperty("beta").GetDouble());
    }

    // ---- sa_get_seasonality -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("4")]
    public async Task Seasonality_DecimalPlacesIsTheCallWithoutItRoundedByTheTest(string places)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement calculated = await OkAsync(dispatcher, SeasonalityTool, WithPlaces(SeasonalityArguments, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, SeasonalityTool, WithPlaces(SeasonalityArguments, places));

        int changed = AssertRoundedCopy(calculated, rounded, int.Parse(places), asDouble: false);
        Assert.True(changed > 0);
    }

    // ---- the three tools: refusals and schema -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(RelativeTool, RelativeArguments, "-1", "INVALID_ARGUMENT")]
    [InlineData(RelativeTool, RelativeArguments, "11", "LIMIT_EXCEEDED")]
    [InlineData(RelativeTool, RelativeArguments, "\"2\"", "INVALID_ARGUMENT")]
    [InlineData(CrossAssetTool, CrossAssetArguments, "-1", "INVALID_ARGUMENT")]
    [InlineData(CrossAssetTool, CrossAssetArguments, "11", "LIMIT_EXCEEDED")]
    [InlineData(CrossAssetTool, CrossAssetArguments, "\"2\"", "INVALID_ARGUMENT")]
    [InlineData(SeasonalityTool, SeasonalityArguments, "-1", "INVALID_ARGUMENT")]
    [InlineData(SeasonalityTool, SeasonalityArguments, "11", "LIMIT_EXCEEDED")]
    [InlineData(SeasonalityTool, SeasonalityArguments, "\"2\"", "INVALID_ARGUMENT")]
    public async Task ABadDecimalPlacesIsRefusedWithTheSharedRule(string tool, string openArguments, string places, string code)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, WithPlaces(openArguments, places)))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("decimalPlaces", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData(RelativeTool)]
    [InlineData(CrossAssetTool)]
    [InlineData(SeasonalityTool)]
    public async Task TheSchemaOffersDecimalPlacesAsAnOptionalBoundedInteger(string tool)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None))!);
        JsonElement entry = McpTestSupport.Result(list).GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == tool);
        JsonElement schema = entry.GetProperty("inputSchema");
        JsonElement property = schema.GetProperty("properties").GetProperty("decimalPlaces");

        Assert.Equal("integer", property.GetProperty("type").GetString());
        Assert.Equal(0, property.GetProperty("minimum").GetInt32());
        Assert.Equal(SeriesOutputOptions.MaxDecimalPlaces, property.GetProperty("maximum").GetInt32());
        if (schema.TryGetProperty("required", out JsonElement required))
        {
            Assert.DoesNotContain(required.EnumerateArray().Select(r => r.GetString()), name => name == "decimalPlaces");
        }
        Assert.Contains("decimalPlaces", entry.GetProperty("description").GetString(), StringComparison.Ordinal);
    }
}
