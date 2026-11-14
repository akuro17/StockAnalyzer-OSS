using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>Disjoint Channel (two independent edges P1-P2 and P3-P4).</summary>
public sealed class DisjointChannelTests
{
    private const int Width = 800;
    private const int Height = 600;

    private static readonly DateTime T0 = new(2026, 1, 1);
    private static readonly IndicatorColor Color1 = new(255, 10, 20, 30);

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    /// <summary>Edge 1 = (100,100)-(300,100), edge 2 = (100,300)-(300,200); center line (100,200)-(300,150) in pixel space.</summary>
    private static DisjointChannelObject Channel(bool filled = true)
        => new(PixelCoordinateTransform.Chart(100, 100), PixelCoordinateTransform.Chart(300, 100),
               PixelCoordinateTransform.Chart(100, 300), PixelCoordinateTransform.Chart(300, 200))
        {
            IsFilled = filled,
            Color = Colors.White,
            Thickness = 2
        };

    private static SKBitmap RenderBitmap(IChartObject obj)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        obj.Render(surface.Canvas, new PixelCoordinateTransform());
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static bool IsDrawn(SKBitmap bitmap, int x, int y)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (bitmap.GetPixel(Math.Clamp(x + dx, 0, Width - 1), Math.Clamp(y + dy, 0, Height - 1)) != SKColors.Black) return true;
            }
        }
        return false;
    }

    // ---------------- Registration ----------------

    [Fact]
    public void Enums_AreAppendedAfterCrossLine_AndToolIsRegistered()
    {
        Assert.Equal((int)DrawingTool.CrossLine + 1, (int)DrawingTool.DisjointChannel);
        Assert.Equal((int)ChartObjectType.CrossLine + 1, (int)ChartObjectType.DisjointChannel);

        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.DisjointChannel);
        Assert.NotNull(behavior);
        Assert.Equal(4, behavior!.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
        var created = Assert.IsType<DisjointChannelObject>(behavior.CreateObject(PixelCoordinateTransform.Chart(5, 5)));
        Assert.Equal(4, created.Points.Count);
        Assert.Equal(ChartObjectType.DisjointChannel, created.Type);

        var channels = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Channels");
        Assert.Contains(channels.Tools, t => t.Tool == DrawingTool.DisjointChannel && t.NameKey == "DrawTool_DisjointChannel");
    }

    [Fact]
    public void Tool_IsNotExposedThroughTheDrawingValueCatalog()
    {
        // U-17=A: the new tools have no MCP surface.
        Assert.False(DrawingValueCatalog.TryGet("DisjointChannel", out _));
        Assert.False(DrawingValueCatalog.TryGet("CrossLine", out _));
    }

    [Fact]
    public void Defaults_MatchTheParallelChannel()
    {
        var parallel = new ParallelChannelObject(default, default, default);
        using var disjoint = new DisjointChannelObject(default, default, default, default);

        Assert.Equal(parallel.Color, disjoint.Color);
        Assert.Equal(parallel.Thickness, disjoint.Thickness);
        Assert.Equal(parallel.StrokeStyle, disjoint.StrokeStyle);
        Assert.Equal(parallel.IsFilled, disjoint.IsFilled);
        Assert.Equal(parallel.MoveAxisMode, disjoint.MoveAxisMode);
        Assert.Equal(parallel.PanelIndex, disjoint.PanelIndex);
    }

    // ---------------- Behaviour (4 clicks, cursor-following preview) ----------------

    [Fact]
    public void Behavior_UnplacedPointsFollowTheCursor_AtEveryStep()
    {
        var behavior = new StockAnalyzer.Avalonia.Drawing.Behaviors.DisjointChannelBehavior();
        var p1 = PixelCoordinateTransform.Chart(10, 10);
        var cursor1 = PixelCoordinateTransform.Chart(20, 25);
        var cursor2 = PixelCoordinateTransform.Chart(30, 40);
        var cursor3 = PixelCoordinateTransform.Chart(50, 60);
        using var obj = Assert.IsType<DisjointChannelObject>(behavior.CreateObject(p1));

        behavior.UpdatePoint(obj, 1, cursor1);   // choosing P2
        Assert.Equal(new[] { p1, cursor1, cursor1, cursor1 }, obj.Points.ToArray());

        behavior.UpdatePoint(obj, 2, cursor2);   // P2 fixed, choosing P3
        Assert.Equal(new[] { p1, cursor1, cursor2, cursor2 }, obj.Points.ToArray());

        behavior.UpdatePoint(obj, 3, cursor3);   // P3 fixed, choosing P4
        Assert.Equal(new[] { p1, cursor1, cursor2, cursor3 }, obj.Points.ToArray());
    }

    // ---------------- Geometry helpers ----------------

    [Fact]
    public void Midpoint_UsesIntegerTickMeanAndPriceMean()
    {
        var a = new ChartPoint(new DateTime(1001), 100m);
        var b = new ChartPoint(new DateTime(1002), 101m);

        var mid = DisjointChannelObject.Midpoint(a, b);

        Assert.Equal(1001L, mid.Time.Ticks);   // (1001 + 1002) / 2 = 1001 (integer division)
        Assert.Equal(100.5m, mid.Price);
    }

    [Fact]
    public void PolygonContains_InsideOutsideAndCrossedQuadrilateral()
    {
        var a = new Point(0, 0);
        var b = new Point(10, 0);
        var c = new Point(10, 10);
        var d = new Point(0, 10);

        Assert.True(DisjointChannelObject.PolygonContains(new Point(5, 5), a, b, c, d));
        Assert.False(DisjointChannelObject.PolygonContains(new Point(15, 5), a, b, c, d));
        Assert.False(DisjointChannelObject.PolygonContains(new Point(5, -1), a, b, c, d));

        // Crossed quadrilateral (bow tie): a-b-d-c. The two lobes are inside, the pinch sides outside.
        Assert.True(DisjointChannelObject.PolygonContains(new Point(5, 1), a, b, d, c));
        Assert.True(DisjointChannelObject.PolygonContains(new Point(5, 9), a, b, d, c));
        Assert.False(DisjointChannelObject.PolygonContains(new Point(1, 5), a, b, d, c));
    }

    // ---------------- Values ----------------

    [Fact]
    public void Values_HandCalculation_FourFixedItemsWithoutRawAnchors()
    {
        // Edge 1: 100 -> 200 over 10 days (150 at day 5). Edge 2: 150 -> 170 (160 at day 5).
        var values = DisjointChannelValues.Calculate(
            new DrawingAnchor(T0, 100m), new DrawingAnchor(T0.AddDays(10), 200m),
            new DrawingAnchor(T0, 150m), new DrawingAnchor(T0.AddDays(10), 170m),
            T0.AddDays(5), Color1);

        Assert.IsType<DrawingCalculatedValue[]>(values);
        Assert.Equal(new[] { "Line1", "Line2", "Mid", "Width" }, values.Select(v => v.Key).ToArray());
        Assert.Equal(150m, values[0].NumericValue);
        Assert.Equal(160m, values[1].NumericValue);
        Assert.Equal(155m, values[2].NumericValue);
        Assert.Equal(10m, values[3].NumericValue);
        Assert.Equal("155.000", values[2].FormattedText);
    }

    [Fact]
    public void Values_ExtrapolateEachEdgeStraightOutsideItsAnchors()
    {
        var values = DisjointChannelValues.Calculate(
            new DrawingAnchor(T0, 100m), new DrawingAnchor(T0.AddDays(10), 200m),
            new DrawingAnchor(T0, 150m), new DrawingAnchor(T0.AddDays(10), 170m),
            T0.AddDays(15), Color1);

        Assert.Equal(250m, values[0].NumericValue);
        Assert.Equal(180m, values[1].NumericValue);
        Assert.Equal(215m, values[2].NumericValue);
        Assert.Equal(70m, values[3].NumericValue);
    }

    [Fact]
    public void Values_IdenticalEdges_GiveZeroWidth()
    {
        var p1 = new DrawingAnchor(T0, 100m);
        var p2 = new DrawingAnchor(T0.AddDays(10), 200m);

        foreach (DateTime t in new[] { T0.AddDays(3), T0.AddDays(-2), T0.AddDays(20) })
        {
            var values = DisjointChannelValues.Calculate(p1, p2, p1, p2, t, Color1);
            Assert.Equal(0m, values[3].NumericValue);
            Assert.Equal(values[0].NumericValue, values[2].NumericValue);
        }
    }

    [Fact]
    public void Values_AllFourAnchorsAtTheSameTime_FollowInterpolatePriceOfEachEdge()
    {
        // Same time on both anchors of an edge: InterpolatePrice returns the first anchor's price, so no value is null and nothing throws.
        var values = DisjointChannelValues.Calculate(
            new DrawingAnchor(T0, 100m), new DrawingAnchor(T0, 120m),
            new DrawingAnchor(T0, 150m), new DrawingAnchor(T0, 170m),
            T0.AddDays(2), Color1);

        Assert.Equal(4, values.Count);
        Assert.Equal(100m, values[0].NumericValue);
        Assert.Equal(150m, values[1].NumericValue);
        Assert.Equal(125m, values[2].NumericValue);
        Assert.Equal(50m, values[3].NumericValue);
    }

    [Fact]
    public void ObjectValues_DelegateToTheCalculator_AndGuardAMissingPoint()
    {
        using var obj = Channel();
        DateTime t = PixelCoordinateTransform.Chart(200, 0).Time;
        var color = new IndicatorColor(Colors.White.A, Colors.White.R, Colors.White.G, Colors.White.B);

        var expected = DisjointChannelValues.Calculate(obj.Points[0].ToAnchor(), obj.Points[1].ToAnchor(), obj.Points[2].ToAnchor(), obj.Points[3].ToAnchor(), t, color);
        var actual = obj.GetCalculatedValues(t);

        Assert.Equal(expected, actual);
        Assert.Equal(100m, actual[0].NumericValue);   // edge 1 is flat at 100
        Assert.Equal(250m, actual[1].NumericValue);   // edge 2: 300 -> 200, at x = 200 it is 250

        obj.Points.RemoveAt(3);
        Assert.Empty(obj.GetCalculatedValues(t));
    }

    // ---------------- Render ----------------

    [Fact]
    public void Render_DrawsTwoEdgesAndTheCenterLine_ButNoSidesOrExtensions()
    {
        using var obj = Channel(filled: false);
        using SKBitmap bitmap = RenderBitmap(obj);

        Assert.True(IsDrawn(bitmap, 200, 100));          // edge 1
        Assert.True(IsDrawn(bitmap, 200, 250));          // edge 2

        // Center line (100,200) -> (300,150) is dashed: some sample along it must be drawn.
        bool centerDrawn = Enumerable.Range(100, 200).Any(x => IsDrawn(bitmap, x, (int)Math.Round(200 - 0.25 * (x - 100))));
        Assert.True(centerDrawn);

        Assert.False(IsDrawn(bitmap, 200, 150));         // between the edges, unfilled
        Assert.False(IsDrawn(bitmap, 100, 150));         // no side connector at P1/P3
        Assert.False(IsDrawn(bitmap, 300, 130));         // no side connector at P2/P4
        Assert.False(IsDrawn(bitmap, 350, 100));         // edges are not extended
        Assert.False(IsDrawn(bitmap, 50, 100));
    }

    [Fact]
    public void Render_Filled_TintsThePolygonP1P2P4P3()
    {
        using var filled = Channel(filled: true);
        using var unfilled = Channel(filled: false);
        using SKBitmap filledBitmap = RenderBitmap(filled);
        using SKBitmap unfilledBitmap = RenderBitmap(unfilled);

        // Inside, away from the center line (which passes (200, 175)).
        Assert.True(IsDrawn(filledBitmap, 200, 120));
        Assert.False(IsDrawn(unfilledBitmap, 200, 120));
        Assert.False(IsDrawn(filledBitmap, 200, 270));   // outside the polygon (below edge 2)
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_AllocatesZeroManagedBytes(bool filled)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        var transform = new PixelCoordinateTransform();
        using var obj = Channel(filled);

        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        long bytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Render_AfterDispose_IsANoOp_NotANativeCrash()
    {
        // Shift+click delete disposes the object immediately while an in-flight frame may still render it
        // (touching a disposed SKPaint is an AccessViolationException that kills the process).
        var obj = Channel();
        obj.Dispose();
        obj.Dispose();   // idempotent

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        obj.Render(surface.Canvas, new PixelCoordinateTransform());
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        Assert.False(IsDrawn(bitmap, 200, 100));
        Assert.False(obj.HitTest(new Point(-100, -100), new PixelCoordinateTransform()));
    }

    [Fact]
    public void RemovingTheObjectFromTheManager_DisposesIt_AndALaterRenderDoesNotCrash()
    {
        var manager = new ChartObjectManager();
        var obj = Channel();
        manager.AddObject(obj);

        Assert.True(manager.RemoveObject(obj.Id));

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        obj.Render(surface.Canvas, new PixelCoordinateTransform());
    }

    // ---------------- Hit test ----------------

    [Fact]
    public void HitTest_EdgesAndCenterLine_AlwaysAndPolygonInteriorOnlyWhenFilled()
    {
        var transform = new PixelCoordinateTransform();
        using var filled = Channel(filled: true);
        using var unfilled = Channel(filled: false);

        foreach (var obj in new[] { filled, unfilled })
        {
            Assert.True(obj.HitTest(new Point(200, 100), transform));   // edge 1
            Assert.True(obj.HitTest(new Point(200, 250), transform));   // edge 2
            Assert.True(obj.HitTest(new Point(200, 175), transform));   // center line
            Assert.False(obj.HitTest(new Point(200, 350), transform));  // below
            Assert.False(obj.HitTest(new Point(350, 100), transform));  // beyond the anchors' times
        }

        Assert.True(filled.HitTest(new Point(200, 130), transform));    // interior
        Assert.False(unfilled.HitTest(new Point(200, 130), transform));
    }

    // ---------------- Translate / persistence ----------------

    [Fact]
    public void Translate_MovesAllFourPoints()
    {
        using var obj = Channel();
        var before = obj.Points.ToArray();

        obj.Translate(TimeSpan.FromDays(2), 7m);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(before[i].Time.AddDays(2), obj.Points[i].Time);
            Assert.Equal(before[i].Price + 7m, obj.Points[i].Price);
        }
    }

    [Fact]
    public void RoundTrip_KeepsFourPointsAndEveryProperty()
    {
        using var original = Channel(filled: false);
        original.Color = Colors.Orange;
        original.Thickness = 3.5;
        original.StrokeStyle = DrawingStrokeStyle.Dash;
        original.ZIndex = 4;
        original.PanelIndex = 2;

        string json = JsonSerializer.Serialize<IChartObject>(original, Options);
        using var restored = Assert.IsType<DisjointChannelObject>(JsonSerializer.Deserialize<IChartObject>(json, Options));

        Assert.Equal(ChartObjectType.DisjointChannel, restored.Type);
        Assert.Equal(original.Points, restored.Points);
        Assert.False(restored.IsFilled);
        Assert.Equal(Colors.Orange, restored.Color);
        Assert.Equal(3.5, restored.Thickness);
        Assert.Equal(DrawingStrokeStyle.Dash, restored.StrokeStyle);
        Assert.Equal(4, restored.ZIndex);
        Assert.Equal(2, restored.PanelIndex);
    }
}
