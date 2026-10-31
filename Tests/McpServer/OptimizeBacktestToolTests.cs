using System.Globalization;
using System.Text.Json;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan 04 / T2: sa_optimize_backtest through the real composition. The stored MSFT closes swing as a sine wave (<see cref="BacktestDiagnosticsTestHost"/>),
/// the strategy is "close above its SMA(period) enters, below exits", so every period gives different trades. Proof (a): each ranked combination equals an
/// individual sa_run_backtest with the same numbers; proof (b): the output does not depend on the degree of parallelism.
/// </summary>
public sealed class OptimizeBacktestToolTests : IDisposable
{
    private const string Param = """{"$param":"period"}""";

    private readonly BacktestDiagnosticsTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static string Sma(string period) => $$$"""{"indicator":"SMA","parameters":{"Period":{{{period}}}}}""";

    private static string Conditions(string period)
        => $$$"""{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{{{Sma(period)}}}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{{{Sma(period)}}}}}""";

    private static string Range(string name, string start, string end, string step) => $$"""{"name":"{{name}}","start":{{start}},"end":{{end}},"step":{{step}}}""";

    private static string Ranges(params string[] ranges) => "[" + string.Join(",", ranges) + "]";

    private static string Args(string conditions, string ranges, string objective = "totalPnL", string extra = "", string period = BacktestDiagnosticsTestHost.Period)
        => $$"""{"symbol":"MSFT","conditions":{{conditions}},"parameterRanges":{{ranges}},"objective":"{{objective}}"{{period}}{{extra}}}""";

    private static string PeriodRanges(string start = "3", string end = "12", string step = "1") => Ranges(Range("period", start, end, step));

    private static async Task<JsonDocument> OptimizeAsync(McpMessageDispatcher dispatcher, string arguments)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_optimize_backtest", arguments))!;

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string conditions, string extra)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{conditions}}{{extra}}}"""))!;

    private static JsonElement Ok(JsonDocument response) => BacktestDiagnosticsTestHost.Result(response);

    private static decimal Metric(JsonElement report, string name) => report.GetProperty(name).GetProperty("value").GetDecimal();

    private static decimal Period(JsonElement entry) => entry.GetProperty("parameters").GetProperty("period").GetDecimal();

    private async Task<(McpMessageDispatcher Dispatcher, JsonElement Result)> OptimizeOkAsync(string arguments)
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();
        return (dispatcher, Ok(await OptimizeAsync(dispatcher, arguments)));
    }

    // ---- proof (a): every ranked combination is the individual run ------------------------------------------------------------------

    [Fact]
    public async Task EveryRankedCombinationEqualsTheIndividualBacktestWithTheSameNumbers()
    {
        var (dispatcher, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges(), extra: ",\"topK\":10"));

        JsonElement ranking = result.GetProperty("ranking");
        Assert.Equal(10, result.GetProperty("combinationCount").GetInt32());
        Assert.Equal(10, ranking.GetArrayLength());
        Assert.Equal(Enumerable.Range(3, 10).Select(p => (decimal)p).OrderBy(p => p), ranking.EnumerateArray().Select(Period).OrderBy(p => p));
        foreach (JsonElement entry in ranking.EnumerateArray())
        {
            string literal = Period(entry).ToString(CultureInfo.InvariantCulture);
            JsonElement report = Ok(await RunAsync(dispatcher, Conditions(literal), BacktestDiagnosticsTestHost.Period)).GetProperty("report");
            Assert.Equal(Metric(report, "totalPnL"), entry.GetProperty("objectiveValue").GetDecimal());
            Assert.Equal(report.GetProperty("totalTrades").GetInt32(), entry.GetProperty("totalTrades").GetInt32());
            foreach (string name in new[] { "totalPnL", "totalReturn", "maxDrawdown", "winRate", "annualizedSharpe" })
            {
                Assert.Equal(Metric(report, name), entry.GetProperty("metrics").GetProperty(name).GetProperty("value").GetDecimal());
            }
        }
    }

    [Fact]
    public async Task AnExpandedNameWhoseBarsIsAParameterEqualsTheIndividualBacktestWithTheSameBars()
    {
        const string RisingBars = """{"left":{"price":"Close"},"comparison":"Rising","bars":{"$param":"bars"}}""";
        static string Rising(string bars) => RisingBars.Replace("""{"$param":"bars"}""", bars);
        static string Strategy(string entry) => $$$"""{"entryLong":{{{entry}}},"exitLong":{{{BacktestDiagnosticsTestHost.Exit}}}}""";

        var (dispatcher, result) = await OptimizeOkAsync(Args(Strategy(RisingBars),Ranges(Range("bars", "1", "3", "1")), extra: ",\"topK\":3"));

        JsonElement ranking = result.GetProperty("ranking");
        Assert.Equal(3, result.GetProperty("combinationCount").GetInt32());
        Assert.Equal(3, ranking.GetArrayLength());
        Assert.Contains(ranking.EnumerateArray(), entry => entry.GetProperty("totalTrades").GetInt32() > 0);
        foreach (JsonElement entry in ranking.EnumerateArray())
        {
            string bars =entry.GetProperty("parameters").GetProperty("bars").GetDecimal().ToString(CultureInfo.InvariantCulture);
            JsonElement report = Ok(await RunAsync(dispatcher, Strategy(Rising(bars)), BacktestDiagnosticsTestHost.Period)).GetProperty("report");
            Assert.Equal(Metric(report, "totalPnL"), entry.GetProperty("objectiveValue").GetDecimal());
            Assert.Equal(report.GetProperty("totalTrades").GetInt32(), entry.GetProperty("totalTrades").GetInt32());
        }
    }

    [Fact]
    public async Task TheRankingIsBestFirstAndTheTradesDiffer()
    {
        var (_, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges(), extra: ",\"topK\":10"));

        decimal[] values = result.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("objectiveValue").GetDecimal()).ToArray();
        Assert.Equal(values.OrderByDescending(v => v), values);
        Assert.True(values.Distinct().Count() > 1);
        Assert.Equal(Enumerable.Range(1, 10), result.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("rank").GetInt32()));
        Assert.Equal("totalPnL", result.GetProperty("objective").GetString());
        Assert.Equal("Maximize", result.GetProperty("objectiveDirection").GetString());
        Assert.Equal(10, result.GetProperty("validCombinationCount").GetInt32());
        Assert.Equal(0, result.GetProperty("invalidCombinationCount").GetInt32());
    }

    [Fact]
    public async Task MinimizeRanksTheLowestValueFirst()
    {
        var (_, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges(), "totalPnL", ",\"objectiveDirection\":\"Minimize\",\"topK\":10"));

        decimal[] values = result.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("objectiveValue").GetDecimal()).ToArray();
        Assert.Equal(values.OrderBy(v => v), values);
        Assert.Equal("Minimize", result.GetProperty("objectiveDirection").GetString());
    }

    [Fact]
    public async Task TheObjectiveNameIsCaseInsensitiveAndEchoedInTheReportSpelling()
    {
        var (_, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges("3", "5"), "AnnualizedSharpe"));

        Assert.Equal("annualizedSharpe", result.GetProperty("objective").GetString());
    }

    // ---- proof (b): the degree of parallelism changes nothing -------------------------------------------------------------------------

    [Fact]
    public async Task TheOutputIsIdenticalForOneAndManyThreads()
    {
        string arguments = Args(Conditions(Param), Ranges(Range("period", "3", "10", "1"), Range("seed", "1", "3", "1")), extra: ",\"topK\":10")
            .Replace("\"conditions\":", "\"report\":{\"bootstrapSeed\":{\"$param\":\"seed\"}},\"conditions\":");
        McpMessageDispatcher sequential = await _host.StartAsync(("OptimizationDegreeOfParallelism", "1"));
        McpMessageDispatcher parallel = await _host.StartAsync(("OptimizationDegreeOfParallelism", "8"));

        string one = Ok(await OptimizeAsync(sequential, arguments)).GetRawText();
        string many = Ok(await OptimizeAsync(parallel, arguments)).GetRawText();

        Assert.Equal(24, JsonDocument.Parse(one).RootElement.GetProperty("combinationCount").GetInt32());
        Assert.Equal(one, many);
    }

    // ---- numbering, ties, several parameters, placeholders in the settings --------------------------------------------------------------

    [Fact]
    public async Task EqualObjectivesKeepTheCombinationOrderAndTheFirstRangeChangesSlowest()
    {
        // The bootstrap seed does not change totalPnL, so every combination ties: the ranking must be the combination order.
        string arguments = Args(Conditions("5"), Ranges(Range("seed", "1", "2", "1"), Range("iterations", "1000", "3000", "1000")), extra: ",\"topK\":6")
            .Replace("\"conditions\":", "\"report\":{\"bootstrapSeed\":{\"$param\":\"seed\"},\"bootstrapIterations\":{\"$param\":\"iterations\"}},\"conditions\":");
        var (_, result) = await OptimizeOkAsync(arguments);

        JsonElement[] ranking = result.GetProperty("ranking").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(0, 6), ranking.Select(e => e.GetProperty("combinationIndex").GetInt32()));
        Assert.Equal(new[] { (1m, 1000m), (1m, 2000m), (1m, 3000m), (2m, 1000m), (2m, 2000m), (2m, 3000m) },
            ranking.Select(e => (e.GetProperty("parameters").GetProperty("seed").GetDecimal(), e.GetProperty("parameters").GetProperty("iterations").GetDecimal())));
        Assert.Equal(1, ranking.Select(e => e.GetProperty("objectiveValue").GetDecimal()).Distinct().Count());
    }

    [Fact]
    public async Task APlaceholderInRiskManagementIsRunAsTheIndividualBacktestWithThatRatio()
    {
        string arguments = Args(Conditions("5"), Ranges(Range("stop", "0.01", "0.03", "0.01")), extra: ",\"topK\":3")
            .Replace("\"conditions\":", "\"riskManagement\":{\"stopLossRatio\":{\"$param\":\"stop\"}},\"conditions\":");
        var (dispatcher, result) = await OptimizeOkAsync(arguments);

        Assert.Equal(3, result.GetProperty("ranking").GetArrayLength());
        foreach (JsonElement entry in result.GetProperty("ranking").EnumerateArray())
        {
            string stop = entry.GetProperty("parameters").GetProperty("stop").GetDecimal().ToString(CultureInfo.InvariantCulture);
            JsonElement report = Ok(await RunAsync(dispatcher, Conditions("5"), BacktestDiagnosticsTestHost.Period + $",\"riskManagement\":{{\"stopLossRatio\":{stop}}}")).GetProperty("report");
            Assert.Equal(Metric(report, "totalPnL"), entry.GetProperty("objectiveValue").GetDecimal());
        }
    }

    [Fact]
    public async Task APlaceholderAsTheRightHandConstantAndTheOffsetWorks()
    {
        string conditions = """{"entryLong":{"left":{"price":"Close","offset":{"$param":"shift"}},"comparison":"GreaterThan","right":{"$param":"level"}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"$param":"level"}}}""";
        var (dispatcher, result) = await OptimizeOkAsync(Args(conditions, Ranges(Range("shift", "0", "2", "1"), Range("level", "98", "100", "2")), extra: ",\"topK\":6"));

        Assert.Equal(6, result.GetProperty("combinationCount").GetInt32());
        JsonElement best = result.GetProperty("ranking")[0];
        string shift = best.GetProperty("parameters").GetProperty("shift").GetDecimal().ToString(CultureInfo.InvariantCulture);
        string level = best.GetProperty("parameters").GetProperty("level").GetDecimal().ToString(CultureInfo.InvariantCulture);
        string literalConditions = "{\"entryLong\":{\"left\":{\"price\":\"Close\",\"offset\":" + shift + "},\"comparison\":\"GreaterThan\",\"right\":" + level
            + "},\"exitLong\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"LessThan\",\"right\":" + level + "}}";
        JsonElement report = Ok(await RunAsync(dispatcher, literalConditions, BacktestDiagnosticsTestHost.Period)).GetProperty("report");
        Assert.Equal(Metric(report, "totalPnL"), best.GetProperty("objectiveValue").GetDecimal());
    }

    // ---- Valid zero, objective roster -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenNoCombinationHasAValidObjectiveTheRankingIsEmptyAndItIsNotAnError()
    {
        // The sine strategy never loses a trade, so ProfitFactor is PositiveInfinity (not Valid) for every combination.
        var (_, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges("3", "5"), "profitFactor"));

        Assert.Equal(0, result.GetProperty("ranking").GetArrayLength());
        Assert.Equal(3, result.GetProperty("combinationCount").GetInt32());
        Assert.Equal(0, result.GetProperty("validCombinationCount").GetInt32());
        Assert.Equal(3, result.GetProperty("invalidCombinationCount").GetInt32());
        Assert.Contains(result.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("profitFactor"));
    }

    [Fact]
    public async Task AnObjectiveOutsideTheRosterIsRefusedAndAnExcludedMetricSaysWhy()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument unknown = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(), "sharpeTimesSqrt252"));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(unknown));
        Assert.Equal("objective", BacktestDiagnosticsTestHost.Param(unknown));

        using JsonDocument excluded = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(), "sqn"));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(excluded));
        Assert.Contains("RiskDataMissing", McpTestSupport.Structured(excluded).GetProperty("message").GetString());
    }

    [Fact]
    public async Task EveryObjectiveOfTheRosterIsAReportMetricAndTheExcludedOnesAreStillNotValidInTheProductionPath()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();
        JsonElement report = Ok(await RunAsync(dispatcher, Conditions("5"), BacktestDiagnosticsTestHost.Period + ",\"riskManagement\":{\"stopLossRatio\":0.05}")).GetProperty("report");

        foreach (string name in OptimizationObjectives.Names)
        {
            Assert.True(report.TryGetProperty(name, out JsonElement metric), $"{name} is not a key of the report JSON.");
            Assert.True(metric.TryGetProperty("status", out _), $"{name} is not a metric.");
        }

        // A metric in Excluded must really be unavailable; when the application makes it Valid, remove the entry.
        foreach (string property in OptimizationObjectives.Excluded.Keys)
        {
            string key = StockAnalyzer.McpServer.Tools.BacktestReportJson.JsonName(property);
            Assert.NotEqual("Valid", report.GetProperty(key).GetProperty("status").GetString());
        }

        Assert.Contains("totalReturn", OptimizationObjectives.Names);
        Assert.Contains("cagr", OptimizationObjectives.Names);
        Assert.Contains("annualizedSharpe", OptimizationObjectives.Names);
        Assert.DoesNotContain("sqn", OptimizationObjectives.Names);
        Assert.Equal(1, OptimizationObjectives.Names.Distinct().Count(n => n == "maxDrawdown"));
    }

    [Fact]
    public async Task TheCapabilitiesListTheObjectivesAndTheExcludedMetricWithItsReason()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        JsonElement capabilities = Ok((await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", """{"indicatorTypeFilter":["RSI"]}"""))!).GetProperty("backtestOptimization");

        Assert.Equal(OptimizationObjectives.Names, capabilities.GetProperty("objectives").EnumerateArray().Select(n => n.GetString()));
        JsonElement excluded = capabilities.GetProperty("excludedObjectives")[0];
        Assert.Equal("sqn", excluded.GetProperty("name").GetString());
        Assert.Contains("RiskDataMissing", excluded.GetProperty("reason").GetString());
    }

    // ---- out of sample ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OutOfSampleRanksOnTheEarlierDaysAndRunsTheReturnedCombinationsOnTheLaterOnes()
    {
        const string split = "2024-09-01";
        var (dispatcher, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges(), extra: $",\"topK\":10,\"outOfSampleStartDate\":\"{split}\""));

        Assert.Equal("2024-08-31", result.GetProperty("period").GetProperty("inSampleEndDate").GetString());
        Assert.Equal(split, result.GetProperty("period").GetProperty("outOfSampleStartDate").GetString());
        foreach (JsonElement entry in result.GetProperty("ranking").EnumerateArray())
        {
            string literal = Period(entry).ToString(CultureInfo.InvariantCulture);
            JsonElement inSample = Ok(await RunAsync(dispatcher, Conditions(literal), ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-08-31\"")).GetProperty("report");
            JsonElement outOfSample = Ok(await RunAsync(dispatcher, Conditions(literal), $",\"startDate\":\"{split}\",\"endDate\":\"2024-12-31\"")).GetProperty("report");
            Assert.Equal(Metric(inSample, "totalPnL"), entry.GetProperty("objectiveValue").GetDecimal());
            Assert.Equal(Metric(outOfSample, "totalPnL"), entry.GetProperty("outOfSample").GetProperty("objectiveValue").GetDecimal());
            Assert.Equal("Valid", entry.GetProperty("outOfSample").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task WithoutOutOfSampleNoEntryCarriesTheKey()
    {
        var (_, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges("3", "4")));

        Assert.All(result.GetProperty("ranking").EnumerateArray(), e => Assert.False(e.TryGetProperty("outOfSample", out _)));
        Assert.False(result.GetProperty("period").TryGetProperty("outOfSampleStartDate", out _));
    }

    [Theory]
    [InlineData("2024-02-01")]
    [InlineData("2024-01-15")]
    [InlineData("2025-01-01")]
    public async Task AnOutOfSampleDateOutsideTheEvaluatedPeriodIsInvalid(string date)
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(), extra: $",\"outOfSampleStartDate\":\"{date}\""));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("outOfSampleStartDate", BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task AnOutOfSampleDateBeyondTheStoredHistoryIsInvalidWhenTheEndDateIsDefaulted()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(), period: ",\"startDate\":\"2024-02-01\"", extra: ",\"outOfSampleStartDate\":\"2030-01-01\""));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("outOfSampleStartDate", BacktestDiagnosticsTestHost.Param(response));
    }

    // ---- sensitivity ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SensitivityIsTheMeanMinAndCountOfTheValidNeighboursOfTheBestCombination()
    {
        var (_, all) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges(), extra: ",\"topK\":10"));
        Dictionary<decimal, decimal> byPeriod = all.GetProperty("ranking").EnumerateArray().ToDictionary(Period, e => e.GetProperty("objectiveValue").GetDecimal());
        JsonElement best = all.GetProperty("ranking")[0];
        decimal bestPeriod = Period(best);
        decimal[] neighbors = new[] { bestPeriod - 1, bestPeriod + 1 }.Where(byPeriod.ContainsKey).Select(p => byPeriod[p]).ToArray();

        JsonElement sensitivity = best.GetProperty("sensitivity");

        Assert.Equal(neighbors.Length, sensitivity.GetProperty("neighborCount").GetInt32());
        Assert.Equal(neighbors.Sum() / neighbors.Length, sensitivity.GetProperty("neighborMean").GetDecimal());
        Assert.Equal(neighbors.Min(), sensitivity.GetProperty("neighborMin").GetDecimal());
    }

    [Fact]
    public async Task AnEdgeCombinationHasOneNeighbourAndALoneCombinationHasNone()
    {
        var (_, edge) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges("3", "5"), extra: ",\"topK\":3"));
        JsonElement first = edge.GetProperty("ranking").EnumerateArray().First(e => Period(e) == 3m);
        var (_, lone) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges("5", "5")));

        Assert.Equal(1, first.GetProperty("sensitivity").GetProperty("neighborCount").GetInt32());
        Assert.Equal(1, lone.GetProperty("combinationCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, lone.GetProperty("ranking")[0].GetProperty("sensitivity").ValueKind);
    }

    // ---- refusals --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MoreCombinationsThanTheLimitIsLimitExceededAndNothingRuns()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync(("MaxOptimizationCombinations", "9"));

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges()));

        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("parameterRanges", BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task TheLimitItselfIsAccepted()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync(("MaxOptimizationCombinations", "10"));

        Assert.Equal(10, Ok(await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges()))).GetProperty("combinationCount").GetInt32());
    }

    [Fact]
    public async Task MoreRangesThanTheLimitIsLimitExceeded()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync(("MaxOptimizationParameters", "1"));
        string conditions = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"$param":"a"}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"$param":"b"}}}""";

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(conditions, Ranges(Range("a", "1", "2", "1"), Range("b", "1", "2", "1"))));

        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal("parameterRanges", BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task AnAstronomicalRangeIsLimitExceededNotACrash()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(Conditions(Param), Ranges(Range("period", "1", "79228162514264337593543950335", "0.0000001"))));

        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(response));
    }

    [Fact]
    public async Task TopKAboveTheLimitIsLimitExceededAndBelowOneIsInvalid()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument above = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(), extra: ",\"topK\":11"));
        using JsonDocument zero = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(), extra: ",\"topK\":0"));

        Assert.Contains(BacktestDiagnosticsTestHost.Code(above), new[] { "LIMIT_EXCEEDED", "INVALID_ARGUMENT" });
        Assert.Equal("topK", BacktestDiagnosticsTestHost.Param(above));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(zero));
    }

    [Fact]
    public async Task AnUnusedRangeAndAnUndefinedPlaceholderAreBothInvalid()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument unused = await OptimizeAsync(dispatcher, Args(Conditions(Param), Ranges(Range("period", "3", "4", "1"), Range("extra", "1", "2", "1"))));
        using JsonDocument undefined = await OptimizeAsync(dispatcher, Args(Conditions("""{"$param":"missing"}"""), PeriodRanges()));

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(unused));
        Assert.Equal("parameterRanges[1].name", BacktestDiagnosticsTestHost.Param(unused));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(undefined));
        Assert.Contains("conditions.entryLong.right.parameters.Period", BacktestDiagnosticsTestHost.Param(undefined));
    }

    [Fact]
    public async Task ABadStepAnEmptyRangeAndADuplicateNameAreInvalid()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument step = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(step: "0")));
        using JsonDocument backwards = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges("9", "3")));
        using JsonDocument twice = await OptimizeAsync(dispatcher, Args(Conditions(Param), Ranges(Range("period", "3", "4", "1"), Range("period", "5", "6", "1"))));

        Assert.Equal("parameterRanges[0].step", BacktestDiagnosticsTestHost.Param(step));
        Assert.Equal("parameterRanges[0].start", BacktestDiagnosticsTestHost.Param(backwards));
        Assert.Equal("parameterRanges[1].name", BacktestDiagnosticsTestHost.Param(twice));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(step));
    }

    [Fact]
    public async Task AFractionalValueForAnIntegerParameterIsRefusedBeforeAnythingRunsAndNamesTheCombination()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges("3", "5", "0.5")));

        string code = BacktestDiagnosticsTestHost.Code(response);
        Assert.Equal("INVALID_INDICATOR_PARAMETER", code); // the code sa_run_backtest gives the same value (plan 04 section 6 says INVALID_ARGUMENT; the existing contract wins)
        Assert.Contains("With period = 3.5", McpTestSupport.Structured(response).GetProperty("message").GetString());
    }

    [Fact]
    public async Task AValueTheEnginesRulesRefuseNamesItsCombination()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges("0", "3")));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Contains("With period = 0", McpTestSupport.Structured(response).GetProperty("message").GetString());
    }

    [Fact]
    public async Task ATemplateIdAndAMissingRequiredArgumentAreInvalid()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument template = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges(), extra: ",\"templateId\":\"00000000-0000-0000-0000-000000000000\""));
        using JsonDocument noObjective = await OptimizeAsync(dispatcher, $$"""{"symbol":"MSFT","conditions":{{Conditions(Param)}},"parameterRanges":{{PeriodRanges()}}}""");
        using JsonDocument noRanges = await OptimizeAsync(dispatcher, $$"""{"symbol":"MSFT","conditions":{{Conditions(Param)}},"objective":"totalPnL"}""");

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(template));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(noObjective));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(noRanges));
    }

    [Fact]
    public async Task AnUnknownSymbolIsSymbolNotFoundAndAnOffListOneNeedsAllowUnregistered()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument response = await OptimizeAsync(dispatcher, Args(Conditions(Param), PeriodRanges()).Replace("\"MSFT\"", "\"NOSUCH\""));

        Assert.Equal("SYMBOL_NOT_FOUND", BacktestDiagnosticsTestHost.Code(response));
    }

    // ---- defaults and the surface --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OmittedDatesAreTheStoredHistoryAndTopKDefaultsToTheLimit()
    {
        var (_, result) = await OptimizeOkAsync(Args(Conditions(Param), PeriodRanges(), period: string.Empty));

        Assert.True(result.GetProperty("period").GetProperty("datesFromStoredHistory").GetBoolean());
        Assert.Equal("2024-01-01", result.GetProperty("period").GetProperty("startDate").GetString());
        Assert.Equal(10, result.GetProperty("ranking").GetArrayLength());
    }

    [Fact]
    public async Task TheToolIsListedAsHeavyAndItsSchemaCarriesTheLimits()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync();

        using JsonDocument list = JsonDocument.Parse(await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""", CancellationToken.None) ?? "{}");
        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_optimize_backtest");

        Assert.Contains("parameterRanges", tool.GetProperty("description").GetString());
        Assert.Equal(10, tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("topK").GetProperty("maximum").GetInt32());
        Assert.Contains("objective", tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }
}
