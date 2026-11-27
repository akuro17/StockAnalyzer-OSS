using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The range regime diagnostic card (DSL expansion run D-8): the object around <see cref="RangeRegimeDiagnosticCardValues"/>.</summary>
public sealed class RangeRegimeDiagnosticCardObjectTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static List<CoreCandleData> MakeCandles(int count)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (i * 0.2m) + Math.Round((decimal)(Math.Sin(i * 0.25) * 4.0) + (decimal)(Math.Cos(i * 1.7) * 0.8), 4);
            list.Add(new CoreCandleData(BaseTime.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000L + i));
        }

        return list;
    }

    private static LinearCoordinateTransform MakeTransform(int width = 900, int height = 600)
        => new(BaseTime, BaseTime.AddDays(170), 70m, 150m, width, height);

    private static ChartPoint At(List<CoreCandleData> candles, int index) => new(candles[index].Timestamp, candles[index].Close);

    private static RangeRegimeDiagnosticCardObject Create(List<CoreCandleData> candles, int first = 20, int last = 120)
        => new(At(candles, first), At(candles, last));

    private static SKColor[] Snapshot(IChartObject obj)
    {
        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        canvas.ClipRect(new SKRect(0, 0, 900, 600));
        obj.Render(canvas, MakeTransform());
        return bitmap.Pixels;
    }

    private static int CountNonBlack(IChartObject obj) => Snapshot(obj).Count(p => p != SKColors.Black);

    [Fact]
    public void Registration_ToolBehaviourCategoryAndEnums()
    {
        Assert.Equal(2, DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.RangeRegimeDiagnosticCard)!.RequiredSteps);
        var algorithms = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Algorithms");
        Assert.Contains(algorithms.Tools, t => t.Tool == DrawingTool.RangeRegimeDiagnosticCard && t.NameKey == "DrawTool_RangeRegimeDiagnosticCard");
        Assert.Equal(DrawingTool.BootstrapConfidenceBand + 1, DrawingTool.RangeRegimeDiagnosticCard);
        Assert.Equal(ChartObjectType.BootstrapConfidenceBand + 1, ChartObjectType.RangeRegimeDiagnosticCard);
        Assert.Equal(ChartObjectType.RangeRegimeDiagnosticCard, new RangeRegimeDiagnosticCardObject().Type);
        Assert.IsAssignableFrom<IRangeAnalysisDrawing>(new RangeRegimeDiagnosticCardObject());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LocaleKeys_ExistForTheToolAndItsParameter(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json"));
        JsonElement locale = JsonDocument.Parse(stream).RootElement;
        foreach (string key in new[] { "DrawTool_RangeRegimeDiagnosticCard", "Param_PeriodsPerYear" })
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    [Fact]
    public void Behavior_CreatesTwoAnchorsAndDoesNotCalculateWhilePlacing()
    {
        var candles = MakeCandles(200);
        var behavior = new RangeRegimeDiagnosticCardBehavior();

        using var obj = (RangeRegimeDiagnosticCardObject)behavior.CreateObject(At(candles, 20), candles);
        behavior.UpdatePoint(obj, 1, At(candles, 120), candles);

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(candles[120].Timestamp, obj.Points[1].Time);
        Assert.False(obj.Card.HasRange);
        Assert.Empty(obj.GetCalculatedValues(candles[120].Timestamp));
    }

    [Fact]
    public void Recalculate_ComputesTheCard_AndTheValuesAreTheOnesTheCatalogueGives()
    {
        var candles = MakeCandles(200);
        using var obj = Create(candles);
        obj.PeriodsPerYear = 52;

        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));

        Assert.Equal(101, obj.Card.RangeBars);
        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[120].Timestamp);
        Assert.Equal("Ann: 52/yr", values[0].FormattedText);
        Assert.Same(values, obj.GetCalculatedValues(candles[120].Timestamp));   // cached while nothing changes

        // The GUI object and the headless catalogue entry share one definition: the same rows for the same anchors and options.
        Assert.True(DrawingValueCatalog.TryGet("RangeRegimeDiagnosticCard", out DrawingValueDescriptor descriptor));
        var anchors = new[] { new DrawingAnchor(candles[20].Timestamp, candles[20].Close), new DrawingAnchor(candles[120].Timestamp, candles[120].Close) };
        var request = new DrawingValueRequest(anchors, candles[120].Timestamp, candles[120].Close, IndicatorColor.Gray, new DrawingValueOptions { RegimeCardPeriodsPerYear = 52 }, candles);
        Assert.Equal(
            descriptor.Calculate(request).Select(v => (v.Key, v.Label, v.NumericValue, v.FormattedText)),
            values.Select(v => (v.Key, v.Label, v.NumericValue, v.FormattedText)));
    }

    [Fact]
    public void PeriodsPerYear_IsClampedAndPickedUpByTheNextRecalculation()
    {
        var candles = MakeCandles(200);
        using var obj = Create(candles);
        obj.Recalculate(candles);
        Assert.Equal(252, obj.Card.PeriodsPerYear);

        obj.PeriodsPerYear = 5000;
        Assert.Equal(366, obj.PeriodsPerYear);
        obj.PeriodsPerYear = 0;
        Assert.Equal(1, obj.PeriodsPerYear);
        obj.PeriodsPerYear = 12;
        obj.Recalculate(candles);
        Assert.Equal(12, obj.Card.PeriodsPerYear);
        Assert.Equal(252, new RangeRegimeDiagnosticCardObject().PeriodsPerYear);
    }

    [Fact]
    public void Dispatcher_AfterMoveFrame_KeepsTheResult_AfterMoveCompleted_Recalculates()
    {
        var candles = MakeCandles(200);
        using var obj = Create(candles);
        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));
        RangeRegimeDiagnosticCard before = obj.Card;

        obj.Translate(TimeSpan.FromDays(10), 0m);
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        Assert.Same(before, obj.Card);
        Assert.Empty(obj.GetCalculatedValues(candles[130].Timestamp));

        DrawingRecalculation.AfterMoveCompleted(obj, new DrawingCalculationContext(candles));
        Assert.NotSame(before, obj.Card);
        Assert.NotEmpty(obj.GetCalculatedValues(candles[130].Timestamp));
    }

    [Fact]
    public void Render_DrawsThePanel_AndHitTestFindsIt_OnlyAfterTheResultExists()
    {
        var candles = MakeCandles(200);
        using var obj = Create(candles);
        LinearCoordinateTransform transform = MakeTransform();
        SKColor[] edgesOnly = Snapshot(obj);   // no result yet: the range edges and the faint band only

        obj.Recalculate(candles);
        SKColor[] withCard = Snapshot(obj);

        Assert.False(edgesOnly.SequenceEqual(withCard), "the card changed nothing on the canvas");   // whatever the theme colours are, the panel differs from the bare band
        Point left = transform.ChartToScreen(obj.Points[0]);
        Assert.True(obj.HitTest(new Point(left.X + 30, 40), transform, 3.0), $"left {left.X}");       // inside the panel (top left of the range)
        Assert.False(obj.HitTest(new Point(left.X + 30, 580), transform, 3.0));     // the range interior below the panel is not a hit
    }

    [Fact]
    public void ShortRange_ShowsDashes_AndRendersWithoutThrowing()
    {
        var candles = MakeCandles(200);
        using var obj = Create(candles, 40, 44);

        obj.Recalculate(candles);

        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[44].Timestamp);
        Assert.Equal("-", values.Single(v => v.Key == "RegimeKpss").FormattedText);
        Assert.Equal(5, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.True(CountNonBlack(obj) > 0);
    }

    [Fact]
    public void RangeWithoutBars_HasNoValues_AndDisposeIsSafe()
    {
        var candles = MakeCandles(200);
        var obj = new RangeRegimeDiagnosticCardObject(new ChartPoint(BaseTime.AddDays(-50), 100m), new ChartPoint(BaseTime.AddDays(-30), 100m));
        obj.Recalculate(candles);
        Assert.Empty(obj.GetCalculatedValues(BaseTime));

        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        obj.Dispose();
        obj.Dispose();
        obj.Render(canvas, MakeTransform());
        Assert.False(obj.HitTest(new Point(100, 40), MakeTransform(), 3.0));
    }

    [Fact]
    public void BothPersistencePaths_KeepThePeriodsPerYear_AndNoComputedState()
    {
        var candles = MakeCandles(200);
        using var obj = Create(candles);
        obj.PeriodsPerYear = 52;
        obj.Recalculate(candles);

        string json = JsonSerializer.Serialize<IChartObject>(obj, JsonOptions);
        using var restored = Assert.IsType<RangeRegimeDiagnosticCardObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        Assert.Equal(52, restored.PeriodsPerYear);
        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.True(!names.Any(n => n.Contains("card", StringComparison.OrdinalIgnoreCase) || n.Contains("lines", StringComparison.OrdinalIgnoreCase) || n.Contains("panelwidth", StringComparison.OrdinalIgnoreCase) || n.Contains("cachedvalues", StringComparison.OrdinalIgnoreCase)), string.Join(",", names));

        var manager = new ChartObjectManager();
        manager.AddObject(obj);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("REG", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        var codecRestored = Assert.IsType<RangeRegimeDiagnosticCardObject>(materialized.Objects[ChartDrawingContextType.Standard].Single());
        Assert.Equal(52, codecRestored.PeriodsPerYear);
        Assert.False(codecRestored.Card.HasRange);   // computed again on load
    }

    [Fact]
    public void SavedObject_ReadsBackThroughTheStoredOptions_ForBothNewTools()
    {
        var candles = MakeCandles(200);
        using var card = Create(candles);
        card.PeriodsPerYear = 52;
        using var cone = new GarchVolatilityConeObject(At(candles, 20), At(candles, 120)) { ForecastBars = 33 };
        var manager = new ChartObjectManager();
        manager.AddObject(card);
        manager.AddObject(cone);

        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("OPT", TimeframeType.Daily), revision: 1L);
        IReadOnlyList<DrawingObjectRecord> records = state.Contexts.Single(c => c.ContextName == DrawingContextNames.Standard).Objects;

        var cardRecord = records.Single(r => r.TypeName == nameof(RangeRegimeDiagnosticCardObject));
        var coneRecord = records.Single(r => r.TypeName == nameof(GarchVolatilityConeObject));
        Assert.Equal(52, DrawingStoredOptions.Read(ParametersAfterTheFile(cardRecord)).RegimeCardPeriodsPerYear);
        Assert.Equal(33, DrawingStoredOptions.Read(ParametersAfterTheFile(coneRecord)).GarchForecastBars);
        Assert.Equal(252, DrawingStoredOptions.Read(ParametersAfterTheFile(coneRecord)).RegimeCardPeriodsPerYear);   // an option of another tool keeps its default
        Assert.True(DrawingValueCatalog.TryGetStored(cardRecord.TypeName, null, out DrawingValueDescriptor cardTool));
        Assert.Equal("RangeRegimeDiagnosticCard", cardTool.Name);
    }

    /// <summary>The parameters of a record as the reader sees them: after a trip through the JSON of the file.</summary>
    private static IReadOnlyDictionary<string, object?> ParametersAfterTheFile(DrawingObjectRecord record)
    {
        string json = JsonSerializer.Serialize(record.StyleAndParameters, DrawingDocumentCodec.SerializerOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }
}
