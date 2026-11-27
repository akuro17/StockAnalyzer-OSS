using System.Linq;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using AColor = global::Avalonia.Media.Color;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The Regular Polygon area fills (settings expansion Run 2): polygon, circumscribed and inscribed circle areas, colour fallback, toggle linkage, z-order, hit test, persistence, no allocation.</summary>
public sealed class RegularPolygonFillTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static readonly AColor LineColor = AColor.FromRgb(0, 0, 255);
    private static readonly AColor Red = AColor.FromRgb(255, 0, 0);
    private static readonly AColor Green = AColor.FromRgb(0, 255, 0);

    private static LinearCoordinateTransform MakeTransform() => new(BaseTime, BaseTime.AddDays(170), 70m, 150m, 900, 600);

    private static RegularPolygonObject Create(int sides = 6)
        => new(new ChartPoint(BaseTime.AddDays(60), 110m), new ChartPoint(BaseTime.AddDays(80), 118m)) { Sides = sides, Color = LineColor, Thickness = 1.0 };

    private static SKBitmap RenderOnBlack(RegularPolygonObject obj, LinearCoordinateTransform transform)
    {
        var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);
        return bitmap;
    }

    private static SKColor PixelAt(SKBitmap bitmap, double x, double y) => bitmap.GetPixel((int)Math.Round(x), (int)Math.Round(y));

    private static (Point Center, double Radius, double Angle) Geometry(RegularPolygonObject obj, LinearCoordinateTransform transform)
    {
        Point center = transform.ChartToScreen(obj.Points[0]);
        Point vertex = transform.ChartToScreen(obj.Points[1]);
        double radius = Math.Sqrt(Math.Pow(vertex.X - center.X, 2) + Math.Pow(vertex.Y - center.Y, 2));
        return (center, radius, Math.Atan2(vertex.Y - center.Y, vertex.X - center.X));
    }

    /// <summary>A point between two vertices at the given share of the circumscribed radius: outside a hexagon when the share is above cos(30 deg) = 0.866.</summary>
    private static Point BetweenVertices((Point Center, double Radius, double Angle) g, double share)
        => new(g.Center.X + (g.Radius * share * Math.Cos(g.Angle + (Math.PI / 6.0))), g.Center.Y + (g.Radius * share * Math.Sin(g.Angle + (Math.PI / 6.0))));

    [Fact]
    public void Defaults_AreTheDefaultDrawingColour_AndOpacity15_AndOpacityIsClamped()
    {
        using var obj = Create();
        Assert.Equal(15, DrawingFillOpacity.DefaultPercent);
        Assert.Equal(15, obj.PolygonFillOpacity);
        Assert.Equal(15, obj.CircumscribedFillOpacity);
        Assert.Equal(15, obj.InscribedFillOpacity);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.PolygonFillColor);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.CircumscribedFillColor);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.InscribedFillColor);

        obj.PolygonFillOpacity = 250;
        obj.CircumscribedFillOpacity = -4;
        Assert.Equal(100, obj.PolygonFillOpacity);
        Assert.Equal(0, obj.CircumscribedFillOpacity);
    }

    [Fact]
    public void OpacityToAlpha_IsTheIntegerShareOf255_AndDefaultRobustOpacityGivesTheOldBandAlpha()
    {
        Assert.Equal(0, DrawingFillOpacity.ToAlpha(0));
        Assert.Equal(127, DrawingFillOpacity.ToAlpha(50));
        Assert.Equal(255, DrawingFillOpacity.ToAlpha(100));
        Assert.Equal(255, DrawingFillOpacity.ToAlpha(400));
        Assert.Equal(0, DrawingFillOpacity.ToAlpha(-3));
        Assert.Equal(28, DrawingFillOpacity.ToAlpha(11));
    }

    [Fact]
    public void PolygonFill_IsDrawnInTheChosenColour_AndNotAtZeroOpacity()
    {
        using var obj = Create();
        LinearCoordinateTransform transform = MakeTransform();
        Point center = transform.ChartToScreen(obj.Points[0]);

        obj.PolygonFillOpacity = 0;
        using (SKBitmap none = RenderOnBlack(obj, transform))
        {
            Assert.Equal(SKColors.Black, PixelAt(none, center.X, center.Y));
        }

        obj.PolygonFillOpacity = 100;
        using (SKBitmap defaultColour = RenderOnBlack(obj, transform))
        {
            AColor expected = DrawingThemeContext.DefaultColor;
            Assert.Equal(new SKColor(expected.R, expected.G, expected.B), PixelAt(defaultColour, center.X, center.Y));
        }

        obj.PolygonFillColor = Red;
        using (SKBitmap chosen = RenderOnBlack(obj, transform))
        {
            Assert.Equal(new SKColor(255, 0, 0), PixelAt(chosen, center.X, center.Y));
        }
    }

    [Fact]
    public void ANewPolygon_IsFilledAtTheDefaultOpacity()
    {
        using var obj = Create();
        LinearCoordinateTransform transform = MakeTransform();
        Point center = transform.ChartToScreen(obj.Points[0]);
        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, transform);

        Assert.Equal(DrawingFillOpacity.ToAlpha(DrawingFillOpacity.DefaultPercent), PixelAt(bitmap, center.X, center.Y).Alpha);
    }

    [Fact]
    public void PolygonFill_HalfOpacity_HasHalfAlpha()
    {
        using var obj = Create();
        obj.PolygonFillColor = Red;
        obj.PolygonFillOpacity = 50;
        LinearCoordinateTransform transform = MakeTransform();
        Point center = transform.ChartToScreen(obj.Points[0]);
        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, transform);

        Assert.Equal(DrawingFillOpacity.ToAlpha(50), PixelAt(bitmap, center.X, center.Y).Alpha);
    }

    [Fact]
    public void CircumscribedFill_NeedsItsToggle_InDrawingAndInTheHitTest()
    {
        using var obj = Create(6);
        obj.CircumscribedFillColor = Green;
        obj.CircumscribedFillOpacity = 100;
        LinearCoordinateTransform transform = MakeTransform();
        var g = Geometry(obj, transform);
        Point corner = BetweenVertices(g, 0.95);   // outside the hexagon (edge middle at 0.866 R), inside the circle

        using (SKBitmap off = RenderOnBlack(obj, transform))
        {
            Assert.Equal(SKColors.Black, PixelAt(off, corner.X, corner.Y));
        }

        Assert.False(obj.HitTest(corner, transform, 1.0));

        obj.ShowCircumscribedCircle = true;
        using (SKBitmap on = RenderOnBlack(obj, transform))
        {
            Assert.Equal(new SKColor(0, 255, 0), PixelAt(on, corner.X, corner.Y));
        }

        Assert.True(obj.HitTest(corner, transform, 1.0));
    }

    [Fact]
    public void InscribedFill_NeedsItsToggle_InDrawingAndInTheHitTest()
    {
        using var obj = Create(6);
        obj.PolygonFillOpacity = 0;   // only the inscribed area is under test
        obj.InscribedFillColor = Green;
        obj.InscribedFillOpacity = 100;
        LinearCoordinateTransform transform = MakeTransform();
        Point center = transform.ChartToScreen(obj.Points[0]);

        using (SKBitmap off = RenderOnBlack(obj, transform))
        {
            Assert.Equal(SKColors.Black, PixelAt(off, center.X, center.Y));
        }

        Assert.False(obj.HitTest(center, transform, 1.0));

        obj.ShowInscribedCircle = true;
        using (SKBitmap on = RenderOnBlack(obj, transform))
        {
            Assert.Equal(new SKColor(0, 255, 0), PixelAt(on, center.X, center.Y));
        }

        Assert.True(obj.HitTest(center, transform, 1.0));
    }

    [Fact]
    public void PolygonFill_IsIndependentOfTheCircleToggles_AndTakesTheInnerHit()
    {
        using var obj = Create(6);
        obj.PolygonFillOpacity = 40;
        LinearCoordinateTransform transform = MakeTransform();
        var g = Geometry(obj, transform);
        Point inside = BetweenVertices(g, 0.5);
        Point outside = BetweenVertices(g, 0.95);

        Assert.True(obj.HitTest(g.Center, transform, 1.0));
        Assert.True(obj.HitTest(inside, transform, 1.0));
        Assert.False(obj.HitTest(outside, transform, 1.0));

        obj.PolygonFillOpacity = 0;
        Assert.False(obj.HitTest(g.Center, transform, 1.0));
    }

    [Fact]
    public void ZOrder_IsCircumscribedThenPolygonThenInscribed()
    {
        using var obj = Create(6);
        obj.ShowCircumscribedCircle = true;
        obj.ShowInscribedCircle = true;
        obj.InscribedFillOpacity = 0;   // the two-layer check first
        obj.CircumscribedFillColor = AColor.FromRgb(0, 255, 255);
        obj.CircumscribedFillOpacity = 100;
        obj.PolygonFillColor = Red;
        obj.PolygonFillOpacity = 100;
        LinearCoordinateTransform transform = MakeTransform();
        var g = Geometry(obj, transform);
        Point ring = BetweenVertices(g, 0.95);

        using (SKBitmap two = RenderOnBlack(obj, transform))
        {
            Assert.Equal(new SKColor(255, 0, 0), PixelAt(two, g.Center.X, g.Center.Y));   // the polygon is above the circumscribed circle
            Assert.Equal(new SKColor(0, 255, 255), PixelAt(two, ring.X, ring.Y));         // only the circle area reaches the corner
        }

        obj.InscribedFillColor = Green;
        obj.InscribedFillOpacity = 100;
        using (SKBitmap three = RenderOnBlack(obj, transform))
        {
            Assert.Equal(new SKColor(0, 255, 0), PixelAt(three, g.Center.X, g.Center.Y));  // the inscribed circle is above the polygon
        }
    }

    [Fact]
    public void Render_HitTest_AllocateNothing_WithEveryFillOn()
    {
        var obj = Create(12);
        obj.ShowCircumscribedCircle = true;
        obj.ShowInscribedCircle = true;
        obj.PolygonFillOpacity = 30;
        obj.CircumscribedFillOpacity = 20;
        obj.InscribedFillOpacity = 40;
        obj.PolygonFillColor = Red;
        LinearCoordinateTransform transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        SKCanvas canvas = surface.Canvas;

        obj.Render(canvas, transform);
        long renderBytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));
        long hitBytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(new Point(500, 300), transform));
        Assert.Equal(0L, renderBytes);
        Assert.Equal(0L, hitBytes);

        obj.Dispose();
        obj.Render(canvas, transform);
        Assert.False(obj.HitTest(new Point(500, 300), transform, 400.0));
    }

    [Fact]
    public void Fills_SurviveBothPersistencePaths_AndOldFilesLoadTheDefaults()
    {
        using var obj = Create(7);
        obj.PolygonFillColor = Red;
        obj.PolygonFillOpacity = 33;
        obj.CircumscribedFillColor = Green;
        obj.CircumscribedFillOpacity = 12;
        obj.InscribedFillOpacity = 77;   // the colour stays the default colour

        string json = JsonSerializer.Serialize<IChartObject>(obj, JsonOptions);
        using var restored = Assert.IsType<RegularPolygonObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        AssertFills(restored);

        var manager = new ChartObjectManager();
        manager.AddObject(obj);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("POLY", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        AssertFills(Assert.IsType<RegularPolygonObject>(materialized.Objects[ChartDrawingContextType.Standard].Single()));

        // A file written before the fills existed has none of the six properties: the defaults apply.
        using var plain = Create(5);
        string plainJson = JsonSerializer.Serialize<IChartObject>(plain, JsonOptions);
        using var legacy = Assert.IsType<RegularPolygonObject>(JsonSerializer.Deserialize<IChartObject>(plainJson, JsonOptions));
        Assert.Equal(DrawingFillOpacity.DefaultPercent, legacy.PolygonFillOpacity);
        Assert.Equal(DrawingThemeContext.DefaultColor, legacy.PolygonFillColor);

        static void AssertFills(RegularPolygonObject r)
        {
            Assert.Equal(7, r.Sides);
            Assert.Equal(Red, r.PolygonFillColor);
            Assert.Equal(33, r.PolygonFillOpacity);
            Assert.Equal(Green, r.CircumscribedFillColor);
            Assert.Equal(12, r.CircumscribedFillOpacity);
            Assert.Equal(DrawingThemeContext.DefaultColor, r.InscribedFillColor);
            Assert.Equal(77, r.InscribedFillOpacity);
        }
    }
}
