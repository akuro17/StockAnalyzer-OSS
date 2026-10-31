using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using StockAnalyzer.Core.Tests.TestHelpers;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan 05 / T3: <c>indicators[i].timeFrame</c> of sa_calculate_indicators, through the real composition. MSFT has one bar on every calendar day from Monday 2024-01-01,
/// so week k is days 7k..7k+6 and month j is the j-th calendar month: the expected value at a daily bar is read off the stored weekly or monthly bars by index
/// (the value of period index - 1), independently of <c>TimeframeAlignment</c>. The omitted-key answer is pinned byte for byte by PointInTimeIndicatorsTests (golden),
/// which this task leaves untouched.
/// </summary>
public sealed class IndicatorTimeFrameTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Primary = "MSFT";
    private const string Comparison = "AAPL";
    private const string RunningWeekDate = "2024-12-18";

    private static readonly DateTime FirstDay = new(2024, 1, 1);
    private static readonly IndicatorFactory Factory = new();
    private static readonly ScreenerCatalogProvider CatalogProvider = new();

    private readonly World _truth = new(ZigZag(), mutate: null);
    private readonly List<World> _extra = new();

    public void Dispose()
    {
        _truth.Dispose();
        foreach (World world in _extra)
        {
            world.Dispose();
        }
    }

    /// <summary>The closes the fixture's own series uses (an alternating up and down sequence with a slow drift).</summary>
    private static List<decimal> ZigZag()
    {
        var closes = new List<decimal>(StoredDays);
        decimal close = 100m;
        for (int i = 0; i < StoredDays; i++)
        {
            close += i % 2 == 0 ? 1.0m : -0.5m;
            closes.Add(close);
        }

        return closes;
    }

    private static int DayIndex(string date) => (DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture) - FirstDay).Days;

    private World WorldWith(Func<int, decimal, decimal> mutate)
    {
        var world = new World(ZigZag(), mutate);
        _extra.Add(world);
        return world;
    }

    private static string Call(string frame, string indicators, string extra = "")
        => "{\"symbol\":\"MSFT\",\"timeFrame\":\"" + frame + "\",\"indicators\":" + indicators + extra + "}";

    private static string Sma(string id, int period, string? timeFrame = null)
        => "{\"id\":\"" + id + "\",\"type\":\"SMA\",\"parameters\":{\"Period\":" + period + "}" + (timeFrame is null ? string.Empty : ",\"timeFrame\":\"" + timeFrame + "\"") + "}";

    private static string List(params string[] indicators) => "[" + string.Join(",", indicators) + "]";

    private static decimal?[] SeriesOf(JsonElement root, string id, string series = "SMA")
        => root.GetProperty("results").GetProperty(id).GetProperty("series").GetProperty(series).EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    private static DateTime[] Timestamps(JsonElement root)
        => root.GetProperty("timestamps").EnumerateArray().Select(t => DateTime.Parse(t.GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static int PeriodIndex(TimeFrame frame, DateTime date) => frame switch
    {
        TimeFrame.W1 => (date.Date - FirstDay).Days / 7,
        TimeFrame.MN1 => ((date.Year - FirstDay.Year) * 12) + date.Month - 1,
        _ => throw new ArgumentOutOfRangeException(nameof(frame)),
    };

    private static CoreCandleData ToCore(CandleData bar) => new(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

    /// <summary>
    /// The value an SMA of <paramref name="period"/> on <paramref name="frame"/> bars must show at each of <paramref name="baseDates"/>: that of the period before the one containing
    /// the date (null before the first), taken from the stored weekly or monthly bars up to <paramref name="last"/> by index.
    /// </summary>
    private static async Task<decimal?[]> ExpectedAsync(World world, TimeFrame frame, int period, DateTime last, IEnumerable<DateTime> baseDates)
    {
        IReadOnlyList<CandleData> bars = await world.Gateway.GetCandlesUpToAsync(Primary, frame, DateOnly.FromDateTime(last), 10000);
        for (int index = 0; index < bars.Count; index++)
        {
            Assert.Equal(PeriodIndex(frame, bars[index].Timestamp), index); // the period of bar i is i: no gaps in this series
        }

        CoreIndicatorSettings sma = CatalogProvider.GetDefaultSettings(IndicatorType.SMA, Factory)!;
        sma.IsEnabled = true;
        ((CoreSmaParameter)sma.ParameterObject!).Period = period;
        Dictionary<string, IIndicatorResult> direct = await world.Gateway.CalculateIndicatorsAsync(bars.Select(ToCore).ToList(), new[] { sma });
        decimal?[] values = direct[sma.Id].GetSeries("Main").ToArray();
        return baseDates.Select(date => PeriodIndex(frame, date) - 1 is var previous && previous >= 0 ? values[previous] : null).ToArray();
    }

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

    // ---- the value at a daily bar is that of the previous closed week / month -------------------------------------------------------

    [Fact]
    public async Task DailyWeeklyAndMonthlyIndicatorsComeBackInOneCallOnTheDailyTimestamps()
    {
        using JsonDocument response = await _truth.CallAsync(Call("D1", List(Sma("d", 3), Sma("w", 3, "W1"), Sma("m", 3, "MN1")), ",\"outputTailCount\":60"));
        JsonElement root = Ok(response);
        DateTime[] dates = Timestamps(root);
        DateTime last = dates[^1];

        Assert.Equal(60, dates.Length);
        Assert.Equal(await ExpectedAsync(_truth, TimeFrame.W1, 3, last, dates), SeriesOf(root, "w"));
        Assert.Equal(await ExpectedAsync(_truth, TimeFrame.MN1, 3, last, dates), SeriesOf(root, "m"));
        Assert.All(SeriesOf(root, "w"), value => Assert.NotNull(value));
        Assert.Equal(60, SeriesOf(root, "d").Length);
        Assert.NotEqual(SeriesOf(root, "d"), SeriesOf(root, "w"));
    }

    [Fact]
    public async Task TheEchoNamesTheFrameAndTheAlignmentOnlyForAnIndicatorOnAnotherFrame()
    {
        using JsonDocument response = await _truth.CallAsync(Call("D1", List(Sma("d", 3), Sma("same", 3, "D1"), Sma("w", 3, "W1"))));
        JsonElement results = Ok(response).GetProperty("results");

        Assert.False(results.GetProperty("d").TryGetProperty("timeFrame", out _));
        Assert.False(results.GetProperty("d").TryGetProperty("alignment", out _));
        Assert.Equal("D1", results.GetProperty("same").GetProperty("timeFrame").GetString());
        Assert.False(results.GetProperty("same").TryGetProperty("alignment", out _));
        Assert.Equal("W1", results.GetProperty("w").GetProperty("timeFrame").GetString());
        Assert.Equal("PreviousClosedPeriod", results.GetProperty("w").GetProperty("alignment").GetString());
    }

    [Fact]
    public async Task TheCallsOwnFrameNamedExplicitlyGivesTheValuesOfTheOmittedKey()
    {
        using JsonDocument omitted = await _truth.CallAsync(Call("D1", List(Sma("a", 5))));
        using JsonDocument named = await _truth.CallAsync(Call("D1", List(Sma("a", 5, "D1"))));

        Assert.Equal(SeriesOf(Ok(omitted), "a"), SeriesOf(Ok(named), "a"));
        Assert.Equal(Ok(omitted).GetProperty("results").GetProperty("a").GetProperty("latestValues").GetRawText(), Ok(named).GetProperty("results").GetProperty("a").GetProperty("latestValues").GetRawText());
    }

    [Fact]
    public async Task BeforeTheFirstClosedPeriodTheValueIsNull()
    {
        // SMA(2) on weekly bars exists from week 1 on, so it is first shown on the daily bars of week 2 (day 14).
        using JsonDocument response = await _truth.CallAsync(Call("D1", List(Sma("w", 2, "W1")), ",\"outputTailCount\":400"));
        decimal?[] values = SeriesOf(Ok(response), "w");

        Assert.Equal(StoredDays, values.Length);
        Assert.All(values.Take(14), value => Assert.Null(value));
        Assert.NotNull(values[14]);
        Assert.Equal(StoredDays - 14, values.Count(value => value is not null));
    }

    [Fact]
    public async Task LookbackBarsLimitsTheCallsBarsOnlyNotTheIndicatorsOwnBars()
    {
        // Ten daily bars cannot hold a five-week average; the weekly SMA uses its own bars, so every returned value exists and equals the one of the full call.
        using JsonDocument narrow = await _truth.CallAsync(Call("D1", List(Sma("w", 5, "W1")), ",\"lookbackBars\":10,\"outputTailCount\":10"));
        using JsonDocument full = await _truth.CallAsync(Call("D1", List(Sma("w", 5, "W1")), ",\"outputTailCount\":10"));
        JsonElement root = Ok(narrow);

        Assert.Equal(10, root.GetProperty("calculatedBarCount").GetInt32());
        Assert.All(SeriesOf(root, "w"), value => Assert.NotNull(value));
        Assert.Equal(SeriesOf(Ok(full), "w"), SeriesOf(root, "w"));
    }

    [Fact]
    public async Task AMonthlyIndicatorOnAWeeklyCallShowsThePreviousClosedMonthOfTheWeekLabel()
    {
        using JsonDocument response = await _truth.CallAsync(Call("W1", List(Sma("m", 3, "MN1")), ",\"outputTailCount\":40"));
        JsonElement root = Ok(response);
        DateTime[] dates = Timestamps(root);

        Assert.Equal(await ExpectedAsync(_truth, TimeFrame.MN1, 3, dates[^1], dates), SeriesOf(root, "m"));
        Assert.Equal("PreviousClosedPeriod", root.GetProperty("results").GetProperty("m").GetProperty("alignment").GetString());
    }

    [Fact]
    public async Task AnAlignedIndicatorHasNoProjectedValuesWhileTheSameIndicatorOnTheCallsBarsKeepsThem()
    {
        string own = "{\"id\":\"d\",\"type\":\"Ichimoku\"}";
        string weekly = "{\"id\":\"w\",\"type\":\"Ichimoku\",\"timeFrame\":\"W1\"}";
        using JsonDocument response = await _truth.CallAsync(Call("D1", List(own, weekly), ",\"outputTailCount\":20"));
        JsonElement results = Ok(response).GetProperty("results");

        Assert.True(results.GetProperty("d").TryGetProperty("projectedValues", out _), results.GetProperty("d").GetRawText());
        Assert.True(results.GetProperty("w").GetProperty("isSuccessful").GetBoolean(), results.GetProperty("w").GetRawText());
        Assert.False(results.GetProperty("w").TryGetProperty("projectedValues", out _));
        Assert.All(results.GetProperty("w").GetProperty("series").EnumerateObject(), series => Assert.Equal(20, series.Value.GetArrayLength()));
    }

    // ---- nothing of the running period reaches the value (mutation) ---------------------------------------------------------------

    [Fact]
    public async Task ChangingTheBarsOfTheRunningWeekAndMonthChangesNoWeeklyOrMonthlyValueButChangesTheDailyOne()
    {
        // Monday 2024-12-16 to the call's last bar 2024-12-18 are the running week (and December the running month): their closes become ten times as large.
        World altered = WorldWith((index, close) => index >= DayIndex("2024-12-16") && index <= DayIndex(RunningWeekDate) ? close * 10m : close);
        string arguments = Call("D1", List(Sma("d", 3), Sma("w", 3, "W1"), Sma("m", 3, "MN1")), ",\"outputTailCount\":30,\"endDate\":\"" + RunningWeekDate + "\"");

        using JsonDocument truth = await _truth.CallAsync(arguments);
        using JsonDocument changed = await altered.CallAsync(arguments);

        Assert.Equal(SeriesOf(Ok(truth), "w"), SeriesOf(Ok(changed), "w"));
        Assert.Equal(SeriesOf(Ok(truth), "m"), SeriesOf(Ok(changed), "m"));
        Assert.NotEqual(SeriesOf(Ok(truth), "d"), SeriesOf(Ok(changed), "d")); // the check can fail: the daily value did see the change
    }

    [Fact]
    public async Task ChangingThePreviousClosedWeekChangesTheWeeklyValueFromTheNextWeekOnAndNothingBefore()
    {
        // The week 2024-12-09..15 is closed when 2024-12-16 begins: its closes become ten times as large.
        World altered = WorldWith((index, close) => index >= DayIndex("2024-12-09") && index <= DayIndex("2024-12-15") ? close * 10m : close);
        string arguments = Call("D1", List(Sma("w", 3, "W1"), Sma("m", 3, "MN1")), ",\"outputTailCount\":30,\"endDate\":\"" + RunningWeekDate + "\"");

        using JsonDocument truth = await _truth.CallAsync(arguments);
        using JsonDocument changed = await altered.CallAsync(arguments);
        DateTime[] dates = Timestamps(Ok(truth));
        decimal?[] before = SeriesOf(Ok(truth), "w");
        decimal?[] after = SeriesOf(Ok(changed), "w");

        for (int index = 0; index < dates.Length; index++)
        {
            if (dates[index] < DateTime.Parse("2024-12-16", System.Globalization.CultureInfo.InvariantCulture))
            {
                Assert.Equal(before[index], after[index]);
            }
            else
            {
                Assert.NotEqual(before[index], after[index]);
            }
        }

        Assert.Equal(SeriesOf(Ok(truth), "m"), SeriesOf(Ok(changed), "m")); // the changed days are still inside the running month
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFinerFrameThanTheCallIsUnsupportedAndNamesTheIndicatorsKey()
    {
        using JsonDocument response = await _truth.CallRawAsync(Call("W1", List(Sma("a", 3), Sma("b", 3, "D1"))));

        Assert.Equal("UNSUPPORTED_TIMEFRAME", Code(response));
        Assert.Equal("indicators[1].timeFrame", Param(response));
    }

    [Fact]
    public async Task AFrameTheDataLayerCannotLoadIsUnsupportedAtTheIndicatorsKey()
    {
        using JsonDocument response = await _truth.CallRawAsync(Call("MN1", List(Sma("a", 3, "H4"))));

        Assert.Equal("UNSUPPORTED_TIMEFRAME", Code(response));
        Assert.Equal("indicators[0].timeFrame", Param(response));
    }

    [Fact]
    public async Task ANameThatIsNotATimeFrameIsRefusedBySchema()
    {
        using JsonDocument response = await _truth.CallRawAsync(Call("D1", List(Sma("a", 3, "w1"))));

        Assert.Equal("INVALID_ARGUMENT", Code(response));
    }

    [Fact]
    public async Task AnIndicatorAndTheIndicatorItReadsMustShareTheirFrame()
    {
        string weeklySma = Sma("s", 3, "W1");
        string rsiOnDaily = "{\"id\":\"r\",\"type\":\"RSI\",\"parameters\":{\"Period\":3},\"sourceIndicatorId\":\"s\"}";
        string rsiOnWeekly = "{\"id\":\"r\",\"type\":\"RSI\",\"parameters\":{\"Period\":3},\"timeFrame\":\"W1\",\"sourceIndicatorId\":\"s\"}";
        string weeklyRsiOnDailySma = "{\"id\":\"r\",\"type\":\"RSI\",\"parameters\":{\"Period\":3},\"timeFrame\":\"W1\",\"sourceIndicatorId\":\"s\"}";

        using JsonDocument mismatched = await _truth.CallRawAsync(Call("D1", List(weeklySma, rsiOnDaily)));
        using JsonDocument reverse = await _truth.CallRawAsync(Call("D1", List(Sma("s", 3), weeklyRsiOnDailySma)));
        using JsonDocument matched = await _truth.CallAsync(Call("D1", List(weeklySma, rsiOnWeekly), ",\"outputTailCount\":30"));

        Assert.Equal("INVALID_ARGUMENT", Code(mismatched));
        Assert.Equal("indicators[1].sourceIndicatorId", Param(mismatched));
        Assert.Equal("INVALID_ARGUMENT", Code(reverse));
        Assert.Equal("indicators[1].sourceIndicatorId", Param(reverse));
        JsonElement chained = Ok(matched).GetProperty("results").GetProperty("r");
        Assert.True(chained.GetProperty("isSuccessful").GetBoolean(), chained.GetRawText());
        Assert.Equal("W1", chained.GetProperty("timeFrame").GetString());
    }

    [Fact]
    public async Task ACrossTickerIndicatorKeepsTheCallsFrame()
    {
        string pairOnWeekly = "{\"id\":\"p\",\"type\":\"PairRatio\",\"parameters\":{\"ComparisonSymbol\":\"" + Comparison + "\"},\"timeFrame\":\"W1\"}";
        string pairNamedDaily = "{\"id\":\"p\",\"type\":\"PairRatio\",\"parameters\":{\"ComparisonSymbol\":\"" + Comparison + "\"},\"timeFrame\":\"D1\"}";

        using JsonDocument refused = await _truth.CallRawAsync(Call("D1", List(pairOnWeekly)));
        using JsonDocument accepted = await _truth.CallAsync(Call("D1", List(pairNamedDaily)));

        Assert.Equal("INVALID_ARGUMENT", Code(refused));
        Assert.Equal("indicators[0].timeFrame", Param(refused));
        Assert.True(Ok(accepted).GetProperty("results").GetProperty("p").GetProperty("isSuccessful").GetBoolean());
    }

    [Fact]
    public async Task AnEndDateBeforeTheFirstStoredBarStillNamesTheEndDate()
    {
        using JsonDocument response = await _truth.CallRawAsync(Call("D1", List(Sma("w", 3, "W1")), ",\"endDate\":\"2023-12-31\""));

        Assert.Equal("INSUFFICIENT_MARKET_DATA", Code(response));
        Assert.Equal("endDate", Param(response));
    }

    // ---- "finer" has one definition ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void FinerMeansAnEarlierMemberOfTheTimeFrameEnumSoAReorderFailsHere()
    {
        Assert.True(ToolArgs.IsFinerThan(TimeFrame.D1, TimeFrame.W1));
        Assert.True(ToolArgs.IsFinerThan(TimeFrame.W1, TimeFrame.MN1));
        Assert.True(ToolArgs.IsFinerThan(TimeFrame.D1, TimeFrame.MN1));
        Assert.False(ToolArgs.IsFinerThan(TimeFrame.W1, TimeFrame.W1));
        Assert.False(ToolArgs.IsFinerThan(TimeFrame.MN1, TimeFrame.D1));
    }

    // ---- the description publishes the feature -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDescriptionAndTheSchemaNameTheIndicatorTimeFrame()
    {
        using JsonDocument list = JsonDocument.Parse((await _truth.DispatchAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/list\"}"))!);
        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_calculate_indicators");

        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("PreviousClosedPeriod", description, StringComparison.Ordinal);
        Assert.Contains("previous CLOSED period", description, StringComparison.Ordinal);
        Assert.Contains("bars of the indicator's own time frame", description, StringComparison.Ordinal);
        Assert.DoesNotContain("bars of the requested time frame", description, StringComparison.Ordinal);
        JsonElement item = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("indicators").GetProperty("items");
        Assert.True(item.GetProperty("properties").TryGetProperty("timeFrame", out _));
        Assert.DoesNotContain("timeFrame", item.TryGetProperty("required", out JsonElement required) ? required.GetRawText() : string.Empty, StringComparison.Ordinal);
    }

    // ---- hosting -----------------------------------------------------------------------------------------------------------------------

    /// <summary>A throw-away data tree with MSFT and AAPL on the same closes, the closes optionally rewritten by <c>mutate(dayIndex, close)</c>.</summary>
    private sealed class World : IDisposable
    {
        private readonly ReadOnlyDataTreeFixture _fixture;
        private readonly McpServerRuntime _runtime;
        private readonly ServiceProvider _services;

        public World(IReadOnlyList<decimal> closes, Func<int, decimal, decimal>? mutate)
        {
            _fixture = ReadOnlyDataTreeFixture.Create(closes.Count, Primary, Comparison);
            if (mutate is not null)
            {
                List<decimal> altered = closes.Select((close, index) => mutate(index, close)).ToList();
                foreach (string symbol in new[] { Primary, Comparison })
                {
                    File.Delete(Path.Combine(_fixture.DailyDirectory, $"{symbol}.parquet"));
                    ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, symbol, altered);
                }
            }

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
            _runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
            _runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None).GetAwaiter().GetResult();
            _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
            Gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        }

        public IReadOnlyStockAnalyzerGateway Gateway { get; }

        public Task<string?> DispatchAsync(string message) => _runtime.Dispatcher.DispatchAsync(message, CancellationToken.None);

        public async Task<JsonDocument> CallRawAsync(string argumentsJson)
            => (await McpTestSupport.CallAsync(_runtime.Dispatcher, "sa_calculate_indicators", argumentsJson))!;

        /// <summary>The raw response of a call (the caller reads the structured answer with Ok, or the error with the error helpers).</summary>
        public Task<JsonDocument> CallAsync(string argumentsJson) => CallRawAsync(argumentsJson);

        public void Dispose()
        {
            _runtime.Dispose();
            _services.Dispose();
            _fixture.Dispose();
        }
    }
}
