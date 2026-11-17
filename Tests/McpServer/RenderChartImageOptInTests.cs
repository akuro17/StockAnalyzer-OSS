using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Rendering;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// FU5 (owner decision U-14): sa_render_chart_image with the opt-in extras. A request without them gives the same picture and the same answer as before; Parabolic SAR dots, the Ichimoku lines and
/// cloud, the oscillator histogram and the saved drawings are each checked against the reported mapping.
/// </summary>
public sealed class RenderChartImageOptInTests : IDisposable
{
    private const int Days = 160;
    private const string Symbol = "DVTEST";
    private static readonly DateTime FirstDay = new(2024, 1, 1);
    private const string Frame = "\"symbol\":\"DVTEST\",\"timeFrame\":\"D1\"";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "dvtest");
    private readonly string _drawings = Path.Combine(Path.GetTempPath(), "sa_image_drawings_" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + Math.Round((decimal)Math.Sin(i / 6.0) * 12m, 2)).ToList();

    public RenderChartImageOptInTests()
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

    private async Task<McpMessageDispatcher> StartAsync(IReadOnlyDrawingDocumentSource? source = null, params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            drawingSource: source ?? new FileReadOnlyDrawingDocumentSource(_drawings));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RenderAsync(McpMessageDispatcher dispatcher, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_render_chart_image", $"{{{Frame}{extra}}}"))!;

    private static byte[] PngOf(JsonDocument response)
        => Convert.FromBase64String(McpTestSupport.Result(response).GetProperty("content")[1].GetProperty("data").GetString()!);

    private static JsonElement Ok(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string[] Strings(JsonElement result, string name)
        => result.TryGetProperty(name, out JsonElement list) ? list.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();

    private static double RowOf(JsonElement result, decimal price)
    {
        JsonElement axes = result.GetProperty("axes");
        decimal min = axes.GetProperty("priceMin").GetDecimal();
        decimal max = axes.GetProperty("priceMax").GetDecimal();
        double top = axes.GetProperty("pricePlotTopPx").GetDouble();
        double bottom = axes.GetProperty("pricePlotBottomPx").GetDouble();
        return top + ((bottom - top) * (1 - (double)((price - min) / (max - min))));
    }

    private static bool IsDark(SKColor color) => color.Red < 90 && color.Green < 90 && color.Blue < 90;

    private static bool HasDarkPixelNear(SKBitmap bitmap, double x, double y)
    {
        int cx = (int)Math.Floor(x);
        int cy = (int)Math.Floor(y);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (cx + dx >= 0 && cy + dy >= 0 && cx + dx < bitmap.Width && cy + dy < bitmap.Height && IsDark(bitmap.GetPixel(cx + dx, cy + dy)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- nothing changes unless asked ----

    [Fact]
    public async Task WithoutTheNewArguments_ThePictureAndTheAnswerHaveNoTraceOfThem_AndFalseIsTheSameAsOmitted()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string extra = ",\"barCount\":60,\"overlays\":[{\"id\":\"s\",\"type\":\"SMA\",\"parameters\":{\"Period\":10}}],\"oscillators\":[{\"id\":\"m\",\"type\":\"MACD\"}]";

        using JsonDocument omitted = await RenderAsync(dispatcher, extra);
        using JsonDocument explicitFalse = await RenderAsync(dispatcher, extra + ",\"includeHistogram\":false,\"includeSavedDrawings\":false");
        JsonElement result = Ok(omitted);

        Assert.Equal(PngOf(omitted), PngOf(explicitFalse));
        Assert.Equal(McpTestSupport.Structured(omitted).GetRawText(), McpTestSupport.Structured(explicitFalse).GetRawText());
        foreach (string name in new[] { "includeHistogram", "includeSavedDrawings", "histograms", "drawingsDrawn" })
        {
            Assert.False(result.TryGetProperty(name, out _), name);
        }

        Assert.False(result.GetProperty("colors").TryGetProperty("cloudAlpha", out _));
        Assert.All(result.GetProperty("legend").EnumerateArray(), entry => Assert.False(entry.TryGetProperty("shape", out _)));
    }

    // ---- Parabolic SAR ----

    [Fact]
    public async Task ParabolicSarIsAcceptedAsAnOverlay_AndItsLegendEntryIsDots()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":80,\"overlays\":[{\"id\":\"sar\",\"type\":\"ParabolicSAR\"}]");
        JsonElement result = Ok(response);

        JsonElement entry = Assert.Single(result.GetProperty("legend").EnumerateArray());
        Assert.Equal("sar", entry.GetProperty("id").GetString());
        Assert.Equal("Dots", entry.GetProperty("shape").GetString());
        Assert.Equal(AiChartImageStyle.AuxiliaryHex[0], entry.GetProperty("colorHex").GetString());

        // The dot of the first bar is at the value sa_calculate_indicators gives for it, in the colour of the legend.
        using JsonDocument calculated = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_calculate_indicators",
            $"{{{Frame},\"indicators\":[{{\"id\":\"sar\",\"type\":\"ParabolicSAR\"}}],\"outputTailCount\":80}}"))!;
        JsonElement values = McpTestSupport.Structured(calculated).GetProperty("results").GetProperty("sar").GetProperty("series").EnumerateObject().First().Value;
        decimal first = values[0].GetDecimal();
        using SKBitmap bitmap = SKBitmap.Decode(PngOf(response));
        JsonElement bar = result.GetProperty("bars")[0];
        SKColor pixel = bitmap.GetPixel((int)Math.Floor(bar.GetProperty("xPx").GetDouble()), (int)Math.Floor(RowOf(result, first)));
        Assert.Equal(AiChartImageStyle.Color(AiChartImageStyle.AuxiliaryHex[0]), pixel);
    }

    // ---- Ichimoku ----

    [Fact]
    public async Task IchimokuDrawsItsFourCausalLinesAndTheCloud_AndLeavesTheChikouSpanOutWithANote()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":100,\"overlays\":[{\"id\":\"ichi\",\"type\":\"Ichimoku\"}]");
        JsonElement result = Ok(response);

        Assert.Equal(
            new[] { "ichi.TenkanSen", "ichi.KijunSen", "ichi.SenkouSpanA", "ichi.SenkouSpanB" },
            result.GetProperty("legend").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal(AiChartImageStyle.CloudBullishHex, result.GetProperty("colors").GetProperty("cloudBullishHex").GetString());
        Assert.Equal(AiChartImageStyle.CloudBearishHex, result.GetProperty("colors").GetProperty("cloudBearishHex").GetString());
        Assert.Equal(AiChartImageStyle.CloudAlpha, result.GetProperty("colors").GetProperty("cloudAlpha").GetInt32());
        string[] notes = Strings(result, "notes");
        Assert.Contains("Ichimoku ChikouSpan is omitted because it is non-causal; TenkanSen, KijunSen, SenkouSpanA, SenkouSpanB and the Kumo cloud are drawn.", notes);
        Assert.Contains(notes, note => note.Contains("beyond the last drawn bar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IchimokuInTheOscillatorListIsStillRefusedWithTheRightListNamed_AndAnIndicatorThatLooksAheadStaysRefused()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument asOscillator = await RenderAsync(dispatcher, ",\"oscillators\":[{\"id\":\"ichi\",\"type\":\"Ichimoku\"}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(asOscillator));
        Assert.Equal("oscillators[0].type", McpTestSupport.Structured(asOscillator).GetProperty("param").GetString());
        Assert.Contains("overlays", McpTestSupport.Structured(asOscillator).GetProperty("hint").GetString());

        using JsonDocument zigzag = await RenderAsync(dispatcher, ",\"overlays\":[{\"id\":\"zz\",\"type\":\"ZigZag\",\"parameters\":{\"Threshold\":3}}]");
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(zigzag));
    }

    // ---- the oscillator histogram ----

    [Fact]
    public async Task TheHistogramIsDrawnOnlyWhenAsked_ItsBarsAreTintedAboveAndBelowZero_AndTheAnswerNamesIt()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        const string oscillator = ",\"barCount\":60,\"oscillators\":[{\"id\":\"m\",\"type\":\"MACD\"}]";

        using JsonDocument bare = await RenderAsync(dispatcher, oscillator);
        using JsonDocument withHistogram = await RenderAsync(dispatcher, oscillator + ",\"includeHistogram\":true");
        JsonElement result = Ok(withHistogram);

        Assert.NotEqual(PngOf(bare), PngOf(withHistogram));
        Assert.True(result.GetProperty("includeHistogram").GetBoolean());
        Assert.Equal("m", Assert.Single(result.GetProperty("histograms").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(AiChartImageStyle.HistogramNonNegativeHex, result.GetProperty("colors").GetProperty("histogramNonNegativeHex").GetString());
        Assert.Equal(AiChartImageStyle.HistogramAlpha, result.GetProperty("colors").GetProperty("histogramAlpha").GetInt32());

        // The values come from sa_calculate_indicators; the middle of each bar (between zero and its value) is red where the value is positive and blue where it is negative.
        using JsonDocument calculated = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_calculate_indicators",
            $"{{{Frame},\"indicators\":[{{\"id\":\"m\",\"type\":\"MACD\"}}],\"outputTailCount\":50}}"))!;
        JsonElement histogramSeries = McpTestSupport.Structured(calculated).GetProperty("results").GetProperty("m").GetProperty("series").GetProperty("Histogram");
        JsonElement panel = result.GetProperty("panels").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "m");
        decimal panelMin = panel.GetProperty("valueMin").GetDecimal();
        decimal panelMax = panel.GetProperty("valueMax").GetDecimal();
        double top = panel.GetProperty("topPx").GetDouble();
        double bottom = panel.GetProperty("bottomPx").GetDouble();
        Assert.True(panelMin < 0m && panelMax > 0m);
        double Row(decimal value) => top + ((bottom - top) * (1 - (double)((value - panelMin) / (panelMax - panelMin))));

        using SKBitmap bitmap = SKBitmap.Decode(PngOf(withHistogram));
        JsonElement[] bars = result.GetProperty("bars").EnumerateArray().ToArray();
        int checkedBars = 0;
        int tinted = 0;
        for (int i = 0; i < 50; i++)
        {
            decimal? value = histogramSeries[i].ValueKind == JsonValueKind.Null ? null : histogramSeries[i].GetDecimal();
            if (value is not { } h || Math.Abs(h) < 0.05m)
            {
                continue;
            }

            // The first 10 of the 60 drawn bars are not in the 50 values: the tail is the last 50 bars.
            JsonElement bar = bars[10 + i];
            SKColor pixel = bitmap.GetPixel((int)Math.Floor(bar.GetProperty("xPx").GetDouble()) - 1, (int)Math.Floor(Row(h / 2m)));
            checkedBars++;
            if (h > 0m ? pixel.Red > pixel.Blue + 15 : pixel.Blue > pixel.Red + 15)
            {
                tinted++;
            }
        }

        Assert.True(checkedBars >= 20, $"Only {checkedBars} bars have a histogram value to check.");
        Assert.True(tinted >= checkedBars * 0.8, $"{tinted} of {checkedBars} bars are tinted.");
    }

    [Fact]
    public async Task AnOscillatorWithoutAHistogramGetsANote_WhenAHistogramIsAsked()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":60,\"oscillators\":[{\"id\":\"r\",\"type\":\"RSI\"}],\"includeHistogram\":true");
        JsonElement result = Ok(response);

        Assert.Contains(Strings(result, "notes"), note => note.Contains("'r' (RSI) has no histogram series", StringComparison.Ordinal));
        Assert.False(result.TryGetProperty("histograms", out _));
    }

    // ---- the saved drawings ----

    private static DrawingStoredPoint Point(int day, decimal price) => DrawingStoredPoint.CreateUtc(DateTime.SpecifyKind(FirstDay.AddDays(day), DateTimeKind.Utc), price);

    private static DrawingObjectRecord Record(string type, IReadOnlyList<DrawingStoredPoint> points, Dictionary<string, object?>? parameters = null, PanelKey? panel = null)
        => new(Guid.NewGuid(), type, panel ?? PanelKey.Main, DrawingCoordinateKind.UtcTime, points, parameters ?? new Dictionary<string, object?> { ["thickness"] = 1.5, ["color"] = "#FFFFFFFF" });

    private void WriteDocument(IReadOnlyList<DrawingObjectRecord> visible, IReadOnlyList<DrawingObjectRecord>? hiddenLayer = null, string context = "Standard")
    {
        var layers = new List<DrawingLayerRecord> { new(Guid.NewGuid(), "Default Layer", PanelKey.Main, true, false, visible.Select(o => o.ObjectId).ToArray()) };
        var all = new List<DrawingObjectRecord>(visible);
        if (hiddenLayer is not null)
        {
            layers.Add(new DrawingLayerRecord(Guid.NewGuid(), "Hidden Layer", PanelKey.Main, false, false, hiddenLayer.Select(o => o.ObjectId).ToArray()));
            all.AddRange(hiddenLayer);
        }

        var state = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey(Symbol, TimeframeType.Daily),
            7,
            new[] { new DrawingContextState(context, layers, all) });
        File.WriteAllText(Path.Combine(_drawings, $"{Symbol}.Daily.json"), JsonSerializer.Serialize(state, DrawingDocumentJsonOptions.Create()));
    }

    private void WriteLayers(IReadOnlyList<(string Name, bool Visible, DrawingObjectRecord[] Objects)> layers, params DrawingObjectRecord[] noLayer)
    {
        var records = new List<DrawingObjectRecord>();
        var layerRecords = new List<DrawingLayerRecord>();
        foreach ((string name, bool visible, DrawingObjectRecord[] objects) in layers)
        {
            layerRecords.Add(new DrawingLayerRecord(Guid.NewGuid(), name, PanelKey.Main, visible, false, objects.Select(o => o.ObjectId).ToArray()));
            records.AddRange(objects);
        }

        // The file lists the objects in the opposite order of the layers, so the file order is not the layer order.
        records.Reverse();
        records.AddRange(noLayer);
        var state = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey(Symbol, TimeframeType.Daily),
            7,
            new[] { new DrawingContextState("Standard", layerRecords, records) });
        File.WriteAllText(Path.Combine(_drawings, $"{Symbol}.Daily.json"), JsonSerializer.Serialize(state, DrawingDocumentJsonOptions.Create()));
    }

    private DrawingObjectRecord Horizontal(decimal price) => Record("HorizontalLineObject", new[] { Point(Days - 30, price) });

    [Fact]
    public async Task SavedDrawings_FollowTheLayerOrderThenTheOrderInsideTheLayer_NotTheOrderOfTheFile_AndTheCapKeepsTheFirstOfThem()
    {
        decimal level = _closes[^30];
        DrawingObjectRecord a = Horizontal(level);
        DrawingObjectRecord b = Horizontal(level + 1m);
        DrawingObjectRecord c = Horizontal(level + 2m);
        DrawingObjectRecord d = Horizontal(level + 3m);
        WriteLayers(new[] { ("Back", true, new[] { b, a }), ("Front", true, new[] { d, c }) });
        McpMessageDispatcher dispatcher = await StartAsync(null, ("MaxImageSavedDrawings", "3"));

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":100,\"includeSavedDrawings\":true");
        JsonElement result = Ok(response);

        Assert.Equal(new[] { b.ObjectId, a.ObjectId, d.ObjectId }, result.GetProperty("drawingsDrawn").EnumerateArray().Select(x => x.GetProperty("objectId").GetGuid()));
        Assert.Contains(Strings(result, "warnings"), w => w.StartsWith("1 more saved drawing(s) are not drawn: at most 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnObjectInNoLayerIsNotDrawn_AsInTheChart_AndANoteCountsIt_AndAHiddenLayerIsSkipped()
    {
        decimal level = _closes[^30];
        DrawingObjectRecord inLayer = Horizontal(level);
        DrawingObjectRecord orphan = Horizontal(level + 1m);
        DrawingObjectRecord inHiddenLayer = Horizontal(level + 2m);
        WriteLayers(new[] { ("Shown", true, new[] { inLayer }), ("Hidden", false, new[] { inHiddenLayer }) }, orphan);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":100,\"includeSavedDrawings\":true");
        JsonElement result = Ok(response);

        Assert.Equal(new[] { inLayer.ObjectId }, result.GetProperty("drawingsDrawn").EnumerateArray().Select(x => x.GetProperty("objectId").GetGuid()));
        Assert.Contains(Strings(result, "notes"), n => n.StartsWith("1 saved drawing(s) belong to no layer and are not drawn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADrawingThatStartsBeforeTheDrawnWindow_IsPlacedByTheLoadedBarsLikeTheChart_NotByAnExtrapolation()
    {
        // 30 bars are drawn; the line starts exactly 10 bars before the first drawn bar. The loaded bars (lookbackBars, default MaxCandlesPerRequest) hold that bar, so its position is -10.
        decimal level = _closes[^30];
        var line = Record("TrendLineObject", new[] { Point(Days - 40, level - 2m), Point(Days - 20, level + 2m) });
        WriteLayers(new[] { ("Layer", true, new[] { line }) });
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":30,\"includeSavedDrawings\":true");
        JsonElement result = Ok(response);

        JsonElement axes = result.GetProperty("axes");
        double first = axes.GetProperty("firstBarCenterXPx").GetDouble();
        double spacing = axes.GetProperty("barSpacingPx").GetDouble();
        JsonElement[] points = result.GetProperty("drawingsDrawn")[0].GetProperty("points").EnumerateArray().ToArray();
        Assert.InRange(Math.Abs(points[0].GetProperty("xPx").GetDouble() - (first - (10 * spacing))), 0, 0.1);
        Assert.InRange(Math.Abs(points[1].GetProperty("xPx").GetDouble() - (first + (10 * spacing))), 0, 0.1);
        Assert.Equal(FirstDay.AddDays(Days - 40).ToString("yyyy-MM-dd"), points[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task SavedDrawings_AreDrawnInTheFixedColourRegardlessOfTheirOwn_AndEchoedWithTheirPixels()
    {
        decimal level = _closes[^30];
        var horizontal = Record("HorizontalLineObject", new[] { Point(Days - 30, level) });
        var vertical = Record("VerticalLineObject", new[] { Point(Days - 20, level) });
        var trend = Record("TrendLineObject", new[] { Point(Days - 50, level - 5m), Point(Days - 40, level + 3m) }, new Dictionary<string, object?> { ["extendMode"] = "Right" });
        var ray = Record("RayObject", new[] { Point(Days - 60, level - 8m), Point(Days - 55, level - 6m) });
        var channel = Record("ParallelChannelObject", new[] { Point(Days - 45, level - 4m), Point(Days - 35, level - 2m), Point(Days - 40, level + 2m) });
        var text = Record("TextObject", new[] { Point(Days - 10, level) });
        var hidden = Record("HorizontalLineObject", new[] { Point(Days - 30, level + 7m) }, new Dictionary<string, object?> { ["isVisible"] = false });
        var inHiddenLayer = Record("HorizontalLineObject", new[] { Point(Days - 30, level - 7m) });
        WriteDocument(new[] { horizontal, vertical, trend, ray, channel, text, hidden }, new[] { inHiddenLayer });
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument bare = await RenderAsync(dispatcher, ",\"barCount\":100");
        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":100,\"includeSavedDrawings\":true");
        JsonElement result = Ok(response);

        Assert.NotEqual(PngOf(bare), PngOf(response));
        Assert.True(result.GetProperty("includeSavedDrawings").GetBoolean());
        Assert.Equal(AiChartImageStyle.SavedDrawingHex, result.GetProperty("colors").GetProperty("savedDrawingHex").GetString());
        JsonElement[] drawn = result.GetProperty("drawingsDrawn").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { horizontal.ObjectId, vertical.ObjectId, trend.ObjectId, ray.ObjectId, channel.ObjectId },
            drawn.Select(d => d.GetProperty("objectId").GetGuid()));
        Assert.Equal(new[] { "HorizontalLine", "VerticalLine", "TrendLine", "Ray", "ParallelChannel" }, drawn.Select(d => d.GetProperty("tool").GetString()));
        Assert.Equal(new[] { 1, 1, 2, 2, 3 }, drawn.Select(d => d.GetProperty("points").GetArrayLength()));
        Assert.Contains(Strings(result, "notes"), note => note.StartsWith("1 saved object(s) of other kinds", StringComparison.Ordinal));

        // The horizontal line is at the pixel row of its price, the vertical line at the pixel column of its bar.
        using SKBitmap bitmap = SKBitmap.Decode(PngOf(response));
        JsonElement horizontalPoint = drawn[0].GetProperty("points")[0];
        Assert.InRange(Math.Abs(horizontalPoint.GetProperty("yPx").GetDouble() - RowOf(result, level)), 0, 0.02);
        Assert.True(HasDarkPixelNear(bitmap, result.GetProperty("axes").GetProperty("pricePlotLeftPx").GetDouble() + 4, RowOf(result, level)));
        JsonElement bar = result.GetProperty("bars").EnumerateArray().Single(b => b.GetProperty("date").GetString() == FirstDay.AddDays(Days - 20).ToString("yyyy-MM-dd"));
        double x = drawn[1].GetProperty("points")[0].GetProperty("xPx").GetDouble();
        Assert.InRange(Math.Abs(x - bar.GetProperty("xPx").GetDouble()), 0, 0.02);
        Assert.True(HasDarkPixelNear(bitmap, x, result.GetProperty("axes").GetProperty("pricePlotTopPx").GetDouble() + 6));
    }

    [Fact]
    public async Task SavedDrawings_NoFileLegacyFileOtherContextAndAnUnreadableFile_AreEachHandled()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument none = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true");
        JsonElement noneResult = Ok(none);
        Assert.Contains(ImageSavedDrawings.NotFoundNote, Strings(noneResult, "notes"));
        Assert.Empty(noneResult.GetProperty("drawingsDrawn").EnumerateArray());

        string path = Path.Combine(_drawings, $"{Symbol}.Daily.json");
        File.WriteAllText(path, "{\"Objects\":{\"Standard\":[]},\"Layers\":{}}");
        using JsonDocument legacy = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true");
        Assert.Contains("Legacy V1 drawing file detected; open and save in the GUI to migrate to V2.", Strings(Ok(legacy), "warnings"));

        WriteDocument(new[] { Record("HorizontalLineObject", new[] { Point(Days - 30, _closes[^30]) }) }, null, "Linear");
        using JsonDocument otherContext = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true");
        Assert.Contains(Strings(Ok(otherContext), "notes"), note => note.Contains("has no 'Standard' context", StringComparison.Ordinal));

        File.WriteAllText(path, "{ not json");
        using JsonDocument damaged = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true");
        Assert.Equal(ToolErrorCodes.DataUnavailable, McpTestSupport.ErrorCodeOf(damaged));
        Assert.Single(Directory.EnumerateFiles(_drawings));
    }

    [Fact]
    public async Task OnlyTheFirstMaxImageSavedDrawingsAreDrawn_AndTheRestAreNamedInAWarning()
    {
        var first = Record("HorizontalLineObject", new[] { Point(Days - 30, _closes[^30]) });
        var second = Record("HorizontalLineObject", new[] { Point(Days - 30, _closes[^30] + 1m) });
        var third = Record("HorizontalLineObject", new[] { Point(Days - 30, _closes[^30] + 2m) });
        WriteDocument(new[] { first, second, third });
        McpMessageDispatcher dispatcher = await StartAsync(null, ("MaxImageSavedDrawings", "2"));

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":100,\"includeSavedDrawings\":true");
        JsonElement result = Ok(response);

        Assert.Equal(new[] { first.ObjectId, second.ObjectId }, result.GetProperty("drawingsDrawn").EnumerateArray().Select(d => d.GetProperty("objectId").GetGuid()));
        Assert.Contains("1 more saved drawing(s) are not drawn: at most 2 are drawn (the first in layer order, then in the order inside the layer).", Strings(result, "warnings"));
    }

    [Fact]
    public async Task ADrawingOutsideTheVisiblePartIsCountedInANote_NotListed()
    {
        var inside = Record("HorizontalLineObject", new[] { Point(Days - 30, _closes[^30]) });
        var far = Record("HorizontalLineObject", new[] { Point(Days - 30, 5000m) });
        WriteDocument(new[] { inside, far });
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":100,\"includeSavedDrawings\":true");
        JsonElement result = Ok(response);

        Assert.Equal(new[] { inside.ObjectId }, result.GetProperty("drawingsDrawn").EnumerateArray().Select(d => d.GetProperty("objectId").GetGuid()));
        Assert.Contains("1 saved drawing(s) lie outside the visible part of the price panel and leave no pixels.", Strings(result, "notes"));
    }

    // ---- M-2 (owner decision A): a drawing with a control point dated after endDate is not drawn ----

    private static string EndDateArgument(int day) => $",\"endDate\":\"{FirstDay.AddDays(day):yyyy-MM-dd}\"";

    [Fact]
    public async Task WithAnEndDate_ADrawingWithAnyPointAfterItIsNotDrawn_AndIsCounted_WhileThePointOnTheDayItselfIsKept()
    {
        const int EndDay = Days - 30;
        decimal level = _closes[EndDay];
        DrawingObjectRecord onTheDay = Horizontal(level); // dated on the endDate: kept
        DrawingObjectRecord futureTrend = Record("TrendLineObject", new[] { Point(EndDay - 10, level - 1m), Point(EndDay + 5, level + 1m) });
        DrawingObjectRecord futureHorizontal = Record("HorizontalLineObject", new[] { Point(EndDay + 1, level + 2m) });
        WriteLayers(new[] { ("Layer", true, new[] { onTheDay, futureTrend, futureHorizontal }) });
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true" + EndDateArgument(EndDay));
        JsonElement result = Ok(response);

        Assert.Equal(new[] { onTheDay.ObjectId }, result.GetProperty("drawingsDrawn").EnumerateArray().Select(x => x.GetProperty("objectId").GetGuid()));
        Assert.Equal(2, result.GetProperty("savedDrawingsAfterEndDate").GetInt32());
        Assert.Contains(DrawingAsOfRule.ExcludedWarning(2), Strings(result, "warnings"));
    }

    [Fact]
    public async Task TheFutureTestComesBeforeTheCap_SoADrawingThatCannotBeDrawnTakesNoPlace()
    {
        const int EndDay = Days - 30;
        decimal level = _closes[EndDay];
        DrawingObjectRecord[] future = Enumerable.Range(1, 3).Select(i => Record("HorizontalLineObject", new[] { Point(EndDay + i, level + i) })).ToArray();
        DrawingObjectRecord[] past = Enumerable.Range(1, 3).Select(i => Record("HorizontalLineObject", new[] { Point(EndDay - i, level - i) })).ToArray();
        WriteLayers(new[] { ("Layer", true, future.Concat(past).ToArray()) });
        McpMessageDispatcher dispatcher = await StartAsync(null, ("MaxImageSavedDrawings", "2"));

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true" + EndDateArgument(EndDay));
        JsonElement result = Ok(response);

        // The three future drawings come first in the layer, yet the two places go to the first two drawings that can be drawn.
        Assert.Equal(new[] { past[0].ObjectId, past[1].ObjectId }, result.GetProperty("drawingsDrawn").EnumerateArray().Select(x => x.GetProperty("objectId").GetGuid()));
        Assert.Equal(3, result.GetProperty("savedDrawingsAfterEndDate").GetInt32());
        Assert.Contains("1 more saved drawing(s) are not drawn: at most 2 are drawn (the first in layer order, then in the order inside the layer).", Strings(result, "warnings"));
    }

    [Fact]
    public async Task WithoutAnEndDate_NothingIsLeftOut_AndTheFieldIsAbsent_AndWithoutTheOptInTheFieldIsAbsentToo()
    {
        const int EndDay = Days - 30;
        decimal level = _closes[EndDay];
        DrawingObjectRecord future = Record("HorizontalLineObject", new[] { Point(EndDay + 1, level) });
        WriteLayers(new[] { ("Layer", true, new[] { future }) });
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument noEndDate = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true");
        using JsonDocument noOptIn = await RenderAsync(dispatcher, ",\"barCount\":60" + EndDateArgument(EndDay));

        JsonElement drawn = Ok(noEndDate);
        Assert.Equal(new[] { future.ObjectId }, drawn.GetProperty("drawingsDrawn").EnumerateArray().Select(x => x.GetProperty("objectId").GetGuid()));
        Assert.False(drawn.TryGetProperty("savedDrawingsAfterEndDate", out _));
        Assert.False(Ok(noOptIn).TryGetProperty("savedDrawingsAfterEndDate", out _));
    }

    [Fact]
    public async Task WithAnEndDateAndNoFutureDrawing_TheCountIsZero_AndThereIsNoWarning()
    {
        const int EndDay = Days - 30;
        DrawingObjectRecord onTheDay = Horizontal(_closes[EndDay]);
        WriteLayers(new[] { ("Layer", true, new[] { onTheDay }) });
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RenderAsync(dispatcher, ",\"barCount\":60,\"includeSavedDrawings\":true" + EndDateArgument(EndDay));
        JsonElement result = Ok(response);

        Assert.Equal(0, result.GetProperty("savedDrawingsAfterEndDate").GetInt32());
        Assert.DoesNotContain(Strings(result, "warnings"), warning => warning.Contains("dated after endDate", StringComparison.Ordinal));
    }
}
