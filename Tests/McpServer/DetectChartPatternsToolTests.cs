using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class DetectChartPatternsToolTests : IDisposable
{
    private const int Days = 120;
    private const string Symbol = "PATTEST";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "pattest");
    private readonly List<McpServerRuntime> _runtimes = new();

    // Generate a multi-wave synthetic price series that triggers zigzag extrema and geometric/harmonic shapes
    private readonly List<decimal> _closes = GenerateSyntheticCloses(Days);

    private static List<decimal> GenerateSyntheticCloses(int count)
    {
        var list = new List<decimal>(count);
        decimal basePrice = 100m;
        for (int i = 0; i < count; i++)
        {
            // Compound wave to form swings
            double swing = Math.Sin(2.0 * Math.PI * i / 25.0) * 15.0 + Math.Cos(2.0 * Math.PI * i / 12.0) * 8.0;
            list.Add(Math.Max(10m, basePrice + (decimal)swing));
        }
        return list;
    }

    public DetectChartPatternsToolTests()
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
        => (await McpTestSupport.CallAsync(dispatcher, "sa_detect_chart_patterns", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

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
    public async Task DetectChartPatterns_ReturnsExpectedStructure()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher));

        Assert.Equal(Symbol, result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal(Days, result.GetProperty("evaluatedBars").GetInt32());
        Assert.True(result.TryGetProperty("patterns", out JsonElement patterns));
        Assert.True(result.TryGetProperty("excludedUnconfirmedCount", out _));
        Assert.True(result.TryGetProperty("truncated", out _));

        foreach (JsonElement p in patterns.EnumerateArray())
        {
            Assert.True(p.TryGetProperty("family", out _));
            Assert.True(p.TryGetProperty("name", out _));
            Assert.True(p.TryGetProperty("direction", out _));
            Assert.True(p.TryGetProperty("startDate", out _));
            Assert.True(p.TryGetProperty("endDate", out _));
            Assert.True(p.TryGetProperty("confirmedAtDate", out _));
            Assert.True(p.TryGetProperty("points", out JsonElement points));
            Assert.NotEmpty(points.EnumerateArray());
        }
    }

    [Fact]
    public async Task DetectChartPatterns_FamilyFiltering_OnlyIncludesRequestedFamilies()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"families\":[\"Harmonic\"]"));

        JsonElement patterns = result.GetProperty("patterns");
        foreach (JsonElement p in patterns.EnumerateArray())
        {
            Assert.Equal("Harmonic", p.GetProperty("family").GetString());
        }

        JsonElement geoResult = Result(await CallAsync(dispatcher, ",\"families\":[\"Geometric\"]"));
        foreach (JsonElement p in geoResult.GetProperty("patterns").EnumerateArray())
        {
            Assert.Equal("Geometric", p.GetProperty("family").GetString());
        }
    }

    [Fact]
    public async Task DetectChartPatterns_MutationTest_FutureBarsDoNotAffectPastAsOfDate()
    {
        var dispatcher = await StartAsync();

        // 1. Evaluate up to Day 80
        DateTime baseDate = new DateTime(2024, 1, 1);
        DateOnly asOfDate = DateOnly.FromDateTime(baseDate.AddDays(79));
        string asOfStr = asOfDate.ToString("yyyy-MM-dd");

        JsonElement beforeMutation = Result(await CallAsync(dispatcher, $",\"endDate\":\"{asOfStr}\""));

        // 2. Mutate future bars (days 80..119) drastically
        var mutatedCloses = new List<decimal>(_closes);
        for (int i = 80; i < Days; i++)
        {
            mutatedCloses[i] = _closes[i] * 3.5m;
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, mutatedCloses);

        // 3. Re-evaluate up to Day 80 on a new server instance
        var mutatedDispatcher = await StartAsync();
        JsonElement afterMutation = Result(await CallAsync(mutatedDispatcher, $",\"endDate\":\"{asOfStr}\""));

        Assert.Equal(beforeMutation.GetProperty("evaluatedBars").GetInt32(), afterMutation.GetProperty("evaluatedBars").GetInt32());
        Assert.Equal(beforeMutation.GetProperty("patterns").GetArrayLength(), afterMutation.GetProperty("patterns").GetArrayLength());
        Assert.Equal(beforeMutation.ToString(), afterMutation.ToString());
    }

    [Fact]
    public async Task DetectChartPatterns_Truncation_EnforcesMaxPatterns()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"maxPatterns\":1"));

        JsonElement patterns = result.GetProperty("patterns");
        Assert.True(patterns.GetArrayLength() <= 1);
    }

    [Fact]
    public async Task DetectChartPatterns_ArgumentValidation()
    {
        var dispatcher = await StartAsync();

        // Missing symbol
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_detect_chart_patterns", """{"timeFrame":"D1"}""")!));

        // Missing timeFrame
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_detect_chart_patterns", $"{{\"symbol\":\"{Symbol}\"}}")!));

        // Unknown family
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"families\":[\"InvalidFamily\"]")));

        // Duplicate family
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"families\":[\"Harmonic\",\"Harmonic\"]")));

        // maxPatterns < 1
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"maxPatterns\":0")));

        // geometricZigZagThreshold out of bounds
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"geometricZigZagThreshold\":0.05")));

        // 64-bit integer overflow on DTW min window
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"dtwMinWindow\":4294967320")));
    }

    [Fact]
    public async Task DetectChartPatterns_AllReturnedPatterns_AreConfirmedOnOrBeforeAsOfDate()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher));

        DateOnly asOfDate = DateOnly.Parse(result.GetProperty("asOfDate").GetString()!);
        int excludedUnconfirmed = result.GetProperty("excludedUnconfirmedCount").GetInt32();
        Assert.True(excludedUnconfirmed >= 0);

        JsonElement patterns = result.GetProperty("patterns");
        foreach (JsonElement p in patterns.EnumerateArray())
        {
            Assert.True(p.TryGetProperty("confirmedAtDate", out JsonElement confElement));
            string? confStr = confElement.GetString();
            Assert.NotNull(confStr);
            DateOnly confDate = DateOnly.Parse(confStr!);
            Assert.True(confDate <= asOfDate, $"Pattern confirmed at {confDate} is after asOfDate {asOfDate}");
        }
    }
}
