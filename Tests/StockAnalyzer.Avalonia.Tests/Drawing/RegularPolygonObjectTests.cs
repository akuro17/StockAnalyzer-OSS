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
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The regular polygon tool (DSL expansion run D-5): vertices in screen space, inscribed circle, placement and the shared object contracts.</summary>
public sealed class RegularPolygonObjectTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static LinearCoordinateTransform MakeTransform(int width = 900, int height = 600)
        => new(BaseTime, BaseTime.AddDays(170), 70m, 150m, width, height);

    private static RegularPolygonObject Create(int sides = 6)
        => new(new ChartPoint(BaseTime.AddDays(60), 110m), new ChartPoint(BaseTime.AddDays(80), 118m)) { Sides = sides };

    private static double Distance(SKPoint a, SKPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    [Fact]
    public void Registration_ToolBehaviourCategoryAndEnums()
    {
        IDrawingToolBehavior behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.RegularPolygon)!;
        Assert.Equal(2, behavior.RequiredSteps);
        var shapes = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Shapes");
        Assert.Contains(shapes.Tools, t => t.Tool == DrawingTool.RegularPolygon && t.NameKey == "DrawTool_RegularPolygon");
        Assert.Equal(DrawingTool.CubicSplineCurve + 1, DrawingTool.RegularPolygon);
        Assert.Equal(ChartObjectType.CubicSplineCurve + 1, ChartObjectType.RegularPolygon);
        Assert.Equal(DrawingTool.RegularPolygon.ToString(), ChartObjectType.RegularPolygon.ToString());
        Assert.Equal(ChartObjectType.RegularPolygon, Create().Type);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LocaleKeys_ExistForTheToolAndItsParameters(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json"));
        JsonElement locale = JsonDocument.Parse(stream).RootElement;
        foreach (string key in new[] { "DrawTool_RegularPolygon", "Param_Sides", "Param_ShowCircumscribedCircle", "Param_ShowInscribedCircle" })
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    [Fact]
    public void Behavior_CreatesTheCentreAndFollowsThePointerWithVertexZero()
    {
        var behavior = new RegularPolygonBehavior();
        using var obj = (RegularPolygonObject)behavior.CreateObject(new ChartPoint(BaseTime.AddDays(10), 100m));
        behavior.UpdatePoint(obj, 1, new ChartPoint(BaseTime.AddDays(30), 105m));

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(BaseTime.AddDays(10), obj.Points[0].Time);
        Assert.Equal(BaseTime.AddDays(30), obj.Points[1].Time);
        Assert.Equal(RegularPolygonObject.DefaultSides, obj.Sides);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(24)]
    public void Vertices_AreOnTheCircle_EquallySpaced_AndVertexZeroIsTheAnchor(int sides)
    {
        var vertices = new SKPoint[RegularPolygonObject.MaxSides];

        int count = RegularPolygonObject.ComputeVertices(400.0, 300.0, 520.0, 260.0, sides, vertices);

        Assert.Equal(sides, count);
        double radius = Math.Sqrt((120.0 * 120.0) + (40.0 * 40.0));
        Assert.Equal(520.0, vertices[0].X, 3);
        Assert.Equal(260.0, vertices[0].Y, 3);
        double side = Distance(vertices[0], vertices[1]);
        for (int k = 0; k < count; k++)
        {
            Assert.Equal(radius, Distance(new SKPoint(400f, 300f), vertices[k]), 2);
            Assert.True(Math.Abs(Distance(vertices[k], vertices[(k + 1) % count]) - side) / side < 1e-4);
        }

        Assert.Equal(2.0 * radius * Math.Sin(Math.PI / sides), side, 2);   // the side of a regular N-gon with circumradius R
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(24)]
    public void Inradius_IsRTimesCosPiOverN_AndTouchesTheMiddleOfEverySide(int sides)
    {
        var vertices = new SKPoint[RegularPolygonObject.MaxSides];
        RegularPolygonObject.ComputeVertices(400.0, 300.0, 500.0, 300.0, sides, vertices);

        double inradius = RegularPolygonObject.InscribedRadius(100.0, sides);

        Assert.Equal(100.0 * Math.Cos(Math.PI / sides), inradius, 9);
        for (int k = 0; k < sides; k++)
        {
            SKPoint a = vertices[k];
            SKPoint b = vertices[(k + 1) % sides];
            var middle = new SKPoint((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);
            Assert.Equal(inradius, Distance(new SKPoint(400f, 300f), middle), 2);
        }
    }

    [Fact]
    public void Vertices_StayRegularInPixelsWhateverTheZoomRatioIs()
    {
        // The anchors are chart points; the polygon is built from their PIXEL positions, so another pixel ratio (a stretched chart) changes the size but never the regularity.
        foreach ((int width, int height) in new[] { (900, 600), (1800, 300), (300, 1200) })
        {
            using var obj = Create(5);
            LinearCoordinateTransform transform = MakeTransform(width, height);
            Point center = transform.ChartToScreen(obj.Points[0]);
            Point vertex = transform.ChartToScreen(obj.Points[1]);
            var vertices = new SKPoint[RegularPolygonObject.MaxSides];

            int count = RegularPolygonObject.ComputeVertices(center.X, center.Y, vertex.X, vertex.Y, 5, vertices);

            Assert.Equal(5, count);
            double side = Distance(vertices[0], vertices[1]);
            for (int k = 1; k < count; k++)
            {
                Assert.True(Math.Abs(Distance(vertices[k], vertices[(k + 1) % count]) - side) / side < 1e-4, $"{width}x{height} side {k}");
            }
        }
    }

    [Fact]
    public void SamePoint_HasNoRadius_DrawsNothing_AndNothingIsHit()
    {
        var vertices = new SKPoint[RegularPolygonObject.MaxSides];
        Assert.Equal(0, RegularPolygonObject.ComputeVertices(100.0, 100.0, 100.0, 100.0, 6, vertices));
        Assert.Equal(0, RegularPolygonObject.ComputeVertices(100.0, 100.0, double.NaN, 100.0, 6, vertices));

        using var obj = new RegularPolygonObject(new ChartPoint(BaseTime.AddDays(60), 110m), new ChartPoint(BaseTime.AddDays(60), 110m)) { ShowCircumscribedCircle = true };
        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, MakeTransform());

        Assert.All(bitmap.Pixels, p => Assert.Equal(SKColors.Black, p));
        Assert.False(obj.HitTest(new Point(500, 300), MakeTransform(), 6.0));
    }

    [Fact]
    public void Sides_AreClamped_AndTheVertexCountFollows()
    {
        using var obj = Create();
        obj.Sides = 1;
        Assert.Equal(RegularPolygonObject.MinSides, obj.Sides);
        obj.Sides = 99;
        Assert.Equal(RegularPolygonObject.MaxSides, obj.Sides);
        var vertices = new SKPoint[RegularPolygonObject.MaxSides];
        Assert.Equal(RegularPolygonObject.MaxSides, RegularPolygonObject.ComputeVertices(0.0, 0.0, 10.0, 0.0, 500, vertices));
        Assert.Equal(RegularPolygonObject.MinSides, RegularPolygonObject.ComputeVertices(0.0, 0.0, 10.0, 0.0, 0, vertices));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Render_DrawsAPolygonOfTheSetColour_AndTheCirclesOnRequest(bool circumscribed, bool inscribed)
    {
        using var obj = Create(6);
        obj.ShowCircumscribedCircle = circumscribed;
        obj.ShowInscribedCircle = inscribed;
        obj.IsSelected = true;
        LinearCoordinateTransform transform = MakeTransform();
        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        obj.Render(canvas, transform);

        Assert.Contains(bitmap.Pixels, p => p != SKColors.Black);
    }

    [Fact]
    public void HitTest_HitsAnEdge_NotTheMiddle_AndTheCircleOnlyWhenShown()
    {
        using var obj = Create(6);
        obj.PolygonFillOpacity = 0;   // a filled polygon is hit inside as well (see RegularPolygonFillTests); this test is about the outline
        LinearCoordinateTransform transform = MakeTransform();
        Point center = transform.ChartToScreen(obj.Points[0]);
        Point vertex = transform.ChartToScreen(obj.Points[1]);
        var vertices = new SKPoint[RegularPolygonObject.MaxSides];
        RegularPolygonObject.ComputeVertices(center.X, center.Y, vertex.X, vertex.Y, 6, vertices);
        var onEdge = new Point((vertices[0].X + vertices[1].X) / 2.0, (vertices[0].Y + vertices[1].Y) / 2.0);

        Assert.True(obj.HitTest(onEdge, transform, 4.0));
        Assert.False(obj.HitTest(center, transform, 4.0));   // the middle of the polygon is not on the outline

        double radius = Math.Sqrt(Math.Pow(vertex.X - center.X, 2) + Math.Pow(vertex.Y - center.Y, 2));
        double angle = Math.Atan2(vertex.Y - center.Y, vertex.X - center.X) + (Math.PI / 6.0);   // between two vertices: the circle bulges beyond the edge
        var onCircle = new Point(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
        Assert.False(obj.HitTest(onCircle, transform, 3.0));
        obj.ShowCircumscribedCircle = true;
        Assert.True(obj.HitTest(onCircle, transform, 3.0));
    }

    [Fact]
    public void Render_HitTest_AllocateNothing_AndDisposeIsSafe()
    {
        var obj = Create(12);
        obj.ShowCircumscribedCircle = true;
        obj.ShowInscribedCircle = true;
        LinearCoordinateTransform transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        SKCanvas canvas = surface.Canvas;

        obj.Render(canvas, transform);
        long renderBytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));
        long hitBytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(new Point(500, 300), transform));
        Assert.Equal(0L, renderBytes);
        Assert.Equal(0L, hitBytes);

        obj.Dispose();
        obj.Dispose();
        obj.Render(canvas, transform);
        Assert.False(obj.HitTest(new Point(500, 300), transform, 400.0));
    }

    [Fact]
    public void Translate_MovesBothPoints_AndTheParametersSurviveBothPersistencePaths()
    {
        using var obj = Create(9);
        obj.ShowInscribedCircle = true;
        obj.Translate(TimeSpan.FromDays(3), 2m);
        Assert.Equal(BaseTime.AddDays(63), obj.Points[0].Time);
        Assert.Equal(112m, obj.Points[0].Price);

        string json = JsonSerializer.Serialize<IChartObject>(obj, JsonOptions);
        using var restored = Assert.IsType<RegularPolygonObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        Assert.Equal(9, restored.Sides);
        Assert.True(restored.ShowInscribedCircle);
        Assert.False(restored.ShowCircumscribedCircle);
        Assert.Equal(obj.Points[1].Time, restored.Points[1].Time);

        var manager = new ChartObjectManager();
        manager.AddObject(obj);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("POLY", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        var codecRestored = Assert.IsType<RegularPolygonObject>(materialized.Objects[ChartDrawingContextType.Standard].Single());
        Assert.Equal(9, codecRestored.Sides);
        Assert.True(codecRestored.ShowInscribedCircle);
    }
}
