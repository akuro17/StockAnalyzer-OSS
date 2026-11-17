using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_calculate_indicators_batch (owner decisions U-04, U-05) through the real composition on a throw-away data tree: every item equals the single-symbol call, one bad symbol
/// does not fail the call, a malformed request does, and a comparison symbol is read once for the whole call.
/// </summary>
public sealed class CalculateIndicatorsBatchToolTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Sma = """[{"id":"s","type":"SMA","parameters":{"Period":5}},{"id":"r","type":"RSI"}]""";
    private const string Correlation = """[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"aapl"}}]""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "7203.T", "aapl", "zzz.empty");
    private readonly List<McpServerRuntime> _runtimes = new();

    public CalculateIndicatorsBatchToolTests()
    {
        File.Delete(Path.Combine(_fixture.DailyDirectory, "ZZZ-EMPTY.parquet")); // listed, but nothing stored
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    /// <summary>Counts how many times the bars of each symbol are asked for, and forwards everything.</summary>
    public class CountingGateway : DispatchProxy
    {
        private IReadOnlyStockAnalyzerGateway _target = null!;

        public Dictionary<string, int> BarReads { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static (IReadOnlyStockAnalyzerGateway Gateway, CountingGateway Counter) Create(IReadOnlyStockAnalyzerGateway target)
        {
            IReadOnlyStockAnalyzerGateway proxy = DispatchProxy.Create<IReadOnlyStockAnalyzerGateway, CountingGateway>();
            var self = (CountingGateway)(object)proxy;
            self._target = target;
            return (proxy, self);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is nameof(IReadOnlyStockAnalyzerGateway.GetCandlesAsync) or nameof(IReadOnlyStockAnalyzerGateway.GetCandlesUpToAsync)
                && args![0] is string symbol)
            {
                lock (BarReads)
                {
                    BarReads[symbol] = BarReads.GetValueOrDefault(symbol) + 1;
                }
            }

            try
            {
                return targetMethod.Invoke(_target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }
    }

    private async Task<McpMessageDispatcher> StartAsync(Func<IReadOnlyStockAnalyzerGateway, IReadOnlyStockAnalyzerGateway>? gatewayWrapper = null, params (string Key, string Value)[] limitOverrides)
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

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()), gatewayWrapper: gatewayWrapper);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> BatchAsync(McpMessageDispatcher dispatcher, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators_batch", argumentsJson))!;

    private static async Task<JsonDocument> SingleAsync(McpMessageDispatcher dispatcher, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators", argumentsJson))!;

    private static string Batch(string symbols, string indicators, string extra = "", string timeFrame = "D1")
        => $$$"""{"symbols":{{{symbols}}},"timeFrame":"{{{timeFrame}}}","indicators":{{{indicators}}}{{{extra}}}}""";

    private static string Single(string symbol, string indicators, string extra = "", string timeFrame = "D1")
        => $$$"""{"symbol":"{{{symbol}}}","timeFrame":"{{{timeFrame}}}","indicators":{{{indicators}}}{{{extra}}}}""";

    private static JsonElement[] Items(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("items").EnumerateArray().ToArray();

    // ---- parity with the single-symbol call --------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryItemEqualsTheSingleSymbolCallForTheSameBars()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string Extra = ""","lookbackBars":300,"outputTailCount":4,"includeSeries":true,"summary":true,"trendBars":5,"lookbackOffsets":[0,2]""";

        using JsonDocument batch = await BatchAsync(dispatcher, Batch("""["msft","7203.T"]""", Sma, Extra));

        Assert.False(McpTestSupport.IsError(batch), McpTestSupport.Structured(batch).GetRawText());
        JsonElement[] items = Items(batch);
        Assert.Equal(new[] { "MSFT", "7203-T" }, items.Select(i => i.GetProperty("symbol").GetString()).ToArray());
        foreach (JsonElement item in items)
        {
            using JsonDocument single = await SingleAsync(dispatcher, Single(item.GetProperty("symbol").GetString()!, Sma, Extra));
            JsonElement expected = McpTestSupport.Structured(single);
            Assert.True(item.GetProperty("isSuccessful").GetBoolean());
            Assert.Equal(expected.GetProperty("results").GetRawText(), item.GetProperty("results").GetRawText());
            foreach (string key in new[] { "calculatedBarCount", "returnedBarCount", "earliestCalculatedTimestamp", "latestTimestamp", "droppedLeadingBar" })
            {
                Assert.Equal(expected.GetProperty(key).GetRawText(), item.GetProperty(key).GetRawText());
            }

            Assert.Equal(expected.GetProperty("timestamps").GetRawText(), item.GetProperty("timestamps").GetRawText());
        }

        JsonElement root = McpTestSupport.Structured(batch);
        Assert.Equal(2, root.GetProperty("symbolCount").GetInt32());
        Assert.Equal(2, root.GetProperty("succeededCount").GetInt32());
        Assert.Equal(0, root.GetProperty("failedCount").GetInt32());
        Assert.Equal(300, root.GetProperty("lookbackBars").GetInt32());
    }

    [Fact]
    public async Task TheDefaultAnswerIsCompact_LatestValuesOnly_OverTheDefaultWindow()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument batch = await BatchAsync(dispatcher, Batch("""["MSFT"]""", Sma));

        JsonElement root = McpTestSupport.Structured(batch);
        JsonElement item = Items(batch).Single();
        using JsonDocument single = await SingleAsync(dispatcher, Single("MSFT", Sma, ""","lookbackBars":500,"outputTailCount":1"""));
        Assert.Equal(500, root.GetProperty("lookbackBars").GetInt32());
        Assert.Equal(0, item.GetProperty("returnedBarCount").GetInt32());
        Assert.False(item.TryGetProperty("timestamps", out _));
        JsonElement sma = item.GetProperty("results").GetProperty("s");
        Assert.False(sma.TryGetProperty("series", out _));
        Assert.Equal(
            McpTestSupport.Structured(single).GetProperty("results").GetProperty("s").GetProperty("latestValues").GetRawText(),
            sma.GetProperty("latestValues").GetRawText());
        Assert.True(sma.TryGetProperty("leadingNullCount", out _));
    }

    [Fact]
    public async Task IncludeSeriesReturnsTheLastBarByDefaultAndDecimalPlacesRoundsIt()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument plain = await BatchAsync(dispatcher, Batch("""["MSFT"]""", Sma, ""","includeSeries":true"""));
        using JsonDocument rounded = await BatchAsync(dispatcher, Batch("""["MSFT"]""", Sma, ""","includeSeries":true,"decimalPlaces":0"""));

        JsonElement before = Items(plain).Single();
        JsonElement after = Items(rounded).Single();
        Assert.Equal(1, before.GetProperty("returnedBarCount").GetInt32());
        decimal close = before.GetProperty("results").GetProperty("s").GetProperty("series").GetProperty("SMA")[0].GetDecimal();
        Assert.NotEqual(Math.Round(close, 0, MidpointRounding.AwayFromZero), close);
        Assert.Equal(Math.Round(close, 0, MidpointRounding.AwayFromZero), after.GetProperty("results").GetProperty("s").GetProperty("series").GetProperty("SMA")[0].GetDecimal());
    }

    // ---- one bad symbol does not fail the call -------------------------------------------------------------------------------------

    [Fact]
    public async Task ASymbolThatIsNotAvailableOrHasNoBarsIsItsOwnItemAndTheOthersStillRun()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument batch = await BatchAsync(dispatcher, Batch("""["NOPE","MSFT","zzz.empty","msft"]""", Sma));

        Assert.False(McpTestSupport.IsError(batch));
        JsonElement root = McpTestSupport.Structured(batch);
        JsonElement[] items = Items(batch);
        Assert.Equal(new[] { "NOPE", "MSFT", "ZZZ-EMPTY" }, items.Select(i => i.GetProperty("symbol").GetString()).ToArray()); // the repeated symbol is evaluated once
        Assert.Equal("SYMBOL_NOT_FOUND", items[0].GetProperty("errorCode").GetString());
        Assert.False(items[0].GetProperty("isSuccessful").GetBoolean());
        Assert.False(items[0].TryGetProperty("results", out _));
        Assert.True(items[1].GetProperty("isSuccessful").GetBoolean());
        Assert.Equal("INSUFFICIENT_MARKET_DATA", items[2].GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(items[2].GetProperty("errorMessage").GetString()));
        Assert.Equal(1, root.GetProperty("succeededCount").GetInt32());
        Assert.Equal(2, root.GetProperty("failedCount").GetInt32());
    }

    // ---- a malformed request fails the call ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}]}""", "INVALID_ARGUMENT", "symbols")]
    [InlineData("""{"symbols":["MSFT"],"groupId":"6f1c6d0e-0f4e-4a43-9c63-3f1f6a6c1a11","timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}]}""", "INVALID_ARGUMENT", "symbols")]
    [InlineData("""{"symbols":["MSFT","bad/symbol"],"timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}]}""", "INVALID_ARGUMENT", "symbols[1]")]
    [InlineData("""{"groupId":"not-a-guid","timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}]}""", "INVALID_ARGUMENT", "groupId")]
    [InlineData("""{"groupId":"6f1c6d0e-0f4e-4a43-9c63-3f1f6a6c1a11","timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}]}""", "GROUP_NOT_FOUND", "groupId")]
    [InlineData("""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}],"outputTailCount":6,"includeSeries":true}""", "LIMIT_EXCEEDED", "outputTailCount")]
    [InlineData("""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}],"outputTailCount":2}""", "INVALID_ARGUMENT", "outputTailCount")]
    [InlineData("""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}],"lookbackBars":10001}""", "LIMIT_EXCEEDED", "lookbackBars")]
    [InlineData("""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"s","type":"NoSuchIndicator"}]}""", "UNKNOWN_INDICATOR", "indicators[0].type")]
    [InlineData("""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"s","type":"SMA","parameters":{"Period":0}}]}""", "INVALID_INDICATOR_PARAMETER", "indicators[0].parameters.Period")]
    [InlineData("""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"nope"}}]}""", "SYMBOL_NOT_FOUND", "indicators[0].parameters.ComparisonSymbol")]
    [InlineData("""{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"c","type":"Correlation"}]}""", "INVALID_INDICATOR_PARAMETER", "indicators[0].parameters.ComparisonSymbol")]
    public async Task AMalformedRequestFailsTheWholeCallBeforeAnySymbolIsEvaluated(string argumentsJson, string code, string parameter)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await BatchAsync(dispatcher, argumentsJson);

        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task MoreSymbolsOrMoreComparisonSymbolsThanTheLimitsAreLimitExceeded()
    {
        McpMessageDispatcher dispatcher = await StartAsync(null, ("MaxBatchIndicatorSymbols", "2"), ("MaxSecondarySymbolsPerCall", "1"));

        using JsonDocument tooMany = await BatchAsync(dispatcher, Batch("""["MSFT","7203.T","AAPL"]""", Sma));
        using JsonDocument tooManyComparisons = await BatchAsync(dispatcher, Batch("""["MSFT"]""",
            """[{"id":"a","type":"Correlation","parameters":{"ComparisonSymbol":"aapl"}},{"id":"b","type":"Correlation","parameters":{"ComparisonSymbol":"7203.T"}}]"""));

        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(tooMany));
        Assert.Equal("symbols", McpTestSupport.Structured(tooMany).GetProperty("param").GetString());
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(tooManyComparisons));
    }

    // ---- U-04: the comparison symbols are read once ---------------------------------------------------------------------------------

    [Fact]
    public async Task ACrossTickerIndicatorGivesTheSingleCallValuesAndTheComparisonSymbolIsReadOnceForTheWholeCall()
    {
        CountingGateway? counter = null;
        McpMessageDispatcher counted = await StartAsync(inner =>
        {
            (IReadOnlyStockAnalyzerGateway gateway, counter) = CountingGateway.Create(inner);
            return gateway;
        });
        McpMessageDispatcher plain = await StartAsync();

        using JsonDocument batch = await BatchAsync(counted, Batch("""["MSFT","7203.T"]""", Correlation, ""","includeSeries":true,"outputTailCount":3"""));

        Assert.False(McpTestSupport.IsError(batch), McpTestSupport.Structured(batch).GetRawText());
        Assert.Equal(1, counter!.BarReads["AAPL"]); // the single call reads it once per symbol: twice here
        foreach (JsonElement item in Items(batch))
        {
            Assert.True(item.GetProperty("isSuccessful").GetBoolean());
            using JsonDocument single = await SingleAsync(plain, Single(item.GetProperty("symbol").GetString()!, Correlation, ""","outputTailCount":3"""));
            Assert.Equal(McpTestSupport.Structured(single).GetProperty("results").GetRawText(), item.GetProperty("results").GetRawText());
            Assert.Contains(item.GetProperty("results").GetProperty("c").GetProperty("series").EnumerateObject().First().Value.EnumerateArray(), v => v.ValueKind != JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task WeeklyBarsWithCompleteBarsOnlyAndAnIndicatorWithItsOwnTimeFrameEqualTheSingleCall()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string OwnFrame = """[{"id":"w","type":"SMA","parameters":{"Period":3},"timeFrame":"W1"},{"id":"d","type":"RSI"}]""";
        const string Extra = ""","includeSeries":true,"outputTailCount":3""";
        const string Weekly = ""","includeSeries":true,"outputTailCount":3,"completeBarsOnly":true""";

        using JsonDocument ownBatch = await BatchAsync(dispatcher, Batch("""["MSFT"]""", OwnFrame, Extra));
        using JsonDocument ownSingle = await SingleAsync(dispatcher, Single("MSFT", OwnFrame, Extra));
        using JsonDocument weeklyBatch = await BatchAsync(dispatcher, Batch("""["MSFT","7203.T"]""", Sma, Weekly, "W1"));

        Assert.Equal(McpTestSupport.Structured(ownSingle).GetProperty("results").GetRawText(), Items(ownBatch).Single().GetProperty("results").GetRawText());
        Assert.Contains("PreviousClosedPeriod", Items(ownBatch).Single().GetProperty("results").GetRawText());
        foreach (JsonElement item in Items(weeklyBatch))
        {
            using JsonDocument single = await SingleAsync(dispatcher, Single(item.GetProperty("symbol").GetString()!, Sma, Weekly, "W1"));
            JsonElement expected = McpTestSupport.Structured(single);
            Assert.Equal(expected.GetProperty("results").GetRawText(), item.GetProperty("results").GetRawText());
            foreach (string key in new[] { "completeBarsOnly", "referenceDate", "lastBarPeriodEnd", "lastBarPartial", "droppedPartialBar", "calculatedBarCount" })
            {
                Assert.Equal(expected.GetProperty(key).GetRawText(), item.GetProperty(key).GetRawText());
            }
        }
    }

    // ---- the tool in the list -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheToolIsListedAsHeavyAndItsIndicatorEntriesAreThoseOfTheSingleTool()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        JsonElement[] tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        JsonElement batch = tools.Single(t => t.GetProperty("name").GetString() == "sa_calculate_indicators_batch").GetProperty("inputSchema").GetProperty("properties");
        JsonElement single = tools.Single(t => t.GetProperty("name").GetString() == "sa_calculate_indicators").GetProperty("inputSchema").GetProperty("properties");

        Assert.Equal(single.GetProperty("indicators").GetProperty("items").GetRawText(), batch.GetProperty("indicators").GetProperty("items").GetRawText());
        Assert.Equal(single.GetProperty("derived").GetProperty("items").GetRawText(), batch.GetProperty("derived").GetProperty("items").GetRawText());
        Assert.Equal(5, batch.GetProperty("outputTailCount").GetProperty("maximum").GetInt32());
        Assert.False(batch.GetProperty("includeSeries").TryGetProperty("default", out _));
    }
}
