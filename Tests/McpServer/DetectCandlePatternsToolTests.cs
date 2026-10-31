using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class DetectCandlePatternsToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "CPTEST";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "cptest");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + (i % 2 == 0 ? 1.0m : -0.5m)).ToList();

    public DetectCandlePatternsToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, _closes);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private List<CandleData> Candles()
    {
        var date = new DateTime(2024, 1, 1);
        return _closes.Select((close, i) => new CandleData(date.AddDays(i), close - 0.2m, close + 0.5m, close - 0.5m, close, 1000)).ToList();
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

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = Symbol, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_detect_candle_patterns", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

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
    public async Task DetectCandlePatterns_EqualsDirectCandlePatternDetector()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"windowBars\":1"));

        Assert.Equal("CPTEST", result.GetProperty("symbol").GetString());
        Assert.Equal(Days, result.GetProperty("barCount").GetInt32());

        // Direct Core detector calculation
        var candles = Candles();
        var expectedPatterns = CandlePatternDetector.DetectPatterns(candles, excludePatternBarsFromContext: false, requireTrendContext: false)
            .Where(p => p != CandlePatternType.None)
            .Select(p => p.ToString())
            .ToArray();

        string[] latestPatterns = result.GetProperty("latestPatterns").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(expectedPatterns, latestPatterns);
    }

    [Fact]
    public async Task DetectCandlePatterns_WindowBars_ScansTrailingWindow()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"windowBars\":10"));

        Assert.Equal(10, result.GetProperty("windowBars").GetInt32());
        JsonElement[] patterns = result.GetProperty("patterns").EnumerateArray().ToArray();

        // Index of matches must stay within trailing 10 bars
        foreach (var p in patterns)
        {
            int idx = p.GetProperty("index").GetInt32();
            Assert.True(idx >= Days - 10 && idx < Days);
        }
    }

    [Fact]
    public async Task DetectCandlePatterns_MutationTest_FutureBarsDoNotAffectHistoricalPatterns()
    {
        var candles = Candles();
        var cutIndex = 50;
        var endDate = candles[cutIndex].Timestamp.Date.ToString("yyyy-MM-dd");

        var dispatcher = await StartAsync();
        JsonElement resultBefore = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"windowBars\":5"));

        // Mutate bars strictly after endDate
        var modifiedCloses = new List<decimal>(_closes);
        for (int i = cutIndex + 1; i < modifiedCloses.Count; i++)
        {
            modifiedCloses[i] = 9999m + i;
        }
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, modifiedCloses);

        JsonElement resultAfter = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"windowBars\":5"));

        JsonElement[] patternsBefore = resultBefore.GetProperty("patterns").EnumerateArray().ToArray();
        JsonElement[] patternsAfter = resultAfter.GetProperty("patterns").EnumerateArray().ToArray();

        Assert.Equal(patternsBefore.Length, patternsAfter.Length);
        for (int i = 0; i < patternsBefore.Length; i++)
        {
            Assert.Equal(patternsBefore[i].GetProperty("index").GetInt32(), patternsAfter[i].GetProperty("index").GetInt32());
            Assert.Equal(patternsBefore[i].GetProperty("pattern").GetString(), patternsAfter[i].GetProperty("pattern").GetString());
        }
    }

    [Fact]
    public async Task DetectCandlePatterns_InvalidArguments_ThrowsToolException()
    {
        var dispatcher = await StartAsync();

        // windowBars < 1
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"windowBars\":0")));
        // windowBars > max (5000)
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"windowBars\":5001")));
        // timeFrame required
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_detect_candle_patterns", $"{{\"symbol\":\"{Symbol}\"}}")!));
    }
}
