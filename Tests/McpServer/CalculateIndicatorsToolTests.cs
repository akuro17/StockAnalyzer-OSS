using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>sa_calculate_indicators through the real composition on a throw-away data tree; values are compared with a direct pipeline call.</summary>
public sealed class CalculateIndicatorsToolTests : IDisposable
{
    private const int StoredDays = 400;

    private static readonly IndicatorFactory Factory = new();
    private static readonly ScreenerCatalogProvider CatalogProvider = new();
    private static readonly IndicatorCatalog Catalog = new(Factory, CatalogProvider);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "7203.T", "aapl");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<ServiceProvider> _providers = new();

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

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(limitOverrides), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_indicators", argumentsJson))!;

    private static string Args(string indicatorsJson, string symbol = "MSFT", string timeFrame = "D1", string extra = "")
        => $$$"""{"symbol":"{{{symbol}}}","timeFrame":"{{{timeFrame}}}","indicators":{{{indicatorsJson}}}{{{extra}}}}""";

    private static JsonElement Result(JsonDocument response, string id) => McpTestSupport.Structured(response).GetProperty("results").GetProperty(id);

    private static decimal?[] Values(JsonElement array)
        => array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    /// <summary>What the application's own pipeline gives for the same stored bars and settings (the parity reference).</summary>
    private async Task<(IReadOnlyList<CoreCandleData> Candles, Dictionary<string, IIndicatorResult> Results)> DirectAsync(
        string symbol, TimeFrame timeFrame, params CoreIndicatorSettings[] settings)
    {
        ServiceProvider provider = new ServiceCollection().AddStockAnalyzerReadOnlyCore(Configuration()).BuildServiceProvider();
        _providers.Add(provider);
        var gateway = provider.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        List<CoreCandleData> candles = (await gateway.GetCandlesAsync(symbol, timeFrame, 10000))
            .Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume))
            .ToList();
        var pipeline = new AnalysisPipelineService(pythonService: null, indicatorFactory: Factory);
        return (candles, await pipeline.CalculateIndicatorsAsync(candles, settings));
    }

    private static CoreIndicatorSettings Default(IndicatorType type) => CatalogProvider.GetDefaultSettings(type, Factory)!;

    // ---- parity ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultRsiSmaAndMacdEqualADirectPipelineCallExactly()
    {
        CoreIndicatorSettings rsi = Default(IndicatorType.RSI), sma = Default(IndicatorType.SMA), macd = Default(IndicatorType.MACD);
        var (candles, direct) = await DirectAsync("MSFT", TimeFrame.D1, rsi, sma, macd);

        using var response = await CallAsync(await StartAsync(),
            Args("""[{"id":"r","type":"RSI"},{"id":"s","type":"SMA"},{"id":"m","type":"MACD"}]""", extra: ""","outputTailCount":50"""));

        JsonElement root = McpTestSupport.Structured(response);
        Assert.False(McpTestSupport.IsError(response));
        Assert.Equal(candles.Count, root.GetProperty("calculatedBarCount").GetInt32());
        Assert.Equal(50, root.GetProperty("returnedBarCount").GetInt32());
        Assert.Equal(
            candles.TakeLast(50).Select(c => c.Timestamp.ToString("yyyy-MM-dd")).ToArray(),
            root.GetProperty("timestamps").EnumerateArray().Select(t => t.GetString()).ToArray());

        void AssertSame(string id, string seriesName, IIndicatorResult expected, string expectedSeriesKey)
        {
            decimal?[] expectedTail = expected.GetSeries(expectedSeriesKey).TakeLast(50).ToArray();
            JsonElement result = Result(response, id);
            Assert.True(result.GetProperty("isSuccessful").GetBoolean());
            Assert.Equal(expectedTail, Values(result.GetProperty("series").GetProperty(seriesName)));
            using var latest = JsonDocument.Parse($"[{result.GetProperty("latestValues").GetProperty(seriesName).GetRawText()}]");
            Assert.Equal(expectedTail[^1], Values(latest.RootElement)[0]);
            int leading = expected.GetSeries(expectedSeriesKey).TakeWhile(v => v is null).Count();
            Assert.Equal(leading, result.GetProperty("leadingNullCount").GetProperty(seriesName).GetInt32());
        }

        AssertSame("r", "RSI", direct[rsi.Id], "Main");
        AssertSame("s", "SMA", direct[sma.Id], "Main");
        AssertSame("m", "MacdLine", direct[macd.Id], "MacdLine");
        AssertSame("m", "Signal", direct[macd.Id], "Signal");
        AssertSame("m", "Histogram", direct[macd.Id], "Histogram");
    }

    [Fact]
    public async Task ParametersAreAppliedCaseInsensitivelyAndEqualADirectCallWithThoseParameters()
    {
        CoreIndicatorSettings rsi = Default(IndicatorType.RSI);
        ((StockAnalyzer.Core.Models.Parameters.CoreRsiParameter)rsi.ParameterObject!).Period = 5;
        var (_, direct) = await DirectAsync("MSFT", TimeFrame.D1, rsi);

        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"r","type":"rsi","parameters":{"period":5}}]"""));

        JsonElement result = Result(response, "r");
        Assert.Equal(direct[rsi.Id].MainValues.TakeLast(50).ToArray(), Values(result.GetProperty("series").GetProperty("RSI")));
        Assert.Equal("Relative Strength Index (5)", result.GetProperty("displayName").GetString());
        Assert.Equal(5, result.GetProperty("leadingNullCount").GetProperty("RSI").GetInt32());
    }

    [Fact]
    public async Task APriceSourceIsAppliedAndEchoed()
    {
        CoreIndicatorSettings sma = Default(IndicatorType.SMA);
        sma.PriceSource = PriceType.Open;
        var (_, direct) = await DirectAsync("MSFT", TimeFrame.D1, sma);

        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"s","type":"SMA","priceSource":"Open"},{"id":"c","type":"SMA"}]"""));

        Assert.Equal("Open", Result(response, "s").GetProperty("priceSource").GetString());
        Assert.Equal("Close", Result(response, "c").GetProperty("priceSource").GetString());
        Assert.Equal(direct[sma.Id].MainValues.TakeLast(50).ToArray(), Values(Result(response, "s").GetProperty("series").GetProperty("SMA")));
        Assert.NotEqual(Values(Result(response, "c").GetProperty("series").GetProperty("SMA")), Values(Result(response, "s").GetProperty("series").GetProperty("SMA")));
    }

    // ---- window and tail ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTailDefaultsToFiftyAndIsCappedByTheBars()
    {
        var dispatcher = await StartAsync();

        using var byDefault = await CallAsync(dispatcher, Args("""[{"id":"r","type":"RSI"}]"""));
        using var more = await CallAsync(dispatcher, Args("""[{"id":"r","type":"RSI"}]""", extra: ""","outputTailCount":500"""));

        Assert.Equal(50, McpTestSupport.Structured(byDefault).GetProperty("returnedBarCount").GetInt32());
        Assert.Equal(50, Values(Result(byDefault, "r").GetProperty("series").GetProperty("RSI")).Length);
        Assert.Equal(StoredDays, McpTestSupport.Structured(more).GetProperty("returnedBarCount").GetInt32()); // fewer bars than the tail asked for
        Assert.Equal(StoredDays, McpTestSupport.Structured(more).GetProperty("timestamps").GetArrayLength());
    }

    [Fact]
    public async Task LookbackBarsLimitsTheCalculatedHistory()
    {
        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"r","type":"RSI"}]""", extra: ""","lookbackBars":30"""));

        JsonElement root = McpTestSupport.Structured(response);
        Assert.Equal(30, root.GetProperty("calculatedBarCount").GetInt32());
        Assert.Equal(30, root.GetProperty("lookbackBars").GetInt32());
        Assert.Equal(14, Result(response, "r").GetProperty("leadingNullCount").GetProperty("RSI").GetInt32());
    }

    [Fact]
    public async Task AHistoryShorterThanTheWarmUpIsAnUnsuccessfulResultNotAnError()
    {
        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"r","type":"RSI"},{"id":"s","type":"SMA"}]""", extra: ""","lookbackBars":5"""));

        Assert.False(McpTestSupport.IsError(response));
        JsonElement rsi = Result(response, "r");
        Assert.False(rsi.GetProperty("isSuccessful").GetBoolean());
        Assert.Contains("warm-up", rsi.GetProperty("errorMessage").GetString());
        Assert.False(rsi.TryGetProperty("series", out _));
    }

    [Fact]
    public async Task WeeklyBarsAreAggregatedFromDailyAndThePartialOldestBarIsDropped()
    {
        var dispatcher = await StartAsync();

        using var all = await CallAsync(dispatcher, Args("""[{"id":"r","type":"SMA","parameters":{"Period":3}}]""", timeFrame: "W1"));
        using var few = await CallAsync(dispatcher, Args("""[{"id":"r","type":"SMA","parameters":{"Period":3}}]""", timeFrame: "W1", extra: ""","lookbackBars":10"""));
        using var weeklyCandles = (await McpTestSupport.CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"W1","count":10}"""))!;

        JsonElement allRoot = McpTestSupport.Structured(all);
        Assert.InRange(allRoot.GetProperty("calculatedBarCount").GetInt32(), 50, 120);
        Assert.False(allRoot.GetProperty("droppedLeadingBar").GetBoolean());
        JsonElement fewRoot = McpTestSupport.Structured(few);
        Assert.Equal(10, fewRoot.GetProperty("calculatedBarCount").GetInt32());
        Assert.True(fewRoot.GetProperty("droppedLeadingBar").GetBoolean());
        Assert.Equal(
            McpTestSupport.Structured(weeklyCandles).GetProperty("candles").EnumerateArray().Select(c => c.GetProperty("timestamp").GetString()).ToArray(),
            fewRoot.GetProperty("timestamps").EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    // ---- projected values and causality ---------------------------------------------------------------------------

    [Fact]
    public async Task IchimokuForwardProjectionIsReturnedSeparatelyAndChikouIsFlagged()
    {
        var (candles, direct) = await DirectAsync("MSFT", TimeFrame.D1, Default(IndicatorType.Ichimoku));
        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"i","type":"Ichimoku"}]"""));

        JsonElement ichimoku = Result(response, "i");
        Assert.Equal(new[] { "ChikouSpan" }, ichimoku.GetProperty("nonCausalSeries").EnumerateArray().Select(n => n.GetString()).ToArray());
        decimal?[] senkouA = direct.Values.Single().GetSeries("SenkouSpanA").ToArray();
        Assert.True(senkouA.Length > candles.Count);
        Assert.Equal(senkouA.Skip(candles.Count).ToArray(), Values(ichimoku.GetProperty("projectedValues").GetProperty("SenkouSpanA")));
        Assert.Equal(senkouA.Skip(candles.Count - 50).Take(50).ToArray(), Values(ichimoku.GetProperty("series").GetProperty("SenkouSpanA")));
        Assert.False(ichimoku.GetProperty("projectedValues").TryGetProperty("TenkanSen", out _));
    }

    [Fact]
    public async Task AnIndicatorFedFromANonCausalSeriesIsFlaggedNonCausalToo()
    {
        using var response = await CallAsync(await StartAsync(),
            Args("""[{"id":"i","type":"Ichimoku"},{"id":"s","type":"SMA","parameters":{"Period":3},"sourceIndicatorId":"i","sourceOutputSeriesName":"ChikouSpan"},{"id":"t","type":"SMA","parameters":{"Period":3},"sourceIndicatorId":"i","sourceOutputSeriesName":"ChikouSpan"}]"""));

        Assert.False(McpTestSupport.IsError(response));
        Assert.Equal(new[] { "SMA" }, Result(response, "s").GetProperty("nonCausalSeries").EnumerateArray().Select(n => n.GetString()).ToArray());
    }

    // ---- indicator on indicator -----------------------------------------------------------------------------------

    [Fact]
    public async Task AChainedIndicatorEqualsADirectChainedCall()
    {
        CoreIndicatorSettings rsi = Default(IndicatorType.RSI), sma = Default(IndicatorType.SMA);
        ((StockAnalyzer.Core.Models.Parameters.CoreSmaParameter)sma.ParameterObject!).Period = 5;
        sma.SourceIndicatorId = rsi.Id;
        var (_, direct) = await DirectAsync("MSFT", TimeFrame.D1, rsi, sma);

        using var response = await CallAsync(await StartAsync(),
            Args("""[{"id":"smaOfRsi","type":"SMA","parameters":{"Period":5},"sourceIndicatorId":"rsi"},{"id":"rsi","type":"RSI"}]"""));

        Assert.Equal(direct[sma.Id].MainValues.TakeLast(50).ToArray(), Values(Result(response, "smaOfRsi").GetProperty("series").GetProperty("SMA")));
        Assert.True(Values(Result(response, "smaOfRsi").GetProperty("series").GetProperty("SMA")).All(v => v is null or < 100m)); // RSI scale, not price scale
    }

    [Fact]
    public async Task AChainedSeriesIsChosenByItsDisplayName()
    {
        CoreIndicatorSettings macd = Default(IndicatorType.MACD), sma = Default(IndicatorType.SMA);
        macd.OutputSeriesName = IndicatorOutputSeriesResolver.Normalize(IndicatorType.MACD, "Signal", macd.PriceSource, Factory);
        sma.SourceIndicatorId = macd.Id;
        var (_, direct) = await DirectAsync("MSFT", TimeFrame.D1, macd, sma);

        using var response = await CallAsync(await StartAsync(),
            Args("""[{"id":"m","type":"MACD"},{"id":"s","type":"SMA","sourceIndicatorId":"m","sourceOutputSeriesName":"signal"}]"""));

        Assert.Equal(direct[sma.Id].MainValues.TakeLast(50).ToArray(), Values(Result(response, "s").GetProperty("series").GetProperty("SMA")));
    }

    public static TheoryData<string, string, string> BadChains => new()
    {
        { """[{"id":"a","type":"SMA","sourceIndicatorId":"a"}]""", ToolErrorCodes.InvalidArgument, "indicators[0].sourceIndicatorId" },
        { """[{"id":"a","type":"SMA","sourceIndicatorId":"b"},{"id":"b","type":"SMA","sourceIndicatorId":"a"}]""", ToolErrorCodes.InvalidArgument, "indicators[0].sourceIndicatorId" },
        { """[{"id":"a","type":"SMA","dynamicPeriodIndicatorId":"b"},{"id":"b","type":"SMA","sourceIndicatorId":"a"}]""", ToolErrorCodes.InvalidArgument, "indicators[0].sourceIndicatorId" },
        { """[{"id":"a","type":"SMA","sourceIndicatorId":"nobody"}]""", ToolErrorCodes.InvalidArgument, "indicators[0].sourceIndicatorId" },
        { """[{"id":"a","type":"SMA","sourceOutputSeriesName":"Main"}]""", ToolErrorCodes.InvalidArgument, "indicators[0].sourceOutputSeriesName" },
        { """[{"id":"m","type":"MACD"},{"id":"a","type":"SMA","sourceIndicatorId":"m","sourceOutputSeriesName":"Nope"}]""", ToolErrorCodes.InvalidOutputSeries, "indicators[1].sourceOutputSeriesName" },
        { """[{"id":"m","type":"MACD"},{"id":"a","type":"SMA","sourceIndicatorId":"m","sourceOutputSeriesName":"Signal"},{"id":"b","type":"SMA","sourceIndicatorId":"m","sourceOutputSeriesName":"Histogram"}]""", ToolErrorCodes.InvalidArgument, "indicators[2].sourceOutputSeriesName" },
    };

    [Theory]
    [MemberData(nameof(BadChains))]
    public async Task BadReferencesAreRejectedBeforeTheCalculation(string indicatorsJson, string code, string parameter)
    {
        using var response = await CallAsync(await StartAsync(), Args(indicatorsJson));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, Param(response));
    }

    // ---- binder ---------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> BadParameters => new()
    {
        { """[{"id":"r","type":"RSI","parameters":{"Nope":1}}]""", "indicators[0].parameters.Nope" },
        { """[{"id":"r","type":"RSI","parameters":{"Period":0}}]""", "indicators[0].parameters.Period" },
        { """[{"id":"r","type":"RSI","parameters":{"Period":10001}}]""", "indicators[0].parameters.Period" },
        { """[{"id":"r","type":"RSI","parameters":{"Period":14.5}}]""", "indicators[0].parameters.Period" },
        { """[{"id":"r","type":"RSI","parameters":{"Period":"14"}}]""", "indicators[0].parameters.Period" },
        { """[{"id":"r","type":"RSI","parameters":{"Period":null}}]""", "indicators[0].parameters.Period" },
        { """[{"id":"r","type":"OBV","parameters":{"Period":1}}]""", "indicators[0].parameters.Period" },
    };

    [Theory]
    [MemberData(nameof(BadParameters))]
    public async Task AWrongParameterIsInvalidIndicatorParameterNamingIt(string indicatorsJson, string parameter)
    {
        using var response = await CallAsync(await StartAsync(), Args(indicatorsJson));

        Assert.Equal(ToolErrorCodes.InvalidIndicatorParameter, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, Param(response));
    }

    [Fact]
    public async Task AnEnumParameterTakesNamesOnly()
    {
        IndicatorCatalogEntry entry = Catalog.Entries.First(e => e.IsRegistered && !e.IsCrossTicker && e.Parameters.Any(p => p.Type == IndicatorParameterReader.EnumType));
        IndicatorParameterDescriptor enumParameter = entry.Parameters.First(p => p.Type == IndicatorParameterReader.EnumType);
        var dispatcher = await StartAsync();

        using var bad = await CallAsync(dispatcher, Args($$$"""[{"id":"x","type":"{{{entry.Type}}}","parameters":{"{{{enumParameter.Name}}}":"NotAValue"}}]"""));
        using var good = await CallAsync(dispatcher, Args($$$"""[{"id":"x","type":"{{{entry.Type}}}","parameters":{"{{{enumParameter.Name.ToLowerInvariant()}}}":"{{{enumParameter.AllowedValues![0].ToUpperInvariant()}}}"}}]"""));

        Assert.Equal(ToolErrorCodes.InvalidIndicatorParameter, McpTestSupport.ErrorCodeOf(bad));
        Assert.Contains(enumParameter.AllowedValues![0], McpTestSupport.Structured(bad).GetProperty("hint").GetString());
        Assert.False(McpTestSupport.IsError(good));
    }

    [Fact]
    public async Task EveryRegisteredNonCrossTickerIndicatorCalculatesWithItsDefaults()
    {
        var dispatcher = await StartAsync();
        string[] types = Catalog.Entries.Where(e => e.IsRegistered && !e.IsCrossTicker).Select(e => e.Type).ToArray();

        var failed = new List<string>();
        int succeeded = 0;
        foreach (string[] batch in types.Chunk(16))
        {
            string indicators = "[" + string.Join(",", batch.Select(t => $$$"""{"id":"{{{t}}}","type":"{{{t}}}"}""")) + "]";
            using var response = await CallAsync(dispatcher, Args(indicators, extra: ""","outputTailCount":1"""));
            Assert.False(McpTestSupport.IsError(response));
            foreach (string type in batch)
            {
                JsonElement result = Result(response, type);
                if (result.GetProperty("isSuccessful").GetBoolean())
                {
                    succeeded++;
                }
                else if (result.GetProperty("errorMessage").GetString() != CalculateIndicatorsTool.AllNullMessage)
                {
                    failed.Add($"{type}: {result.GetProperty("errorMessage").GetString()}");
                }
            }
        }

        // The synthetic fixture bars are flat zig-zags, so a few signal-type indicators legitimately report nothing (all null); any other failure is a defect.
        Assert.Empty(failed);
        Assert.True(succeeded > types.Length - 10, $"{succeeded} of {types.Length} produced values");
    }

    // ---- arguments, limits, symbols -------------------------------------------------------------------------------

    public static TheoryData<string, string, string> BadShapes => new()
    {
        { """{"symbol":"MSFT","timeFrame":"D1"}""", ToolErrorCodes.InvalidArgument, "indicators" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[]}""", ToolErrorCodes.InvalidArgument, "indicators" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"type":"RSI"}]}""", ToolErrorCodes.InvalidArgument, "indicators[0].id" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a"}]}""", ToolErrorCodes.InvalidArgument, "indicators[0].type" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"RSI","bogus":1}]}""", ToolErrorCodes.InvalidArgument, "indicators[0].bogus" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"RSI","parameters":5}]}""", ToolErrorCodes.InvalidArgument, "indicators[0].parameters" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"RSI","priceSource":"Nope"}]}""", ToolErrorCodes.InvalidArgument, "indicators[0].priceSource" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":["RSI"]}""", ToolErrorCodes.InvalidArgument, "indicators[0]" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":" ","type":"RSI"}]}""", ToolErrorCodes.InvalidArgument, "indicators[0].id" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"RSI"},{"id":"a","type":"SMA"}]}""", ToolErrorCodes.InvalidArgument, "indicators[1].id" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"RSI"}],"outputTailCount":0}""", ToolErrorCodes.InvalidArgument, "outputTailCount" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"RSI"}],"lookbackBars":10001}""", ToolErrorCodes.LimitExceeded, "lookbackBars" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"RSI"}],"outputTailCount":501}""", ToolErrorCodes.LimitExceeded, "outputTailCount" },
        { """{"symbol":"MSFT","timeFrame":"H1","indicators":[{"id":"a","type":"RSI"}]}""", ToolErrorCodes.UnsupportedTimeFrame, "timeFrame" },
        { """{"symbol":"NOPE","timeFrame":"D1","indicators":[{"id":"a","type":"RSI"}]}""", ToolErrorCodes.SymbolNotFound, "symbol" },
        { """{"symbol":"..\\x","timeFrame":"D1","indicators":[{"id":"a","type":"RSI"}]}""", ToolErrorCodes.InvalidArgument, "symbol" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"NotAnIndicator"}]}""", ToolErrorCodes.UnknownIndicator, "indicators[0].type" },
        { """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"a","type":"5"}]}""", ToolErrorCodes.UnknownIndicator, "indicators[0].type" },
    };

    [Theory]
    [MemberData(nameof(BadShapes))]
    public async Task BadArgumentsAreRejectedWithTheCatalogueCodeAndTheArgumentPath(string argumentsJson, string code, string parameter)
    {
        using var response = await CallAsync(await StartAsync(), argumentsJson);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, Param(response));
    }

    [Fact]
    public async Task ATypeDefinedButNotRegisteredIsUnknownIndicatorWithAHint()
    {
        string unregistered = Catalog.Entries.First(e => !e.IsRegistered).Type;

        using var response = await CallAsync(await StartAsync(), Args($$$"""[{"id":"a","type":"{{{unregistered}}}"}]"""));

        Assert.Equal(ToolErrorCodes.UnknownIndicator, McpTestSupport.ErrorCodeOf(response));
        Assert.Contains("cannot be calculated", McpTestSupport.Structured(response).GetProperty("message").GetString());
    }

    [Fact]
    public async Task MoreIndicatorsThanTheLimitAreRejected()
    {
        using var response = await CallAsync(await StartAsync(("MaxIndicatorsPerCall", "2")), Args("""[{"id":"a","type":"RSI"},{"id":"b","type":"SMA"},{"id":"c","type":"EMA"}]"""));

        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("indicators", Param(response));
    }

    [Fact]
    public async Task AResultLargerThanTheResponseLimitIsLimitExceeded()
    {
        string indicators = "[" + string.Join(",", Enumerable.Range(0, 16).Select(i => $$$"""{"id":"s{{{i}}}","type":"SMA","parameters":{"Period":{{{i + 2}}}}}""")) + "]";

        using var response = await CallAsync(await StartAsync(("MaxResponseBytes", "65536")), Args(indicators, extra: ""","outputTailCount":300"""));

        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
    }

    // ---- cross-ticker ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACrossTickerIndicatorLoadsAndAlignsItsComparisonSymbol()
    {
        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"aapl"}}]"""));

        Assert.False(McpTestSupport.IsError(response));
        JsonElement result = Result(response, "c");
        Assert.True(result.GetProperty("isSuccessful").GetBoolean(), result.GetRawText());
        Assert.Contains("AAPL", result.GetProperty("displayName").GetString());
        Assert.Contains(Values(result.GetProperty("series").EnumerateObject().First().Value), v => v is not null);
    }

    [Fact]
    public async Task ACrossTickerIndicatorWithoutAComparisonSymbolIsRejected()
    {
        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"c","type":"Correlation"}]"""));

        Assert.Equal(ToolErrorCodes.InvalidIndicatorParameter, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("indicators[0].parameters.ComparisonSymbol", Param(response));
    }

    [Fact]
    public async Task AnUnknownComparisonSymbolIsSymbolNotFoundAndTooManyAreLimitExceeded()
    {
        using var unknown = await CallAsync(await StartAsync(), Args("""[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"NOPE"}}]"""));
        using var tooMany = await CallAsync(await StartAsync(("MaxSecondarySymbolsPerCall", "1")),
            Args("""[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"aapl"}},{"id":"d","type":"Correlation","parameters":{"ComparisonSymbol":"7203.T"}}]"""));

        Assert.Equal(ToolErrorCodes.SymbolNotFound, McpTestSupport.ErrorCodeOf(unknown));
        Assert.Equal("indicators[0].parameters.ComparisonSymbol", Param(unknown));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(tooMany));
    }

    [Fact]
    public async Task ComparingASymbolWithItselfNeedsNoSecondaryLoad()
    {
        using var response = await CallAsync(await StartAsync(), Args("""[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"msft"}}]"""));

        Assert.True(Result(response, "c").GetProperty("isSuccessful").GetBoolean());
    }

    // ---- read-only guarantee and listing --------------------------------------------------------------------------

    [Fact]
    public async Task CallsNeverChangeTheDataTree()
    {
        IReadOnlyList<string> before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        (await CallAsync(dispatcher, Args("""[{"id":"r","type":"RSI"}]"""))).Dispose();
        (await CallAsync(dispatcher, Args("""[{"id":"r","type":"RSI"}]""", timeFrame: "MN1"))).Dispose();
        (await CallAsync(dispatcher, Args("""[{"id":"c","type":"Correlation","parameters":{"ComparisonSymbol":"aapl"}}]""", timeFrame: "W1"))).Dispose();
        (await CallAsync(dispatcher, Args("""[{"id":"r","type":"Nope"}]"""))).Dispose();

        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task TheToolIsListedWithAStrictNestedSchema()
    {
        var dispatcher = await StartAsync();

        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_calculate_indicators");
        JsonElement schema = tool.GetProperty("inputSchema");
        Assert.Equal(new[] { "symbol", "timeFrame", "indicators" }, schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
        JsonElement item = schema.GetProperty("properties").GetProperty("indicators").GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "id", "type" }, item.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.All(tool.GetProperty("description").GetString()!, c => Assert.True(c < 128, "descriptions are English (ASCII)"));
    }
}
