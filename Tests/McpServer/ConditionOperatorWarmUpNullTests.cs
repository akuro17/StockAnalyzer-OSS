using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Characterization (plan 05 follow-up): an expanded comparison (CrossesAbove, Rising) that reads a bar before the indicator has a value is false, in both engines. A null operand makes the
/// leaf false for every operator, so a warm-up gap can neither start a trade nor match a symbol; if a null were read as zero the same trees would fire, which is what each case below
/// would show. The series rise by one per day from 100 (UP), and SMA(P) first has a value at the P-th bar.
/// </summary>
public sealed class ConditionOperatorWarmUpNullTests : IDisposable
{
    private const int Bars = 60;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Bars, "up", "cross");
    private readonly List<McpServerRuntime> _runtimes = new();

    public ConditionOperatorWarmUpNullTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "UP", Enumerable.Range(0, Bars).Select(i => 100m + i).ToArray());
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "CROSS", Enumerable.Range(0, Bars).Select(i => i == Bars - 1 ? 110m : 100m).ToArray());
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
        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build(),
            new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static string Sma(int period) => ConditionJsonBuilder.Sma(period);

    // ---- screener (the latest bar, index 59) --------------------------------------------------------------------------------------------

    private async Task<string[]> MatchedAsync(string conditionTree)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", "{\"conditionTree\":" + conditionTree + "}"))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Screener_CrossesAboveIsFalseWhenTheIndicatorHadNoValueTheBarBefore()
    {
        // SMA(60) over 60 bars has a value at the last bar only. Read as zero, the previous bar would be "<= 0" and every symbol would match.
        Assert.Empty(await MatchedAsync(ConditionJsonBuilder.Cmp(Sma(60), "CrossesAbove", "0")));
    }

    [Fact]
    public async Task Screener_CrossesAboveStillMatchesWhenBothBarsHaveAValue()
    {
        // the control: SMA(2) of CROSS is 100 the bar before and 105 now, so it crosses above 102; UP never crosses 102 at the last bar
        Assert.Equal(new[] { "CROSS" }, await MatchedAsync(ConditionJsonBuilder.Cmp(Sma(2), "CrossesAbove", "102")));
    }

    [Theory]
    [InlineData(57, true)]   // values at the last four bars: Rising 3 holds on the rising series
    [InlineData(58, false)]  // the oldest of the four values is a warm-up gap
    [InlineData(59, false)]  // two gaps
    [InlineData(60, false)]  // three gaps
    public async Task Screener_RisingNeedsAValueAtEveryBarItReads(int period, bool matchesUp)
    {
        string[] matched = await MatchedAsync(ConditionJsonBuilder.Streak(Sma(period), 3));

        Assert.Equal(matchesUp ? new[] { "UP" } : Array.Empty<string>(), matched);
    }

    // ---- backtest (every bar from the first) ---------------------------------------------------------------------------------------------

    private const string BacktestPeriod = ",\"startDate\":\"2024-01-01\",\"endDate\":\"2024-02-29\"";
    private static readonly DateOnly FirstDay = new(2024, 1, 1);

    /// <summary>The entry dates of the closed trades of a backtest of UP. The exit fires once the close is above 140 (index 41), so a trade entered before that is closed (an open trade is not listed).</summary>
    private async Task<DateOnly[]> EntryDatesAsync(string entryLong)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string exit = ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "GreaterThan", "140");
        using JsonDocument response = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_run_backtest",
            "{\"symbol\":\"UP\",\"conditions\":{\"entryLong\":" + entryLong + ",\"exitLong\":" + exit + "}" + BacktestPeriod + "}"))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).GetProperty("trades").GetProperty("items").EnumerateArray()
            .Select(trade => DateOnly.Parse(trade.GetProperty("entryDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    [Fact]
    public async Task Backtest_RisingStartsAtTheFirstBarWhereAllFourValuesExist()
    {
        // SMA(5) has values from index 4; Rising 3 reads indexes t, t-1, t-2, t-3, so the first signal is index 7 and the trade is entered on the next bar (index 8).
        // Read as zero, the gap would let index 6 signal (SMA[4] > 0 = SMA[3]).
        DateOnly[] entries = await EntryDatesAsync(ConditionJsonBuilder.Streak(Sma(5), 3));

        Assert.NotEmpty(entries);
        Assert.Equal(FirstDay.AddDays(8), entries[0]);
    }

    [Fact]
    public async Task Backtest_CrossesAboveNeverFiresOnTheFirstValueOfAnIndicator()
    {
        // SMA(5) is above 0 from its first value, but the bar before it has none, so there is no crossing; read as zero the signal would be index 4.
        Assert.Empty(await EntryDatesAsync(ConditionJsonBuilder.Cmp(Sma(5), "CrossesAbove", "0")));
    }
}
