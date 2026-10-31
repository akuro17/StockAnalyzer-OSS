using System.Globalization;
using System.Text.Json;
using StockAnalyzer.McpServer.Protocol;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan 04 / T3: sa_run_walk_forward_analysis through the real composition on the sine-wave MSFT series (400 daily bars from 2024-01-01, one bar per calendar
/// day, <see cref="BacktestDiagnosticsTestHost"/>). Proof: every window equals the individual sa_run_backtest runs on its in-sample part (best combination)
/// and on its out-of-sample part (that combination), the out-of-sample part is always after the in-sample part, and the output does not depend on the threads.
/// </summary>
public sealed class RunWalkForwardToolTests : IDisposable
{
    private static readonly DateOnly FirstDay = new(2024, 1, 1);

    private readonly BacktestDiagnosticsTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static string Sma(string period) => $$$"""{"indicator":"SMA","parameters":{"Period":{{{period}}}}}""";

    private static string Conditions(string period)
        => $$$"""{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{{{Sma(period)}}}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{{{Sma(period)}}}}}""";

    private const string Param = """{"$param":"period"}""";

    private static string Ranges(string start = "3", string end = "8", string step = "1")
        => $$"""[{"name":"period","start":{{start}},"end":{{end}},"step":{{step}}}]""";

    private static string Generator(string mode = "Rolling", int inSample = 100, int outOfSample = 40, int step = 40)
        => $$$"""{"generator":{"mode":"{{{mode}}}","inSampleBars":{{{inSample}}},"outOfSampleBars":{{{outOfSample}}},"stepBars":{{{step}}}}}""";

    private static string Args(string windows, string ranges = null!, string objective = "totalPnL", string extra = "", string conditions = null!)
        => $$"""{"symbol":"MSFT","conditions":{{conditions ?? Conditions(Param)}},"parameterRanges":{{ranges ?? Ranges()}},"objective":"{{objective}}","windows":{{windows}}{{extra}}}""";

    private static string Day(int offset) => FirstDay.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonDocument> WalkAsync(McpMessageDispatcher dispatcher, string arguments)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_walk_forward_analysis", arguments))!;

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string period, string from, string to)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{Conditions(period)}},"startDate":"{{from}}","endDate":"{{to}}"}"""))!;

    private static JsonElement Ok(JsonDocument response) => BacktestDiagnosticsTestHost.Result(response);

    private static decimal? Value(JsonElement metric)
        => metric.ValueKind == JsonValueKind.Null || metric.GetProperty("value").ValueKind == JsonValueKind.Null ? null : metric.GetProperty("value").GetDecimal();

    private static decimal Metric(JsonElement report, string name) => report.GetProperty(name).GetProperty("value").GetDecimal();

    private async Task<(McpMessageDispatcher Dispatcher, JsonElement Result)> WalkOkAsync(string arguments)
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();
        return (dispatcher, Ok(await WalkAsync(dispatcher, arguments)));
    }

    // ---- expanded comparison names -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExpandedNameGivesTheSameWindowsAsTheComparisonsWrittenOut()
    {
        // CrossesAbove = "left now above right and, one bar earlier, at or below it" with both offsets raised by one (plan 05 section 6.2).
        string sugar = $$$"""{"left":{"price":"Close"},"comparison":"CrossesAbove","right":{{{Sma(Param)}}}}""";
        string written = $$$"""{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":{{{Sma(Param)}}}},{"left":{"price":"Close","offset":1},"comparison":"LessThanOrEqual","right":{"indicator":"SMA","parameters":{"Period":{{{Param}}}},"offset":1}}]}""";
        static string Both(string entry) => $$$"""{"entryLong":{{{entry}}},"exitLong":{{{BacktestDiagnosticsTestHost.Exit}}}}""";

        var (_, viaName) = await WalkOkAsync(Args(Generator(), conditions: Both(sugar)));
        var (_, viaComparisons) = await WalkOkAsync(Args(Generator(), conditions: Both(written)));

        // Equal results prove nothing when both are empty: the compared windows must have traded.
        JsonElement[] windows = viaName.GetProperty("windows").EnumerateArray().ToArray();
        Assert.True(viaName.GetProperty("windowCount").GetInt32() > 0);
        Assert.Contains(windows, window => window.GetProperty("isTotalTrades").GetInt32() > 0);
        Assert.Equal(viaComparisons.GetProperty("windows").GetRawText(), viaName.GetProperty("windows").GetRawText());
    }

    [Fact]
    public async Task AnExpandedNameWhoseBarsIsAParameterGivesEachWindowTheIndividualBacktestOfItsBestBars()
    {
        const string BarsParam = """{"$param":"bars"}""";
        static string Strategy(string bars) => $$$"""{"entryLong":{"left":{"price":"Close"},"comparison":"Rising","bars":{{{bars}}}},"exitLong":{{{BacktestDiagnosticsTestHost.Exit}}}}""";
        const string BarsRange = """[{"name":"bars","start":1,"end":3,"step":1}]""";

        McpMessageDispatcher dispatcher = await _host.StartAsync();
        JsonElement result = Ok(await WalkAsync(dispatcher, Args(Generator("Rolling", 120, 60, 80), ranges: BarsRange,objective: "totalReturn", conditions: Strategy(BarsParam))));

        JsonElement[] windows = result.GetProperty("windows").EnumerateArray().ToArray();
        Assert.True(windows.Length >= 3);
        Assert.Contains(windows, window => window.GetProperty("isTotalTrades").GetInt32() > 0);
        foreach (JsonElement window in windows)
        {
            decimal bars = window.GetProperty("bestParameters").GetProperty("bars").GetDecimal();
            Assert.InRange(bars, 1m, 3m);
            string literal = bars.ToString(CultureInfo.InvariantCulture);
            string period = $",\"startDate\":\"{window.GetProperty("inSample").GetProperty("start").GetString()}\",\"endDate\":\"{window.GetProperty("inSample").GetProperty("end").GetString()}\"";
            using JsonDocument run = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{Strategy(literal)}}{{period}}}"""))!;
            JsonElement report = Ok(run).GetProperty("report");

            Assert.Equal(Metric(report, "totalReturn"), Value(window.GetProperty("isObjective")));
            Assert.Equal(report.GetProperty("totalTrades").GetInt32(), window.GetProperty("isTotalTrades").GetInt32());
        }
    }

    // ---- windows from the generator ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RollingWindowsAreCutInBarsFromTheStoredHistoryAndTheOutOfSamplePartFollows()
    {
        var (_, result) = await WalkOkAsync(Args(Generator()));

        JsonElement[] windows = result.GetProperty("windows").EnumerateArray().ToArray();
        // 400 bars, IS 100 + OOS 40, step 40: window k needs 140 + 40k <= 400 -> k = 0..6
        Assert.Equal(7, windows.Length);
        Assert.Equal(7, result.GetProperty("windowCount").GetInt32());
        Assert.Equal("Rolling", result.GetProperty("windowMode").GetString());
        Assert.Equal(400, result.GetProperty("period").GetProperty("barCount").GetInt32());
        for (int k = 0; k < windows.Length; k++)
        {
            Assert.Equal(k, windows[k].GetProperty("index").GetInt32());
            Assert.Equal(Day(40 * k), windows[k].GetProperty("inSample").GetProperty("start").GetString());
            Assert.Equal(Day((40 * k) + 99), windows[k].GetProperty("inSample").GetProperty("end").GetString());
            Assert.Equal(Day((40 * k) + 100), windows[k].GetProperty("outOfSample").GetProperty("start").GetString());
            Assert.Equal(Day((40 * k) + 139), windows[k].GetProperty("outOfSample").GetProperty("end").GetString());
        }
    }

    [Fact]
    public async Task AnchoredWindowsKeepTheFirstBarAndTheInSamplePartGrows()
    {
        var (_, result) = await WalkOkAsync(Args(Generator("Anchored", 100, 50, 100)));

        JsonElement[] windows = result.GetProperty("windows").EnumerateArray().ToArray();
        // IS 100/200/300 with OOS 50 -> ends at 150/250/350 (<= 399); a fourth would need 450
        Assert.Equal(3, windows.Length);
        Assert.All(windows, w => Assert.Equal(Day(0), w.GetProperty("inSample").GetProperty("start").GetString()));
        Assert.Equal(new[] { Day(99), Day(199), Day(299) }, windows.Select(w => w.GetProperty("inSample").GetProperty("end").GetString()));
        Assert.Equal(new[] { Day(100), Day(200), Day(300) }, windows.Select(w => w.GetProperty("outOfSample").GetProperty("start").GetString()));
    }

    [Fact]
    public async Task StartAndEndDateCutThePeriodTheGeneratorWorksOn()
    {
        var (_, result) = await WalkOkAsync(Args(Generator("Rolling", 60, 30, 30), extra: $",\"startDate\":\"{Day(100)}\",\"endDate\":\"{Day(279)}\""));

        // 180 bars, window k needs 90 + 30k <= 180 -> k = 0..3
        JsonElement[] windows = result.GetProperty("windows").EnumerateArray().ToArray();
        Assert.Equal(4, windows.Length);
        Assert.Equal(180, result.GetProperty("period").GetProperty("barCount").GetInt32());
        Assert.Equal(Day(100), windows[0].GetProperty("inSample").GetProperty("start").GetString());
        Assert.Equal(Day(279), windows[3].GetProperty("outOfSample").GetProperty("end").GetString());
    }

    // ---- proof: every window equals the individual runs ----------------------------------------------------------------------------------------

    [Fact]
    public async Task EachWindowEqualsTheIndividualBacktestsOfItsBestCombination()
    {
        var (dispatcher, result) = await WalkOkAsync(Args(Generator("Rolling", 120, 60, 80), objective: "totalReturn"));

        JsonElement[] windows = result.GetProperty("windows").EnumerateArray().ToArray();
        Assert.True(windows.Length >= 3);
        foreach (JsonElement window in windows)
        {
            string isStart = window.GetProperty("inSample").GetProperty("start").GetString()!;
            string isEnd = window.GetProperty("inSample").GetProperty("end").GetString()!;
            string oosStart = window.GetProperty("outOfSample").GetProperty("start").GetString()!;
            string oosEnd = window.GetProperty("outOfSample").GetProperty("end").GetString()!;

            // the best in-sample combination by the individual runs: highest totalReturn, ties by the lower period (= combination number)
            var inSample = new List<(int Period, decimal Value, JsonElement Report)>();
            for (int period = 3; period <= 8; period++)
            {
                JsonElement report = Ok(await RunAsync(dispatcher, period.ToString(CultureInfo.InvariantCulture), isStart, isEnd)).GetProperty("report");
                inSample.Add((period, Metric(report, "totalReturn"), report));
            }

            (int bestPeriod, decimal bestValue, JsonElement bestReport) = inSample.OrderByDescending(x => x.Value).ThenBy(x => x.Period).First();
            JsonElement oosReport = Ok(await RunAsync(dispatcher, bestPeriod.ToString(CultureInfo.InvariantCulture), oosStart, oosEnd)).GetProperty("report");

            Assert.Equal((decimal)bestPeriod, window.GetProperty("bestParameters").GetProperty("period").GetDecimal());
            Assert.Equal(bestValue, Value(window.GetProperty("isObjective")));
            Assert.Equal(Metric(bestReport, "barSharpe"), Value(window.GetProperty("isBarSharpe")));
            Assert.Equal(Metric(bestReport, "maxDrawdown"), Value(window.GetProperty("isMaxDrawdown")));
            Assert.Equal(bestReport.GetProperty("totalTrades").GetInt32(), window.GetProperty("isTotalTrades").GetInt32());
            Assert.Equal(Metric(oosReport, "totalReturn"), Value(window.GetProperty("oosObjective")));
            Assert.Equal(Metric(oosReport, "totalReturn"), Value(window.GetProperty("oosTotalReturn")));
            Assert.Equal(Metric(oosReport, "barSharpe"), Value(window.GetProperty("oosBarSharpe")));
            Assert.Equal(Metric(oosReport, "maxDrawdown"), Value(window.GetProperty("oosMaxDrawdown")));
            Assert.Equal(oosReport.GetProperty("totalTrades").GetInt32(), window.GetProperty("oosTotalTrades").GetInt32());
            Assert.Equal("Evaluated", window.GetProperty("status").GetString());
            Assert.False(window.GetProperty("isInsufficientData").GetBoolean());
        }
    }

    [Fact]
    public async Task MinimizePicksTheLowestInSampleValue()
    {
        var (dispatcher, result) = await WalkOkAsync(Args(Generator("Rolling", 150, 50, 100), objective: "totalReturn", extra: ",\"objectiveDirection\":\"Minimize\""));

        JsonElement window = result.GetProperty("windows")[0];
        var values = new List<(int Period, decimal Value)>();
        for (int period = 3; period <= 8; period++)
        {
            JsonElement report = Ok(await RunAsync(dispatcher, period.ToString(CultureInfo.InvariantCulture), window.GetProperty("inSample").GetProperty("start").GetString()!, window.GetProperty("inSample").GetProperty("end").GetString()!)).GetProperty("report");
            values.Add((period, Metric(report, "totalReturn")));
        }

        Assert.Equal((decimal)values.OrderBy(x => x.Value).ThenBy(x => x.Period).First().Period, window.GetProperty("bestParameters").GetProperty("period").GetDecimal());
        Assert.Equal("Minimize", result.GetProperty("objectiveDirection").GetString());
    }

    [Theory]
    [InlineData("Rolling")]
    [InlineData("Anchored")]
    public async Task TheOutOfSamplePartIsAlwaysAfterTheInSamplePart(string mode)
    {
        var (_, result) = await WalkOkAsync(Args(Generator(mode, 80, 30, 40)));

        Assert.All(result.GetProperty("windows").EnumerateArray(), window =>
        {
            Assert.True(string.CompareOrdinal(window.GetProperty("inSample").GetProperty("end").GetString(), window.GetProperty("outOfSample").GetProperty("start").GetString()) < 0);
            Assert.True(string.CompareOrdinal(window.GetProperty("inSample").GetProperty("start").GetString(), window.GetProperty("inSample").GetProperty("end").GetString()) <= 0);
            Assert.True(string.CompareOrdinal(window.GetProperty("outOfSample").GetProperty("start").GetString(), window.GetProperty("outOfSample").GetProperty("end").GetString()) <= 0);
        });
    }

    // ---- aggregate -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAggregateIsTheRatioOfTheMeansOverTheWindowsWithBothBarSharpesValid()
    {
        var (_, result) = await WalkOkAsync(Args(Generator("Rolling", 100, 40, 40), objective: "totalReturn"));

        JsonElement[] windows = result.GetProperty("windows").EnumerateArray().ToArray();
        decimal?[] isSharpe = windows.Select(w => Value(w.GetProperty("isBarSharpe"))).ToArray();
        decimal?[] oosSharpe = windows.Select(w => Value(w.GetProperty("oosBarSharpe"))).ToArray();
        var both = Enumerable.Range(0, windows.Length).Where(i => isSharpe[i] is not null && oosSharpe[i] is not null).ToArray();
        JsonElement aggregate = result.GetProperty("aggregate");

        Assert.NotEmpty(both);
        decimal meanIs = both.Average(i => isSharpe[i]!.Value);
        decimal meanOos = both.Average(i => oosSharpe[i]!.Value);
        Assert.Equal(meanIs, aggregate.GetProperty("meanIsBarSharpe").GetDecimal());
        Assert.Equal(meanOos, aggregate.GetProperty("meanOosBarSharpe").GetDecimal());
        Assert.Equal(both.Length, aggregate.GetProperty("barSharpeWindowCount").GetInt32());
        if (meanIs > 0m)
        {
            Assert.Equal(meanOos / meanIs, aggregate.GetProperty("barSharpeEfficiency").GetDecimal());
            Assert.Equal(JsonValueKind.Null, aggregate.GetProperty("barSharpeEfficiencyReason").ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, aggregate.GetProperty("barSharpeEfficiency").ValueKind);
            Assert.Equal("InSampleBarSharpeMeanNotPositive", aggregate.GetProperty("barSharpeEfficiencyReason").GetString());
        }

        decimal[] isObjective = windows.Select(w => Value(w.GetProperty("isObjective"))).Where(v => v is not null).Select(v => v!.Value).ToArray();
        decimal[] oosObjective = windows.Select(w => Value(w.GetProperty("oosObjective"))).Where(v => v is not null).Select(v => v!.Value).ToArray();
        Assert.Equal(isObjective.Average(), aggregate.GetProperty("meanIsObjective").GetDecimal());
        Assert.Equal(oosObjective.Average(), aggregate.GetProperty("meanOosObjective").GetDecimal());
        Assert.Equal(windows.Length, aggregate.GetProperty("evaluatedWindowCount").GetInt32());
    }

    [Theory]
    [InlineData(100, 1, 40)]
    [InlineData(100, 2, 50)]
    [InlineData(40, 60, 60)]
    public async Task AnEfficiencyIsNullExactlyWhenItsReasonIsGiven(int inSample, int outOfSample, int step)
    {
        var (_, result) = await WalkOkAsync(Args(Generator("Rolling", inSample, outOfSample, step), objective: "totalReturn"));

        JsonElement aggregate = result.GetProperty("aggregate");
        bool hasValue = aggregate.GetProperty("barSharpeEfficiency").ValueKind != JsonValueKind.Null;
        bool hasReason = aggregate.GetProperty("barSharpeEfficiencyReason").ValueKind != JsonValueKind.Null;
        Assert.NotEqual(hasValue, hasReason);
        if (!hasValue)
        {
            Assert.Contains(aggregate.GetProperty("barSharpeEfficiencyReason").GetString(), new[] { "NoWindowWithValidInSampleAndOutOfSampleBarSharpe", "InSampleBarSharpeMeanNotPositive" });
        }
    }

    [Fact]
    public async Task NoDeflatedSharpeOrOverfittingKeysAreWritten()
    {
        var (_, result) = await WalkOkAsync(Args(Generator()));

        string text = result.GetRawText();
        foreach (string forbidden in new[] { "dsr", "deflated", "pbo", "overfitting", "bootstrapMaxDrawdown", "confidenceInterval", "wfe\"" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- windows from a list, insufficient data ------------------------------------------------------------------------------------------------

    private static string Listed(params (string IsStart, string IsEnd, string OosStart, string OosEnd)[] windows)
        => "{\"list\":[" + string.Join(",", windows.Select(w => $"{{\"inSampleStart\":\"{w.IsStart}\",\"inSampleEnd\":\"{w.IsEnd}\",\"outOfSampleStart\":\"{w.OosStart}\",\"outOfSampleEnd\":\"{w.OosEnd}\"}}")) + "]}";

    [Fact]
    public async Task AListOfDatedWindowsIsRunAsGivenAndEqualsTheIndividualRuns()
    {
        string windows = Listed((Day(50), Day(199), Day(200), Day(260)), (Day(120), Day(259), Day(260), Day(330)));
        var (dispatcher, result) = await WalkOkAsync(Args(windows, objective: "totalReturn"));

        Assert.Equal("List", result.GetProperty("windowMode").GetString());
        Assert.False(result.TryGetProperty("period", out _));
        JsonElement[] listed = result.GetProperty("windows").EnumerateArray().ToArray();
        Assert.Equal(2, listed.Length);
        Assert.Equal(Day(50), listed[0].GetProperty("inSample").GetProperty("start").GetString());
        Assert.Equal(Day(330), listed[1].GetProperty("outOfSample").GetProperty("end").GetString());
        foreach (JsonElement window in listed)
        {
            string period = window.GetProperty("bestParameters").GetProperty("period").GetDecimal().ToString(CultureInfo.InvariantCulture);
            JsonElement oos = Ok(await RunAsync(dispatcher, period, window.GetProperty("outOfSample").GetProperty("start").GetString()!, window.GetProperty("outOfSample").GetProperty("end").GetString()!)).GetProperty("report");
            Assert.Equal(Metric(oos, "totalReturn"), Value(window.GetProperty("oosObjective")));
        }
    }

    [Fact]
    public async Task AWindowWithoutStoredBarsIsMarkedAndLeftOutOfTheAggregate()
    {
        string windows = Listed((Day(-300), Day(-200), Day(-199), Day(-100)), (Day(50), Day(199), Day(200), Day(260)));
        var (_, result) = await WalkOkAsync(Args(windows, objective: "totalReturn"));

        JsonElement[] listed = result.GetProperty("windows").EnumerateArray().ToArray();
        Assert.True(listed[0].GetProperty("isInsufficientData").GetBoolean());
        Assert.Equal("NoInSampleBars", listed[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("bestParameters").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("oosObjective").ValueKind);
        Assert.False(listed[1].GetProperty("isInsufficientData").GetBoolean());
        Assert.Equal(2, result.GetProperty("windowCount").GetInt32());
        Assert.Equal(1, result.GetProperty("evaluatedWindowCount").GetInt32());
        Assert.Equal(1, result.GetProperty("aggregate").GetProperty("evaluatedWindowCount").GetInt32());
    }

    [Fact]
    public async Task WhenNoWindowCanBeEvaluatedTheWindowsAreReturnedMarkedAndTheAggregateIsNull()
    {
        string windows = Listed((Day(-300), Day(-200), Day(-199), Day(-100)));
        var (_, result) = await WalkOkAsync(Args(windows));

        Assert.Equal(1, result.GetProperty("windows").GetArrayLength());
        Assert.True(result.GetProperty("windows")[0].GetProperty("isInsufficientData").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("aggregate").ValueKind);
        Assert.Equal(0, result.GetProperty("evaluatedWindowCount").GetInt32());
        Assert.Contains(result.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("No window"));
    }

    [Fact]
    public async Task AnObjectiveThatIsNeverValidMarksEveryWindowAndIsNotAnError()
    {
        // Over 2024-02-01 .. 2024-12-30 the sine strategy never loses a trade, so ProfitFactor is PositiveInfinity (not Valid) for every period.
        var (_, result) = await WalkOkAsync(Args(Listed((Day(31), Day(364), Day(365), Day(365))), objective: "profitFactor"));

        Assert.All(result.GetProperty("windows").EnumerateArray(), w =>
        {
            Assert.True(w.GetProperty("isInsufficientData").GetBoolean());
            Assert.Equal("NoValidInSampleObjective", w.GetProperty("status").GetString());
        });
        Assert.Equal(JsonValueKind.Null, result.GetProperty("aggregate").ValueKind);
    }

    [Fact]
    public async Task AWindowWhoseOutOfSamplePartHasNoStoredBarKeepsItsInSampleResultAndHasNullOutOfSampleValues()
    {
        var (_, result) = await WalkOkAsync(Args(Listed((Day(50), Day(199), Day(500), Day(520))), objective: "totalReturn"));

        JsonElement window = result.GetProperty("windows")[0];
        Assert.Equal("NoOutOfSampleBars", window.GetProperty("status").GetString());
        Assert.False(window.GetProperty("isInsufficientData").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, window.GetProperty("bestParameters").ValueKind);
        Assert.NotNull(Value(window.GetProperty("isObjective")));
        Assert.Equal(JsonValueKind.Null, window.GetProperty("oosObjective").ValueKind);
        Assert.Equal(JsonValueKind.Null, window.GetProperty("oosTotalTrades").ValueKind);
        JsonElement aggregate = result.GetProperty("aggregate");
        Assert.Equal(JsonValueKind.Null, aggregate.GetProperty("meanOosObjective").ValueKind);
        Assert.Equal(JsonValueKind.Null, aggregate.GetProperty("barSharpeEfficiency").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, aggregate.GetProperty("meanIsObjective").ValueKind);
    }

    // ---- minimum in-sample trades (owner decision 2026-10-30) --------------------------------------------------------------------------------------

    [Fact]
    public async Task AWindowWhereNoCombinationTradedInSampleIsMarkedNoInSampleTradesAndIsNotRunOutOfSample()
    {
        // Five bars from the start of the data: no SMA(6..8) is defined yet, so no combination trades and totalReturn would be a Valid 0 for all of them.
        var (_, result) = await WalkOkAsync(Args(Listed((Day(0), Day(4), Day(5), Day(40))), Ranges("6", "8"), "totalReturn"));

        JsonElement window = result.GetProperty("windows")[0];
        Assert.Equal("NoInSampleTrades", window.GetProperty("status").GetString());
        Assert.True(window.GetProperty("isInsufficientData").GetBoolean());
        Assert.Equal(JsonValueKind.Null, window.GetProperty("bestParameters").ValueKind);
        Assert.Equal(JsonValueKind.Null, window.GetProperty("oosObjective").ValueKind);
        Assert.Equal(JsonValueKind.Null, window.GetProperty("oosTotalTrades").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("aggregate").ValueKind);
        Assert.Equal(1, result.GetProperty("minInSampleTrades").GetInt32());
    }

    [Fact]
    public async Task MinInSampleTradesZeroKeepsAZeroTradeCombinationEligible()
    {
        var (_, result) = await WalkOkAsync(Args(Listed((Day(0), Day(4), Day(5), Day(40))), Ranges("6", "8"), "totalReturn", ",\"minInSampleTrades\":0"));

        JsonElement window = result.GetProperty("windows")[0];
        Assert.Equal("Evaluated", window.GetProperty("status").GetString());
        Assert.Equal(0, window.GetProperty("isTotalTrades").GetInt32());
        Assert.Equal(0, result.GetProperty("minInSampleTrades").GetInt32());
    }

    [Fact]
    public async Task ACombinationWithoutInSampleTradesIsNeverChosenAsTheBestOne()
    {
        // 41 bars from the start: SMA(3) trades, SMA(40) is defined only at the last bar and does not. Minimize would pick a zero-trade combination (return 0).
        var (dispatcher, result) = await WalkOkAsync(Args(Listed((Day(0), Day(40), Day(41), Day(80))), Ranges("3", "40"), "totalReturn", ",\"objectiveDirection\":\"Minimize\""));

        var individual = new List<(int Period, decimal Return, int Trades)>();
        for (int period = 3; period <= 40; period++)
        {
            JsonElement report = Ok(await RunAsync(dispatcher, period.ToString(CultureInfo.InvariantCulture), Day(0), Day(40))).GetProperty("report");
            individual.Add((period, Metric(report, "totalReturn"), report.GetProperty("totalTrades").GetInt32()));
        }

        Assert.Contains(individual, x => x.Trades == 0);
        Assert.Contains(individual, x => x.Trades > 0);
        (int bestPeriod, _, int bestTrades) = individual.Where(x => x.Trades >= 1).OrderBy(x => x.Return).ThenBy(x => x.Period).First();
        JsonElement window = result.GetProperty("windows")[0];
        Assert.Equal((decimal)bestPeriod, window.GetProperty("bestParameters").GetProperty("period").GetDecimal());
        Assert.Equal(bestTrades, window.GetProperty("isTotalTrades").GetInt32());
        Assert.True(window.GetProperty("isTotalTrades").GetInt32() >= 1);
    }

    [Fact]
    public async Task ARaisedMinimumMarksEveryWindowThatTradedLessAndNegativeIsInvalid()
    {
        var (_, result) = await WalkOkAsync(Args(Generator(), objective: "totalReturn", extra: ",\"minInSampleTrades\":1000"));
        McpMessageDispatcher dispatcher = await _host.StartAsync();
        using JsonDocument negative = await WalkAsync(dispatcher, Args(Generator(), extra: ",\"minInSampleTrades\":-1"));

        Assert.All(result.GetProperty("windows").EnumerateArray(), w => Assert.Equal("NoInSampleTrades", w.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("aggregate").ValueKind);
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(negative));
        Assert.Equal("minInSampleTrades", BacktestDiagnosticsTestHost.Param(negative));
    }

    // ---- threads ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOutputIsIdenticalForOneAndManyThreads()
    {
        string arguments = Args(Generator("Rolling", 100, 40, 40), objective: "totalReturn");
        McpMessageDispatcher sequential = await _host.StartAsync(("OptimizationDegreeOfParallelism", "1"));
        McpMessageDispatcher parallel = await _host.StartAsync(("OptimizationDegreeOfParallelism", "8"));

        string one = Ok(await WalkAsync(sequential, arguments)).GetRawText();
        string many = Ok(await WalkAsync(parallel, arguments)).GetRawText();

        Assert.Equal(7, JsonDocument.Parse(one).RootElement.GetProperty("windowCount").GetInt32());
        Assert.Equal(one, many);
    }

    // ---- refusals --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGeneratorThatGivesNoWindowIsInvalid()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await WalkAsync(dispatcher, Args(Generator("Rolling", 300, 200, 10)));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("windows.generator", BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task MoreWindowsThanTheLimitIsLimitExceededAndTheLimitItselfIsAccepted()
    {
        McpMessageDispatcher tight = await _host.StartAsync(("MaxWalkForwardWindows", "6"));
        McpMessageDispatcher exact = await _host.StartAsync(("MaxWalkForwardWindows", "7"));

        using JsonDocument refused = await WalkAsync(tight, Args(Generator()));
        JsonElement accepted = Ok(await WalkAsync(exact, Args(Generator())));

        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(refused));
        Assert.Equal("windows.generator", BacktestDiagnosticsTestHost.Param(refused));
        Assert.Equal(7, accepted.GetProperty("windowCount").GetInt32());
    }

    [Fact]
    public async Task AListLongerThanTheLimitIsLimitExceeded()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync(("MaxWalkForwardWindows", "1"));
        string windows = Listed((Day(0), Day(99), Day(100), Day(139)), (Day(40), Day(139), Day(140), Day(179)));

        using JsonDocument response = await WalkAsync(dispatcher, Args(windows));

        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("windows.list", BacktestDiagnosticsTestHost.Param(response));
    }

    [Theory]
    [InlineData("2024-03-01", "2024-03-10", "2024-03-10", "2024-03-20", "windows.list[0].outOfSampleStart")]
    [InlineData("2024-03-01", "2024-03-10", "2024-03-05", "2024-03-20", "windows.list[0].outOfSampleStart")]
    [InlineData("2024-03-10", "2024-03-01", "2024-03-11", "2024-03-20", "windows.list[0].inSampleStart")]
    [InlineData("2024-03-01", "2024-03-10", "2024-03-20", "2024-03-11", "windows.list[0].outOfSampleStart")]
    public async Task AListedWindowWhoseOutOfSamplePartIsNotAfterTheInSamplePartOrWhoseDatesAreReversedIsInvalid(string isStart, string isEnd, string oosStart, string oosEnd, string param)
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await WalkAsync(dispatcher, Args(Listed((isStart, isEnd, oosStart, oosEnd))));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal(param, BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task ABadDateInAListedWindowIsInvalid()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await WalkAsync(dispatcher, Args(Listed(("2024-3-1", "2024-03-10", "2024-03-11", "2024-03-20"))));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("windows.list[0].inSampleStart", BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task StartAndEndDatesWithAListAreRefusedInsteadOfIgnored()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await WalkAsync(dispatcher, Args(Listed((Day(0), Day(99), Day(100), Day(139))), extra: ",\"startDate\":\"2024-02-01\""));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("startDate", BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task BothOrNeitherOfGeneratorAndListIsInvalid()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();
        string generator = Generator();
        string both = "{" + generator[1..^1] + "," + Listed((Day(0), Day(99), Day(100), Day(139)))[1..];

        using JsonDocument twice = await WalkAsync(dispatcher, Args(both));
        using JsonDocument none = await WalkAsync(dispatcher, Args("{}"));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(twice));
        Assert.Equal("windows", BacktestDiagnosticsTestHost.Param(twice));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(none));
        Assert.Equal("windows", BacktestDiagnosticsTestHost.Param(none));
    }

    [Theory]
    [InlineData("Weekly", 100, 40, 40)]
    [InlineData("Rolling", 0, 40, 40)]
    [InlineData("Rolling", 100, 0, 40)]
    [InlineData("Rolling", 100, 40, 0)]
    [InlineData("Rolling", 100, 40, -5)]
    public async Task ABadModeOrALengthBelowOneIsInvalid(string mode, int inSample, int outOfSample, int step)
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await WalkAsync(dispatcher, Args(Generator(mode, inSample, outOfSample, step)));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.StartsWith("windows.generator", BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task TheRangesObjectiveAndPlaceholderChecksOfTheOptimizerApplyAndRunBeforeAnyData()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument unused = await WalkAsync(dispatcher, Args(Generator(), ranges: """[{"name":"period","start":3,"end":4,"step":1},{"name":"x","start":1,"end":2,"step":1}]"""));
        using JsonDocument sqn = await WalkAsync(dispatcher, Args(Generator(), objective: "sqn"));
        using JsonDocument fractional = await WalkAsync(dispatcher, Args(Generator(), ranges: Ranges("3", "5", "0.5")));
        McpMessageDispatcher tight = await _host.StartAsync(("MaxOptimizationCombinations", "5"));
        using JsonDocument tooMany = await WalkAsync(tight, Args(Generator()));
        using JsonDocument noSymbol = await WalkAsync(dispatcher, Args(Generator()).Replace("\"MSFT\"", "\"NOSUCH\""));

        Assert.Equal("parameterRanges[1].name", BacktestDiagnosticsTestHost.Param(unused));
        Assert.Equal("objective", BacktestDiagnosticsTestHost.Param(sqn));
        Assert.Equal("INVALID_INDICATOR_PARAMETER", BacktestDiagnosticsTestHost.Code(fractional));
        Assert.Contains("With period = 3.5", McpTestSupport.Structured(fractional).GetProperty("message").GetString());
        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(tooMany));
        Assert.Equal("parameterRanges", BacktestDiagnosticsTestHost.Param(tooMany));
        Assert.Equal("SYMBOL_NOT_FOUND", BacktestDiagnosticsTestHost.Code(noSymbol));
    }

    [Fact]
    public async Task APlaceholderInTheSettingsIsSweptPerWindowToo()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();
        string arguments = Args(Generator("Rolling", 100, 40, 80), ranges: """[{"name":"stop","start":0.01,"end":0.03,"step":0.01}]""", conditions: Conditions("5"))
            .Replace("\"windows\":", "\"riskManagement\":{\"stopLossRatio\":{\"$param\":\"stop\"}},\"windows\":");

        JsonElement result = Ok(await WalkAsync(dispatcher, arguments));

        Assert.Equal(3, result.GetProperty("combinationCount").GetInt32());
        Assert.All(result.GetProperty("windows").EnumerateArray(), w => Assert.True(w.GetProperty("bestParameters").TryGetProperty("stop", out _)));
    }

    [Fact]
    public async Task TheToolIsListedWithItsWindowSchema()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument list = JsonDocument.Parse(await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""", CancellationToken.None) ?? "{}");
        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_run_walk_forward_analysis");

        Assert.Contains("barSharpeEfficiency", tool.GetProperty("description").GetString());
        JsonElement windows = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("windows");
        Assert.True(windows.GetProperty("properties").TryGetProperty("generator", out _));
        Assert.True(windows.GetProperty("properties").TryGetProperty("list", out _));
        Assert.Contains("windows", tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }
}
