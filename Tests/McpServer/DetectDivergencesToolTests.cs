using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class DetectDivergencesToolTests : IDisposable
{
    private const int Days = 300;
    private const string Period = ",\"startDate\":\"2024-03-01\",\"endDate\":\"2024-09-30\"";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "7203.T");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private IConfiguration Configuration(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in overrides)
        {
            values[key.Contains(':') ? key : $"AiIntegration:{key}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(overrides), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> DetectAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = "MSFT")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_detect_divergences", $$"""{"symbol":"{{symbol}}","timeFrame":"D1"{{extra}}}"""))!;

    private static JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string ErrorCode(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    [Fact]
    public async Task DetectDivergences_ReturnsCompleteStructure()
    {
        var dispatcher = await StartAsync();
        string extra = Period + ",\"indicator\":\"RSI\",\"pivotOrder\":3";

        JsonElement result = Result(await DetectAsync(dispatcher, extra));

        Assert.Equal("MSFT", result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.True(result.GetProperty("loadedBarCount").GetInt32() > 0);
        Assert.True(result.GetProperty("evaluatedBarCount").GetInt32() > 0);
        Assert.False(result.GetProperty("truncated").GetBoolean());

        JsonElement events = result.GetProperty("events");
        Assert.NotNull(events);
    }

    [Fact]
    public async Task ProofC_MatchesCoreDetectorDirectCallExactly()
    {
        var dispatcher = await StartAsync();
        int pivotOrder = 3;
        string extra = Period + $",\"indicator\":\"RSI\",\"pivotOrder\":{pivotOrder}";

        JsonElement result = Result(await DetectAsync(dispatcher, extra));

        // Load the same bars directly through the test database
        using var db = new DuckDBConnectionManager();
        var dataService = new ParquetDataService(db, Microsoft.Extensions.Options.Options.Create(_fixture.Settings));
        IReadOnlyList<CandleData> candles = await dataService.LoadCandlesAsync("MSFT", TimeFrame.D1, 0);

        // Filter up to 2024-09-30 (effectiveEnd)
        var endDate = new DateOnly(2024, 9, 30);
        var startDate = new DateOnly(2024, 3, 1);
        int cutIndex = -1;
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            if (DateOnly.FromDateTime(candles[i].Timestamp) <= endDate)
            {
                cutIndex = i;
                break;
            }
        }
        var loaded = candles.Take(cutIndex + 1).ToList();
        var coreCandles = loaded.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

        // Calculate RSI directly
        var rsi = IndicatorFactory.Default.Create(IndicatorType.RSI);
        var rsiResult = rsi.Calculate(coreCandles);

        var highs = loaded.Select(c => c.High).ToList();
        var lows = loaded.Select(c => c.Low).ToList();
        IReadOnlyList<DivergenceSignal> expectedDivergences = DivergenceCrossDetector.DetectDivergences(highs, lows, rsiResult.MainValues, pivotOrder);

        int startIndex = -1;
        for (int i = 0; i < loaded.Count; i++)
        {
            if (DateOnly.FromDateTime(loaded[i].Timestamp) >= startDate)
            {
                startIndex = i;
                break;
            }
        }

        var expectedFiltered = expectedDivergences.Where(d => d.PriceEndIndex >= startIndex && d.PriceEndIndex <= cutIndex).ToList();

        JsonElement events = result.GetProperty("events");
        var actualDivergences = events.EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "Divergence").ToList();

        Assert.Equal(expectedFiltered.Count, actualDivergences.Count);

        for (int i = 0; i < expectedFiltered.Count; i++)
        {
            DivergenceSignal exp = expectedFiltered[i];
            JsonElement act = actualDivergences[i];

            Assert.Equal(exp.Type.ToString(), act.GetProperty("signalType").GetString());
            Assert.Equal(exp.PriceStartIndex, act.GetProperty("priceStartIndex").GetInt32());
            Assert.Equal(exp.PriceEndIndex, act.GetProperty("priceEndIndex").GetInt32());
            Assert.Equal(exp.PriceStartValue, act.GetProperty("priceStartValue").GetDecimal());
            Assert.Equal(exp.PriceEndValue, act.GetProperty("priceEndValue").GetDecimal());
            Assert.Equal(exp.IndicatorStartIndex, act.GetProperty("indicatorStartIndex").GetInt32());
            Assert.Equal(exp.IndicatorEndIndex, act.GetProperty("indicatorEndIndex").GetInt32());
            Assert.Equal(exp.IndicatorStartValue, act.GetProperty("indicatorStartValue").GetDecimal());
            Assert.Equal(exp.IndicatorEndValue, act.GetProperty("indicatorEndValue").GetDecimal());
        }
    }

    [Fact]
    public async Task DetectCrosses_EmitsMovingAverageCrossoversCorrectly()
    {
        var dispatcher = await StartAsync();
        string extra = Period + ",\"detectCrosses\":true,\"shortMaPeriod\":5,\"longMaPeriod\":15";

        JsonElement result = Result(await DetectAsync(dispatcher, extra));
        JsonElement events = result.GetProperty("events");

        var crosses = events.EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "Cross").ToList();

        foreach (JsonElement c in crosses)
        {
            string signalType = c.GetProperty("signalType").GetString()!;
            Assert.True(signalType == "GoldenCross" || signalType == "DeadCross");
            Assert.True(c.GetProperty("crossIndex").GetInt32() > 0);
            Assert.NotNull(c.GetProperty("shortValue"));
            Assert.NotNull(c.GetProperty("longValue"));
        }
    }

    [Fact]
    public async Task InvalidArguments_FailGracefully()
    {
        var dispatcher = await StartAsync();

        // pivotOrder < 2
        JsonDocument badPivot = await DetectAsync(dispatcher, ",\"pivotOrder\":1");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(badPivot));

        // shortMaPeriod >= longMaPeriod (unconditional even when detectCrosses: false)
        JsonDocument badMaUnconditional = await DetectAsync(dispatcher, ",\"detectCrosses\":false,\"shortMaPeriod\":50,\"longMaPeriod\":20");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(badMaUnconditional));

        // Unknown indicator -> UNKNOWN_INDICATOR
        JsonDocument badInd = await DetectAsync(dispatcher, ",\"indicator\":\"NonExistentIndicator\"");
        Assert.Equal("UNKNOWN_INDICATOR", ErrorCode(badInd));

        // Unknown parameter -> INVALID_INDICATOR_PARAMETER
        JsonDocument badParam = await DetectAsync(dispatcher, ",\"indicator\":\"RSI\",\"indicatorParameters\":{\"NonExistentParam\":10}");
        Assert.Equal("INVALID_INDICATOR_PARAMETER", ErrorCode(badParam));

        // Non-causal indicator (e.g. ZigZag) -> INVALID_ARGUMENT
        JsonDocument nonCausal = await DetectAsync(dispatcher, ",\"indicator\":\"ZigZag\"");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(nonCausal));

        // startDate > endDate
        JsonDocument badDates = await DetectAsync(dispatcher, ",\"startDate\":\"2024-05-01\",\"endDate\":\"2024-04-01\"");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(badDates));

        // Unsupported timeFrame -> UNSUPPORTED_TIMEFRAME
        JsonDocument badFrame = (await McpTestSupport.CallAsync(dispatcher, "sa_detect_divergences", """{"symbol":"MSFT","timeFrame":"M5"}"""))!;
        Assert.Equal("UNSUPPORTED_TIMEFRAME", ErrorCode(badFrame));
    }

    [Fact]
    public async Task AllowUnregistered_EmitsWarnings_WhenOffList()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, "OFFLIST", Days);
        var dispatcher = await StartAsync();
        JsonDocument doc = (await McpTestSupport.CallAsync(dispatcher, "sa_detect_divergences", """{"symbol":"OFFLIST","timeFrame":"D1","allowUnregistered":true}"""))!;
        JsonElement result = Result(doc);
        Assert.True(result.TryGetProperty("warnings", out JsonElement warnings));
        Assert.True(warnings.GetArrayLength() > 0);
        Assert.Contains("not in the ticker list", warnings[0].GetString()!);
    }
}
