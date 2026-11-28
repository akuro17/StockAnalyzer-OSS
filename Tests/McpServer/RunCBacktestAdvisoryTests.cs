using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

public sealed class RunCBacktestAdvisoryTests : IDisposable
{
    private const int Days = 120;
    private const string Conditions =
        """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}}""";

    private const string NoEntryConditions =
        """{"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "aapl", "highpx", "lowpx");
    private readonly List<McpServerRuntime> _runtimes = new();

    public RunCBacktestAdvisoryTests()
    {
        List<decimal> aaplCloses = Enumerable.Range(0, Days)
            .Select(i => Math.Round(100m + (decimal)(10.0 * Math.Sin(i / 4.0)), 2))
            .ToList();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "AAPL", aaplCloses);

        List<decimal> highCloses = Enumerable.Range(0, Days)
            .Select(i => Math.Round(9000m + (decimal)(100.0 * Math.Sin(i / 4.0)), 2))
            .ToList();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "HIGHPX", highCloses);

        List<decimal> lowCloses = Enumerable.Range(0, Days)
            .Select(i => Math.Round(2000m + (decimal)(50.0 * Math.Sin(i / 4.0)), 2))
            .ToList();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "LOWPX", lowCloses);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartMcpAsync(params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static string[] ReadStringArray(JsonElement root, string property)
        => root.TryGetProperty(property, out JsonElement arr)
            ? arr.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();

    [Fact]
    public void Guards_ReturnNullAndDoNotThrow_WhenCapitalNonPositiveOrNoBars()
    {
        var limits = new McpLimits();
        var invalidConfig = new BacktestConfiguration { ExecutionModel = ExecutionModel.Legacy, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m };
        var largeQtyConfig = new BacktestConfiguration { ExecutionModel = ExecutionModel.Legacy, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 6000m };
        // BacktestConfiguration rejects <= 0 in setter, so verify no-bars / zero-trades / decimal-overflow guards directly.
        Assert.Null(BacktestAdvisory.ForSingleSizing(invalidConfig, highestClose: null, totalTrades: 1, sizingParameterExplicit: false, limits));
        Assert.Null(BacktestAdvisory.ForSingleSizing(invalidConfig, highestClose: 0m, totalTrades: 1, sizingParameterExplicit: false, limits));
        Assert.Null(BacktestAdvisory.ForSingleSizing(invalidConfig, highestClose: 100m, totalTrades: 0, sizingParameterExplicit: false, limits));
        Assert.Null(BacktestAdvisory.ForSingleSizing(largeQtyConfig, highestClose: decimal.MaxValue / 2m, totalTrades: 1, sizingParameterExplicit: true, limits));
        Assert.Null(BacktestAdvisory.ForPortfolioSizing(invalidConfig, Array.Empty<decimal>(), pooledTrades: 1, sizingParameterExplicit: false, limits));
        Assert.Null(BacktestAdvisory.ForPortfolioSizing(largeQtyConfig, new[] { decimal.MaxValue / 2m, 100m }, pooledTrades: 1, sizingParameterExplicit: true, limits));
        Assert.Null(BacktestAdvisory.ForSingleSampleSize(0, limits));
        Assert.Null(BacktestAdvisory.ForPortfolioSampleSize(Array.Empty<(string, int)>(), limits));
        Assert.Null(BacktestAdvisory.ForOptimizeSampleSize(Array.Empty<SweepRun>(), Array.Empty<SweepRun>(), limits));
        Assert.Null(BacktestAdvisory.ForWalkForwardSampleSize(Array.Empty<(int, int?, int?)>(), limits));
    }

    [Fact]
    public async Task AllFourBacktestTools_EmitSizingNotionalSmallForDefault1Share_AndRespectPercentOfEquityAndZeroTrades()
    {
        McpMessageDispatcher dispatcher = await StartMcpAsync();

        // 1) Default 1 share across all 4 tools -> SIZING_NOTIONAL_SMALL with "default fixed size of 1 share"
        using JsonDocument singleDef = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.False(McpTestSupport.IsError(singleDef), McpTestSupport.Structured(singleDef).ToString());
        string[] singleWarnings = ReadStringArray(McpTestSupport.Structured(singleDef), "warnings");
        Assert.Contains(singleWarnings, w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal) && w.Contains("default fixed size of 1 share", StringComparison.Ordinal));

        using JsonDocument portDef = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest",
            $$"""{"symbols":["AAPL","HIGHPX"],"conditions":{{Conditions}},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.False(McpTestSupport.IsError(portDef), McpTestSupport.Structured(portDef).ToString());
        JsonElement portStructured = McpTestSupport.Structured(portDef);
        Assert.False(portStructured.TryGetProperty("warnings", out _)); // CF-1: notes[] only
        string[] portNotes = ReadStringArray(portStructured, "notes");
        Assert.Contains(portNotes, w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal) && w.Contains("default fixed size of 1 share", StringComparison.Ordinal));
        Assert.Contains(portNotes, w => w.StartsWith("ZERO_FRICTION_ASSUMPTIONS:", StringComparison.Ordinal));
        Assert.Contains(portNotes, w => w.StartsWith("PRICE_LIMIT_ALWAYS_FILL:", StringComparison.Ordinal));
        Assert.Contains(portNotes, w => w.StartsWith("PRICE_RETURN_EXCLUDES_DIVIDENDS:", StringComparison.Ordinal));
        Assert.Contains(portNotes, w => w.StartsWith("SURVIVORSHIP_BIAS_UNIVERSE:", StringComparison.Ordinal));

        const string paramConditions =
            """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}}}""";

        using JsonDocument optDef = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"objective":"totalPnL","startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.False(McpTestSupport.IsError(optDef), McpTestSupport.Structured(optDef).ToString());
        string[] optWarnings = ReadStringArray(McpTestSupport.Structured(optDef), "warnings");
        Assert.Contains(optWarnings, w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal) && w.Contains("default fixed size of 1 share", StringComparison.Ordinal));
        Assert.Contains(optWarnings, w => w.StartsWith("ZERO_FRICTION_ASSUMPTIONS:", StringComparison.Ordinal));
        Assert.Contains(optWarnings, w => w.StartsWith("PRICE_LIMIT_ALWAYS_FILL:", StringComparison.Ordinal));
        Assert.Contains(optWarnings, w => w.StartsWith("PRICE_RETURN_EXCLUDES_DIVIDENDS:", StringComparison.Ordinal));
        Assert.Contains(optWarnings, w => w.StartsWith("SURVIVORSHIP_BIAS_UNIVERSE:", StringComparison.Ordinal));

        using JsonDocument wfDef = (await McpTestSupport.CallAsync(dispatcher, "sa_run_walk_forward_analysis",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"objective":"totalPnL","windows":{"generator":{"mode":"Rolling","inSampleBars":40,"outOfSampleBars":25,"stepBars":25} },"startDate":"2024-01-10","endDate":"2024-04-25"}"""))!;
        Assert.False(McpTestSupport.IsError(wfDef), McpTestSupport.Structured(wfDef).ToString());
        string[] wfWarnings = ReadStringArray(McpTestSupport.Structured(wfDef), "warnings");
        Assert.Contains(wfWarnings, w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal) && w.Contains("default fixed size of 1 share", StringComparison.Ordinal));
        Assert.Contains(wfWarnings, w => w.StartsWith("ZERO_FRICTION_ASSUMPTIONS:", StringComparison.Ordinal));
        Assert.Contains(wfWarnings, w => w.StartsWith("PRICE_LIMIT_ALWAYS_FILL:", StringComparison.Ordinal));
        Assert.Contains(wfWarnings, w => w.StartsWith("PRICE_RETURN_EXCLUDES_DIVIDENDS:", StringComparison.Ordinal));
        Assert.Contains(wfWarnings, w => w.StartsWith("SURVIVORSHIP_BIAS_UNIVERSE:", StringComparison.Ordinal));

        // 2) Explicit fixed size (e.g. 5 shares) -> mentions "fixed size of 5 share(s)"
        using JsonDocument singleExplicit = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"account":{"sizingModel":"FixedQuantity","sizingParameter":5},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(singleExplicit), "warnings"),
            w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal) && w.Contains("fixed size of 5 share(s)", StringComparison.Ordinal));

        // 3) PercentOfEquity -> NO SIZING_NOTIONAL_SMALL warning, while execution-realism & disclosure advisories apply when zero-cost / AlwaysFill
        using JsonDocument pctSingle = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"account":{"sizingModel":"PercentOfEquity","sizingParameter":0.2},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        string[] pctWarnings = ReadStringArray(McpTestSupport.Structured(pctSingle), "warnings");
        Assert.DoesNotContain(pctWarnings, w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));
        Assert.Contains(pctWarnings, w => w.StartsWith("ZERO_FRICTION_ASSUMPTIONS:", StringComparison.Ordinal));
        Assert.Contains(pctWarnings, w => w.StartsWith("PRICE_LIMIT_ALWAYS_FILL:", StringComparison.Ordinal));
        Assert.Contains(pctWarnings, w => w.StartsWith("PRICE_RETURN_EXCLUDES_DIVIDENDS:", StringComparison.Ordinal));
        Assert.Contains(pctWarnings, w => w.StartsWith("SURVIVORSHIP_BIAS_UNIVERSE:", StringComparison.Ordinal));

        // 3b) Explicit non-zero cost + RejectIfAtLimitPrice suppresses ZERO_FRICTION_ASSUMPTIONS and PRICE_LIMIT_ALWAYS_FILL
        using JsonDocument explicitSingle = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"account":{"sizingModel":"PercentOfEquity","sizingParameter":0.2,"commissionFlat":1.0,"priceLimitFillMode":"RejectIfAtLimitPrice"},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        string[] explicitWarnings = ReadStringArray(McpTestSupport.Structured(explicitSingle), "warnings");
        Assert.DoesNotContain(explicitWarnings, w => w.StartsWith("ZERO_FRICTION_ASSUMPTIONS:", StringComparison.Ordinal));
        Assert.DoesNotContain(explicitWarnings, w => w.StartsWith("PRICE_LIMIT_ALWAYS_FILL:", StringComparison.Ordinal));
        Assert.Contains(explicitWarnings, w => w.StartsWith("PRICE_RETURN_EXCLUDES_DIVIDENDS:", StringComparison.Ordinal));
        Assert.Contains(explicitWarnings, w => w.StartsWith("SURVIVORSHIP_BIAS_UNIVERSE:", StringComparison.Ordinal));

        // 4) Zero trades -> NO SIZING_NOTIONAL_SMALL warning
        using JsonDocument zeroTradeSingle = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{NoEntryConditions}},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        string[] zeroWarnings = ReadStringArray(McpTestSupport.Structured(zeroTradeSingle), "warnings");
        Assert.DoesNotContain(zeroWarnings, w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PortfolioSumRuleAndSweepPlaceholderMaxRule_FollowSpecExactly()
    {
        McpMessageDispatcher dispatcher = await StartMcpAsync();

        // Portfolio: HIGHPX (~9100) + AAPL (~110) = 9210 / 1,000,000 < 0.01 -> warns;
        // HIGHPX (~9100) + LOWPX (~2050) = 11150 / 1,000,000 >= 0.01 -> does NOT warn.
        using JsonDocument warnPort = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest",
            $$"""{"symbols":["AAPL","HIGHPX"],"conditions":{{Conditions}},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(warnPort), "notes"),
            n => n.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));

        using JsonDocument cleanPort = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest",
            $$"""{"symbols":["HIGHPX","LOWPX"],"conditions":{{Conditions}},"startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.DoesNotContain(ReadStringArray(McpTestSupport.Structured(cleanPort), "notes"),
            n => n.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));

        // Sweep with sizingParameter placeholder:
        // (a) qty in [5, 200] -> max notional = 200 * 110 / 1,000,000 = 2.2% >= 1% -> NO warning
        using JsonDocument cleanSweep = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"parameterRanges":[{"name":"qty","start":5,"end":200,"step":195}],"account":{"sizingModel":"FixedQuantity","sizingParameter":{"$param":"qty"} },"objective":"totalPnL","startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.False(McpTestSupport.IsError(cleanSweep), McpTestSupport.Structured(cleanSweep).ToString());
        Assert.DoesNotContain(ReadStringArray(McpTestSupport.Structured(cleanSweep), "warnings"),
            w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));

        // (b) qty in [5, 50] -> max notional = 50 * 110 / 1,000,000 = 0.55% < 1% -> ONE warning
        using JsonDocument warnSweep = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"parameterRanges":[{"name":"qty","start":5,"end":50,"step":45}],"account":{"sizingModel":"FixedQuantity","sizingParameter":{"$param":"qty"} },"objective":"totalPnL","startDate":"2024-01-10","endDate":"2024-04-15"}"""))!;
        Assert.Single(ReadStringArray(McpTestSupport.Structured(warnSweep), "warnings"),
            w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampleTooSmall_WarnsAt19EvaluatedBars_AndOmitsAt20AcrossAllFourTools()
    {
        McpMessageDispatcher dispatcher = await StartMcpAsync();
        const string accountPct = """{"sizingModel":"PercentOfEquity","sizingParameter":0.2}""";
        const string paramConditions =
            """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}}}""";

        // 1) Single: 2024-01-10..2024-01-28 = 19 bars vs 2024-01-10..2024-01-29 = 20 bars
        using JsonDocument single19 = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"account":{{accountPct}},"startDate":"2024-01-10","endDate":"2024-01-28"}"""))!;
        JsonElement single19Json = McpTestSupport.Structured(single19);
        Assert.Equal(BacktestReportGenerator.MinReturnSamplesForRatioMetrics - 1, single19Json.GetProperty("period").GetProperty("evaluatedBarCount").GetInt32());
        Assert.Contains(ReadStringArray(single19Json, "warnings"), w => w.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal) && w.Contains("19", StringComparison.Ordinal) && w.Contains("20", StringComparison.Ordinal));

        using JsonDocument single20 = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"account":{{accountPct}},"startDate":"2024-01-10","endDate":"2024-01-29"}"""))!;
        JsonElement single20Json = McpTestSupport.Structured(single20);
        Assert.Equal(BacktestReportGenerator.MinReturnSamplesForRatioMetrics, single20Json.GetProperty("period").GetProperty("evaluatedBarCount").GetInt32());
        Assert.DoesNotContain(ReadStringArray(single20Json, "warnings"), w => w.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal));

        // 2) Portfolio: 19 bars warns in notes[], 20 bars does not
        using JsonDocument port19 = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest",
            $$"""{"symbols":["AAPL","LOWPX"],"conditions":{{Conditions}},"account":{{accountPct}},"startDate":"2024-01-10","endDate":"2024-01-28"}"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(port19), "notes"), n => n.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal));

        using JsonDocument port20 = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest",
            $$"""{"symbols":["AAPL","LOWPX"],"conditions":{{Conditions}},"account":{{accountPct}},"startDate":"2024-01-10","endDate":"2024-01-29"}"""))!;
        Assert.DoesNotContain(ReadStringArray(McpTestSupport.Structured(port20), "notes"), n => n.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal));

        // 3) Optimize: 19 bars warns, 20 bars does not
        using JsonDocument opt19 = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"account":{{accountPct}},"objective":"totalPnL","minTrades":0,"startDate":"2024-01-10","endDate":"2024-01-28"}"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(opt19), "warnings"), w => w.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal));

        using JsonDocument opt20 = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"account":{{accountPct}},"objective":"totalPnL","minTrades":0,"startDate":"2024-01-10","endDate":"2024-01-29"}"""))!;
        Assert.DoesNotContain(ReadStringArray(McpTestSupport.Structured(opt20), "warnings"), w => w.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal));

        // 4) Walk-Forward: out-of-sample 19 bars warns naming Window 0 (out-of-sample), 20 bars does not
        using JsonDocument wf19 = (await McpTestSupport.CallAsync(dispatcher, "sa_run_walk_forward_analysis",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"account":{{accountPct}},"objective":"totalPnL","minInSampleTrades":0,"windows":{"list":[{"inSampleStart":"2024-01-10","inSampleEnd":"2024-02-05","outOfSampleStart":"2024-02-06","outOfSampleEnd":"2024-02-24"}] } }"""))!;
        Assert.False(McpTestSupport.IsError(wf19), McpTestSupport.Structured(wf19).ToString());
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(wf19), "warnings"),
            w => w.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal) && w.Contains("Window 0 (out-of-sample)", StringComparison.Ordinal) && w.Contains("19", StringComparison.Ordinal));

        using JsonDocument wf20 = (await McpTestSupport.CallAsync(dispatcher, "sa_run_walk_forward_analysis",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"account":{{accountPct}},"objective":"totalPnL","minInSampleTrades":0,"windows":{"list":[{"inSampleStart":"2024-01-10","inSampleEnd":"2024-02-05","outOfSampleStart":"2024-02-06","outOfSampleEnd":"2024-02-25"}] } }"""))!;
        Assert.DoesNotContain(ReadStringArray(McpTestSupport.Structured(wf20), "warnings"),
            w => w.StartsWith("SAMPLE_TOO_SMALL:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SizingNotionalSmall_StillWarnsWhenMaxCandlesPerRequestTruncatesOlderEvaluatedWindow()
    {
        McpMessageDispatcher dispatcher = await StartMcpAsync(
            ("MaxCandlesPerRequest", "40"),
            ("DefaultCandleCount", "40"),
            ("MaxOutputTailBars", "40"),
            ("DefaultOutputTailBars", "40"));

        // Fixture spans 120 days from 2024-01-01..2024-04-29; MaxCandlesPerRequest=40 only loads the tail (~mid-March onward),
        // while startDate..endDate evaluates January..February.
        const string earlyStart = "2024-01-01";
        const string earlyEnd = "2024-02-15";

        using JsonDocument single = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest",
            $$"""{"symbol":"AAPL","conditions":{{Conditions}},"startDate":"{{earlyStart}}","endDate":"{{earlyEnd}}"}"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(single), "warnings"),
            w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));

        using JsonDocument portfolio = (await McpTestSupport.CallAsync(dispatcher, "sa_run_portfolio_backtest",
            $$"""{"symbols":["AAPL","LOWPX"],"conditions":{{Conditions}},"startDate":"{{earlyStart}}","endDate":"{{earlyEnd}}"}"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(portfolio), "notes"),
            n => n.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));

        const string paramConditions =
            """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}}}""";
        using JsonDocument optimize = (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"objective":"totalPnL","minTrades":0,"startDate":"{{earlyStart}}","endDate":"{{earlyEnd}}"}"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(optimize), "warnings"),
            w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));

        using JsonDocument wf = (await McpTestSupport.CallAsync(dispatcher, "sa_run_walk_forward_analysis",
            $$"""{"symbol":"AAPL","conditions":{{paramConditions}},"parameterRanges":[{"name":"period","start":3,"end":5,"step":2}],"objective":"totalPnL","minInSampleTrades":0,"windows":{"list":[{"inSampleStart":"2024-01-01","inSampleEnd":"2024-01-31","outOfSampleStart":"2024-02-01","outOfSampleEnd":"2024-02-15"}] } }"""))!;
        Assert.Contains(ReadStringArray(McpTestSupport.Structured(wf), "warnings"),
            w => w.StartsWith("SIZING_NOTIONAL_SMALL:", StringComparison.Ordinal));
    }
}
