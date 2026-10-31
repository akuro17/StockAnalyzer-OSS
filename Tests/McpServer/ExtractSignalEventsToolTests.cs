using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class ExtractSignalEventsToolTests : IDisposable
{
    private const int Days = 300;
    private const string SimpleCondition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
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

    private async Task<McpMessageDispatcher> StartWithFixtureAsync(ReadOnlyDataTreeFixture fixture)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = fixture.Settings.TickerListPath,
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(config, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> ExtractAsync(McpMessageDispatcher dispatcher, string condition, string extra = "", string symbol = "MSFT")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_extract_signal_events", $$"""{"symbol":"{{symbol}}","timeFrame":"D1","condition":{{condition}}{{extra}}}"""))!;

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
    public async Task ExtractEvents_ReturnsCompleteStructureWithForwardReturnsAndComparisons()
    {
        var dispatcher = await StartAsync();
        string extra = Period + ",\"forwardBars\":[1,5],\"includeBooleanSeries\":true";

        JsonElement result = Result(await ExtractAsync(dispatcher, SimpleCondition, extra));

        Assert.Equal("MSFT", result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.True(result.GetProperty("loadedBarCount").GetInt32() > 0);
        Assert.True(result.GetProperty("evaluatedBarCount").GetInt32() > 0);
        Assert.True(result.GetProperty("eventCount").GetInt32() > 0);
        Assert.True(result.GetProperty("returnedEventCount").GetInt32() > 0);
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal("OldestFirst", result.GetProperty("order").GetString());
        Assert.NotNull(result.GetProperty("lastEventBarsAgo"));

        JsonElement events = result.GetProperty("events");
        Assert.True(events.GetArrayLength() > 0);
        JsonElement first = events[0];

        Assert.True(first.GetProperty("barIndex").GetInt32() >= 0);
        Assert.NotEmpty(first.GetProperty("timestamp").GetString()!);
        Assert.True(first.GetProperty("close").GetDecimal() > 0m);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("barsSincePreviousEvent").ValueKind);
        Assert.True(first.GetProperty("lookAheadDiagnostic").GetBoolean());

        JsonElement comparisons = first.GetProperty("comparisons");
        Assert.True(comparisons.GetArrayLength() > 0);
        Assert.Equal("children[0]", comparisons[0].GetProperty("path").GetString());
        Assert.True(comparisons[0].GetProperty("result").GetBoolean());

        JsonElement fwd = first.GetProperty("forwardReturns");
        Assert.True(fwd.TryGetProperty("1", out _));
        Assert.True(fwd.TryGetProperty("5", out _));

        JsonElement fwdNext = first.GetProperty("forwardReturnsFromNextOpen");
        Assert.True(fwdNext.TryGetProperty("1", out _));
        Assert.True(fwdNext.TryGetProperty("5", out _));

        JsonElement booleanSeries = result.GetProperty("booleanSeries");
        Assert.Equal(result.GetProperty("evaluatedBarCount").GetInt32(), booleanSeries.GetProperty("values").GetArrayLength());
        Assert.Equal(result.GetProperty("evaluatedBarCount").GetInt32(), booleanSeries.GetProperty("timestamps").GetArrayLength());
    }

    [Fact]
    public async Task ProofA_MatchesBacktestConditionTreeEvaluatorDirectly()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await ExtractAsync(dispatcher, SimpleCondition, Period));

        var extractedIndices = new List<int>();
        foreach (JsonElement ev in result.GetProperty("events").EnumerateArray())
        {
            extractedIndices.Add(ev.GetProperty("barIndex").GetInt32());
        }

        using var db = new DuckDBConnectionManager();
        IOptions<MarketDataSettings> options = Options.Create(_fixture.Settings);
        var preparation = new BacktestRunPreparationService(new ParquetDataService(db, options));

        BacktestConditionSide Close(int offset) => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close, OutputName = IndicatorResult.MainSeriesName, Offset = offset };
        BacktestConditionGroup Group() => new(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(new BacktestConditionLeaf(
            new BacktestConditionEntry { Left = Close(0), Operator = ComparisonOperator.GreaterThan, TargetMode = RightHandTargetMode.Indicator, Right = Close(1) })));

        var tree = new BacktestConditionTree(Group(), BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd);
        var snapshot = new BacktestRunSnapshot(
            "MSFT", TimeFrame.D1, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 9, 30, 0, 0, 0, DateTimeKind.Utc),
            new BacktestConfiguration { InitialCapital = 1000000m }, ImmutableArray<BacktestConditionEntry>.Empty, null,
            ImmutableArray<StrategyIndicatorRequest>.Empty, BacktestReportDefaults.BuiltIn, 42, 1000, tree);

        PreparedBacktestRun prepared = await preparation.PrepareAsync(snapshot, CancellationToken.None);
        var strategy = (ConditionBasedBacktestStrategy)prepared.Strategy;

        var expectedIndices = new List<int>();
        for (int i = prepared.EvaluationStartIndex; i < prepared.Input.Bars.Length; i++)
        {
            var context = new StrategyContext
            {
                BarIndex = i,
                Bar = prepared.Input.Bars[i],
                PositionSide = null,
                Snapshot = new EquityPoint(i, prepared.Input.Bars[i].Timestamp, 1000000m, 1000000m, 0m, 0m),
                Indicators = ConditionSeriesEvaluator.PrepareIndicators(IndicatorFactory.Default, prepared.Input.Bars, TimeFrame.D1, strategy.GetRequiredIndicators())
            };

            StrategyOrderRequest? request = strategy.Evaluate(context);
            if (request is { SignalType: SignalType.LongEntry })
            {
                expectedIndices.Add(i);
            }
        }

        Assert.Equal(expectedIndices, extractedIndices);
    }

    [Fact]
    public async Task ProofB_NoLookaheadMutationTest_FutureBarsDoNotAffectPastEvents()
    {
        using var alteredFixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "7203.T");

        // Mutate future bars in alteredFixture: for days after 2024-06-30 (day index ~182), multiply closes by 10
        var originalCloses = new List<decimal>(Days);
        decimal c = 100m;
        for (int i = 0; i < Days; i++)
        {
            c += (i % 2 == 0) ? 1.0m : -0.5m;
            originalCloses.Add(c);
        }
        var cutDate = new DateTime(2024, 6, 30);
        var firstDay = new DateTime(2024, 1, 1);
        int cutIndex = (int)(cutDate - firstDay).TotalDays;

        var mutatedCloses = originalCloses.Select((close, idx) => idx > cutIndex ? close * 10m : close).ToList();
        string msftFile = Path.Combine(alteredFixture.DailyDirectory, "msft.parquet");
        File.Delete(msftFile);
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(alteredFixture.DailyDirectory, "msft", mutatedCloses);

        var dispatcherTruth = await StartAsync();
        var dispatcherAltered = await StartWithFixtureAsync(alteredFixture);

        string cutPeriod = ",\"startDate\":\"2024-03-01\",\"endDate\":\"2024-06-30\",\"forwardBars\":[1,5]";

        JsonElement truthResult = Result(await ExtractAsync(dispatcherTruth, SimpleCondition, cutPeriod));
        JsonElement alteredResult = Result(await ExtractAsync(dispatcherAltered, SimpleCondition, cutPeriod));

        // 1. Both runs must succeed and produce events
        int eventCount = truthResult.GetProperty("eventCount").GetInt32();
        Assert.True(eventCount > 0);

        // 2. Exact match: JSON of truthResult and alteredResult must be completely identical
        Assert.Equal(truthResult.GetRawText(), alteredResult.GetRawText());

        // 3. Negative check: without endDate, mutating future bars MUST produce different answers
        JsonElement truthUnbounded = Result(await ExtractAsync(dispatcherTruth, SimpleCondition, ",\"startDate\":\"2024-03-01\",\"forwardBars\":[1,5]"));
        JsonElement alteredUnbounded = Result(await ExtractAsync(dispatcherAltered, SimpleCondition, ",\"startDate\":\"2024-03-01\",\"forwardBars\":[1,5]"));
        Assert.NotEqual(truthUnbounded.GetRawText(), alteredUnbounded.GetRawText());
    }

    [Fact]
    public async Task EventMode_FirstTrueBarOfRun_SkipsConsecutiveMatches()
    {
        var dispatcher = await StartAsync();
        // Constant condition: Close > 0 (always true for all bars)
        string alwaysTrue = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""";

        JsonElement everyTrueResult = Result(await ExtractAsync(dispatcher, alwaysTrue, Period + ",\"eventMode\":\"EveryTrueBar\""));
        JsonElement firstTrueResult = Result(await ExtractAsync(dispatcher, alwaysTrue, Period + ",\"eventMode\":\"FirstTrueBarOfRun\""));

        int everyCount = everyTrueResult.GetProperty("eventCount").GetInt32();
        int firstCount = firstTrueResult.GetProperty("eventCount").GetInt32();

        Assert.True(everyCount > 1);
        Assert.Equal(1, firstCount); // Since it is always true on every bar, only the first bar is emitted
    }

    [Fact]
    public async Task MaxEvents_TruncatesCorrectlyWithOldestAndNewestOrder()
    {
        var dispatcher = await StartAsync();
        string extraOldest = Period + ",\"maxEvents\":3,\"order\":\"OldestFirst\"";
        string extraNewest = Period + ",\"maxEvents\":3,\"order\":\"NewestFirst\"";

        JsonElement oldestResult = Result(await ExtractAsync(dispatcher, SimpleCondition, extraOldest));
        JsonElement newestResult = Result(await ExtractAsync(dispatcher, SimpleCondition, extraNewest));

        Assert.True(oldestResult.GetProperty("truncated").GetBoolean());
        Assert.True(newestResult.GetProperty("truncated").GetBoolean());

        Assert.Equal(3, oldestResult.GetProperty("returnedEventCount").GetInt32());
        Assert.Equal(3, newestResult.GetProperty("returnedEventCount").GetInt32());

        int oldestFirstIndex = oldestResult.GetProperty("events")[0].GetProperty("barIndex").GetInt32();
        int newestFirstIndex = newestResult.GetProperty("events")[0].GetProperty("barIndex").GetInt32();

        Assert.True(oldestFirstIndex < newestFirstIndex);
    }

    [Fact]
    public async Task InvalidArguments_FailGracefully()
    {
        var dispatcher = await StartAsync();

        // Empty condition
        JsonDocument emptyCond = await ExtractAsync(dispatcher, "{}");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(emptyCond));

        // Unknown eventMode
        JsonDocument badMode = await ExtractAsync(dispatcher, SimpleCondition, ",\"eventMode\":\"InvalidMode\"");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(badMode));

        // Start after End
        JsonDocument badDates = await ExtractAsync(dispatcher, SimpleCondition, ",\"startDate\":\"2024-05-01\",\"endDate\":\"2024-04-01\"");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(badDates));

        // Forward horizon out of range
        JsonDocument badForward = await ExtractAsync(dispatcher, SimpleCondition, ",\"forwardBars\":[0]");
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(badForward));

        // Unsupported timeFrame
        JsonDocument badFrame = (await McpTestSupport.CallAsync(dispatcher, "sa_extract_signal_events", $$"""{"symbol":"MSFT","timeFrame":"M5","condition":{{SimpleCondition}}}"""))!;
        Assert.Equal("UNSUPPORTED_TIMEFRAME", ErrorCode(badFrame));
    }

    [Fact]
    public async Task ForeignTimeFrameCondition_LoadsWithoutCrash()
    {
        var dispatcher = await StartAsync();
        string weeklyCondition = """{"left":{"indicator":"SMA","parameters":{"Period":5},"frame":"W1"},"comparison":"GreaterThan","right":0}""";

        JsonElement result = Result(await ExtractAsync(dispatcher, weeklyCondition, Period));
        Assert.True(result.GetProperty("eventCount").GetInt32() > 0);
    }

    [Fact]
    public async Task AllowUnregistered_EmitsWarnings_WhenOffList()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, "OFFLIST", Days);
        var dispatcher = await StartAsync();
        JsonDocument doc = await ExtractAsync(dispatcher, SimpleCondition, Period + ",\"allowUnregistered\":true", symbol: "OFFLIST");
        JsonElement result = Result(doc);
        Assert.True(result.TryGetProperty("warnings", out JsonElement warnings));
        Assert.True(warnings.GetArrayLength() > 0);
        Assert.Contains("not in the ticker list", warnings[0].GetString()!);
    }
}
