using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
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
        => await StartWithGatewayAsync(null, overrides);

    private async Task<McpMessageDispatcher> StartWithGatewayAsync(Func<IReadOnlyStockAnalyzerGateway, IReadOnlyStockAnalyzerGateway>? gatewayWrapper, params (string Key, string Value)[] overrides)
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
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()), gatewayWrapper: gatewayWrapper);
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
    public async Task ExecutedSleevesCarryRunStatus_AndSleevesWithoutDataDoNot_AndNoDiagnosticsAreReportedForANormalRun()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\",\"EMPTY\"]", extra: ",\"includeEquityCurve\":\"Weekly\""));

        JsonElement msft = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "MSFT");
        JsonElement empty = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "EMPTY");
        Assert.Equal("Completed", msft.GetProperty("runStatus").GetString());
        Assert.False(empty.TryGetProperty("runStatus", out _));
        Assert.False(result.TryGetProperty("diagnosticsUnavailable", out _));
        Assert.True(result.GetProperty("composite").TryGetProperty("equityCurve", out _));
    }

    [Fact]
    public async Task InsolventSleeve_StaysInTheCompositeAtItsFinalEquity_AndIsLabelledByRunStatus()
    {
        // AAPL: flat at 100, one rising day (entry signal), one more day at 100 (entry fills at about 99.8), then a drop to 1 that wipes out the sleeve:
        // 6000 shares at 99.8 on 500,000 of sleeve capital (initial margin 0.3) lose about 594,000 at the next Open.
        var closes = Enumerable.Repeat(100m, 400).ToArray();
        closes[300] = 101m;
        for (int i = 302; i < closes.Length; i++)
        {
            closes[i] = 1m;
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "AAPL", closes);
        var dispatcher = await StartAsync();
        const string account = ",\"account\":{\"initialCapital\":1000000,\"sizingModel\":\"FixedQuantity\",\"sizingParameter\":6000,\"initialMarginRatio\":0.3,\"maintenanceMarginRatio\":0.2}";

        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"AAPL\",\"MSFT\"]", extra: account));

        JsonElement aapl = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "AAPL");
        JsonElement msft = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "MSFT");
        Assert.Equal("Insolvent", aapl.GetProperty("runStatus").GetString());
        Assert.Equal("Succeeded", aapl.GetProperty("status").GetString()); // the existing vocabulary and meaning are unchanged
        Assert.Equal(0m, aapl.GetProperty("finalEquity").GetDecimal());
        Assert.Equal(2, result.GetProperty("succeededCount").GetInt32());
        // The lost sleeve is NOT replaced by flat cash: the composite is the insolvent final equity (0) plus the other sleeve.
        Assert.Equal(msft.GetProperty("finalEquity").GetDecimal(), result.GetProperty("composite").GetProperty("finalEquity").GetDecimal());
        Assert.Contains(result.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("went insolvent"));
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public async Task NotCompletedSleeve_BecomesStatusFailedAndFlatCash_WhileTheOtherSleeveRuns(RunStatus ended)
    {
        // The evaluation path never returns these two statuses through the MCP tools (an engine failure there is an exception), so the gateway is wrapped:
        // AAPL's artifact is rebuilt from the real one with the given run status.
        var dispatcher = await StartWithGatewayAsync(inner => StatusRewritingGateway.Create(inner, "AAPL", ended));
        const string account = ",\"account\":{\"initialCapital\":1000000}";

        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"AAPL\",\"MSFT\"]", extra: account));

        JsonElement aapl = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "AAPL");
        JsonElement msft = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "MSFT");
        Assert.Equal("Failed", aapl.GetProperty("status").GetString());
        Assert.Equal(ended.ToString(), aapl.GetProperty("runStatus").GetString());
        Assert.Contains(ended.ToString(), aapl.GetProperty("reason").GetString());
        Assert.Equal(500000m, aapl.GetProperty("finalEquity").GetDecimal()); // flat cash: the sleeve capital, neither hidden nor lost
        Assert.Equal("Completed", msft.GetProperty("runStatus").GetString());
        Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
        Assert.Equal(500000m + msft.GetProperty("finalEquity").GetDecimal(), result.GetProperty("composite").GetProperty("finalEquity").GetDecimal());
    }

    /// <summary>The real gateway behind a proxy: every call is forwarded; the batch backtest of one symbol comes back with another run status.</summary>
    public class StatusRewritingGateway : DispatchProxy
    {
        private IReadOnlyStockAnalyzerGateway _target = null!;
        private string _symbol = string.Empty;
        private RunStatus _status;

        public static IReadOnlyStockAnalyzerGateway Create(IReadOnlyStockAnalyzerGateway target, string symbol, RunStatus status)
        {
            var proxy = DispatchProxy.Create<IReadOnlyStockAnalyzerGateway, StatusRewritingGateway>();
            var self = (StatusRewritingGateway)(object)proxy;
            self._target = target;
            self._symbol = symbol;
            self._status = status;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                object? result = targetMethod!.Invoke(_target, args);
                bool isBatch = targetMethod.Name == nameof(IReadOnlyStockAnalyzerGateway.RunBacktestBatchAsync)
                    && targetMethod.ReturnType == typeof(Task<IReadOnlyList<BacktestEvaluationArtifact>>);
                return isBatch ? RewriteAsync((Task<IReadOnlyList<BacktestEvaluationArtifact>>)result!, (IReadOnlyList<BacktestRunSnapshot>)args![0]!) : result;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        private async Task<IReadOnlyList<BacktestEvaluationArtifact>> RewriteAsync(Task<IReadOnlyList<BacktestEvaluationArtifact>> batch, IReadOnlyList<BacktestRunSnapshot> snapshots)
        {
            IReadOnlyList<BacktestEvaluationArtifact> artifacts = await batch.ConfigureAwait(false);
            var rewritten = new List<BacktestEvaluationArtifact>(artifacts.Count);
            for (int i = 0; i < artifacts.Count; i++)
            {
                BacktestEvaluationArtifact artifact = artifacts[i];
                if (snapshots[i].Symbol != _symbol)
                {
                    rewritten.Add(artifact);
                    continue;
                }

                BacktestResult source = artifact.Result;
                var result = new BacktestResult(
                    source.Orders, source.Fills, source.Trades, source.EquityPoints, source.Signals, source.Configuration, _status,
                    source.StrategyName, source.ReproducibilityHash, source.IsInsufficientData, source.Warnings);
                rewritten.Add(new BacktestEvaluationArtifact(
                    result, null, artifact.RunFingerprint, artifact.Metadata, artifact.BootstrapDiagnostics, artifact.Sampling,
                    artifact.RatioDrawdown, artifact.AmountDrawdown, artifact.ReportIdentity));
            }

            return rewritten;
        }
    }


    // =========================================================================================
    // Engine arithmetic failure (owner decision D-8 = C): real engine, prices of 1e27 and 100000 shares overflow decimal at the first entry
    // =========================================================================================

    private const string HugeAccount = ",\"account\":{\"initialCapital\":1000000,\"sizingModel\":\"FixedQuantity\",\"sizingParameter\":100000}";

    private void SeedOverflowing(string symbol)
    {
        var closes = Enumerable.Repeat(1e27m, 400).ToArray();
        closes[300] = 1.01e27m; // one rising day: the entry signal; the entry fills on the next bar
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, symbol, closes);
    }

    private static string ErrorMessage(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("message").GetString()!;

    [Fact]
    public async Task OneOverflowingSymbol_IsAFailedSleeveThatSaysWhereAndTheOtherSleeveRuns()
    {
        SeedOverflowing("AAPL");
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"AAPL\",\"MSFT\"]", extra: HugeAccount));

        JsonElement aapl = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "AAPL");
        JsonElement msft = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "MSFT");
        Assert.Equal("Failed", aapl.GetProperty("status").GetString());
        Assert.Equal("Failed", aapl.GetProperty("runStatus").GetString());
        string reason = aapl.GetProperty("reason").GetString()!;
        Assert.Matches(@"^ArithmeticOverflow in \w+ at bar \d+ \(20\d\d-\d\d-\d\d\)$", reason);
        Assert.Equal(500000m, aapl.GetProperty("finalEquity").GetDecimal()); // flat cash, neither hidden nor lost
        Assert.Equal("Completed", msft.GetProperty("runStatus").GetString());
        Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
        Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
        Assert.Equal(500000m + msft.GetProperty("finalEquity").GetDecimal(), result.GetProperty("composite").GetProperty("finalEquity").GetDecimal());
    }

    [Fact]
    public async Task EverySymbolOverflowing_IsTheArithmeticErrorNamingTheFirstSymbolInRequestOrder()
    {
        SeedOverflowing("AAPL");
        SeedOverflowing("MSFT");
        var dispatcher = await StartAsync();

        JsonDocument response = await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\",\"AAPL\"]", extra: HugeAccount);

        Assert.Equal("BACKTEST_ARITHMETIC_OVERFLOW", ErrorCode(response));
        string message = ErrorMessage(response);
        Assert.StartsWith("2 of 2 symbols overflowed decimal arithmetic and none finished (first, in request order: MSFT, ArithmeticOverflow in ", message, StringComparison.Ordinal);
        Assert.DoesNotContain("had no data", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverflowingSymbolAndASymbolWithoutData_IsTheErrorBecauseNothingFinished()
    {
        SeedOverflowing("AAPL");
        var dispatcher = await StartAsync();

        JsonDocument response = await RunPortfolioAsync(dispatcher, symbols: "[\"AAPL\",\"EMPTY\"]", extra: HugeAccount);

        Assert.Equal("BACKTEST_ARITHMETIC_OVERFLOW", ErrorCode(response));
        Assert.EndsWith("1 had no data.", ErrorMessage(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverflowingSymbolNextToAFinishedOneAndOneWithoutData_Continues()
    {
        SeedOverflowing("AAPL");
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"AAPL\",\"MSFT\",\"EMPTY\"]", extra: HugeAccount));

        Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
        Assert.Equal(2, result.GetProperty("failedCount").GetInt32()); // the overflow and the symbol without data
    }

    [Fact]
    public async Task AnInsolventSleeveCountsAsFinished_SoAnOverflowNextToItIsNotAnError()
    {
        // MSFT: flat at 100, one rising day, then a crash to 1 that wipes the sleeve out (as in the insolvent-sleeve test); AAPL overflows.
        var closes = Enumerable.Repeat(100m, 400).ToArray();
        closes[300] = 101m;
        for (int i = 302; i < closes.Length; i++)
        {
            closes[i] = 1m;
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", closes);
        SeedOverflowing("AAPL");
        var dispatcher = await StartAsync();
        const string account = ",\"account\":{\"initialCapital\":1000000,\"sizingModel\":\"FixedQuantity\",\"sizingParameter\":6000,\"initialMarginRatio\":0.3,\"maintenanceMarginRatio\":0.2}";

        JsonElement result = Result(await RunPortfolioAsync(dispatcher, symbols: "[\"AAPL\",\"MSFT\"]", extra: account));

        JsonElement msft = result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "MSFT");
        Assert.Equal("Insolvent", msft.GetProperty("runStatus").GetString());
        Assert.Equal("Failed", result.GetProperty("symbolResults").EnumerateArray().First(s => s.GetProperty("symbol").GetString() == "AAPL").GetProperty("status").GetString());
    }

    [Fact]
    public async Task WithoutAnyOverflow_NothingChanges_AndNoSleeveIsFailed()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunPortfolioAsync(dispatcher));

        Assert.Equal(0, result.GetProperty("failedCount").GetInt32());
        Assert.All(result.GetProperty("symbolResults").EnumerateArray(), s => Assert.Equal("Completed", s.GetProperty("runStatus").GetString()));
    }

    [Fact]
    public async Task ASingleBacktestThatOverflows_IsTheArithmeticErrorNamingTheAccountSettings()
    {
        SeedOverflowing("AAPL");
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"AAPL","conditions":{{Conditions}}{{Period}}{{HugeAccount}}}"""))!;

        Assert.Equal("BACKTEST_ARITHMETIC_OVERFLOW", ErrorCode(response));
        string message = ErrorMessage(response);
        Assert.Contains("AAPL", message, StringComparison.Ordinal);
        Assert.Contains("sizingParameter 100000", message, StringComparison.Ordinal);
    }

    // An OverflowException that the engine's diagnostic entry point does not convert (a strategy's own, for example) still ends the call; the gateway is wrapped so the
    // batch throws one directly. Both branches of the attribution are pinned: an index that reached the tool names that symbol, a missing or invalid one names none.
    [Theory]
    [InlineData(1, "AAPL")] // symbols below are requested as [MSFT, AAPL]: index 1 is AAPL, not the first symbol
    [InlineData(0, "MSFT")]
    public async Task AnOverflowOutsideTheEngineWithAnIndex_NamesTheSymbolAtThatIndex(int index, string expectedSymbol)
    {
        var dispatcher = await StartWithGatewayAsync(inner => OverflowThrowingGateway.Create(inner, index));

        using JsonDocument response = await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\",\"AAPL\"]", extra: HugeAccount);

        Assert.Equal("BACKTEST_ARITHMETIC_OVERFLOW", ErrorCode(response));
        string message = ErrorMessage(response);
        Assert.StartsWith($"The backtest of {expectedSymbol} overflowed decimal arithmetic (account: initialCapital ", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)] // no tag at all
    [InlineData(-1)]
    [InlineData(2)] // two symbols: valid indexes are 0 and 1
    public async Task AnOverflowOutsideTheEngineWithoutAUsableIndex_NamesNoSymbolInsteadOfTheFirstOne(int? index)
    {
        var dispatcher = await StartWithGatewayAsync(inner => OverflowThrowingGateway.Create(inner, index));

        using JsonDocument response = await RunPortfolioAsync(dispatcher, symbols: "[\"MSFT\",\"AAPL\"]", extra: HugeAccount);

        Assert.Equal("BACKTEST_ARITHMETIC_OVERFLOW", ErrorCode(response));
        string message = ErrorMessage(response);
        Assert.StartsWith("The backtest of one of the symbols overflowed decimal arithmetic.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("MSFT", message, StringComparison.Ordinal);
        Assert.DoesNotContain("AAPL", message, StringComparison.Ordinal);
    }

    // The hint states the decimal limit from decimal.MaxValue in one fixed form: scientific notation, invariant culture, never the 29 digits.
    [Fact]
    public async Task TheOverflowHint_WritesTheDecimalLimitInOneFixedShortForm_WhateverTheCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // a comma as the decimal separator
            SeedOverflowing("AAPL");
            var dispatcher = await StartAsync();

            using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"AAPL","conditions":{{Conditions}}{{Period}}{{HugeAccount}}}"""))!;

            string hint = McpTestSupport.Structured(response).GetProperty("hint").GetString()!;
            Assert.Contains("above about 7.9e28.", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("79228162514264337593543950335", hint, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
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
