using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The robust and weighted regression line tools and the PCHIP / spline anchor curves (DSL expansion runs D-3 and D-4).</summary>
public sealed class RangeLineAndCurveObjectsTests
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
            decimal close = 100m + (i * 0.2m) + Math.Round((decimal)(Math.Sin(i * 0.25) * 4.0), 4);
            list.Add(new CoreCandleData(BaseTime.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000L + i));
        }

        return list;
    }

    private static LinearCoordinateTransform MakeTransform(int width = 900, int height = 600)
        => new(BaseTime, BaseTime.AddDays(170), 70m, 150m, width, height);

    private static ChartPoint At(List<CoreCandleData> candles, int index) => new(candles[index].Timestamp, candles[index].Close);

    public static IEnumerable<object[]> LineTools()
    {
        yield return new object[] { DrawingTool.RobustRegressionTrend };
        yield return new object[] { DrawingTool.WeightedRegressionTrend };
    }

    public static IEnumerable<object[]> CurveTools()
    {
        yield return new object[] { DrawingTool.PchipCurve };
        yield return new object[] { DrawingTool.CubicSplineCurve };
    }

    private static RangeCurveObjectBase CreateLine(DrawingTool tool, List<CoreCandleData> candles, int first = 20, int last = 100)
        => tool == DrawingTool.RobustRegressionTrend
            ? new RobustRegressionTrendObject(At(candles, first), At(candles, last))
            : new WeightedRegressionTrendObject(At(candles, first), At(candles, last));

    private static InterpolationCurveObjectBase CreateCurve(DrawingTool tool, List<CoreCandleData> candles)
    {
        var points = new List<ChartPoint> { At(candles, 10), At(candles, 40), At(candles, 70), At(candles, 120) };
        return tool == DrawingTool.PchipCurve ? new PchipCurveObject(points) : new CubicSplineCurveObject(points);
    }

    private static void RenderOnce(IChartObject obj, int width = 900, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.ClipRect(new SKRect(0, 0, width, height));
        obj.Render(canvas, MakeTransform(width, height));
    }

    [Fact]
    public void Registration_ToolsBehavioursCategoriesAndEnums()
    {
        var channels = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Channels");
        var lines = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Lines");
        foreach (DrawingTool tool in new[] { DrawingTool.RobustRegressionTrend, DrawingTool.WeightedRegressionTrend })
        {
            Assert.Equal(2, DrawingToolBehaviorRegistry.GetBehavior(tool)!.RequiredSteps);
            Assert.Contains(channels.Tools, t => t.Tool == tool && t.NameKey == $"DrawTool_{tool}");
        }

        foreach (DrawingTool tool in new[] { DrawingTool.PchipCurve, DrawingTool.CubicSplineCurve })
        {
            IDrawingToolBehavior behavior = DrawingToolBehaviorRegistry.GetBehavior(tool)!;
            Assert.Equal(0, behavior.RequiredSteps);   // variable number of anchors, like the polyline
            Assert.False(behavior.FinishesOnRelease);
            Assert.Contains(lines.Tools, t => t.Tool == tool && t.NameKey == $"DrawTool_{tool}");
        }

        Assert.Equal(DrawingTool.NonlinearRegressionCurve + 1, DrawingTool.RobustRegressionTrend);
        Assert.Equal(DrawingTool.NonlinearRegressionCurve + 4, DrawingTool.CubicSplineCurve);
        Assert.Equal(ChartObjectType.NonlinearRegressionCurve + 1, ChartObjectType.RobustRegressionTrend);
        Assert.Equal(ChartObjectType.NonlinearRegressionCurve + 4, ChartObjectType.CubicSplineCurve);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LocaleKeys_ExistForTheToolsParametersAndTheWeightingEnum(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json"));
        JsonElement locale = JsonDocument.Parse(stream).RootElement;
        var keys = new List<string>
        {
            "DrawTool_RobustRegressionTrend", "DrawTool_WeightedRegressionTrend", "DrawTool_PchipCurve", "DrawTool_CubicSplineCurve",
            "Param_UseSiegelMedian", "Param_ExtendRight", "Param_WeightingMode", "Param_DecayFactor", "Param_StdDevMultiplier",
        };
        keys.AddRange(Enum.GetValues<WeightedRegressionMode>().Select(m => $"Enum_{nameof(WeightedRegressionMode)}_{m}"));
        foreach (string key in keys)
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    // ---------------------------------------------------------------- line tools

    [Fact]
    public void Robust_RecalculateFitsTheNewestThirtyBars_AndTheAnchorsStay()
    {
        var candles = MakeCandles(150);
        using var obj = new RobustRegressionTrendObject(At(candles, 20), At(candles, 100)) { UseSiegelMedian = true, StdDevMultiplier = 1.5m, ExtendRight = true };

        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));

        RangeLineFit expected = RobustRegressionTrendValues.Calculate(candles, 20, 100, true, 1.5m, true);
        Assert.True(obj.Fit.IsOk);
        Assert.Equal(expected.Curve.Prices, obj.Curve.Prices);
        Assert.Equal(30 + 49, obj.Curve.Count);                         // 30 fitted bars + the 49 existing bars after the range end
        Assert.Equal(candles[71].Timestamp, obj.Curve.Times[0]);          // only the newest 30 bars are drawn
        Assert.Equal(candles[20].Timestamp, obj.Points[0].Time);          // the anchors are untouched
        Assert.Equal(candles[100].Timestamp, obj.Points[1].Time);
        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[100].Timestamp);
        Assert.Equal(81, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(30, values.Single(v => v.Key == "EffectiveBars").NumericValue);
        RenderOnce(obj);
    }

    [Fact]
    public void Weighted_RecalculateUsesTheModeAndTheDecay_AndTheParametersAreClamped()
    {
        var candles = MakeCandles(150);
        using var obj = new WeightedRegressionTrendObject(At(candles, 20), At(candles, 100)) { WeightingMode = WeightedRegressionMode.ExponentialDecay, DecayFactor = 0.9, StdDevMultiplier = 3m };

        obj.Recalculate(candles);

        RangeLineFit expected = WeightedRegressionTrendValues.Calculate(candles, 20, 100, WeightedRegressionMode.ExponentialDecay, 0.9, 3m, false);
        Assert.Equal(expected.Curve.Prices, obj.Curve.Prices);
        Assert.Equal(expected.Slope, obj.Fit.Slope);

        obj.DecayFactor = 5.0;
        Assert.Equal(0.99, obj.DecayFactor);
        obj.DecayFactor = 0.1;
        Assert.Equal(0.80, obj.DecayFactor);
        obj.StdDevMultiplier = 0m;
        Assert.Equal(0.1m, obj.StdDevMultiplier);
        Assert.Equal(WeightedRegressionTrendValues.DefaultDecayFactor, new WeightedRegressionTrendObject().DecayFactor);
    }

    [Theory]
    [MemberData(nameof(LineTools))]
    public void Lines_MoveFrameKeepsTheResult_MoveCompletedRecalculates(DrawingTool tool)
    {
        var candles = MakeCandles(160);
        using var obj = CreateLine(tool, candles);
        obj.Recalculate(candles);
        RangeCurve before = obj.Curve;
        Assert.True(before.Count > 0);

        obj.Translate(TimeSpan.FromDays(9), 0m);
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        Assert.Same(before, obj.Curve);
        Assert.Empty(obj.GetCalculatedValues(candles[110].Timestamp));

        DrawingRecalculation.AfterMoveCompleted(obj, new DrawingCalculationContext(candles));
        Assert.NotSame(before, obj.Curve);
        Assert.NotEmpty(obj.GetCalculatedValues(candles[110].Timestamp));
    }

    [Theory]
    [MemberData(nameof(LineTools))]
    public void Lines_Render_HitTest_AllocateNothing_AndDisposeIsSafe(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        var obj = CreateLine(tool, candles, 75, 100);   // 26 bars: no truncation badge, so no text is drawn (Skia converts a drawn string to UTF-8: a few hundred bytes)
        obj.Recalculate(candles);
        var transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        SKCanvas canvas = surface.Canvas;

        obj.Render(canvas, transform);
        long renderBytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));
        long hitBytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(new Point(500, 300), transform));
        Assert.Equal(0L, renderBytes);
        Assert.Equal(0L, hitBytes);

        RangeCurve curve = obj.Curve;
        Point onLine = transform.ChartToScreen(new ChartPoint(curve.Times[curve.Count / 2], curve.Prices[curve.Count / 2]));
        Assert.True(obj.HitTest(onLine, transform, 4.0));
        Assert.False(obj.HitTest(new Point(onLine.X, 3), transform, 4.0));

        obj.Dispose();
        obj.Dispose();
        obj.Render(canvas, transform);
        Assert.False(obj.HitTest(onLine, transform, 4.0));
    }

    [Fact]
    public void Lines_DrawingDocumentCodec_RoundTrip_KeepsTheParameters()
    {
        var candles = MakeCandles(150);
        var manager = new ChartObjectManager();
        var robust = new RobustRegressionTrendObject(At(candles, 20), At(candles, 100)) { UseSiegelMedian = true, StdDevMultiplier = 2.5m, ExtendRight = true };
        var weighted = new WeightedRegressionTrendObject(At(candles, 30), At(candles, 110)) { WeightingMode = WeightedRegressionMode.ExponentialDecay, DecayFactor = 0.88, ExtendRight = true };
        robust.Recalculate(candles);
        weighted.Recalculate(candles);
        manager.AddObject(robust);
        manager.AddObject(weighted);

        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("LIN", TimeframeType.Daily), revision: 1L);
        string json = DrawingDocumentCodec.SerializeToJson(state);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(json));

        var objects = materialized.Objects[ChartDrawingContextType.Standard];
        var restoredRobust = Assert.IsType<RobustRegressionTrendObject>(objects.Single(o => o is RobustRegressionTrendObject));
        var restoredWeighted = Assert.IsType<WeightedRegressionTrendObject>(objects.Single(o => o is WeightedRegressionTrendObject));
        Assert.True(restoredRobust.UseSiegelMedian);
        Assert.Equal(2.5m, restoredRobust.StdDevMultiplier);
        Assert.True(restoredRobust.ExtendRight);
        Assert.Equal(WeightedRegressionMode.ExponentialDecay, restoredWeighted.WeightingMode);
        Assert.Equal(0.88, restoredWeighted.DecayFactor);
        Assert.True(restoredWeighted.ExtendRight);
        Assert.Equal(0, restoredRobust.Curve.Count);   // computed again on load
    }

    // ---------------------------------------------------------------- anchor curves

    [Theory]
    [MemberData(nameof(CurveTools))]
    public void Behavior_PlacesAnchorsLikeThePolyline(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        IDrawingToolBehavior behavior = DrawingToolBehaviorRegistry.GetBehavior(tool)!;

        var obj = (InterpolationCurveObjectBase)behavior.CreateObject(At(candles, 10), candles);
        using (obj)
        {
            Assert.Equal(2, obj.Points.Count);   // the first anchor and the one that follows the pointer
            behavior.UpdatePoint(obj, 1, At(candles, 30), candles);
            Assert.Equal(candles[30].Timestamp, obj.Points[1].Time);
            obj.AddPoint(At(candles, 50));       // a click: a new trailing anchor
            behavior.UpdatePoint(obj, 2, At(candles, 60), candles);
            Assert.Equal(3, obj.Points.Count);
            Assert.Equal(candles[30].Timestamp, obj.Points[1].Time);
            Assert.Equal(candles[60].Timestamp, obj.Points[2].Time);
        }
    }

    [Theory]
    [MemberData(nameof(CurveTools))]
    public void Curve_IsCalculatedByTheDispatcher_AndStaleUntilThen(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        using var obj = CreateCurve(tool, candles);
        Assert.Empty(obj.GetCalculatedValues(candles[10].Timestamp));   // nothing computed yet
        RenderOnce(obj);                                                  // draws the straight polyline through the anchors

        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));

        Assert.True(obj.Curve.IsOk);
        Assert.Equal(4, obj.Curve.DistinctAnchors);
        Assert.Equal((3 * InterpolationCurveValues.SamplesPerSegment) + 1, obj.Curve.Count);
        Assert.Equal(new[] { "AnchorCount", "SampleCount", "CurveStart", "CurveEnd" }, obj.GetCalculatedValues(candles[10].Timestamp).Select(v => v.Key).ToArray());

        // An anchor moves: the result no longer belongs to the anchors until the next recalculation (a drag recalculates every frame: it is cheap).
        obj.Points[1] = At(candles, 45);
        Assert.Empty(obj.GetCalculatedValues(candles[10].Timestamp));
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        Assert.NotEmpty(obj.GetCalculatedValues(candles[10].Timestamp));
        RenderOnce(obj);
        RenderOnce(obj, 300, 40);
    }

    [Theory]
    [MemberData(nameof(CurveTools))]
    public void Curve_Render_HitTest_AllocateNothing_AndDisposeIsSafe(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        var obj = CreateCurve(tool, candles);
        obj.IsSelected = false;
        obj.Recalculate(candles);
        var transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        SKCanvas canvas = surface.Canvas;

        obj.Render(canvas, transform);
        long renderBytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));
        long hitBytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(new Point(500, 300), transform));
        Assert.Equal(0L, renderBytes);
        Assert.Equal(0L, hitBytes);

        InterpolationCurve curve = obj.Curve;
        int mid = curve.Count / 2;
        Point onCurve = transform.ChartToScreen(new ChartPoint(curve.Times[mid], curve.Prices[mid]));
        Assert.True(obj.HitTest(onCurve, transform, 4.0));
        Assert.False(obj.HitTest(new Point(onCurve.X, 3), transform, 4.0));

        obj.IsSelected = true;
        RenderOnce(obj);

        obj.Dispose();
        obj.Dispose();
        obj.Render(canvas, transform);
        obj.Recalculate(candles);
        Assert.False(obj.HitTest(onCurve, transform, 4.0));
        Assert.Empty(obj.GetCalculatedValues(candles[10].Timestamp));
    }

    [Theory]
    [MemberData(nameof(CurveTools))]
    public async Task Curve_RenderAndDispose_ConcurrentSafety_DoesNotThrow(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        var obj = CreateCurve(tool, candles);
        obj.Recalculate(candles);
        var transform = MakeTransform();

        Task renderTask = Task.Run(() =>
        {
            using var bitmap = new SKBitmap(640, 480);
            using var canvas = new SKCanvas(bitmap);
            for (int i = 0; i < 40; i++)
            {
                obj.Render(canvas, transform);
            }
        });
        Task disposeTask = Task.Run(() => obj.Dispose());

        await Task.WhenAll(renderTask, disposeTask);
        obj.Dispose();
    }

    [Fact]
    public void Curve_DrawingDocumentCodec_AndJsonConverter_KeepEveryAnchorAndNothingComputed()
    {
        var candles = MakeCandles(150);
        var manager = new ChartObjectManager();
        var pchip = CreateCurve(DrawingTool.PchipCurve, candles);
        var spline = CreateCurve(DrawingTool.CubicSplineCurve, candles);
        pchip.Thickness = 2.0;
        pchip.Recalculate(candles);
        spline.Recalculate(candles);
        manager.AddObject(pchip);
        manager.AddObject(spline);

        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("CRV", TimeframeType.Daily), revision: 1L);
        string json = DrawingDocumentCodec.SerializeToJson(state);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(json));

        var objects = materialized.Objects[ChartDrawingContextType.Standard];
        var restoredPchip = Assert.IsType<PchipCurveObject>(objects.Single(o => o is PchipCurveObject));
        var restoredSpline = Assert.IsType<CubicSplineCurveObject>(objects.Single(o => o is CubicSplineCurveObject));
        Assert.Equal(4, restoredPchip.Points.Count);
        Assert.Equal(candles[120].Timestamp, restoredPchip.Points[3].Time);
        Assert.Equal(4, restoredSpline.Points.Count);
        Assert.Equal(2.0, restoredPchip.Thickness);
        Assert.Equal(InterpolationCurveStatus.NoData, restoredPchip.Curve.Status);   // not stored: computed again on load

        string objectJson = JsonSerializer.Serialize<IChartObject>(pchip, JsonOptions);
        using var viaConverter = Assert.IsType<PchipCurveObject>(JsonSerializer.Deserialize<IChartObject>(objectJson, JsonOptions));
        Assert.Equal(4, viaConverter.Points.Count);
        using JsonDocument document = JsonDocument.Parse(objectJson);
        Assert.DoesNotContain(document.RootElement.EnumerateObject().Select(p => p.Name), n => (n.Contains("curve", StringComparison.OrdinalIgnoreCase) && !n.Equals("curveMode", StringComparison.OrdinalIgnoreCase)) || n.Contains("signature", StringComparison.OrdinalIgnoreCase));   // "curveMode" is a user setting; the computed curve is not stored
    }
}
