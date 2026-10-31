using System.Globalization;
using System.Text.Json;
using StockAnalyzer.McpServer.Protocol;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_backtest's optional diagnostics: per-trade MAE/MFE (covered against the window path in BacktestGuiMcpEquivalenceTests), the equity curve,
/// the drawdown episodes and the periodic returns. The numbers themselves are proved in the Core tests; these tests pin the arguments, the limits,
/// the shape, and that omitting the arguments leaves the output as it was.
/// </summary>
public sealed class BacktestDiagnosticsToolTests : IDisposable
{
    private readonly BacktestDiagnosticsTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string extra)
        => BacktestDiagnosticsTestHost.RunAsync(dispatcher, BacktestDiagnosticsTestHost.Period + extra);

    private static JsonElement Result(JsonDocument response) => BacktestDiagnosticsTestHost.Result(response);

    private static (string Date, decimal Equity, decimal? Ratio)[] CurvePoints(JsonElement result)
        => result.GetProperty("equityCurve").GetProperty("points").EnumerateArray()
            .Select(p => (
                p.GetProperty("date").GetString()!,
                p.GetProperty("equity").GetDecimal(),
                p.GetProperty("drawdownRatio").ValueKind == JsonValueKind.Null ? (decimal?)null : p.GetProperty("drawdownRatio").GetDecimal()))
            .ToArray();

    // ---- omitted arguments: the output is what it was ------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheNewArgumentsNoDiagnosticsKeyIsWritten_AndExplicitOffValuesGiveTheSameOutput()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement plain = Result(await RunAsync(dispatcher, ""));
        JsonElement off = Result(await RunAsync(dispatcher, ",\"includeEquityCurve\":\"None\",\"includeDrawdownEpisodes\":0,\"includePeriodicReturns\":false"));

        foreach (string key in new[] { "equityCurve", "drawdownEpisodes", "periodicReturns" })
        {
            Assert.False(plain.TryGetProperty(key, out _), key);
        }

        Assert.Equal(plain.GetRawText(), off.GetRawText());
    }

    [Fact]
    public async Task TheDiagnosticsAreAdditive_EveryOtherKeyOfTheResultIsUnchanged()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement plain = Result(await RunAsync(dispatcher, ""));
        JsonElement full = Result(await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Bar\",\"includeDrawdownEpisodes\":3,\"includePeriodicReturns\":true"));

        foreach (JsonProperty property in plain.EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), full.GetProperty(property.Name).GetRawText());
        }
    }

    // ---- equity curve --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBarCurveHasOnePointPerEvaluatedBar_EndsAtEquityFinal_AndMeasuresTheDrawdownFromTheRunningHigh()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Bar\""));

        JsonElement curve = result.GetProperty("equityCurve");
        var points = CurvePoints(result);
        Assert.Equal("Bar", curve.GetProperty("resolution").GetString());
        Assert.Equal(result.GetProperty("period").GetProperty("evaluatedBarCount").GetInt32(), curve.GetProperty("pointCount").GetInt32());
        Assert.Equal(points.Length, curve.GetProperty("pointCount").GetInt32());
        Assert.Equal(result.GetProperty("equity").GetProperty("final").GetDecimal(), points[^1].Equity);
        Assert.True(string.CompareOrdinal(points[0].Date, "2024-02-01") >= 0);

        decimal high = result.GetProperty("equity").GetProperty("initial").GetDecimal();
        foreach ((string date, decimal equity, decimal? ratio) in points)
        {
            high = Math.Max(high, equity);
            Assert.Equal((equity - high) / high, ratio);
            Assert.True(ratio <= 0m, date);
        }

        Assert.Contains(points, p => p.Ratio < 0m);
    }

    [Fact]
    public async Task TheWeeklyCurveIsTheLastBarOfEachIsoWeekOfTheBarCurve()
    {
        var dispatcher = await _host.StartAsync();

        var bar = CurvePoints(Result(await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Bar\"")));
        JsonElement weeklyResult = Result(await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Weekly\""));
        var weekly = CurvePoints(weeklyResult);

        Assert.Equal("Weekly", weeklyResult.GetProperty("equityCurve").GetProperty("resolution").GetString());
        Assert.True(weekly.Length < bar.Length);
        var byDate = bar.ToDictionary(p => p.Date);
        Assert.All(weekly, point => Assert.Equal(byDate[point.Date], point));
        string[] expectedDates = bar
            .GroupBy(p => System.Globalization.ISOWeek.GetYear(DateTime.ParseExact(p.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)) * 100
                + System.Globalization.ISOWeek.GetWeekOfYear(DateTime.ParseExact(p.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .Select(g => g.Last().Date)
            .ToArray();
        Assert.Equal(expectedDates, weekly.Select(p => p.Date).ToArray());
    }

    [Fact]
    public async Task TheMonthlyCurveHasOnePointPerMonth_AndEndsOnTheLastBar()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Monthly\""));

        var monthly = CurvePoints(result);
        Assert.Equal(11, monthly.Length); // February..December 2024
        Assert.Equal(11, monthly.Select(p => p.Date[..7]).Distinct().Count());
        Assert.Equal(result.GetProperty("equity").GetProperty("final").GetDecimal(), monthly[^1].Equity);
    }

    [Fact]
    public async Task ACurveLongerThanTheLimitIsRefusedNotCut_AndTheHintNamesTheCoarserResolutions()
    {
        var dispatcher = await _host.StartAsync(("MaxEquityCurvePoints", "10"));

        using JsonDocument bar = await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Bar\"");
        using JsonDocument monthly = await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Monthly\"");

        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(bar));
        Assert.Equal("includeEquityCurve", BacktestDiagnosticsTestHost.Param(bar));
        Assert.Contains("Weekly", McpTestSupport.Structured(bar).GetProperty("hint").GetString());
        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(monthly));
    }

    [Fact]
    public async Task ACurveExactlyAtTheLimitIsReturned()
    {
        var dispatcher = await _host.StartAsync(("MaxEquityCurvePoints", "11"));

        JsonElement result = Result(await RunAsync(dispatcher, ",\"includeEquityCurve\":\"Monthly\""));

        Assert.Equal(11, result.GetProperty("equityCurve").GetProperty("pointCount").GetInt32());
    }

    // ---- drawdown episodes ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEpisodesAreRankedByDepth_AndRankOneIsTheReportsMaximumDrawdown()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, ",\"includeDrawdownEpisodes\":3"));

        JsonElement[] episodes = result.GetProperty("drawdownEpisodes").EnumerateArray().ToArray();
        Assert.InRange(episodes.Length, 1, 3);
        Assert.Equal(Enumerable.Range(1, episodes.Length), episodes.Select(e => e.GetProperty("rank").GetInt32()));
        decimal[] depths = episodes.Select(e => e.GetProperty("depthRatio").GetDecimal()).ToArray();
        Assert.Equal(depths.OrderByDescending(d => d), depths);

        JsonElement report = result.GetProperty("report");
        Assert.Equal(report.GetProperty("maxDrawdown").GetProperty("value").GetDecimal(), depths[0], 9);
        JsonElement top = episodes[0];
        decimal peak = top.GetProperty("peakEquity").GetDecimal();
        Assert.Equal((peak - top.GetProperty("troughEquity").GetDecimal()) / peak, depths[0]);
        if (top.GetProperty("recoveryBars").ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(report.GetProperty("maxDepthDrawdownDuration").GetProperty("value").GetDecimal(), top.GetProperty("underwaterBars").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, top.GetProperty("recoveryDate").ValueKind);
            Assert.Equal(JsonValueKind.Null, top.GetProperty("underwaterBars").ValueKind);
        }
    }

    [Fact]
    public async Task AskingForMoreEpisodesThanExistReturnsThemAll_AndZeroAsksForNone()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement all = Result(await RunAsync(dispatcher, ",\"includeDrawdownEpisodes\":5")).GetProperty("drawdownEpisodes");
        JsonElement one = Result(await RunAsync(dispatcher, ",\"includeDrawdownEpisodes\":1")).GetProperty("drawdownEpisodes");

        Assert.Equal(1, one.GetArrayLength());
        Assert.Equal(all[0].GetRawText(), one[0].GetRawText());
    }

    [Fact]
    public async Task AnEpisodeCountAboveTheLimitIsRefused_AndNegativeOrNonsenseIsInvalid()
    {
        var dispatcher = await _host.StartAsync(("MaxDrawdownEpisodes", "2"));

        using JsonDocument above = await RunAsync(dispatcher, ",\"includeDrawdownEpisodes\":3");
        using JsonDocument negative = await RunAsync(dispatcher, ",\"includeDrawdownEpisodes\":-1");
        using JsonDocument text = await RunAsync(dispatcher, ",\"includeDrawdownEpisodes\":\"2\"");

        Assert.Equal("LIMIT_EXCEEDED", BacktestDiagnosticsTestHost.Code(above));
        Assert.Equal("includeDrawdownEpisodes", BacktestDiagnosticsTestHost.Param(above));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(negative));
        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(text));
    }

    // ---- periodic returns ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePeriodicReturnsChainFromTheInitialCapitalToTheFinalEquity()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, ",\"includePeriodicReturns\":true"));

        JsonElement periodic = result.GetProperty("periodicReturns");
        JsonElement[] monthly = periodic.GetProperty("monthly").EnumerateArray().ToArray();
        JsonElement yearly = Assert.Single(periodic.GetProperty("yearly").EnumerateArray());
        Assert.Equal(Enumerable.Range(2, 11), monthly.Select(m => m.GetProperty("month").GetInt32()));
        Assert.All(monthly, m => Assert.False(m.GetProperty("isPartial").GetBoolean())); // 2024-02-01 .. 2024-12-31 cut no month
        Assert.True(yearly.GetProperty("isPartial").GetBoolean()); // the period starts in February, so 2024 is cut
        Assert.Equal(2024, yearly.GetProperty("year").GetInt32());

        decimal initial = result.GetProperty("equity").GetProperty("initial").GetDecimal();
        decimal final = result.GetProperty("equity").GetProperty("final").GetDecimal();
        Assert.Equal(final / initial - 1m, yearly.GetProperty("returnRatio").GetDecimal(), 12);
        decimal compounded = monthly.Aggregate(1m, (product, m) => product * (1m + m.GetProperty("returnRatio").GetDecimal())) - 1m;
        Assert.Equal(yearly.GetProperty("returnRatio").GetDecimal(), compounded, 12);
    }

    [Fact]
    public async Task ATrailingPartialMonthIsMarked()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await BacktestDiagnosticsTestHost.RunAsync(
            dispatcher, ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-03-15\",\"includePeriodicReturns\":true"));

        JsonElement[] monthly = result.GetProperty("periodicReturns").GetProperty("monthly").EnumerateArray().ToArray();
        Assert.Equal(new[] { false, true }, monthly.Select(m => m.GetProperty("isPartial").GetBoolean()));
    }

    // ---- weekly and monthly bars ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(",\"includePeriodicReturns\":true", "includePeriodicReturns")]
    [InlineData(",\"includeEquityCurve\":\"Monthly\"", "includeEquityCurve")]
    public async Task OnWeeklyBarsMonthAndYearDiagnosticsAreRefusedBeforeAnyDataIsRead(string extra, string parameter)
    {
        var dispatcher = await _host.StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, ",\"timeFrame\":\"W1\"" + extra);

        Assert.Equal("UNSUPPORTED_TIMEFRAME", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal(parameter, BacktestDiagnosticsTestHost.Param(response));
        Assert.Contains("D1", McpTestSupport.Structured(response).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task OnWeeklyBarsTheBarAndWeeklyCurvesAndTheEpisodesAreExactAndAllowed()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, ",\"timeFrame\":\"W1\",\"includeEquityCurve\":\"Bar\",\"includeDrawdownEpisodes\":2"));
        JsonElement weekly = Result(await RunAsync(dispatcher, ",\"timeFrame\":\"W1\",\"includeEquityCurve\":\"Weekly\""));

        var bar = CurvePoints(result);
        Assert.Equal(result.GetProperty("period").GetProperty("evaluatedBarCount").GetInt32(), bar.Length);
        Assert.Equal(result.GetProperty("equity").GetProperty("final").GetDecimal(), bar[^1].Equity);
        Assert.Equal(bar, CurvePoints(weekly)); // every weekly bar is the last bar of its own ISO week
        Assert.False(result.TryGetProperty("periodicReturns", out _));
    }

    [Fact]
    public async Task OnMonthlyBarsTheMonthlyCurveEqualsTheBarCurve_AndThePeriodicReturnsChainFromTheInitialCapital()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, ",\"timeFrame\":\"MN1\",\"includeEquityCurve\":\"Monthly\",\"includePeriodicReturns\":true"));
        JsonElement bar = Result(await RunAsync(dispatcher, ",\"timeFrame\":\"MN1\",\"includeEquityCurve\":\"Bar\""));

        Assert.Equal(CurvePoints(bar), CurvePoints(result));
        JsonElement[] monthly = result.GetProperty("periodicReturns").GetProperty("monthly").EnumerateArray().ToArray();
        JsonElement yearly = Assert.Single(result.GetProperty("periodicReturns").GetProperty("yearly").EnumerateArray());
        Assert.Equal(CurvePoints(result).Length, monthly.Length);
        Assert.Equal(CurvePoints(result).Select(p => int.Parse(p.Date[5..7], CultureInfo.InvariantCulture)), monthly.Select(m => m.GetProperty("month").GetInt32()));
        decimal initial = result.GetProperty("equity").GetProperty("initial").GetDecimal();
        decimal final = result.GetProperty("equity").GetProperty("final").GetDecimal();
        Assert.Equal(final / initial - 1m, yearly.GetProperty("returnRatio").GetDecimal(), 12);
        decimal compounded = monthly.Aggregate(1m, (product, m) => product * (1m + m.GetProperty("returnRatio").GetDecimal())) - 1m;
        Assert.Equal(yearly.GetProperty("returnRatio").GetDecimal(), compounded, 12);
    }

    // ---- empty period and argument errors ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APeriodWithoutBarsGivesEmptyDiagnosticsNotAnError()
    {
        var dispatcher = await _host.StartAsync();

        JsonElement result = Result(await BacktestDiagnosticsTestHost.RunAsync(
            dispatcher, ",\"startDate\":\"2030-01-01\",\"endDate\":\"2030-12-31\",\"includeEquityCurve\":\"Bar\",\"includeDrawdownEpisodes\":2,\"includePeriodicReturns\":true"));

        Assert.Equal(0, result.GetProperty("equityCurve").GetProperty("pointCount").GetInt32());
        Assert.Equal(0, result.GetProperty("equityCurve").GetProperty("points").GetArrayLength());
        Assert.Equal(0, result.GetProperty("drawdownEpisodes").GetArrayLength());
        Assert.Equal(0, result.GetProperty("periodicReturns").GetProperty("monthly").GetArrayLength());
        Assert.Equal(0, result.GetProperty("periodicReturns").GetProperty("yearly").GetArrayLength());
    }

    [Theory]
    [InlineData(",\"includeEquityCurve\":\"Daily\"", "includeEquityCurve")]
    [InlineData(",\"includeEquityCurve\":1", "includeEquityCurve")]
    [InlineData(",\"includePeriodicReturns\":\"yes\"", "includePeriodicReturns")]
    public async Task AWrongTypeOrValueIsRefusedNamingTheArgument(string extra, string parameter)
    {
        var dispatcher = await _host.StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, extra);

        Assert.Equal("INVALID_ARGUMENT", BacktestDiagnosticsTestHost.Code(response));
        Assert.Equal(parameter, BacktestDiagnosticsTestHost.Param(response));
    }

    [Fact]
    public async Task TheToolSchemaStatesTheNewArgumentsAndTheirLimits()
    {
        McpMessageDispatcher dispatcher = await _host.StartAsync(("MaxDrawdownEpisodes", "7"));
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_run_backtest");
        JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal(new[] { "None", "Bar", "Weekly", "Monthly" }, properties.GetProperty("includeEquityCurve").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(7, properties.GetProperty("includeDrawdownEpisodes").GetProperty("maximum").GetInt32());
        Assert.Equal(0, properties.GetProperty("includeDrawdownEpisodes").GetProperty("default").GetInt32());
        Assert.Equal("boolean", properties.GetProperty("includePeriodicReturns").GetProperty("type").GetString());
    }
}
