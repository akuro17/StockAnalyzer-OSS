using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class AnalyzeMarketStructureToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "MSTEST";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "mstest");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes;

    public AnalyzeMarketStructureToolTests()
    {
        // Alternating wave with swings large enough to trigger ZigZag 5% reversals
        _closes = new List<decimal>(Days);
        decimal price = 100m;
        for (int i = 0; i < Days; i++)
        {
            int cycle = i % 20;
            if (cycle < 10) price += 2.0m;
            else price -= 2.0m;
            _closes.Add(price);
        }

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
        => (await McpTestSupport.CallAsync(dispatcher, "sa_analyze_market_structure", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

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
    public async Task MarketStructure_EqualsDirectCoreDetectCausalAndExtractConfirmedPivots()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"zigzagThresholdPercent\":3.0,\"outputTailCount\":100"));

        Assert.Equal("MSTEST", result.GetProperty("symbol").GetString());
        Assert.Equal(Days, result.GetProperty("barCount").GetInt32());

        var candles = Candles();
        var expectedPivots = MarketStructureDetector.ExtractConfirmedPivots(candles, 3.0m);
        var expectedShifts = MarketStructureDetector.DetectCausal(candles, 3.0m);

        JsonElement[] pivots = result.GetProperty("pivots").EnumerateArray().ToArray();
        Assert.Equal(expectedPivots.Count, pivots.Length);
        for (int i = 0; i < expectedPivots.Count; i++)
        {
            Assert.Equal(expectedPivots[i].Index, pivots[i].GetProperty("index").GetInt32());
            Assert.Equal(expectedPivots[i].Price, pivots[i].GetProperty("price").GetDecimal());
            Assert.Equal(expectedPivots[i].IsHigh, pivots[i].GetProperty("isHigh").GetBoolean());
            if (expectedPivots[i].ConfirmedAtIndex.HasValue)
            {
                Assert.Equal(expectedPivots[i].ConfirmedAtIndex.Value, pivots[i].GetProperty("confirmedAtIndex").GetInt32());
            }
        }

        JsonElement[] shifts = result.GetProperty("shifts").EnumerateArray().ToArray();
        Assert.Equal(expectedShifts.Count, shifts.Length);
        for (int i = 0; i < expectedShifts.Count; i++)
        {
            Assert.Equal(expectedShifts[i].Type.ToString(), shifts[i].GetProperty("type").GetString());
            Assert.Equal(expectedShifts[i].Index, shifts[i].GetProperty("index").GetInt32());
            Assert.Equal(expectedShifts[i].Price, shifts[i].GetProperty("price").GetDecimal());
        }

        Assert.Equal(5, result.GetProperty("leftStrength").GetInt32());
        Assert.Equal(5, result.GetProperty("rightStrength").GetInt32());
        Assert.Equal(expectedPivots.Count, result.GetProperty("pivotCount").GetInt32());
        Assert.Equal(expectedShifts.Count, result.GetProperty("shiftCount").GetInt32());
        Assert.Equal(pivots.Length, result.GetProperty("returnedPivotCount").GetInt32());
        Assert.Equal(shifts.Length, result.GetProperty("returnedShiftCount").GetInt32());

        if (expectedShifts.Count > 0)
        {
            var latest = result.GetProperty("latestShift");
            Assert.Equal(expectedShifts[^1].Type.ToString(), latest.GetProperty("type").GetString());
            Assert.Equal(expectedShifts[^1].Index, latest.GetProperty("index").GetInt32());
        }
    }

    [Fact]
    public async Task MarketStructure_MutationTest_FutureBarsDoNotAffectHistoricalShifts()
    {
        var candles = Candles();
        var cutIndex = 60;
        var endDate = candles[cutIndex].Timestamp.Date.ToString("yyyy-MM-dd");

        var dispatcher = await StartAsync();
        JsonElement resultBefore = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"zigzagThresholdPercent\":3.0"));

        // Mutate bars strictly after endDate
        var modifiedCloses = new List<decimal>(_closes);
        for (int i = cutIndex + 1; i < modifiedCloses.Count; i++)
        {
            modifiedCloses[i] = 9999m + i;
        }
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, modifiedCloses);

        JsonElement resultAfter = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"zigzagThresholdPercent\":3.0"));

        JsonElement[] shiftsBefore = resultBefore.GetProperty("shifts").EnumerateArray().ToArray();
        JsonElement[] shiftsAfter = resultAfter.GetProperty("shifts").EnumerateArray().ToArray();

        Assert.Equal(shiftsBefore.Length, shiftsAfter.Length);
        for (int i = 0; i < shiftsBefore.Length; i++)
        {
            Assert.Equal(shiftsBefore[i].GetProperty("type").GetString(), shiftsAfter[i].GetProperty("type").GetString());
            Assert.Equal(shiftsBefore[i].GetProperty("index").GetInt32(), shiftsAfter[i].GetProperty("index").GetInt32());
            Assert.Equal(shiftsBefore[i].GetProperty("price").GetDecimal(), shiftsAfter[i].GetProperty("price").GetDecimal());
        }
    }

    [Fact]
    public async Task MarketStructure_Causality_ConfirmedPivotReplicatedAtConfirmedBar()
    {
        var candles = Candles();
        var confirmedPivots = MarketStructureDetector.ExtractConfirmedPivots(candles, 3.0m);
        Assert.NotEmpty(confirmedPivots);

        // Pick the first confirmed pivot
        var firstPivot = confirmedPivots[0];
        Assert.True(firstPivot.ConfirmedAtIndex.HasValue);
        int confirmedIdx = firstPivot.ConfirmedAtIndex.Value;
        var confirmedDate = candles[confirmedIdx].Timestamp.Date.ToString("yyyy-MM-dd");

        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, $",\"endDate\":\"{confirmedDate}\",\"zigzagThresholdPercent\":3.0"));

        JsonElement[] pivots = result.GetProperty("pivots").EnumerateArray().ToArray();
        Assert.Contains(pivots, p => p.GetProperty("index").GetInt32() == firstPivot.Index && p.GetProperty("price").GetDecimal() == firstPivot.Price);
    }

    [Fact]
    public async Task MarketStructure_InvalidArguments_ThrowsToolException()
    {
        var dispatcher = await StartAsync();

        // zigzagThresholdPercent < 0.1
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"zigzagThresholdPercent\":0.0")));
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"zigzagThresholdPercent\":0.05")));
        // zigzagThresholdPercent > 50
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"zigzagThresholdPercent\":50.1")));
        // leftStrength < 1
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"leftStrength\":0")));
        // leftStrength > MaxCandlesPerRequest
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"leftStrength\":10001")));
        // rightStrength < 1
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"rightStrength\":0")));
        // rightStrength > MaxCandlesPerRequest
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"rightStrength\":10001")));
        // timeFrame required
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_analyze_market_structure", $"{{\"symbol\":\"{Symbol}\"}}")!));
    }
}
