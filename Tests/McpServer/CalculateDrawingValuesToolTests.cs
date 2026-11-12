using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>sa_calculate_drawing_values: parity with the GUI drawing objects, the three control-point modes, errors and the culture rule.</summary>
public sealed class CalculateDrawingValuesToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "DVTEST";
    private const string Tool = "sa_calculate_drawing_values";
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "dvtest");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + Math.Round((decimal)Math.Sin(i / 5.0) * 10m, 2)).ToList();

    public CalculateDrawingValuesToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, _closes);
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
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string anchorPart, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, Tool, $"{{\"symbol\":\"{Symbol}\",\"timeFrame\":\"D1\",\"tool\":\"{tool}\",{anchorPart}{extra}}}"))!;

    private static JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static (string Code, string? Param) Error(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement structured = McpTestSupport.Structured(response);
        return (structured.GetProperty("code").GetString()!, structured.TryGetProperty("param", out JsonElement param) ? param.GetString() : null);
    }

    private static string DateText(int dayIndex) => FirstDay.AddDays(dayIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- (a) parity with the GUI objects ----

    private static readonly Dictionary<string, Func<ChartPoint[], bool, IChartObject>> Factories = new(StringComparer.Ordinal)
    {
        ["TrendLine"] = (p, _) => new TrendLineObject(p[0], p[1]),
        ["Ray"] = (p, _) => new RayObject(p[0], p[1]),
        ["HorizontalLine"] = (p, _) => new HorizontalLineObject(p[0]),
        ["VerticalLine"] = (p, _) => new VerticalLineObject(p[0]),
        ["Arrow"] = (p, _) => new ArrowObject(p[0], p[1]),
        ["PriceLabel"] = (p, _) => new PriceLabelObject(p[0]),
        ["FibonacciRetracement"] = (p, ext) => new FibonacciRetracementObject(p[0], p[1]) { ShowExtensions = ext },
        ["FibonacciFan"] = (p, _) => new FibonacciFanObject(p[0], p[1]),
        ["FibonacciExpansion"] = (p, _) => new FibonacciExpansionObject(p[0], p[1], p[2]),
        ["FibonacciChannel"] = (p, _) => new FibonacciChannelObject(p[0], p[1], p[2]),
        ["ParallelChannel"] = (p, _) => new ParallelChannelObject(p[0], p[1], p[2]),
        ["GannFan"] = (p, _) => new GannFanObject(p[0], p[1]),
        ["GannBox"] = (p, _) => new GannBoxObject(p[0], p[1]),
        ["SineLine"] = (p, _) => new SineLineObject(p[0], p[1]),
        ["TimeCycles"] = (p, _) => new TimeCyclesObject(p[0], p[1]),
    };

    public static TheoryData<string, bool> EveryTool()
    {
        var data = new TheoryData<string, bool>();
        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            data.Add(descriptor.Name, false);
        }

        data.Add("FibonacciRetracement", true);
        return data;
    }

    private static string AnchorPartFor(DrawingValueDescriptor descriptor) => descriptor.PointCount switch
    {
        1 => "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":10",
        2 => "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":40",
        _ => $"\"anchorMode\":\"Explicit\",\"points\":[{{\"date\":\"{DateText(31)}\",\"price\":100.5}},{{\"date\":\"{DateText(60)}\",\"priceField\":\"High\"}},{{\"date\":\"{DateText(50)}\",\"price\":98.25}}]",
    };

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task EveryTool_ReturnsExactlyWhatTheGuiObjectReturnsForTheSameControlPoints(string toolName, bool showExtensions)
    {
        Assert.True(DrawingValueCatalog.TryGet(toolName, out DrawingValueDescriptor descriptor));
        var dispatcher = await StartAsync();
        string lastDay = DateText(Days - 1);
        string earlierDay = DateText(70);
        string futureDay = "2024-05-20";
        string extra = $",\"evaluationDates\":[\"{lastDay}\",\"{earlierDay}\",\"{futureDay}\"]" + (showExtensions ? ",\"showExtensions\":true" : string.Empty);

        JsonElement result = Result(await CallAsync(dispatcher, toolName, AnchorPartFor(descriptor), extra));

        Assert.Equal(toolName, result.GetProperty("tool").GetString());
        JsonElement[] points = result.GetProperty("controlPoints").EnumerateArray().ToArray();
        Assert.Equal(descriptor.PointCount, points.Length);
        ChartPoint[] chartPoints = points
            .Select(p => new ChartPoint(DateTime.ParseExact(p.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture), p.GetProperty("price").GetDecimal()))
            .ToArray();

        JsonElement[] evaluations = result.GetProperty("evaluations").EnumerateArray().ToArray();
        Assert.Equal(new[] { lastDay, earlierDay, futureDay }, evaluations.Select(e => e.GetProperty("date").GetString()).ToArray());
        Assert.Equal(new[] { true, true, false }, evaluations.Select(e => e.GetProperty("hasBar").GetBoolean()).ToArray());
        Assert.Equal(JsonValueKind.Null, evaluations[2].GetProperty("currentPrice").ValueKind);

        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(DrawingValueFormat.CultureName);
        try
        {
            foreach (JsonElement evaluation in evaluations)
            {
                DateTime timestamp = DateTime.ParseExact(evaluation.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                decimal? currentPrice = evaluation.GetProperty("currentPrice").ValueKind == JsonValueKind.Null ? null : evaluation.GetProperty("currentPrice").GetDecimal();

                IChartObject obj = Factories[toolName](chartPoints, showExtensions);
                try
                {
                    var expected = ((IDrawingCalculatedValuesProvider)obj).GetCalculatedValues(timestamp, currentPrice);
                    JsonElement[] actual = evaluation.GetProperty("values").EnumerateArray().ToArray();

                    Assert.Equal(expected.Count, actual.Length);
                    Assert.NotEmpty(actual);
                    for (int i = 0; i < expected.Count; i++)
                    {
                        Assert.Equal(expected[i].Key, actual[i].GetProperty("key").GetString());
                        Assert.Equal(expected[i].Label, actual[i].GetProperty("label").GetString());
                        Assert.Equal(expected[i].FormattedText, actual[i].GetProperty("text").GetString());
                        JsonElement value = actual[i].GetProperty("value");
                        if (expected[i].NumericValue is { } number)
                        {
                            Assert.Equal(number, value.GetDecimal());
                        }
                        else
                        {
                            Assert.Equal(JsonValueKind.Null, value.ValueKind);
                        }
                    }
                }
                finally
                {
                    (obj as IDisposable)?.Dispose();
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task DefaultEvaluation_IsTheLastLoadedBar_WithItsCloseAsCurrentPrice()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "HorizontalLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5"));

        JsonElement evaluation = Assert.Single(result.GetProperty("evaluations").EnumerateArray());
        Assert.Equal(DateText(Days - 1), evaluation.GetProperty("date").GetString());
        Assert.True(evaluation.GetProperty("hasBar").GetBoolean());
        Assert.Equal(_closes[^1], evaluation.GetProperty("currentPrice").GetDecimal());
        Assert.Equal(Days, result.GetProperty("barCount").GetInt32());
        Assert.False(result.TryGetProperty("warnings", out _));
    }

    // ---- (b) RangeExtremes ----

    [Fact]
    public async Task RangeExtremes_TiesChooseTheFirstBar_AndTheEarlierBarIsTheFirstPoint()
    {
        // lows are close-0.5: minimum 89.5 on days 1 and 3 -> day 1; highs are close+0.5: maximum 110.5 on days 4 and 5 -> day 4.
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, new List<decimal> { 100m, 90m, 95m, 90m, 110m, 110m, 105m });
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":7"));

        JsonElement[] points = result.GetProperty("controlPoints").EnumerateArray().ToArray();
        Assert.Equal(new[] { DateText(1), DateText(4) }, points.Select(p => p.GetProperty("date").GetString()).ToArray());
        Assert.Equal(new[] { 89.5m, 110.5m }, points.Select(p => p.GetProperty("price").GetDecimal()).ToArray());
        Assert.Equal(new[] { "Low", "High" }, points.Select(p => p.GetProperty("source").GetString()).ToArray());
        Assert.Equal(7, result.GetProperty("anchorBars").GetInt32());
    }

    [Fact]
    public async Task RangeExtremes_WhenTheHighIsBeforeTheLow_TheHighComesFirst()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, new List<decimal> { 100m, 120m, 110m, 90m, 95m });
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":5"));

        Assert.Equal(new[] { "High", "Low" }, result.GetProperty("controlPoints").EnumerateArray().Select(p => p.GetProperty("source").GetString()).ToArray());
    }

    [Fact]
    public async Task RangeExtremes_HighestHighAndLowestLowOnTheSameBar_IsInsufficientMarketData()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, Enumerable.Repeat(100m, 10).ToList());
        var dispatcher = await StartAsync();

        (string code, string? param) = Error(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":5"));

        Assert.Equal("INSUFFICIENT_MARKET_DATA", code);
        Assert.Equal("anchorBars", param);
    }

    // ---- (c) RangeEndpoints ----

    [Fact]
    public async Task RangeEndpoints_UsesTheFirstAndLastBarOfTheWindow_WithTheChosenPriceField()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":10,\"priceField\":\"High\""));

        JsonElement[] points = result.GetProperty("controlPoints").EnumerateArray().ToArray();
        Assert.Equal(new[] { DateText(Days - 10), DateText(Days - 1) }, points.Select(p => p.GetProperty("date").GetString()).ToArray());
        Assert.Equal(new[] { _closes[Days - 10] + 0.5m, _closes[^1] + 0.5m }, points.Select(p => p.GetProperty("price").GetDecimal()).ToArray());
        Assert.All(points, p => Assert.Equal("High", p.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task RangeEndpoints_OnePointTool_UsesOnlyTheFirstBarOfTheWindow_AndCloseByDefault()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "HorizontalLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":10"));

        JsonElement point = Assert.Single(result.GetProperty("controlPoints").EnumerateArray());
        Assert.Equal(DateText(Days - 10), point.GetProperty("date").GetString());
        Assert.Equal(_closes[Days - 10], point.GetProperty("price").GetDecimal());
        Assert.Equal("Close", point.GetProperty("source").GetString());
    }

    [Fact]
    public async Task AnchorBarsLeftOut_IsEveryLoadedBar_ForBothRangeModes()
    {
        var dispatcher = await StartAsync();

        JsonElement endpoints = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeEndpoints\""));
        Assert.Equal(Days, endpoints.GetProperty("anchorBars").GetInt32());
        Assert.Equal(new[] { DateText(0), DateText(Days - 1) }, endpoints.GetProperty("controlPoints").EnumerateArray().Select(p => p.GetProperty("date").GetString()).ToArray());

        JsonElement given = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":" + Days));
        JsonElement omitted = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeExtremes\""));
        Assert.Equal(given.GetRawText(), omitted.GetRawText());
    }

    [Fact]
    public async Task AnchorBarsLeftOut_WithTooFewLoadedBars_IsInsufficientMarketData()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, new List<decimal> { 100m });
        var dispatcher = await StartAsync();

        Assert.Equal(("INSUFFICIENT_MARKET_DATA", "anchorBars"), Error(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeExtremes\"")));
        Assert.Equal(("INSUFFICIENT_MARKET_DATA", "anchorBars"), Error(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeEndpoints\"")));
        JsonElement onePoint = Result(await CallAsync(dispatcher, "HorizontalLine", "\"anchorMode\":\"RangeEndpoints\""));
        Assert.Equal(1, onePoint.GetProperty("anchorBars").GetInt32());
    }

    [Fact]
    public async Task TimeCycles_WithoutEvaluationDates_EvaluatesOnTheSecondPoint_AndNextCycleIsOneCycleLater()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "TimeCycles", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":10"));

        // The first point is DateText(Days - 10), the second (and the default evaluation date) DateText(Days - 1): a cycle of 9 days, evaluated on the boundary.
        JsonElement values = result.GetProperty("evaluations")[0].GetProperty("values");
        string nextCycle = values.EnumerateArray().Single(v => v.GetProperty("key").GetString() == "NextCycle").GetProperty("text").GetString()!;
        Assert.Equal(DateText(Days - 1 + 9) + " 00:00", nextCycle);
        decimal progress = values.EnumerateArray().Single(v => v.GetProperty("key").GetString() == "CycleProgress").GetProperty("value").GetDecimal();
        Assert.Equal(0m, progress);
    }

    // ---- (g) requestedDate, the period warning of W1/MN1 evaluation dates, and the texts of two errors ----

    private static async Task<JsonDocument> CallFrameAsync(McpMessageDispatcher dispatcher, string timeFrame, string tool, string anchorPart, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, Tool, $"{{\"symbol\":\"{Symbol}\",\"timeFrame\":\"{timeFrame}\",\"tool\":\"{tool}\",{anchorPart}{extra}}}"))!;

    private static string[] Warnings(JsonElement result)
        => result.TryGetProperty("warnings", out JsonElement warnings) ? warnings.EnumerateArray().Select(w => w.GetString()!).ToArray() : Array.Empty<string>();

    [Fact]
    public async Task Explicit_ControlPointsNameTheRequestedDate_InAFixedKeyOrder_AndTheRangeModesDoNot()
    {
        var bars = new List<(DateTime Date, decimal Close)>();
        for (int i = 0; i < Days; i++)
        {
            if (i != 20)
            {
                bars.Add((FirstDay.AddDays(i), _closes[i]));
            }
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromDatedCloses(_fixture.DailyDirectory, Symbol, bars);
        var dispatcher = await StartAsync();

        JsonElement explicitResult = Result(await CallAsync(dispatcher, "TrendLine", $"\"anchorMode\":\"Explicit\",\"points\":[{{\"date\":\"{DateText(20)}\"}},{{\"date\":\"{DateText(30)}\",\"price\":123.4}}]"));
        JsonElement[] points = explicitResult.GetProperty("controlPoints").EnumerateArray().ToArray();
        Assert.Equal(DateText(20), points[0].GetProperty("requestedDate").GetString());
        Assert.Equal(DateText(19), points[0].GetProperty("date").GetString());
        Assert.Equal(DateText(30), points[1].GetProperty("requestedDate").GetString());
        Assert.Equal(DateText(30), points[1].GetProperty("date").GetString());
        Assert.Equal(new[] { "index", "date", "requestedDate", "price", "source" }, points[0].EnumerateObject().Select(p => p.Name).ToArray());

        JsonElement rangeResult = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5"));
        Assert.All(rangeResult.GetProperty("controlPoints").EnumerateArray(), p => Assert.Equal(new[] { "index", "date", "price", "source" }, p.EnumerateObject().Select(q => q.Name).ToArray()));
    }

    [Fact]
    public async Task WeeklyBars_AnEvaluationDateInsideALoadedWeek_WarnsWithTheBarDate_AndChangesNoValue()
    {
        var dispatcher = await StartAsync();
        const string Anchor = "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":2";

        // Every calendar day has a bar, so the weekly bars are dated Mondays: 2024-01-01, 2024-01-08, ... 2024-04-08 (the last bar is 2024-04-09).
        JsonElement inside = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", Anchor, ",\"evaluationDates\":[\"2024-01-10\"]"));
        Assert.False(inside.GetProperty("evaluations")[0].GetProperty("hasBar").GetBoolean());
        Assert.Equal(
            new[] { "evaluationDates[0]: no W1 bar is dated 2024-01-10; the bar of that period is dated 2024-01-08. The value uses the date only to extend the geometry." },
            Warnings(inside));

        JsonElement exact = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", Anchor, ",\"evaluationDates\":[\"2024-01-08\"]"));
        Assert.True(exact.GetProperty("evaluations")[0].GetProperty("hasBar").GetBoolean());
        Assert.Empty(Warnings(exact));

        JsonElement notLoaded = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", Anchor, ",\"evaluationDates\":[\"2024-04-20\"]"));
        Assert.False(notLoaded.GetProperty("evaluations")[0].GetProperty("hasBar").GetBoolean());
        Assert.Empty(Warnings(notLoaded));

        JsonElement farFuture = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", Anchor, ",\"evaluationDates\":[\"0001-01-01\",\"9999-12-31\"]"));
        Assert.All(farFuture.GetProperty("evaluations").EnumerateArray(), e => Assert.False(e.GetProperty("hasBar").GetBoolean()));
        Assert.Empty(Warnings(farFuture));
    }

    [Fact]
    public async Task MonthlyBars_AnEvaluationDateInsideALoadedMonth_WarnsWithTheFirstOfTheMonth()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallFrameAsync(dispatcher, "MN1", "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":2", ",\"evaluationDates\":[\"2024-02-15\"]"));

        Assert.Equal(
            new[] { "evaluationDates[0]: no MN1 bar is dated 2024-02-15; the bar of that period is dated 2024-02-01. The value uses the date only to extend the geometry." },
            Warnings(result));
    }

    [Fact]
    public async Task DailyBars_ADateWithoutABar_HasNoPeriodWarning()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5", ",\"evaluationDates\":[\"2024-04-20\"]"));

        Assert.False(result.GetProperty("evaluations")[0].GetProperty("hasBar").GetBoolean());
        Assert.Empty(Warnings(result));
    }

    // ---- (h) showExtensions false, completeBarsOnly and droppedLeadingBar ----

    private const string TwoBarAnchor = "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":2";

    private static readonly string[] PartialBarKeys = { "completeBarsOnly", "referenceDate", "lastBarPeriodEnd", "lastBarPartial", "droppedPartialBar" };

    [Fact]
    public async Task ShowExtensionsFalse_IsAcceptedForEveryTool_AndTrueIsStillRefusedForOthers()
    {
        var dispatcher = await StartAsync();

        JsonElement accepted = Result(await CallAsync(dispatcher, "TrendLine", TwoBarAnchor, ",\"showExtensions\":false"));
        Assert.Equal(2, accepted.GetProperty("controlPoints").GetArrayLength());
        Assert.Equal(("INVALID_ARGUMENT", "showExtensions"), Error(await CallAsync(dispatcher, "TrendLine", TwoBarAnchor, ",\"showExtensions\":true")));
    }

    [Fact]
    public async Task TheSchema_HasCompleteBarsOnlyAsABooleanWithoutADefault()
    {
        await StartAsync();

        Assert.True(_runtimes.Last().Tools.TryGet(Tool, out RegisteredTool? registered));
        JsonElement property = registered!.Schema.GetProperty("properties").GetProperty("completeBarsOnly");
        Assert.Equal("boolean", property.GetProperty("type").GetString());
        Assert.False(property.TryGetProperty("default", out _));
    }

    [Fact]
    public async Task WeeklyBars_WithoutCompleteBarsOnly_ReportDroppedLeadingBarOnly_AndUseTheRunningWeek()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", TwoBarAnchor));

        Assert.False(result.GetProperty("droppedLeadingBar").GetBoolean());
        Assert.All(PartialBarKeys, key => Assert.False(result.TryGetProperty(key, out _), key));
        Assert.Equal(15, result.GetProperty("barCount").GetInt32());
        Assert.Equal("2024-04-08", result.GetProperty("evaluations")[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task WeeklyBars_CompleteBarsOnlyTrue_LeavesOutTheRunningWeekBeforeTheControlPointsAreChosen()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", TwoBarAnchor, ",\"completeBarsOnly\":true"));

        Assert.Equal(
            new[] { "symbol", "timeFrame", "lookbackBars", "barCount", "droppedLeadingBar", "completeBarsOnly", "referenceDate", "lastBarPeriodEnd", "lastBarPartial", "droppedPartialBar", "tool" },
            result.EnumerateObject().Select(p => p.Name).Take(11).ToArray());
        Assert.Equal(14, result.GetProperty("barCount").GetInt32());
        Assert.False(result.GetProperty("droppedLeadingBar").GetBoolean());
        Assert.True(result.GetProperty("completeBarsOnly").GetBoolean());
        Assert.Equal("2024-04-09", result.GetProperty("referenceDate").GetString());
        Assert.Equal("2024-04-07", result.GetProperty("lastBarPeriodEnd").GetString());
        Assert.False(result.GetProperty("lastBarPartial").GetBoolean());
        Assert.True(result.GetProperty("droppedPartialBar").GetBoolean());
        Assert.Equal("2024-04-01", result.GetProperty("evaluations")[0].GetProperty("date").GetString());
        Assert.Equal("2024-04-01", result.GetProperty("controlPoints")[1].GetProperty("date").GetString());
    }

    [Fact]
    public async Task WeeklyBars_CompleteBarsOnlyFalse_KeepsTheRunningWeekAndSaysSo()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", TwoBarAnchor, ",\"completeBarsOnly\":false"));

        Assert.Equal(15, result.GetProperty("barCount").GetInt32());
        Assert.False(result.GetProperty("completeBarsOnly").GetBoolean());
        Assert.True(result.GetProperty("lastBarPartial").GetBoolean());
        Assert.False(result.GetProperty("droppedPartialBar").GetBoolean());
        Assert.Equal("2024-04-14", result.GetProperty("lastBarPeriodEnd").GetString());
    }

    [Fact]
    public async Task WeeklyBars_CompleteBarsOnlyTrue_WithAnEndDateThatCompletesTheWeek_DropsNothing()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallFrameAsync(dispatcher, "W1", "TrendLine", TwoBarAnchor, ",\"completeBarsOnly\":true,\"endDate\":\"2024-04-07\""));

        Assert.False(result.GetProperty("droppedPartialBar").GetBoolean());
        Assert.False(result.GetProperty("lastBarPartial").GetBoolean());
        Assert.Equal("2024-04-07", result.GetProperty("referenceDate").GetString());
        Assert.Equal("2024-04-01", result.GetProperty("evaluations")[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task MonthlyBars_CompleteBarsOnlyTrue_LeavesOutTheRunningMonth()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallFrameAsync(dispatcher, "MN1", "TrendLine", TwoBarAnchor, ",\"completeBarsOnly\":true"));

        Assert.Equal(3, result.GetProperty("barCount").GetInt32());
        Assert.Equal("2024-03-31", result.GetProperty("lastBarPeriodEnd").GetString());
        Assert.True(result.GetProperty("droppedPartialBar").GetBoolean());
        Assert.Equal("2024-03-01", result.GetProperty("evaluations")[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task MonthlyBars_CompleteBarsOnlyTrue_WhenOnlyTheRunningMonthIsStored_IsInsufficientMarketData()
    {
        var dispatcher = await StartAsync();

        Assert.Equal(
            ("INSUFFICIENT_MARKET_DATA", "completeBarsOnly"),
            Error(await CallFrameAsync(dispatcher, "MN1", "TrendLine", TwoBarAnchor, ",\"completeBarsOnly\":true,\"endDate\":\"2024-01-20\"")));
    }

    [Fact]
    public async Task DailyBars_CompleteBarsOnly_IsIgnored_AndTheAnswerHasNoNewKey()
    {
        var dispatcher = await StartAsync();

        JsonElement without = Result(await CallAsync(dispatcher, "TrendLine", TwoBarAnchor));
        JsonElement with = Result(await CallAsync(dispatcher, "TrendLine", TwoBarAnchor, ",\"completeBarsOnly\":true"));

        Assert.Equal(without.GetRawText(), with.GetRawText());
        Assert.False(with.TryGetProperty("droppedLeadingBar", out _));
    }

    [Fact]
    public async Task CompleteBarsOnly_ANonBoolean_IsInvalidArgument()
    {
        var dispatcher = await StartAsync();

        Assert.Equal(("INVALID_ARGUMENT", "completeBarsOnly"), Error(await CallFrameAsync(dispatcher, "W1", "TrendLine", TwoBarAnchor, ",\"completeBarsOnly\":\"yes\"")));
    }

    [Fact]
    public async Task WeeklyBars_CompleteBarsOnlyTrue_AnExplicitDateAfterTheReducedLastBar_IsOutsideTheLoadedBars()
    {
        var dispatcher = await StartAsync();

        Assert.Equal(
            ("INVALID_ARGUMENT", "points[1].date"),
            Error(await CallFrameAsync(dispatcher, "W1", "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\"},{\"date\":\"2024-04-09\"}]", ",\"completeBarsOnly\":true")));
    }

    [Fact]
    public async Task TheSameBarError_NamesTheHighTheLowAndTheirDates()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, Enumerable.Repeat(100m, 10).ToList());
        var dispatcher = await StartAsync();

        JsonElement error = McpTestSupport.Structured(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":5"));

        Assert.Equal("INSUFFICIENT_MARKET_DATA", error.GetProperty("code").GetString());
        Assert.Equal("anchorBars", error.GetProperty("param").GetString());
        Assert.Equal(
            $"The highest high (100.5 on {DateText(5)}) and the lowest low (99.5 on {DateText(5)}) of the anchor window are on the same bar; use a larger anchorBars or anchorMode Explicit.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheDecimalOverflowError_NamesTheControlPointPricesAndTheEvaluationDate()
    {
        var dispatcher = await StartAsync();

        JsonElement error = McpTestSupport.Structured(await CallAsync(
            dispatcher,
            "TrendLine",
            "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\",\"price\":0},{\"date\":\"2024-01-11\",\"price\":5000000000000000000000000000}],\"evaluationDates\":[\"9999-12-31\"]"));

        Assert.Equal("INVALID_ARGUMENT", error.GetProperty("code").GetString());
        Assert.Equal("evaluationDates[0]", error.GetProperty("param").GetString());
        Assert.Equal("The value is outside the decimal range; check the control point prices and evaluationDates[0].", error.GetProperty("message").GetString());
    }

    // ---- (d) Explicit ----

    [Fact]
    public async Task Explicit_ADateWithoutABar_ResolvesToTheLatestEarlierBar_AndWarns()
    {
        var bars = new List<(DateTime Date, decimal Close)>();
        for (int i = 0; i < Days; i++)
        {
            if (i != 20)
            {
                bars.Add((FirstDay.AddDays(i), _closes[i]));
            }
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromDatedCloses(_fixture.DailyDirectory, Symbol, bars);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "TrendLine", $"\"anchorMode\":\"Explicit\",\"points\":[{{\"date\":\"{DateText(20)}\"}},{{\"date\":\"{DateText(30)}\",\"price\":123.4}}]"));

        JsonElement[] points = result.GetProperty("controlPoints").EnumerateArray().ToArray();
        Assert.Equal(DateText(19), points[0].GetProperty("date").GetString());
        Assert.Equal(_closes[19], points[0].GetProperty("price").GetDecimal());
        Assert.Equal("Close", points[0].GetProperty("source").GetString());
        Assert.Equal(DateText(30), points[1].GetProperty("date").GetString());
        Assert.Equal(123.4m, points[1].GetProperty("price").GetDecimal());
        Assert.Equal("Explicit", points[1].GetProperty("source").GetString());
        Assert.False(result.TryGetProperty("anchorBars", out _));

        string warning = Assert.Single(result.GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.Equal($"points[0].date {DateText(20)} has no stored bar; resolved to {DateText(19)}.", warning);
    }

    [Fact]
    public async Task Explicit_PriceFieldOfThePointWinsOverTheTopLevelField_AndNegativeAndZeroPricesAreAccepted()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "Arrow",
            $"\"anchorMode\":\"Explicit\",\"priceField\":\"Open\",\"points\":[{{\"date\":\"{DateText(10)}\",\"priceField\":\"Low\"}},{{\"date\":\"{DateText(20)}\"}}]"));
        JsonElement[] points = result.GetProperty("controlPoints").EnumerateArray().ToArray();
        Assert.Equal(_closes[10] - 0.5m, points[0].GetProperty("price").GetDecimal());
        Assert.Equal("Low", points[0].GetProperty("source").GetString());
        Assert.Equal(_closes[20] - 0.2m, points[1].GetProperty("price").GetDecimal());
        Assert.Equal("Open", points[1].GetProperty("source").GetString());

        JsonElement signed = Result(await CallAsync(dispatcher, "Arrow",
            $"\"anchorMode\":\"Explicit\",\"points\":[{{\"date\":\"{DateText(10)}\",\"price\":0}},{{\"date\":\"{DateText(20)}\",\"price\":-25.5}}]"));
        Assert.Equal(0m, signed.GetProperty("controlPoints")[0].GetProperty("price").GetDecimal());
        Assert.Equal(-25.5m, signed.GetProperty("controlPoints")[1].GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Explicit_TwoPointsOnTheSameBar_AreAccepted_AndAnEmptyResultIsReportedWithAWarning()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, "FibonacciFan", $"\"anchorMode\":\"Explicit\",\"points\":[{{\"date\":\"{DateText(10)}\",\"price\":100}},{{\"date\":\"{DateText(10)}\",\"price\":110}}]"));

        Assert.Empty(result.GetProperty("evaluations")[0].GetProperty("values").EnumerateArray());
        Assert.Equal("evaluationDates[0]: the tool returned no values for these control points.", Assert.Single(result.GetProperty("warnings").EnumerateArray()).GetString());
    }

    // ---- (e) causality ----

    [Fact]
    public async Task MutationTest_BarsAfterEndDateDoNotChangeTheResult()
    {
        const int cutIndex = 60;
        string endDate = DateText(cutIndex);
        string args = "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":30";
        string extra = $",\"endDate\":\"{endDate}\"";
        var dispatcher = await StartAsync();

        JsonElement before = Result(await CallAsync(dispatcher, "FibonacciRetracement", args, extra));

        var mutated = new List<decimal>(_closes);
        for (int i = cutIndex + 1; i < mutated.Count; i++)
        {
            mutated[i] = 9999m + i;
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, mutated);
        JsonElement after = Result(await CallAsync(dispatcher, "FibonacciRetracement", args, extra));

        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal(endDate, before.GetProperty("evaluations")[0].GetProperty("date").GetString());
        Assert.Equal(cutIndex + 1, before.GetProperty("barCount").GetInt32());
    }

    // ---- (f) errors ----

    public static TheoryData<string, string, string, string?> ErrorCases() => new()
    {
        // tool, anchor part (and extra arguments), expected code, expected param
        { "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":1", "INVALID_ARGUMENT", "anchorBars" },
        { "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":0", "INVALID_ARGUMENT", "anchorBars" },
        { "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":10001", "LIMIT_EXCEEDED", "anchorBars" },
        { "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":101", "INSUFFICIENT_MARKET_DATA", "anchorBars" },
        { "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":10,\"priceField\":\"Close\"", "INVALID_ARGUMENT", "priceField" },
        { "TrendLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":10,\"points\":[]", "INVALID_ARGUMENT", "points" },
        { "HorizontalLine", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":10", "INVALID_ARGUMENT", "anchorMode" },
        { "FibonacciExpansion", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":10", "INVALID_ARGUMENT", "anchorMode" },
        { "FibonacciExpansion", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":10", "INVALID_ARGUMENT", "anchorMode" },
        { "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":1", "INVALID_ARGUMENT", "anchorBars" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"anchorBars\":10,\"points\":[]", "INVALID_ARGUMENT", "anchorBars" },
        { "TrendLine", "\"anchorMode\":\"Explicit\"", "INVALID_ARGUMENT", "points" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\"}]", "INVALID_ARGUMENT", "points" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\"},{\"date\":\"2024-01-11\"},{\"date\":\"2024-01-12\"}]", "INVALID_ARGUMENT", "points" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024/01/10\"},{\"date\":\"2024-01-11\"}]", "INVALID_ARGUMENT", "points[0].date" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2023-12-31\"},{\"date\":\"2024-01-11\"}]", "INVALID_ARGUMENT", "points[0].date" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\"},{\"date\":\"2024-04-10\"}]", "INVALID_ARGUMENT", "points[1].date" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\",\"price\":1,\"priceField\":\"High\"},{\"date\":\"2024-01-11\"}]", "INVALID_ARGUMENT", "points[0]" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\"},{\"date\":\"2024-01-11\",\"price\":1e400}]", "INVALID_ARGUMENT", "points[1].price" },
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\"},{\"date\":\"2024-01-11\",\"price\":1e40}]", "INVALID_ARGUMENT", "points[1].price" },
        { "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5,\"evaluationDates\":[]", "INVALID_ARGUMENT", "evaluationDates" },
        { "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5,\"evaluationDates\":[\"2024-01-10\",\"2024-01-10\"]", "INVALID_ARGUMENT", "evaluationDates[1]" },
        { "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5,\"evaluationDates\":[\"10/01/2024\"]", "INVALID_ARGUMENT", "evaluationDates[0]" },
        { "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5,\"showExtensions\":true", "INVALID_ARGUMENT", "showExtensions" },
        // 5e27 over one day, extended to the year 9999 (about 3 million days) is far outside the decimal range.
        { "TrendLine", "\"anchorMode\":\"Explicit\",\"points\":[{\"date\":\"2024-01-10\",\"price\":0},{\"date\":\"2024-01-11\",\"price\":5000000000000000000000000000}],\"evaluationDates\":[\"9999-12-31\"]", "INVALID_ARGUMENT", "evaluationDates[0]" },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task InvalidArguments_AreRejectedWithTheDocumentedCodeAndParameter(string toolName, string anchorPart, string expectedCode, string? expectedParam)
    {
        var dispatcher = await StartAsync();

        (string code, string? param) = Error(await CallAsync(dispatcher, toolName, anchorPart));

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedParam, param);
    }

    [Fact]
    public async Task EvaluationDates_MoreThanTheLimit_IsLimitExceeded_AndTheLimitItselfIsAccepted()
    {
        var limits = new McpLimits();
        var dispatcher = await StartAsync();
        string Dates(int count) => string.Join(",", Enumerable.Range(0, count).Select(i => $"\"{DateText(i)}\""));

        JsonElement ok = Result(await CallAsync(dispatcher, "HorizontalLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5", $",\"evaluationDates\":[{Dates(limits.MaxDrawingEvaluationDates)}]"));
        (string code, string? param) = Error(await CallAsync(dispatcher, "HorizontalLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":5", $",\"evaluationDates\":[{Dates(limits.MaxDrawingEvaluationDates + 1)}]"));

        Assert.Equal(limits.MaxDrawingEvaluationDates, ok.GetProperty("evaluations").GetArrayLength());
        Assert.Equal("LIMIT_EXCEEDED", code);
        Assert.Equal("evaluationDates", param);
    }

    [Fact]
    public async Task UnknownSymbolAndMissingRequiredArguments_AreErrors()
    {
        var dispatcher = await StartAsync();

        Assert.Equal("SYMBOL_NOT_FOUND", Error((await McpTestSupport.CallAsync(dispatcher, Tool, "{\"symbol\":\"NOSUCH\",\"timeFrame\":\"D1\",\"tool\":\"TrendLine\",\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":3}"))!).Code);
        Assert.Equal("INVALID_ARGUMENT", Error((await McpTestSupport.CallAsync(dispatcher, Tool, $"{{\"symbol\":\"{Symbol}\",\"timeFrame\":\"D1\",\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":3}}"))!).Code);
        Assert.Equal("INVALID_ARGUMENT", Error((await McpTestSupport.CallAsync(dispatcher, Tool, $"{{\"symbol\":\"{Symbol}\",\"timeFrame\":\"D1\",\"tool\":\"Pitchfork\",\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":3}}"))!).Code);
    }

    [Fact]
    public async Task EndDateBeforeTheFirstBar_IsInsufficientMarketData()
    {
        var dispatcher = await StartAsync();

        (string code, string? param) = Error(await CallAsync(dispatcher, "TrendLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":2", ",\"endDate\":\"2023-01-01\""));

        Assert.Equal("INSUFFICIENT_MARKET_DATA", code);
        Assert.Equal("endDate", param);
    }

    // ---- (g) culture ----

    [Fact]
    public async Task ValueTexts_AreTheSameWhateverTheCallerCulture()
    {
        var dispatcher = await StartAsync();
        string anchor = "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":40";
        string extra = ",\"showExtensions\":true";

        JsonElement english = Result(await CallAsync(dispatcher, "FibonacciRetracement", anchor, extra));
        string englishText = english.GetRawText();

        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            JsonElement german = Result(await CallAsync(dispatcher, "FibonacciRetracement", anchor, extra));
            Assert.Equal(englishText, german.GetRawText());
            Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Contains("Fib 23.6%", englishText);
        Assert.Contains("\"text\":\"", englishText);
    }

    // ---- schema ----

    [Fact]
    public async Task ToolIsListed_WithAnAsciiDescription_AStrictSchema_AndTheCatalogAsEnum()
    {
        var dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == Tool);

        Assert.All(tool.GetProperty("description").GetString()!, c => Assert.True(c < 128));
        Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            DrawingValueCatalog.All.Select(d => d.Name).ToArray(),
            tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("tool").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal(new[] { "symbol", "timeFrame", "tool", "anchorMode" }, tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(v => v.GetString()).ToArray());
    }
}
