using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class AnalyzeCyclesToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "CYCTEST";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "cyctest", "shortcyc");
    private readonly List<McpServerRuntime> _runtimes = new();

    // Generate a strong synthetic cycle of period 10 (sin(2*pi*i / 10)) on top of a baseline of 100
    private readonly List<decimal> _closes = Enumerable.Range(0, Days)
        .Select(i => 100m + (decimal)(Math.Sin(2.0 * Math.PI * i / 10.0) * 15.0))
        .ToList();

    public AnalyzeCyclesToolTests()
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

        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = Symbol, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_analyze_cycles", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

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
    public async Task AnalyzeCycles_EqualsDirectFftSpectrumAnalysis()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"topBins\":5"));

        Assert.Equal(Symbol, result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal(Days, result.GetProperty("barCount").GetInt32());

        // Direct Core calculation
        double[] samples = _closes.Select(c => (double)c).ToArray();
        FftSpectrumResult expected = FftSpectrumAnalysis.CalculateSpectrum(samples);

        Assert.NotNull(expected.DominantBin);
        Assert.Equal(expected.DominantPeriod, result.GetProperty("dominantPeriodBars").GetDouble(), precision: 6);
        Assert.Equal(expected.DominantPower, result.GetProperty("dominantPower").GetDouble(), precision: 6);

        JsonElement bins = result.GetProperty("bins");
        var expectedTop5 = expected.Bins.OrderByDescending(b => b.Power).ThenBy(b => b.Period).Take(5).ToList();
        Assert.Equal(expectedTop5.Count, bins.GetArrayLength());

        for (int i = 0; i < expectedTop5.Count; i++)
        {
            Assert.Equal(expectedTop5[i].Period, bins[i].GetProperty("periodBars").GetDouble(), precision: 6);
            Assert.Equal(expectedTop5[i].Power, bins[i].GetProperty("power").GetDouble(), precision: 6);
        }
    }

    [Fact]
    public async Task AnalyzeCycles_MutationTest_FutureBarsDoNotAffectPastAsOfDate()
    {
        var dispatcher = await StartAsync();

        // 1. Evaluate up to Day 60
        DateTime baseDate = new DateTime(2024, 1, 1);
        DateOnly asOfDate = DateOnly.FromDateTime(baseDate.AddDays(59));
        string asOfStr = asOfDate.ToString("yyyy-MM-dd");

        JsonElement beforeMutation = Result(await CallAsync(dispatcher, $",\"endDate\":\"{asOfStr}\""));
        double periodBefore = beforeMutation.GetProperty("dominantPeriodBars").GetDouble();
        double powerBefore = beforeMutation.GetProperty("dominantPower").GetDouble();
        int countBefore = beforeMutation.GetProperty("barCount").GetInt32();

        // 2. Mutate future bars (days 60..99) by multiplying them by 5.0
        var mutatedCloses = new List<decimal>(_closes);
        for (int i = 60; i < Days; i++)
        {
            mutatedCloses[i] = _closes[i] * 5.0m;
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, mutatedCloses);

        // 3. Re-evaluate up to Day 60 on a new server instance
        var mutatedDispatcher = await StartAsync();
        JsonElement afterMutation = Result(await CallAsync(mutatedDispatcher, $",\"endDate\":\"{asOfStr}\""));

        Assert.Equal(countBefore, afterMutation.GetProperty("barCount").GetInt32());
        Assert.Equal(periodBefore, afterMutation.GetProperty("dominantPeriodBars").GetDouble(), precision: 6);
        Assert.Equal(powerBefore, afterMutation.GetProperty("dominantPower").GetDouble(), precision: 6);

        // Verify JSON string identical
        Assert.Equal(beforeMutation.ToString(), afterMutation.ToString());
    }

    [Fact]
    public async Task AnalyzeCycles_RangeBars_CalculatesOnSubset()
    {
        var dispatcher = await StartAsync();
        int range = 32;
        JsonElement result = Result(await CallAsync(dispatcher, $",\"rangeBars\":{range}"));

        Assert.Equal(range, result.GetProperty("barCount").GetInt32());

        double[] last32Samples = _closes.Skip(_closes.Count - range).Select(c => (double)c).ToArray();
        FftSpectrumResult expected = FftSpectrumAnalysis.CalculateSpectrum(last32Samples);

        Assert.Equal(expected.DominantPeriod, result.GetProperty("dominantPeriodBars").GetDouble(), precision: 6);
        Assert.Equal(expected.DominantPower, result.GetProperty("dominantPower").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task AnalyzeCycles_InsufficientSamples_ReturnsNullDominantAndEmptyBins()
    {
        string shortSymbol = "SHORTCYC";
        var shortCloses = new List<decimal> { 100m, 102m }; // Only 2 bars (< 4 min sample count)
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, shortSymbol, shortCloses);

        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, symbol: shortSymbol));

        Assert.Equal(2, result.GetProperty("barCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("dominantPeriodBars").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("dominantPower").ValueKind);
        Assert.Empty(result.GetProperty("bins").EnumerateArray());
    }

    [Fact]
    public async Task AnalyzeCycles_ArgumentValidation()
    {
        var dispatcher = await StartAsync();

        // Missing symbol
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_analyze_cycles", """{"timeFrame":"D1"}""")!));

        // Missing timeFrame
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_analyze_cycles", $"{{\"symbol\":\"{Symbol}\"}}")!));

        // rangeBars < 2
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"rangeBars\":1")));

        // rangeBars > lookbackBars
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"lookbackBars\":10,\"rangeBars\":20")));

        // topBins < 1
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"topBins\":0")));
    }
}
