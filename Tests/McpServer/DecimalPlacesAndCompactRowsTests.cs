using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Owner decisions U-02 and U-03 through the real composition on a throw-away data tree: the optional <c>decimalPlaces</c> of sa_get_candles and sa_calculate_indicators and the
/// optional <c>compactRows</c> of sa_get_candles. Without them the answers keep their previous shape; the counts are never rounded.
/// </summary>
public sealed class DecimalPlacesAndCompactRowsTests : IDisposable
{
    private const int StoredDays = 60;
    private const string CandleSymbol = "MSFT";

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

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, tool, argumentsJson))!;

    private static string CandleArgs(string extra = "") => $$$"""{"symbol":"{{{CandleSymbol}}}","timeFrame":"D1"{{{extra}}}}""";

    private static string IndicatorArgs(string extra = "")
        => $$$"""{"symbol":"{{{CandleSymbol}}}","timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}}],"outputTailCount":10{{{extra}}}}""";

    private static decimal Away(decimal value, int places) => Math.Round(value, places, MidpointRounding.AwayFromZero);

    // ---- sa_get_candles: decimalPlaces ------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetCandles_DecimalPlacesRoundsOpenHighLowCloseAwayFromZeroAndNeverTheVolume()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument plain = await CallAsync(dispatcher, "sa_get_candles", CandleArgs());
        using JsonDocument rounded = await CallAsync(dispatcher, "sa_get_candles", CandleArgs(""","decimalPlaces":0"""));

        JsonElement[] before = McpTestSupport.Structured(plain).GetProperty("candles").EnumerateArray().ToArray();
        JsonElement[] after = McpTestSupport.Structured(rounded).GetProperty("candles").EnumerateArray().ToArray();
        Assert.Equal(before.Length, after.Length);
        Assert.True(before.Any(c => c.GetProperty("close").GetDecimal() != Away(c.GetProperty("close").GetDecimal(), 0)), "the stored prices must have decimals for the rounding to be visible");
        for (int i = 0; i < before.Length; i++)
        {
            foreach (string field in new[] { "open", "high", "low", "close" })
            {
                Assert.Equal(Away(before[i].GetProperty(field).GetDecimal(), 0), after[i].GetProperty(field).GetDecimal());
            }

            Assert.Equal(before[i].GetProperty("volume").GetInt64(), after[i].GetProperty("volume").GetInt64());
            Assert.Equal(before[i].GetProperty("timestamp").GetString(), after[i].GetProperty("timestamp").GetString());
        }
    }

    [Fact]
    public async Task GetCandles_WithoutTheNewArgumentsTheAnswerHasNoCompactKeysAndItsCandlesAreUnchanged()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument plain = await CallAsync(dispatcher, "sa_get_candles", CandleArgs());
        using JsonDocument explicitDefaults = await CallAsync(dispatcher, "sa_get_candles", CandleArgs(""","compactRows":false"""));

        JsonElement result = McpTestSupport.Structured(plain);
        Assert.False(result.TryGetProperty("candleColumns", out _));
        Assert.False(result.TryGetProperty("candleRows", out _));
        Assert.Equal(StoredDays, result.GetProperty("candles").GetArrayLength());
        Assert.Equal(McpTestSupport.Structured(plain).GetRawText(), McpTestSupport.Structured(explicitDefaults).GetRawText());
    }

    [Theory]
    [InlineData(""","decimalPlaces":11""", "LIMIT_EXCEEDED")]
    [InlineData(""","decimalPlaces":-1""", "INVALID_ARGUMENT")]
    [InlineData(",\"decimalPlaces\":\"2\"", "INVALID_ARGUMENT")]
    [InlineData(",\"compactRows\":\"yes\"", "INVALID_ARGUMENT")]
    public async Task GetCandles_RefusesABadDecimalPlacesOrCompactRows(string extra, string code)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_candles", CandleArgs(extra));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
    }

    // ---- sa_get_candles: compactRows --------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetCandles_CompactRowsReturnsAnEmptyCandlesArrayAndTheSameBarsAsColumnsAndRows()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument plain = await CallAsync(dispatcher, "sa_get_candles", CandleArgs());
        using JsonDocument compact = await CallAsync(dispatcher, "sa_get_candles", CandleArgs(""","compactRows":true"""));

        JsonElement result = McpTestSupport.Structured(compact);
        JsonElement[] bars = McpTestSupport.Structured(plain).GetProperty("candles").EnumerateArray().ToArray();
        JsonElement[] rows = result.GetProperty("candleRows").EnumerateArray().ToArray();
        Assert.Equal(0, result.GetProperty("candles").GetArrayLength());
        Assert.Equal(new[] { "timestamp", "open", "high", "low", "close", "volume" }, result.GetProperty("candleColumns").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.Equal(bars.Length, result.GetProperty("barCount").GetInt32());
        Assert.Equal(bars.Length, rows.Length);
        for (int i = 0; i < bars.Length; i++)
        {
            JsonElement[] row = rows[i].EnumerateArray().ToArray();
            Assert.Equal(6, row.Length);
            Assert.Equal(bars[i].GetProperty("timestamp").GetString(), row[0].GetString());
            Assert.Equal(bars[i].GetProperty("open").GetDecimal(), row[1].GetDecimal());
            Assert.Equal(bars[i].GetProperty("high").GetDecimal(), row[2].GetDecimal());
            Assert.Equal(bars[i].GetProperty("low").GetDecimal(), row[3].GetDecimal());
            Assert.Equal(bars[i].GetProperty("close").GetDecimal(), row[4].GetDecimal());
            Assert.Equal(bars[i].GetProperty("volume").GetInt64(), row[5].GetInt64());
        }
    }

    [Fact]
    public async Task GetCandles_CompactRowsAndDecimalPlacesTogetherRoundThePricesOfTheRowsOnly()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument plain = await CallAsync(dispatcher, "sa_get_candles", CandleArgs());
        using JsonDocument compact = await CallAsync(dispatcher, "sa_get_candles", CandleArgs(""","compactRows":true,"decimalPlaces":1"""));

        JsonElement[] bars = McpTestSupport.Structured(plain).GetProperty("candles").EnumerateArray().ToArray();
        JsonElement[] rows = McpTestSupport.Structured(compact).GetProperty("candleRows").EnumerateArray().ToArray();
        for (int i = 0; i < bars.Length; i++)
        {
            JsonElement[] row = rows[i].EnumerateArray().ToArray();
            Assert.Equal(Away(bars[i].GetProperty("open").GetDecimal(), 1), row[1].GetDecimal());
            Assert.Equal(Away(bars[i].GetProperty("close").GetDecimal(), 1), row[4].GetDecimal());
            Assert.Equal(bars[i].GetProperty("volume").GetInt64(), row[5].GetInt64());
        }
    }

    [Fact]
    public async Task GetCandles_TheOutputSchemaStillRequiresCandlesAndOnlyOffersTheCompactKeysAsOptional()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_get_candles");

        JsonElement output = tool.GetProperty("outputSchema");
        string[] required = output.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToArray();
        JsonElement rowItems = output.GetProperty("properties").GetProperty("candleRows").GetProperty("items");
        Assert.Contains("candles", required);
        Assert.DoesNotContain("candleRows", required);
        Assert.DoesNotContain("candleColumns", required);
        Assert.Equal("array", rowItems.GetProperty("type").GetString());
        Assert.Equal(new[] { "string", "number" }, rowItems.GetProperty("items").GetProperty("type").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal("integer", tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("decimalPlaces").GetProperty("type").GetString());
        Assert.Equal("boolean", tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("compactRows").GetProperty("type").GetString());
    }

    // ---- sa_calculate_indicators: decimalPlaces ---------------------------------------------------------------------------------

    private static decimal?[] Numbers(JsonElement array)
        => array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    [Fact]
    public async Task CalculateIndicators_DecimalPlacesRoundsEveryDecimalNumberAndNeverACount()
    {
        const string Extra = ""","summary":true,"trendBars":5,"lookbackOffsets":[0,3],"derived":[{"id":"d","op":"Multiply","left":{"ref":"s"},"right":{"constant":1.23456}}]""";
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument plain = await CallAsync(dispatcher, "sa_calculate_indicators", IndicatorArgs(Extra));
        using JsonDocument rounded = await CallAsync(dispatcher, "sa_calculate_indicators", IndicatorArgs(Extra + ""","decimalPlaces":2"""));

        Assert.False(McpTestSupport.IsError(plain), McpTestSupport.Structured(plain).GetRawText());
        Assert.False(McpTestSupport.IsError(rounded), McpTestSupport.Structured(rounded).GetRawText());
        JsonElement results = McpTestSupport.Structured(plain).GetProperty("results");
        JsonElement roundedResults = McpTestSupport.Structured(rounded).GetProperty("results");
        bool anyVisible = false;
        foreach (string id in new[] { "s", "d" })
        {
            JsonElement before = results.GetProperty(id);
            JsonElement after = roundedResults.GetProperty(id);
            decimal?[] seriesBefore = Numbers(before.GetProperty("series").GetProperty(id == "s" ? "SMA" : "Main"));
            decimal?[] seriesAfter = Numbers(after.GetProperty("series").GetProperty(id == "s" ? "SMA" : "Main"));
            Assert.Equal(seriesBefore.Select(v => v is { } n ? Away(n, 2) : (decimal?)null), seriesAfter);
            anyVisible |= seriesBefore.Any(v => v is { } n && n != Away(n, 2));

            string name = id == "s" ? "SMA" : "Main";
            Assert.Equal(Away(before.GetProperty("latestValues").GetProperty(name).GetDecimal(), 2), after.GetProperty("latestValues").GetProperty(name).GetDecimal());
            Assert.Equal(before.GetProperty("leadingNullCount").GetProperty(name).GetInt32(), after.GetProperty("leadingNullCount").GetProperty(name).GetInt32());

            JsonElement summaryBefore = before.GetProperty("summary").GetProperty(name);
            JsonElement summaryAfter = after.GetProperty("summary").GetProperty(name);
            foreach (string field in new[] { "latest", "mean", "stdDev", "zScoreOfLatest", "percentileRankOfLatest", "trendSlope" })
            {
                decimal? expected = summaryBefore.GetProperty(field).ValueKind == JsonValueKind.Null ? null : Away(summaryBefore.GetProperty(field).GetDecimal(), 2);
                decimal? actual = summaryAfter.GetProperty(field).ValueKind == JsonValueKind.Null ? null : summaryAfter.GetProperty(field).GetDecimal();
                Assert.Equal(expected, actual);
            }

            Assert.Equal(summaryBefore.GetProperty("count").GetRawText(), summaryAfter.GetProperty("count").GetRawText());
            Assert.Equal(summaryBefore.GetProperty("trendBars").GetRawText(), summaryAfter.GetProperty("trendBars").GetRawText());
            foreach (string offset in new[] { "0", "3" })
            {
                decimal expectedPast = Away(before.GetProperty("pastValues").GetProperty(name).GetProperty(offset).GetDecimal(), 2);
                Assert.Equal(expectedPast, after.GetProperty("pastValues").GetProperty(name).GetProperty(offset).GetDecimal());
            }
        }

        Assert.True(anyVisible, "the calculated values must have more than two decimals for the rounding to be visible");
    }

    [Fact]
    public async Task CalculateIndicators_WithoutDecimalPlacesTheAnswerIsTheCalculatedNumbers()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument first = await CallAsync(dispatcher, "sa_calculate_indicators", IndicatorArgs());
        using JsonDocument second = await CallAsync(dispatcher, "sa_calculate_indicators", IndicatorArgs());

        Assert.Equal(McpTestSupport.Structured(first).GetRawText(), McpTestSupport.Structured(second).GetRawText());
        decimal?[] series = Numbers(McpTestSupport.Structured(first).GetProperty("results").GetProperty("s").GetProperty("series").GetProperty("SMA"));
        Assert.Contains(series, v => v is { } n && n != Away(n, 0)); // the stored prices have one decimal, so rounding to none is visible
    }

    [Theory]
    [InlineData(""","decimalPlaces":11""", "LIMIT_EXCEEDED")]
    [InlineData(""","decimalPlaces":-1""", "INVALID_ARGUMENT")]
    [InlineData(""","decimalPlaces":1.5""", "INVALID_ARGUMENT")]
    public async Task CalculateIndicators_RefusesABadDecimalPlaces(string extra, string code)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_calculate_indicators", IndicatorArgs(extra));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
    }

    // ---- U-01 on the real composition -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ParallelLightCallsGiveTheSameAnswersAsTheSameCallsOneAfterAnother()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string candleArgs = CandleArgs(""","count":30""");
        string indicatorArgs = IndicatorArgs(""","summary":true""");
        using JsonDocument candleSerial = await CallAsync(dispatcher, "sa_get_candles", candleArgs);
        using JsonDocument indicatorSerial = await CallAsync(dispatcher, "sa_calculate_indicators", indicatorArgs);

        var calls = new List<(string Tool, string Expected, Task<JsonDocument?> Call)>();
        for (int i = 0; i < 12; i++)
        {
            bool candles = i % 2 == 0;
            string tool = candles ? "sa_get_candles" : "sa_calculate_indicators";
            string expected = McpTestSupport.Structured(candles ? candleSerial : indicatorSerial).GetRawText();
            calls.Add((tool, expected, McpTestSupport.CallAsync(dispatcher, tool, candles ? candleArgs : indicatorArgs, id: 100 + i)));
        }

        foreach ((string tool, string expected, Task<JsonDocument?> call) in calls)
        {
            using JsonDocument response = (await call)!;
            Assert.False(McpTestSupport.IsError(response), tool);
            Assert.Equal(expected, McpTestSupport.Structured(response).GetRawText());
        }
    }
}
