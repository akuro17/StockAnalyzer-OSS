using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class ProjectPricePathToolTests : IDisposable
{
    private const int Days = 80;
    private const string Symbol = "PROJTEST";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "projtest");
    private readonly List<McpServerRuntime> _runtimes = new();

    private readonly List<decimal> _closes = Enumerable.Range(0, Days)
        .Select(i => 100m + (i * 0.5m) + (decimal)(Math.Sin(2.0 * Math.PI * i / 12.0) * 5.0))
        .ToList();

    public ProjectPricePathToolTests()
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
        => (await McpTestSupport.CallAsync(dispatcher, "sa_project_price_path", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

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
    public async Task ProjectPricePath_Arima_MatchesDirectAnalysis()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"method\":\"Arima\",\"horizonBars\":10"));

        Assert.Equal(Symbol, result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal("Arima", result.GetProperty("method").GetString());
        Assert.Equal(10, result.GetProperty("horizonBars").GetInt32());
        Assert.Equal(McpDisclaimerConstants.ResearchNotAdvice, result.GetProperty("disclaimer").GetString());
        Assert.False(result.GetProperty("isEmpty").GetBoolean());

        var path = result.GetProperty("projectedPath");
        Assert.Equal(10, path.GetArrayLength());

        // Direct call
        var startDate = new DateTime(2024, 1, 1);
        var coreCandles = _closes.Select((c, i) => new CoreCandleData(startDate.AddDays(i), c - 0.2m, c + 0.5m, c - 0.5m, c, 1000)).ToList();
        var expected = ArimaProjectionAnalysis.CalculateProjection(
            coreCandles, 0, coreCandles.Count - 1,
            p: 1, d: 1, q: 1, futureSteps: 10, priceSource: PriceType.Close,
            timeframeSpan: TimeSpan.FromDays(1), showConfidenceBand: true, confidenceMultiplier: 2.0m);

        for (int k = 0; k < 10; k++)
        {
            var pt = path[k];
            Assert.Equal(k + 1, pt.GetProperty("barOffset").GetInt32());
            Assert.Equal(expected.ProjectedPoints[k + 1].Y, pt.GetProperty("value").GetDouble(), precision: 6);
            Assert.Equal(expected.LowerBandPoints[k + 1].Y, pt.GetProperty("lower").GetDouble(), precision: 6);
            Assert.Equal(expected.UpperBandPoints[k + 1].Y, pt.GetProperty("upper").GetDouble(), precision: 6);
        }
    }

    [Theory]
    [InlineData("Fft")]
    [InlineData("Ssa")]
    [InlineData("Hmm")]
    public async Task ProjectPricePath_AllMethods_SucceedAndContainDisclaimer(string method)
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, $",\"method\":\"{method}\",\"horizonBars\":5"));

        Assert.Equal(method, result.GetProperty("method").GetString());
        Assert.Equal(McpDisclaimerConstants.ResearchNotAdvice, result.GetProperty("disclaimer").GetString());
        Assert.False(result.GetProperty("isEmpty").GetBoolean());

        var path = result.GetProperty("projectedPath");
        Assert.Equal(5, path.GetArrayLength());
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, path[i].GetProperty("barOffset").GetInt32());
            Assert.True(path[i].TryGetProperty("value", out _));
            Assert.True(path[i].TryGetProperty("lower", out _));
            Assert.True(path[i].TryGetProperty("upper", out _));
        }
    }

    [Fact]
    public async Task ProjectPricePath_AsOfInvariance_MutationAfterEndDate_DoesNotAlterOutput()
    {
        var dispatcher1 = await StartAsync();
        // Day 50 cutoff: 2024-01-01 + 49 days = 2024-02-19
        string endDateStr = "2024-02-19";

        JsonElement res1 = Result(await CallAsync(dispatcher1, $",\"method\":\"Arima\",\"horizonBars\":10,\"endDate\":\"{endDateStr}\""));

        // Mutate closes after day 50 dramatically
        var mutatedCloses = new List<decimal>(_closes);
        for (int i = 50; i < Days; i++)
        {
            mutatedCloses[i] = 99999m;
        }
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, mutatedCloses);

        var dispatcher2 = await StartAsync();
        JsonElement res2 = Result(await CallAsync(dispatcher2, $",\"method\":\"Arima\",\"horizonBars\":10,\"endDate\":\"{endDateStr}\""));

        Assert.Equal(res1.ToString(), res2.ToString());
    }

    [Fact]
    public async Task ProjectPricePath_Validation()
    {
        var dispatcher = await StartAsync();

        // Unknown method
        string errMethod = ErrorCode(await CallAsync(dispatcher, ",\"method\":\"Unknown\""));
        Assert.Equal("INVALID_ARGUMENT", errMethod);

        // Missing method
        string errNoMethod = ErrorCode(await CallAsync(dispatcher, ""));
        Assert.Equal("INVALID_ARGUMENT", errNoMethod);

        // Horizon over limit (MaxProjectionBars is 100)
        string errHorizon = ErrorCode(await CallAsync(dispatcher, ",\"method\":\"Arima\",\"horizonBars\":101"));
        Assert.Equal("LIMIT_EXCEEDED", errHorizon);

        // arimaP out of range
        string errP = ErrorCode(await CallAsync(dispatcher, ",\"method\":\"Arima\",\"arimaP\":10"));
        Assert.Equal("INVALID_ARGUMENT", errP);

        // hmmStates out of range (states must be 2..3)
        string errHmm4 = ErrorCode(await CallAsync(dispatcher, ",\"method\":\"Hmm\",\"hmmStates\":4"));
        Assert.Equal("INVALID_ARGUMENT", errHmm4);

        // 64-bit integer overflow on arimaP
        string errOverflow = ErrorCode(await CallAsync(dispatcher, ",\"method\":\"Arima\",\"arimaP\":4294967320"));
        Assert.Equal("INVALID_ARGUMENT", errOverflow);
    }

    [Fact]
    public async Task ProjectPricePath_ArimaPZero_Succeeds()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"method\":\"Arima\",\"horizonBars\":5,\"arimaP\":0"));

        Assert.Equal("Arima", result.GetProperty("method").GetString());
        Assert.False(result.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(5, result.GetProperty("projectedPath").GetArrayLength());
    }
}
