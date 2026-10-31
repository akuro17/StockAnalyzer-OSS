using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan 05 / T1: the optional <c>frame</c> key of a backtest condition side, through the real composition on a throw-away data tree. MSFT has a bar on
/// every calendar day from Monday 2024-01-01, so week k of the series is days 7k..7k+6 and every daily High of week k equals 110 + 10 * (k mod 3).
/// "High on W1 above 115" therefore holds for a week exactly when the PREVIOUS closed week had High 120 or 130, i.e. in the weeks with k mod 3 = 2 or 0;
/// "at or below 115" holds in the weeks with k mod 3 = 1. A strategy that enters on the first and exits on the second trades in a three-week cycle
/// (enter in a week with k mod 3 = 2, exit in a week with k mod 3 = 1) - and would trade one week earlier if the running week leaked in.
/// </summary>
public sealed class BacktestFrameSideTests
{
    private const int Days = 400;
    private const int WeeksPerCycle = 3;
    private const string BaseHigh = "110 + 10 * ((range // 7) % 3)";
    private const string Period = ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-12-31\"";
    private const string WeeklyEntry = """{"left":{"price":"High","frame":"W1"},"comparison":"GreaterThan","right":115}""";
    private const string WeeklyExit = """{"left":{"price":"High","frame":"W1"},"comparison":"LessThanOrEqual","right":115}""";

    private static readonly string WeeklyConditions = $$"""{"entryLong":{{WeeklyEntry}},"exitLong":{{WeeklyExit}}}""";
    private static readonly DateOnly FirstDay = new(2024, 1, 1);

    private static string SeriesQuery(string highExpression)
        => "SELECT DATE '2024-01-01'+CAST(range AS INTEGER) AS date," +
           "CAST(100+(range%7)*0.5 AS DOUBLE) AS open," +
           $"CAST({highExpression} AS DOUBLE) AS high," +
           "CAST(99+(range%7)*0.5 AS DOUBLE) AS low," +
           "CAST(100+(range%7)*0.5 AS DOUBLE) AS close," +
           $"CAST(1000 AS BIGINT) AS volume FROM range({Days})";

    /// <summary>Runs one tool on its own data tree and runtime, so two data sets never share a cache.</summary>
    private static async Task<JsonDocument> CallAsync(string highExpression, string tool, string arguments)
    {
        using var fixture = ReadOnlyDataTreeFixture.Create(Days, "msft");
        McpTestSupport.WriteParquet(Path.Combine(fixture.DailyDirectory, "MSFT.parquet"), SeriesQuery(highExpression));
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(fixture)).Build();
        using McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return (await McpTestSupport.CallAsync(runtime.Dispatcher, tool, arguments))!;
    }

    private static Task<JsonDocument> RunAsync(string conditions, string extra = Period, string highExpression = BaseHigh)
        => CallAsync(highExpression, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{conditions}}{{extra}}}""");

    private static JsonElement Ok(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string Code(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    private static int WeekOf(string date) => (DateOnly.Parse(date).DayNumber - FirstDay.DayNumber) / 7;

    private static IReadOnlyList<(int EntryWeek, int ExitWeek)> TradeWeeks(JsonElement result)
        => result.GetProperty("trades").GetProperty("items").EnumerateArray()
            .Select(trade => (WeekOf(trade.GetProperty("entryDate").GetString()!), WeekOf(trade.GetProperty("exitDate").GetString()!)))
            .ToArray();

    private static string Side(string frame) => $$"""{"price":"Close","frame":{{frame}}}""";

    private static string OneComparison(string side) => $$$"""{"entryLong":{"left":{{{side}}},"comparison":"GreaterThan","right":0}}""";

    // ---- alignment: the previous closed week, never the running one -----------------------------------------------------------------

    [Fact]
    public async Task AWeeklySideOnDailyBarsTradesInTheWeeksAfterTheWeekThatSetTheSignal()
    {
        using JsonDocument response = await RunAsync(WeeklyConditions);

        IReadOnlyList<(int EntryWeek, int ExitWeek)> trades = TradeWeeks(Ok(response));

        Assert.True(trades.Count >= 5, $"expected a three-week cycle over the evaluated weeks, got {trades.Count} trades");
        Assert.All(trades, trade =>
        {
            Assert.Equal(2, trade.EntryWeek % WeeksPerCycle);
            Assert.Equal(1, trade.ExitWeek % WeeksPerCycle);
            Assert.Equal(2, trade.ExitWeek - trade.EntryWeek);
        });
    }

    [Fact]
    public async Task ChangingTheRunningWeeksBarsDoesNotChangeTheSignals()
    {
        // Every bar from 2024-12-30 (week 52, the week the run ends in) on is mutated: no decision of the evaluated bars may read them.
        using JsonDocument baseline = await RunAsync(WeeklyConditions);
        using JsonDocument mutated = await RunAsync(WeeklyConditions, highExpression: $"CASE WHEN range >= 364 THEN 500 ELSE {BaseHigh} END");

        Assert.Equal(TradeWeeks(Ok(baseline)), TradeWeeks(Ok(mutated)));
        Assert.Equal(
            Ok(baseline).GetProperty("trades").GetProperty("items").EnumerateArray().Select(t => (t.GetProperty("entryDate").GetString(), t.GetProperty("exitDate").GetString())),
            Ok(mutated).GetProperty("trades").GetProperty("items").EnumerateArray().Select(t => (t.GetProperty("entryDate").GetString(), t.GetProperty("exitDate").GetString())));
    }

    [Fact]
    public async Task ChangingThePreviousClosedWeekChangesTheNextWeeksSignalAndNothingBefore()
    {
        // Week 16 (k mod 3 = 1) has High 120 and sets the entry signal of week 17; its own High 110 would have decided week 16 itself if it leaked.
        using JsonDocument baseline = await RunAsync(WeeklyConditions);
        using JsonDocument mutated = await RunAsync(WeeklyConditions, highExpression: $"CASE WHEN range // 7 = 16 THEN 110 ELSE {BaseHigh} END");

        IReadOnlyList<(int EntryWeek, int ExitWeek)> before = TradeWeeks(Ok(baseline));
        IReadOnlyList<(int EntryWeek, int ExitWeek)> after = TradeWeeks(Ok(mutated));

        Assert.Equal(before.Count, after.Count);
        int changed = Enumerable.Range(0, before.Count).Where(i => before[i] != after[i]).Single();
        Assert.Equal((17, 19), before[changed]);
        Assert.Equal((18, 19), after[changed]);
        Assert.True(changed > 0, "the trades before the mutated week must exist and stay unchanged");
    }

    // ---- the same frame is the same as no frame --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEvaluatedFrameNamedExplicitlyIsTheRunWithoutTheKey()
    {
        string withoutKey = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}}""";
        string withKey = """{"entryLong":{"left":{"price":"Close","frame":"D1"},"comparison":"GreaterThan","right":{"price":"Close","offset":1,"frame":"D1"}}}""";

        using JsonDocument plain = await RunAsync(withoutKey);
        using JsonDocument named = await RunAsync(withKey);

        Assert.Equal(Ok(plain).GetProperty("runFingerprint").GetProperty("sha256").GetString(), Ok(named).GetProperty("runFingerprint").GetProperty("sha256").GetString());
        Assert.Equal(Ok(plain).GetProperty("reproducibilityHash").GetString(), Ok(named).GetProperty("reproducibilityHash").GetString());
        Assert.Equal("Close > Close [1 bars back]", Ok(named).GetProperty("conditionRoots")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task TheEchoNamesTheFrameOfASideThatReadsAnotherFrame()
    {
        using JsonDocument response = await RunAsync(WeeklyConditions);

        Assert.Contains("High@W1", Ok(response).GetProperty("conditionRoots")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AMonthlySideOnAWeeklyRunIsAccepted()
    {
        using JsonDocument response = await RunAsync(OneComparison(Side("\"MN1\"")), ",\"timeFrame\":\"W1\"");

        Assert.Equal("W1", Ok(response).GetProperty("timeFrame").GetString());
    }

    // ---- refusals ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"w1\"")]
    [InlineData("\"\"")]
    [InlineData("\" W1\"")]
    [InlineData("\"H4\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task AFrameThatIsNotExactlyD1W1OrMN1IsInvalid(string frame)
    {
        using JsonDocument response = await RunAsync(OneComparison(Side(frame)));

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal("conditions.entryLong.left.frame", Param(response));
    }

    [Fact]
    public async Task AFinerFrameThanTheRunIsUnsupportedAndNamesTheRightSidePath()
    {
        string conditions = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","frame":"D1"}}}""";

        using JsonDocument response = await RunAsync(conditions, ",\"timeFrame\":\"W1\"");

        Assert.Equal("UNSUPPORTED_TIMEFRAME", Code(response));
        Assert.Equal("conditions.entryLong.right.frame", Param(response));
    }

    [Fact]
    public async Task TheParameterSweepChecksTheFrameAgainstTheTimeFrameOfTheRun()
    {
        string conditions = """{"entryLong":{"left":{"price":"Close","frame":"D1"},"comparison":"GreaterThan","right":{"$param":"level"}}}""";
        string ranges = """[{"name":"level","start":1,"end":2,"step":1}]""";

        using JsonDocument response = await CallAsync(
            BaseHigh,
            "sa_optimize_backtest",
            $$"""{"symbol":"MSFT","conditions":{{conditions}},"parameterRanges":{{ranges}},"objective":"totalPnL","timeFrame":"W1"{{Period}}}""");

        Assert.Equal("UNSUPPORTED_TIMEFRAME", Code(response));
    }

    [Fact]
    public async Task AnUnknownSideKeyIsStillRefused()
    {
        using JsonDocument response = await RunAsync(OneComparison("""{"price":"Close","timeFrame":"W1"}"""));

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal("conditions.entryLong.left.timeFrame", Param(response));
    }
}
