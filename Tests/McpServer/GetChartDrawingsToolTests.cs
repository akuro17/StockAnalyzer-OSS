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
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>sa_get_chart_drawings (owner decision U-13): saved drawings read as they are, values equal to the chart objects', legacy and unsupported objects handled without failing.</summary>
public sealed class GetChartDrawingsToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "DVTEST";
    private const string Tool = "sa_get_chart_drawings";
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "dvtest");
    private readonly string _drawings = Path.Combine(Path.GetTempPath(), "sa_chart_drawings_" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + Math.Round((decimal)Math.Sin(i / 5.0) * 10m, 2)).ToList();

    public GetChartDrawingsToolTests()
    {
        Directory.CreateDirectory(_drawings);
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, _closes);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
        Directory.Delete(_drawings, recursive: true);
    }

    private async Task<McpMessageDispatcher> StartAsync(IReadOnlyDrawingDocumentSource? source = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            drawingSource: source ?? new FileReadOnlyDrawingDocumentSource(_drawings));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, Tool, $"{{\"symbol\":\"{Symbol}\",\"timeFrame\":\"D1\"{extra}}}"))!;

    private static JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string[] Warnings(JsonElement result)
        => result.TryGetProperty("warnings", out JsonElement warnings) ? warnings.EnumerateArray().Select(w => w.GetString()!).ToArray() : Array.Empty<string>();

    private static DrawingStoredPoint Point(int day, decimal price) => DrawingStoredPoint.CreateUtc(DateTime.SpecifyKind(FirstDay.AddDays(day), DateTimeKind.Utc), price);

    private static DrawingObjectRecord Record(string type, IReadOnlyList<DrawingStoredPoint> points, Dictionary<string, object?>? parameters = null, DrawingCoordinateKind kind = DrawingCoordinateKind.UtcTime)
        => new(Guid.NewGuid(), type, PanelKey.Main, kind, points, parameters ?? new Dictionary<string, object?> { ["thickness"] = 1.5, ["customName"] = type });

    private void WriteDocument(string context, params DrawingObjectRecord[] objects)
    {
        var layer = new DrawingLayerRecord(Guid.NewGuid(), "Default Layer", PanelKey.Main, true, false, objects.Select(o => o.ObjectId).ToArray());
        var state = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey(Symbol, TimeframeType.Daily),
            7,
            new[] { new DrawingContextState(context, new[] { layer }, objects) });
        File.WriteAllText(Path.Combine(_drawings, $"{Symbol}.Daily.json"), JsonSerializer.Serialize(state, DrawingDocumentJsonOptions.Create()));
    }

    private static JsonElement ObjectOf(JsonElement result, Guid id)
        => result.GetProperty("objects").EnumerateArray().Single(o => o.GetProperty("objectId").GetGuid() == id);

    [Fact]
    public async Task NoFile_HasNoDrawings_AndSaysSo()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher));

        Assert.False(result.GetProperty("drawingFileFound").GetBoolean());
        Assert.False(result.GetProperty("legacyFormat").GetBoolean());
        Assert.Empty(result.GetProperty("layers").EnumerateArray());
        Assert.Empty(result.GetProperty("objects").EnumerateArray());
        Assert.Equal("Standard", result.GetProperty("context").GetString());
        Assert.Empty(Warnings(result));
        Assert.Empty(Directory.EnumerateFiles(_drawings));
    }

    [Fact]
    public async Task ALegacyFile_IsReported_WithTheMigrationHint_AndLeftUntouched()
    {
        string path = Path.Combine(_drawings, $"{Symbol}.Daily.json");
        File.WriteAllText(path, "{\"Objects\":{\"Standard\":[]},\"Layers\":{}}");
        byte[] before = File.ReadAllBytes(path);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher));

        Assert.True(result.GetProperty("drawingFileFound").GetBoolean());
        Assert.True(result.GetProperty("legacyFormat").GetBoolean());
        Assert.Empty(result.GetProperty("layers").EnumerateArray());
        Assert.Empty(result.GetProperty("objects").EnumerateArray());
        Assert.Equal(new[] { "Legacy V1 drawing file detected; open and save in the GUI to migrate to V2." }, Warnings(result));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.EnumerateFiles(_drawings));
    }

    [Fact]
    public async Task ADamagedFile_IsDataUnavailable_AndNoCorruptedCopyIsMade()
    {
        string path = Path.Combine(_drawings, $"{Symbol}.Daily.json");
        File.WriteAllText(path, "{ not json");
        var dispatcher = await StartAsync();

        JsonDocument response = await CallAsync(dispatcher);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("DATA_UNAVAILABLE", McpTestSupport.Structured(response).GetProperty("code").GetString());
        Assert.Single(Directory.EnumerateFiles(_drawings));
    }

    [Fact]
    public async Task ACalculableObject_GetsExactlyTheValuesOfTheChartObject_AtTheLastBar()
    {
        var trend = Record("TrendLineObject", new[] { Point(10, 100m), Point(20, 110m) });
        var horizontal = Record("HorizontalLineObject", new[] { Point(30, 105m) });
        var fib = Record("FibonacciRetracementObject", new[] { Point(5, 90m), Point(40, 120m) }, new Dictionary<string, object?> { ["showExtensions"] = true });
        WriteDocument("Standard", trend, horizontal, fib);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher));

        DateTime lastTime = FirstDay.AddDays(Days - 1);
        decimal lastClose = _closes[^1];
        Assert.Equal(lastTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), result.GetProperty("evaluationDate").GetString());
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            AssertValues(ObjectOf(result, trend.ObjectId), new TrendLineObject(new ChartPoint(FirstDay.AddDays(10), 100m), new ChartPoint(FirstDay.AddDays(20), 110m)), lastTime, lastClose);
            AssertValues(ObjectOf(result, horizontal.ObjectId), new HorizontalLineObject(new ChartPoint(FirstDay.AddDays(30), 105m)), lastTime, lastClose);
            AssertValues(ObjectOf(result, fib.ObjectId), new FibonacciRetracementObject(new ChartPoint(FirstDay.AddDays(5), 90m), new ChartPoint(FirstDay.AddDays(40), 120m)) { ShowExtensions = true }, lastTime, lastClose);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void AssertValues(JsonElement item, IChartObject chartObject, DateTime time, decimal close)
    {
        Assert.True(item.GetProperty("valuesSupported").GetBoolean());
        var expected = ((IDrawingCalculatedValuesProvider)chartObject).GetCalculatedValues(time, close);
        JsonElement[] actual = item.GetProperty("calculatedValues").EnumerateArray().ToArray();
        Assert.Equal(expected.Count, actual.Length);
        Assert.NotEmpty(actual);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Key, actual[i].GetProperty("key").GetString());
            Assert.Equal(expected[i].Label, actual[i].GetProperty("label").GetString());
            Assert.Equal(expected[i].FormattedText, actual[i].GetProperty("text").GetString());
        }

        (chartObject as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task TheSavedOptions_AreApplied_ToTheBarBasedTools()
    {
        var regression = Record("RegressionTrendObject", new[] { Point(10, 100m), Point(60, 100m) }, new Dictionary<string, object?> { ["stdDevMultiplier"] = 1.5m, ["extendRight"] = true });
        var vwap = Record("AnchoredVwapObject", new[] { Point(50, 100m) }, new Dictionary<string, object?> { ["showBand1"] = true, ["band1Multiplier"] = 1.0m });
        var pivots = Record("ClassicPivotPointsObject", new[] { Point(80, 100m) }, new Dictionary<string, object?> { ["period"] = "Week", ["extendMode"] = "ExtendBoth" });
        WriteDocument("Standard", regression, vwap, pivots);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher));

        string[] RegressionLabels() => ObjectOf(result, regression.ObjectId).GetProperty("calculatedValues").EnumerateArray().Select(v => v.GetProperty("label").GetString()!).ToArray();
        Assert.Equal("+1.5σ Band", RegressionLabels()[1]);
        string[] vwapKeys = ObjectOf(result, vwap.ObjectId).GetProperty("calculatedValues").EnumerateArray().Select(v => v.GetProperty("key").GetString()!).ToArray();
        Assert.Equal(new[] { "AnchorDate", "AnchoredVWAP", "Upper1", "Lower1" }, vwapKeys);
        JsonElement[] pivotValues = ObjectOf(result, pivots.ObjectId).GetProperty("calculatedValues").EnumerateArray().ToArray();
        Assert.Equal("Week", pivotValues[0].GetProperty("text").GetString());
        Assert.Equal("ExtendBoth", pivotValues[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ObjectsWithoutACalculator_OrWithoutTimeCoordinates_AreReturnedWholeWithNoValues()
    {
        var text = Record("TextObject", new[] { Point(15, 101m) }, new Dictionary<string, object?> { ["text"] = "Buy here", ["fontSize"] = 14 });
        var logicalIndex = Record(
            "TrendLineObject",
            new[] { DrawingStoredPoint.CreateIndex(3m, 100m), DrawingStoredPoint.CreateIndex(9m, 105m) },
            kind: DrawingCoordinateKind.LogicalIndex);
        var oneOfTwoPoints = Record("TrendLineObject", new[] { Point(10, 100m) });
        WriteDocument("Standard", text, logicalIndex, oneOfTwoPoints);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher));

        Assert.Equal(3, result.GetProperty("objects").GetArrayLength());
        foreach (Guid id in new[] { text.ObjectId, logicalIndex.ObjectId, oneOfTwoPoints.ObjectId })
        {
            JsonElement item = ObjectOf(result, id);
            Assert.False(item.GetProperty("valuesSupported").GetBoolean());
            Assert.Empty(item.GetProperty("calculatedValues").EnumerateArray());
            Assert.True(item.GetProperty("points").GetArrayLength() >= 1);
        }

        JsonElement textItem = ObjectOf(result, text.ObjectId);
        Assert.Equal("TextObject", textItem.GetProperty("type").GetString());
        Assert.Equal("Buy here", textItem.GetProperty("parameters").GetProperty("text").GetString());
        Assert.Equal(DateText(15), textItem.GetProperty("points")[0].GetProperty("date").GetString());
        JsonElement indexItem = ObjectOf(result, logicalIndex.ObjectId);
        Assert.Equal("LogicalIndex", indexItem.GetProperty("coordinateKind").GetString());
        Assert.Equal(3m, indexItem.GetProperty("points")[0].GetProperty("xValue").GetDecimal());
        Assert.Empty(Warnings(result));
    }

    private static string DateText(int day) => FirstDay.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task APositionInTheInvalidOrder_IsStillCalculated_AndAWarningNamesIt()
    {
        // Both are the one class LongShortPositionObject; the saved "type" says which tool it is.
        var invalid = Record("LongShortPositionObject", new[] { Point(10, 100m), Point(10, 105m), Point(10, 90m) }, new Dictionary<string, object?> { ["type"] = "LongPosition" });
        var valid = Record("LongShortPositionObject", new[] { Point(10, 100m), Point(10, 105m), Point(10, 90m) }, new Dictionary<string, object?> { ["type"] = "ShortPosition" });
        var unknownKind = Record("LongShortPositionObject", new[] { Point(10, 100m), Point(10, 105m), Point(10, 90m) });
        WriteDocument("Standard", invalid, valid, unknownKind);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher));

        Assert.True(ObjectOf(result, invalid.ObjectId).GetProperty("valuesSupported").GetBoolean());
        Assert.Equal(4, ObjectOf(result, invalid.ObjectId).GetProperty("calculatedValues").GetArrayLength());
        Assert.True(ObjectOf(result, valid.ObjectId).GetProperty("valuesSupported").GetBoolean());
        Assert.False(ObjectOf(result, unknownKind.ObjectId).GetProperty("valuesSupported").GetBoolean());
        string warning = Assert.Single(Warnings(result));
        Assert.Contains(invalid.ObjectId.ToString(), warning);
        Assert.Contains("Stop < Entry < Target", warning);
    }

    [Fact]
    public async Task TheLayersAndTheContexts_AreListed_AndAnUnknownContextGivesAWarning()
    {
        var trend = Record("TrendLineObject", new[] { Point(10, 100m), Point(20, 110m) });
        WriteDocument("Standard", trend);
        var dispatcher = await StartAsync();

        JsonElement standard = Result(await CallAsync(dispatcher));
        JsonElement layer = Assert.Single(standard.GetProperty("layers").EnumerateArray());
        Assert.Equal("Default Layer", layer.GetProperty("name").GetString());
        Assert.True(layer.GetProperty("isVisible").GetBoolean());
        Assert.Equal(trend.ObjectId, Assert.Single(layer.GetProperty("objectIds").EnumerateArray()).GetGuid());
        Assert.Equal(layer.GetProperty("layerId").GetGuid(), ObjectOf(standard, trend.ObjectId).GetProperty("layerId").GetGuid());
        Assert.Equal(new[] { "Standard" }, standard.GetProperty("availableContexts").EnumerateArray().Select(c => c.GetString()).ToArray());

        JsonElement renko = Result(await CallAsync(dispatcher, ",\"context\":\"Renko\""));
        Assert.Empty(renko.GetProperty("objects").EnumerateArray());
        Assert.Contains(Warnings(renko), w => w.Contains("no context 'Renko'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndDate_EvaluatesAtTheLastBarOnOrBeforeIt()
    {
        var trend = Record("TrendLineObject", new[] { Point(10, 100m), Point(20, 110m) });
        WriteDocument("Standard", trend);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, $",\"endDate\":\"{DateText(50)}\""));

        Assert.Equal(DateText(50), result.GetProperty("evaluationDate").GetString());
        // 100 + (50 - 10) / 10 * 10 = 140 at day 50.
        JsonElement value = ObjectOf(result, trend.ObjectId).GetProperty("calculatedValues")[0];
        Assert.Equal("TrendLinePrice", value.GetProperty("key").GetString());
        Assert.Equal(140m, value.GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task AFileWithOnlyUncalculableObjects_NeedsNoBars_AndSucceedsForASymbolWithoutData()
    {
        var text = Record("TextObject", new[] { Point(15, 101m) });
        File.WriteAllText(Path.Combine(_drawings, "NODATA.Daily.json"), JsonSerializer.Serialize(
            new DrawingDocumentState(
                DrawingDocumentState.CurrentSchemaVersion,
                new DrawingDocumentKey("NODATA", TimeframeType.Daily),
                1,
                new[] { new DrawingContextState("Standard", Array.Empty<DrawingLayerRecord>(), new[] { text }) }),
            DrawingDocumentJsonOptions.Create()));
        var dispatcher = await StartAsync();

        JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, "{\"symbol\":\"NODATA\",\"timeFrame\":\"D1\",\"allowUnregistered\":true}"))!;

        // Without stored bars the symbol itself is unknown: the error is the usual one, not a failure of the drawing file.
        if (McpTestSupport.IsError(response))
        {
            Assert.Equal("SYMBOL_NOT_FOUND", McpTestSupport.Structured(response).GetProperty("code").GetString());
        }
        else
        {
            Assert.Equal(1, McpTestSupport.Structured(response).GetProperty("objects").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("H1")]
    [InlineData("M5")]
    public async Task AnIntradayTimeFrame_IsRefused(string timeFrame)
    {
        var dispatcher = await StartAsync();

        JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, $"{{\"symbol\":\"{Symbol}\",\"timeFrame\":\"{timeFrame}\"}}"))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Contains(McpTestSupport.Structured(response).GetProperty("code").GetString(), new[] { "INVALID_ARGUMENT", "UNSUPPORTED_TIMEFRAME" });
    }

    [Fact]
    public async Task TheToolIsListed_WithAnAsciiDescription_AStrictSchema_AndTheStoredTimeFramesOnly()
    {
        var dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == Tool);

        Assert.All(tool.GetProperty("description").GetString()!, c => Assert.True(c < 128));
        Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "D1", "W1", "MN1" }, tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("timeFrame").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal(new[] { "symbol", "timeFrame" }, tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(v => v.GetString()).ToArray());
    }

    // ---- M-2 (owner decision B): lookAheadSuspect with an endDate ----

    [Fact]
    public async Task WithAnEndDate_EveryObjectCarriesLookAheadSuspect_TrueWhenAnyControlPointIsDatedAfterIt_AndNothingIsRemoved()
    {
        decimal level = _closes[60];
        var onTheDay = Record("HorizontalLineObject", new[] { Point(60, level) }); // the endDate itself is not after it
        var earlier = Record("HorizontalLineObject", new[] { Point(10, level) });
        var secondPointLater = Record("TrendLineObject", new[] { Point(40, level), Point(61, level + 1m) });
        var thirdPointLater = Record("ParallelChannelObject", new[] { Point(50, level), Point(55, level + 1m), Point(70, level + 2m) });
        var lateInTheDay = Record("HorizontalLineObject", new[] { DrawingStoredPoint.CreateUtc(DateTime.SpecifyKind(FirstDay.AddDays(60).AddHours(23), DateTimeKind.Utc), level) });
        WriteDocument("Standard", onTheDay, earlier, secondPointLater, thirdPointLater, lateInTheDay);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, $",\"endDate\":\"{DateText(60)}\""));

        Assert.Equal(5, result.GetProperty("objects").GetArrayLength());
        Assert.False(ObjectOf(result, onTheDay.ObjectId).GetProperty("lookAheadSuspect").GetBoolean());
        Assert.False(ObjectOf(result, earlier.ObjectId).GetProperty("lookAheadSuspect").GetBoolean());
        Assert.False(ObjectOf(result, lateInTheDay.ObjectId).GetProperty("lookAheadSuspect").GetBoolean());
        Assert.True(ObjectOf(result, secondPointLater.ObjectId).GetProperty("lookAheadSuspect").GetBoolean());
        Assert.True(ObjectOf(result, thirdPointLater.ObjectId).GetProperty("lookAheadSuspect").GetBoolean());
        Assert.Contains(DrawingAsOfRule.SuspectWarning(2), Warnings(result));
    }

    [Fact]
    public async Task WithoutAnEndDate_NothingIsChecked_NoFieldAndNoWarning()
    {
        var future = Record("TrendLineObject", new[] { Point(40, _closes[40]), Point(Days + 30, _closes[40]) });
        WriteDocument("Standard", future);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher));

        Assert.False(ObjectOf(result, future.ObjectId).TryGetProperty("lookAheadSuspect", out _));
        Assert.DoesNotContain(Warnings(result), warning => warning.Contains("lookAheadSuspect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithAnEndDateAndNoSuspect_TheFieldIsFalseOnEveryObject_AndThereIsNoWarning()
    {
        var past = Record("HorizontalLineObject", new[] { Point(10, _closes[10]) });
        WriteDocument("Standard", past);
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, $",\"endDate\":\"{DateText(60)}\""));

        Assert.False(ObjectOf(result, past.ObjectId).GetProperty("lookAheadSuspect").GetBoolean());
        Assert.DoesNotContain(Warnings(result), warning => warning.Contains("lookAheadSuspect", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAsOfRule_ComparesTheCalendarDateOfThePoint_NotTheStampOfALastBar()
    {
        // A weekly bar is stamped on its Monday (2024-01-08); a point on the Wednesday of that week is inside the period of that bar.
        DrawingObjectRecord wednesday = Record("HorizontalLineObject", new[] { DrawingStoredPoint.CreateUtc(new DateTime(2024, 1, 10, 15, 30, 0, DateTimeKind.Utc), 1m) });

        Assert.False(DrawingAsOfRule.IsAfter(wednesday, new DateOnly(2024, 1, 12))); // endDate Friday of that week
        Assert.False(DrawingAsOfRule.IsAfter(wednesday, new DateOnly(2024, 1, 10))); // the same day
        Assert.True(DrawingAsOfRule.IsAfter(wednesday, new DateOnly(2024, 1, 9)));   // endDate Tuesday
    }

    [Fact]
    public void TheAsOfRule_HasNoDateToCompareForAnObjectWithoutTimeBasedPoints()
    {
        DrawingObjectRecord indexBased = Record("HorizontalLineObject", new[] { DrawingStoredPoint.CreateIndex(5m, 1m) }, kind: DrawingCoordinateKind.LogicalIndex);

        Assert.False(DrawingAsOfRule.IsAfter(indexBased, new DateOnly(2000, 1, 1)));
    }
}
