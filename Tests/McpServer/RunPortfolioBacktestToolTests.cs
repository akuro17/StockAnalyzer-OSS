using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class RunPortfolioBacktestToolTests : IDisposable
{
    private const int Days = 400;
    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Exit = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}""";
    private const string Period = ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-12-31\"";
    private static readonly string Conditions = $$"""{"entryLong":{{Entry}},"exitLong":{{Exit}}}""";

    private readonly ReadOnlyDataTreeFixture _fixture;
    private readonly List<McpServerRuntime> _runtimes = new();

    public RunPortfolioBacktestToolTests()
    {
        _fixture = ReadOnlyDataTreeFixture.Create(Days, "MSFT", "AAPL", "EMPTY");

        // Seed MSFT with sine wave closes
        decimal[] msftCloses = Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 6.0)), 2)).ToArray();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", msftCloses);

        // Seed AAPL with cosine wave closes
        decimal[] aaplCloses = Enumerable.Range(0, Days).Select(i => Math.Round(150m + (decimal)(15 * Math.Cos(i / 8.0)), 2)).ToArray();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "AAPL", aaplCloses);

        // "EMPTY" ticker list entry is left without a parquet file so it has 0 bars
        string emptyParquet = Path.Combine(_fixture.DailyDirectory, "EMPTY.parquet");
        if (File.Exists(emptyParquet))
        {
            File.Delete(emptyParquet);
        }
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }
        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
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
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RunPortfolioAsync(McpMessageDispatcher dispatcher, string extra = "", string symbols = "[\"MSFT\",\"AAPL\"]")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest", $$"""{"symbols":{{symbols}},"conditions":{{Conditions}}{{Period}}{{extra}}}"""))!;

    private static async Task<JsonDocument> RunSingleBacktestAsync(McpMessageDispatcher dispatcher, string symbol, decimal capital, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"{{symbol}}","conditions":{{Conditions}}{{Period}},"account":{"initialCapital":{{capital}}}{{extra}}}"""))!;

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

    // =========================================================================================
    // Proof (a): Individual sleeve results match standalone sa_run_backtest down to runFingerprint
    // =========================================================================================

    [Fact]
    public async Task ProofA_EachSleeveResultMatchesStandaloneRunBacktestDownToFingerprint()
    {
        var dispatcher = await StartAsync();
        const decimal totalCapital = 1000000m;
        const decimal sleeveCapital = 500000m; // 1,000,000 / 2

        // Run portfolio backtest on MSFT and AAPL
        JsonElement portfolioResult = Result(await RunPortfolioAsync(dispatcher, extra: $",\"account\":{{\"initialCapital\":{totalCapital}}}"));

        Assert.Equal("IndependentSleeves", portfolioResult.GetProperty("method").GetString());
        Assert.Equal("Equal", portfolioResult.GetProperty("allocation").GetString());
        Assert.Equal(2, portfolioResult.GetProperty("symbolCount").GetInt32());
        Assert.Equal(2, portfolioResult.GetProperty("succeededCount").GetInt32());
        Assert.Equal(0, portfolioResult.GetProperty("failedCount").GetInt32());
        Assert.Equal(totalCapital, portfolioResult.GetProperty("allocatedTotal").GetDecimal());

        JsonElement symbolResults = portfolioResult.GetProperty("symbolResults");
        Assert.Equal(2, symbolResults.GetArrayLength());

        // Find MSFT in symbolResults
        JsonElement msftSleeve = symbolResults.EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "MSFT");
        Assert.Equal("Succeeded", msftSleeve.GetProperty("status").GetString());
        Assert.Equal(sleeveCapital, msftSleeve.GetProperty("sleeveCapital").GetDecimal());

        // Run standalone backtest for MSFT with sleeveCapital
        JsonElement msftStandalone = Result(await RunSingleBacktestAsync(dispatcher, "MSFT", sleeveCapital));
        JsonElement standaloneReport = msftStandalone.GetProperty("report");

        // Verify metric equivalence
        Assert.Equal(msftStandalone.GetProperty("equity").GetProperty("final").GetDecimal(), msftSleeve.GetProperty("finalEquity").GetDecimal());
        Assert.Equal(standaloneReport.GetProperty("totalTrades").GetInt32(), msftSleeve.GetProperty("totalTrades").GetInt32());
        Assert.Equal(standaloneReport.GetProperty("totalReturn").GetProperty("value").GetDecimal(), msftSleeve.GetProperty("totalReturn").GetProperty("value").GetDecimal());
        Assert.Equal(standaloneReport.GetProperty("winRate").GetProperty("value").GetDecimal(), msftSleeve.GetProperty("winRate").GetProperty("value").GetDecimal());
        Assert.Equal(standaloneReport.GetProperty("profitFactor").GetProperty("status").GetString(), msftSleeve.GetProperty("profitFactor").GetProperty("status").GetString());
        if (standaloneReport.GetProperty("profitFactor").GetProperty("value").ValueKind == JsonValueKind.Number)
        {
            Assert.Equal(standaloneReport.GetProperty("profitFactor").GetProperty("value").GetDecimal(), msftSleeve.GetProperty("profitFactor").GetProperty("value").GetDecimal());
        }
        Assert.Equal(standaloneReport.GetProperty("maxDrawdown").GetProperty("value").GetDecimal(), msftSleeve.GetProperty("maxDrawdown").GetProperty("value").GetDecimal());

        // Verify runFingerprint equivalence
        JsonElement sleeveFingerprint = msftSleeve.GetProperty("runFingerprint");
        JsonElement standaloneFingerprint = msftStandalone.GetProperty("runFingerprint");
        Assert.True(sleeveFingerprint.GetProperty("isAvailable").GetBoolean());
        Assert.True(standaloneFingerprint.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(standaloneFingerprint.GetProperty("sha256").GetString(), sleeveFingerprint.GetProperty("sha256").GetString());
        Assert.Equal(standaloneFingerprint.GetProperty("schemaVersion").GetInt32(), sleeveFingerprint.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(standaloneFingerprint.GetProperty("executionSemanticsVersion").GetInt32(), sleeveFingerprint.GetProperty("executionSemanticsVersion").GetInt32());
    }

    // =========================================================================================
    // Proof (b): 1-symbol portfolio equals standalone run
    // =========================================================================================

    [Fact]
    public async Task ProofB_OneSymbolPortfolioMatchesStandaloneBacktestExactly()
    {
        var dispatcher = await StartAsync();
        const decimal capital = 500000m;

        JsonElement portfolioResult = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\"]", extra: $",\"account\":{{\"initialCapital\":{capital}}}"));
        JsonElement standaloneResult = Result(await RunSingleBacktestAsync(dispatcher, "MSFT", capital));

        JsonElement composite = portfolioResult.GetProperty("composite");
        JsonElement standaloneReport = standaloneResult.GetProperty("report");

        Assert.Equal(1, portfolioResult.GetProperty("symbolCount").GetInt32());
        Assert.Equal(1, portfolioResult.GetProperty("succeededCount").GetInt32());
        Assert.Equal(capital, composite.GetProperty("allocatedTotal").GetDecimal());
        Assert.Equal(standaloneResult.GetProperty("equity").GetProperty("final").GetDecimal(), composite.GetProperty("finalEquity").GetDecimal());
        Assert.Equal(standaloneReport.GetProperty("totalReturn").GetProperty("value").GetDecimal(), composite.GetProperty("totalReturn").GetDecimal());
        Assert.Equal((double)standaloneReport.GetProperty("maxDrawdown").GetProperty("value").GetDecimal(), (double)composite.GetProperty("maxDrawdownRatio").GetDecimal(), precision: 6);
        Assert.Equal(standaloneReport.GetProperty("totalTrades").GetInt32(), composite.GetProperty("pooledTradeCount").GetInt32());
        Assert.Equal(standaloneReport.GetProperty("winRate").GetProperty("value").GetDecimal(), composite.GetProperty("pooledWinRate").GetDecimal());

        if (composite.TryGetProperty("worstEpisode", out JsonElement worstEp) && worstEp.ValueKind == JsonValueKind.Object)
        {
            Assert.True(worstEp.TryGetProperty("peakDate", out _));
            Assert.True(worstEp.TryGetProperty("troughDate", out _));
            Assert.True(worstEp.TryGetProperty("depthRatio", out _));
            Assert.True(worstEp.TryGetProperty("peakEquity", out _));
            Assert.True(worstEp.TryGetProperty("troughEquity", out _));
        }
    }

    // =========================================================================================
    // Proof (d): Output order and values do not depend on parallelism
    // =========================================================================================

    [Fact]
    public async Task ProofD_OutputOrderAndValuesDoNotDependOnParallelism()
    {
        var dispatcher1 = await StartAsync(("OptimizationDegreeOfParallelism", "1"));
        var dispatcher4 = await StartAsync(("OptimizationDegreeOfParallelism", "4"));

        JsonDocument doc1 = await RunPortfolioAsync(dispatcher1, extra: ",\"account\":{\"initialCapital\":1000000}");
        JsonDocument doc4 = await RunPortfolioAsync(dispatcher4, extra: ",\"account\":{\"initialCapital\":1000000}");

        string json1 = McpTestSupport.Structured(doc1).ToString();
        string json4 = McpTestSupport.Structured(doc4).ToString();

        Assert.Equal(json1, json4);
    }

    // =========================================================================================
    // D-11b: Failed sleeve remains as cash without reallocation
    // =========================================================================================

    [Fact]
    public async Task FailedSleeveTreatedAsCashWithoutReallocation_D11b()
    {
        var dispatcher = await StartAsync();
        const decimal totalCapital = 1000000m;

        // "EMPTY" has no stored parquet data
        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\",\"EMPTY\"]", extra: $",\"account\":{{\"initialCapital\":{totalCapital}}}"));

        Assert.Equal(2, result.GetProperty("symbolCount").GetInt32());
        Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
        Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
        Assert.Equal(totalCapital, result.GetProperty("allocatedTotal").GetDecimal());

        JsonElement symbolResults = result.GetProperty("symbolResults");
        JsonElement emptySleeve = symbolResults.EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "EMPTY");
        Assert.Equal("InsufficientData", emptySleeve.GetProperty("status").GetString());
        Assert.Equal(500000m, emptySleeve.GetProperty("sleeveCapital").GetDecimal());
        Assert.Equal(500000m, emptySleeve.GetProperty("finalEquity").GetDecimal());
        Assert.NotNull(emptySleeve.GetProperty("reason").GetString());

        JsonElement msftSleeve = symbolResults.EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "MSFT");
        Assert.Equal("Succeeded", msftSleeve.GetProperty("status").GetString());

        // Composite final equity = MSFT final equity + 500,000 cash
        decimal expectedCompositeFinal = msftSleeve.GetProperty("finalEquity").GetDecimal() + 500000m;
        Assert.Equal(expectedCompositeFinal, result.GetProperty("composite").GetProperty("finalEquity").GetDecimal());

        // Verify notes contain model limitation and failed sleeve cash note per D-11b
        JsonElement notes = result.GetProperty("notes");
        Assert.True(notes.GetArrayLength() >= 3);
        Assert.Contains(notes.EnumerateArray(), n => n.GetString()!.Contains("Independent sleeves model"));
        Assert.Contains(notes.EnumerateArray(), n => n.GetString()!.Contains("failed or had insufficient data; their allocated capital remains as cash"));
    }

    // =========================================================================================
    // All sleeves failed
    // =========================================================================================

    [Fact]
    public async Task AllSleevesFailed_ProducesZeroReturnAndAllocatedTotal()
    {
        var dispatcher = await StartAsync();
        const decimal totalCapital = 500000m;

        // EMPTY has no data
        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"EMPTY\"]", extra: $",\"account\":{{\"initialCapital\":{totalCapital}}}"));

        Assert.Equal(1, result.GetProperty("symbolCount").GetInt32());
        Assert.Equal(0, result.GetProperty("succeededCount").GetInt32());
        Assert.Equal(1, result.GetProperty("failedCount").GetInt32());

        JsonElement composite = result.GetProperty("composite");
        Assert.Equal(totalCapital, composite.GetProperty("finalEquity").GetDecimal());
        Assert.Equal(0m, composite.GetProperty("totalReturn").GetDecimal());
        Assert.Equal(0m, composite.GetProperty("maxDrawdownRatio").GetDecimal());
        Assert.Equal(JsonValueKind.Null, composite.GetProperty("worstEpisode").ValueKind);
        Assert.Equal(0, composite.GetProperty("pooledTradeCount").GetInt32());
    }

    // =========================================================================================
    // Equity curve sampling
    // =========================================================================================

    [Fact]
    public async Task EquityCurveSamplingWeekly_EmitsSampledCurve()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await RunPortfolioAsync(dispatcher, extra: ",\"includeEquityCurve\":\"Weekly\""));

        JsonElement composite = result.GetProperty("composite");
        Assert.True(composite.TryGetProperty("equityCurve", out JsonElement curve));
        Assert.Equal("Weekly", curve.GetProperty("resolution").GetString());
        Assert.True(curve.GetProperty("pointCount").GetInt32() > 0);

        JsonElement points = curve.GetProperty("points");
        Assert.True(points.GetArrayLength() > 0);

        JsonElement firstPoint = points[0];
        Assert.True(firstPoint.TryGetProperty("date", out _));
        Assert.True(firstPoint.TryGetProperty("equity", out _));
        Assert.True(firstPoint.TryGetProperty("drawdownRatio", out _));
    }

    [Fact]
    public async Task AllowUnregistered_EmitsOffListWarningInNotes()
    {
        var dispatcher = await StartAsync();
        decimal[] closes = Enumerable.Repeat(100m, 100).ToArray();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "OFFLIST", closes);

        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\",\"OFFLIST\"]", extra: ",\"allowUnregistered\":true"));

        Assert.Equal(2, result.GetProperty("symbolCount").GetInt32());
        JsonElement notes = result.GetProperty("notes");
        Assert.Contains(notes.EnumerateArray(), n => n.GetString()!.Contains("not in the ticker list and were used"));
    }

    // =========================================================================================
    // Argument validation
    // =========================================================================================

    [Fact]
    public async Task MaxConcurrentPositions_RejectsWithInvalidArgument()
    {
        var dispatcher = await StartAsync();
        JsonDocument doc = await RunPortfolioAsync(dispatcher, extra: ",\"maxConcurrentPositions\":5");
        Assert.Equal(ToolErrorCodes.InvalidArgument, ErrorCode(doc));
    }

    [Fact]
    public async Task UnsupportedAllocation_RejectsWithInvalidArgument()
    {
        var dispatcher = await StartAsync();
        JsonDocument doc = await RunPortfolioAsync(dispatcher, extra: ",\"allocation\":\"MarketCap\"");
        Assert.Equal(ToolErrorCodes.InvalidArgument, ErrorCode(doc));
    }

    [Fact]
    public async Task MonthlyCurveOnWeeklyBars_RejectsWithUnsupportedTimeFrame()
    {
        var dispatcher = await StartAsync();
        JsonDocument doc = await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\"]", extra: ",\"timeFrame\":\"W1\",\"includeEquityCurve\":\"Monthly\"");
        Assert.Equal(ToolErrorCodes.UnsupportedTimeFrame, ErrorCode(doc));
    }

    [Fact]
    public async Task SymbolsExceedingLimit_RejectsWithLimitExceeded()
    {
        var dispatcher = await StartAsync(("MaxPortfolioBacktestSymbols", "2"));
        JsonDocument doc = await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\",\"AAPL\",\"EMPTY\"]");
        Assert.Equal(ToolErrorCodes.LimitExceeded, ErrorCode(doc));
    }

    [Fact]
    public async Task MissingBothSymbolsAndGroupId_RejectsWithInvalidArgument()
    {
        var dispatcher = await StartAsync();
        JsonDocument doc = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest", "{\"conditions\": " + Conditions + "}"))!;
        Assert.Equal(ToolErrorCodes.InvalidArgument, ErrorCode(doc));
    }
}
