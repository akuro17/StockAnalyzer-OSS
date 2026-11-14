using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary><see cref="ParallelChannelObject.ChannelMode"/> (Parallel | FlatOpposite).</summary>
public sealed class ParallelChannelFlatOppositeTests
{
    private const int Width = 800;
    private const int Height = 600;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private static readonly DateTime T0 = new(2026, 1, 1);
    private static readonly IndicatorColor Gray = new(255, 10, 20, 30);

    private static ParallelChannelObject Channel(ChannelMode mode, bool filled = true)
        => new(PixelCoordinateTransform.Chart(100, 100), PixelCoordinateTransform.Chart(300, 200), PixelCoordinateTransform.Chart(200, 300))
        {
            ChannelMode = mode,
            IsFilled = filled,
            Color = Colors.White,
            Thickness = 2
        };

    private static SKBitmap Render(IChartObject obj, ICoordinateTransform? transform = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        obj.Render(surface.Canvas, transform ?? new PixelCoordinateTransform());
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

    // ---------------- Definition ----------------

    [Fact]
    public void ChannelMode_Values_AreFixed_AndDefaultIsParallel()
    {
        Assert.Equal(0, (int)ChannelMode.Parallel);
        Assert.Equal(1, (int)ChannelMode.FlatOpposite);
        Assert.Equal(ChannelMode.Parallel, new ParallelChannelObject(default, default, default).ChannelMode);
    }

    [Fact]
    public void ChannelMode_IsAParametersCategoryAnalysisTaggedProperty_NotHiddenByTheSharedEditor()
    {
        var property = typeof(ParallelChannelObject).GetProperty(nameof(ParallelChannelObject.ChannelMode))!;

        var category = (System.ComponentModel.CategoryAttribute)Attribute.GetCustomAttribute(property, typeof(System.ComponentModel.CategoryAttribute))!;
        var tags = Attribute.GetCustomAttributes(property, typeof(ParameterTagAttribute)).Cast<ParameterTagAttribute>().Select(t => t.Tag);

        Assert.Equal("Parameters", category.Category);
        // "Common" is hidden by the fallback settings editor and by the preset editor; a tool-specific parameter must use "Analysis".
        Assert.Contains(DrawingParameterTags.Analysis, tags);
        Assert.DoesNotContain(DrawingParameterTags.Common, tags);
    }

    // ---------------- Values ----------------

    [Fact]
    public void Values_Parallel_DefaultArgumentEqualsExplicitParallel()
    {
        var p1 = new DrawingAnchor(T0, 100m);
        var p2 = new DrawingAnchor(T0.AddDays(10), 200m);
        var p3 = new DrawingAnchor(T0.AddDays(4), 130m);

        foreach (DateTime t in new[] { T0.AddDays(5), T0.AddDays(-3), T0.AddDays(17.25) })
        {
            var implicitMode = ParallelChannelValues.Calculate(p1, p2, p3, t, Gray);
            var explicitMode = ParallelChannelValues.Calculate(p1, p2, p3, t, Gray, ChannelMode.Parallel);

            Assert.Equal(implicitMode.Count, explicitMode.Count);
            for (int i = 0; i < implicitMode.Count; i++) Assert.Equal(implicitMode[i], explicitMode[i]);
        }
    }

    [Fact]
    public void Values_Parallel_HandCalculationIsUnchanged()
    {
        // Base edge at day 5 = 150; base edge at P3 (day 4) = 140; offset = -10 -> parallel edge 140.
        var values = ParallelChannelValues.Calculate(new DrawingAnchor(T0, 100m), new DrawingAnchor(T0.AddDays(10), 200m), new DrawingAnchor(T0.AddDays(4), 130m), T0.AddDays(5), Gray);

        Assert.Equal(new[] { "UpperLine", "Midline", "LowerLine", "ChannelWidth", "Slope" }, values.Select(v => v.Key).ToArray());
        Assert.Equal(150m, values[0].NumericValue);
        Assert.Equal(145m, values[1].NumericValue);
        Assert.Equal(140m, values[2].NumericValue);
        Assert.Equal(10m, values[3].NumericValue);
        Assert.Equal(10m, values[4].NumericValue);
    }

    [Fact]
    public void Values_FlatOpposite_HandCalculation_SameItemNamesAndOrder()
    {
        // Base edge at day 5 = 150, opposite edge = P3 price 130: upper 150, mid 140, lower 130, width 20, slope of the base edge 10 / day.
        var values = ParallelChannelValues.Calculate(new DrawingAnchor(T0, 100m), new DrawingAnchor(T0.AddDays(10), 200m), new DrawingAnchor(T0.AddDays(4), 130m), T0.AddDays(5), Gray, ChannelMode.FlatOpposite);

        Assert.Equal(new[] { "UpperLine", "Midline", "LowerLine", "ChannelWidth", "Slope" }, values.Select(v => v.Key).ToArray());
        Assert.Equal(150m, values[0].NumericValue);
        Assert.Equal(140m, values[1].NumericValue);
        Assert.Equal(130m, values[2].NumericValue);
        Assert.Equal(20m, values[3].NumericValue);
        Assert.Equal(10m, values[4].NumericValue);
    }

    [Fact]
    public void Values_FlatOpposite_OppositeEdgeAboveTheBaseEdge_SwapsUpperAndLower_AndWidthFollowsTheTime()
    {
        var p1 = new DrawingAnchor(T0, 100m);
        var p2 = new DrawingAnchor(T0.AddDays(10), 200m);
        var p3 = new DrawingAnchor(T0.AddDays(4), 230m);

        var atDay5 = ParallelChannelValues.Calculate(p1, p2, p3, T0.AddDays(5), Gray, ChannelMode.FlatOpposite);
        Assert.Equal(230m, atDay5[0].NumericValue);   // upper = flat edge
        Assert.Equal(190m, atDay5[1].NumericValue);   // (150 + 230) / 2
        Assert.Equal(150m, atDay5[2].NumericValue);   // lower = base edge
        Assert.Equal(80m, atDay5[3].NumericValue);

        // The width is the distance between the two edges at that time (the base edge keeps rising, the opposite edge is flat).
        var atDay9 = ParallelChannelValues.Calculate(p1, p2, p3, T0.AddDays(9), Gray, ChannelMode.FlatOpposite);
        Assert.Equal(40m, atDay9[3].NumericValue);    // |230 - 190|
    }

    [Theory]
    [InlineData(ChannelMode.Parallel)]
    [InlineData(ChannelMode.FlatOpposite)]
    public void Values_SameTimeForBothBaseAnchors_ReturnsNothingInEveryMode(ChannelMode mode)
    {
        var values = ParallelChannelValues.Calculate(new DrawingAnchor(T0, 100m), new DrawingAnchor(T0, 120m), new DrawingAnchor(T0.AddDays(2), 130m), T0.AddDays(1), Gray, mode);

        Assert.Empty(values);
    }

    [Fact]
    public void ObjectValues_PassTheObjectsChannelMode()
    {
        var parallel = Channel(ChannelMode.Parallel);
        var flat = Channel(ChannelMode.FlatOpposite);
        // Day 250, not P3's own day 200: both edges pass through P3 there, so the two modes agree at that time.
        DateTime t = PixelCoordinateTransform.Chart(250, 0).Time;
        var color = new IndicatorColor(Colors.White.A, Colors.White.R, Colors.White.G, Colors.White.B);

        var expectedParallel = ParallelChannelValues.Calculate(parallel.Points[0].ToAnchor(), parallel.Points[1].ToAnchor(), parallel.Points[2].ToAnchor(), t, color);
        var expectedFlat = ParallelChannelValues.Calculate(flat.Points[0].ToAnchor(), flat.Points[1].ToAnchor(), flat.Points[2].ToAnchor(), t, color, ChannelMode.FlatOpposite);

        Assert.Equal(expectedParallel, parallel.GetCalculatedValues(t));
        Assert.Equal(expectedFlat, flat.GetCalculatedValues(t));
        Assert.NotEqual(parallel.GetCalculatedValues(t)[1].NumericValue, flat.GetCalculatedValues(t)[1].NumericValue);
    }

    // ---------------- Render / hit test ----------------

    [Fact]
    public void Render_Parallel_IsPixelIdenticalToTheOriginalConstruction()
    {
        var obj = Channel(ChannelMode.Parallel);
        using SKBitmap actual = Render(obj);

        // The original screen-space construction: opposite edge through s3 with the slope of s1-s2.
        var s1 = new SKPoint(100, 100);
        var s2 = new SKPoint(300, 200);
        var s3 = new SKPoint(200, 300);
        float m = (s2.Y - s1.Y) / (s2.X - s1.X);
        var s1p = new SKPoint(s1.X, m * (s1.X - s3.X) + s3.Y);
        var s2p = new SKPoint(s2.X, m * (s2.X - s3.X) + s3.Y);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        using (var path = new SKPath())
        {
            path.MoveTo(s1);
            path.LineTo(s2);
            path.LineTo(s2p);
            path.LineTo(s1p);
            path.Close();
            using var fill = new SKPaint { Color = new SKColor(obj.SkiaColor.Red, obj.SkiaColor.Green, obj.SkiaColor.Blue, 30), Style = SKPaintStyle.Fill };
            surface.Canvas.DrawPath(path, fill);
            using var stroke = new SKPaint
            {
                Color = obj.SkiaColor,
                StrokeWidth = (float)obj.Thickness,
                Style = SKPaintStyle.Stroke,
                PathEffect = obj.StrokeStyle.ToSkPathEffect(),
                IsAntialias = true
            };
            surface.Canvas.DrawPath(path, stroke);
            using var center = new SKPaint
            {
                Color = obj.SkiaColor,
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke,
                PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
                IsAntialias = true
            };
            surface.Canvas.DrawLine((s1.X + s1p.X) / 2, (s1.Y + s1p.Y) / 2, (s2.X + s2p.X) / 2, (s2.Y + s2p.Y) / 2, center);
        }
        using var image = surface.Snapshot();
        using SKBitmap expected = SKBitmap.FromImage(image);

        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));   // span compare: no 2 MB array copies (they would trigger collections that disturb the allocation probes of other tests)
    }

    [Fact]
    public void Render_FlatOpposite_DrawsAHorizontalOppositeEdgeAtThePriceOfP3()
    {
        var flat = Channel(ChannelMode.FlatOpposite);
        var parallel = Channel(ChannelMode.Parallel);
        using SKBitmap flatBitmap = Render(flat);
        using SKBitmap parallelBitmap = Render(parallel);

        // Opposite edge y = 300 from x = 100 to x = 300; the parallel mode would be at y = 260 for x = 120.
        Assert.True(IsDrawn(flatBitmap, 120, 300));
        Assert.True(IsDrawn(flatBitmap, 280, 300));
        Assert.False(IsDrawn(parallelBitmap, 120, 300));
        // Base edge and the two vertical sides are drawn; nothing beyond the anchors' times.
        Assert.True(IsDrawn(flatBitmap, 200, 150));
        Assert.True(IsDrawn(flatBitmap, 100, 250));
        Assert.True(IsDrawn(flatBitmap, 300, 250));
        Assert.False(IsDrawn(flatBitmap, 350, 300));
        // Filled polygon P1, P2, (t2, p3), (t1, p3): its inside is tinted.
        Assert.True(IsDrawn(flatBitmap, 200, 260));
    }

    [Fact]
    public void Render_FlatOpposite_CenterLineIsTheDataMidpointLine_EvenOnALogPriceScale()
    {
        // Log scale: pixel Y = 600 - 100 * ln(price). Center line starts at (t1, (100 + 300) / 2 = 200) -> y = 70.1, not at the screen midpoint (~84.6).
        var flat = Channel(ChannelMode.FlatOpposite, filled: false);
        using SKBitmap bitmap = Render(flat, new PixelCoordinateTransform(logarithmicPrice: true));

        Assert.True(IsDrawn(bitmap, 104, 70));
        Assert.False(IsDrawn(bitmap, 104, 85));
    }

    [Fact]
    public void HitTest_FlatOpposite_UsesTheFlatPolygonAndEdges()
    {
        var transform = new PixelCoordinateTransform();
        var flat = Channel(ChannelMode.FlatOpposite);
        var parallel = Channel(ChannelMode.Parallel);

        Assert.True(flat.HitTest(new Point(120, 300), transform));   // on the flat opposite edge
        Assert.True(flat.HitTest(new Point(200, 260), transform));   // inside the polygon
        Assert.True(flat.HitTest(new Point(200, 150), transform));   // on the base edge
        Assert.False(flat.HitTest(new Point(350, 300), transform));  // beyond P2's time
        Assert.False(flat.HitTest(new Point(200, 350), transform));  // below the opposite edge

        Assert.False(parallel.HitTest(new Point(120, 300), transform));
    }

    // ---------------- Persistence ----------------

    [Fact]
    public void ChannelMode_RoundTrips_AndOldJsonWithoutItReadsAsParallel()
    {
        var original = Channel(ChannelMode.FlatOpposite);

        string json = JsonSerializer.Serialize<IChartObject>(original, Options);
        var restored = Assert.IsType<ParallelChannelObject>(JsonSerializer.Deserialize<IChartObject>(json, Options));
        Assert.Equal(ChannelMode.FlatOpposite, restored.ChannelMode);
        Assert.Equal(3, restored.Points.Count);
        Assert.Equal(original.Points[2].Price, restored.Points[2].Price);
        Assert.True(restored.IsFilled);
        Assert.Equal(Colors.White, restored.Color);

        var node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("channelMode"));
        var old = Assert.IsType<ParallelChannelObject>(JsonSerializer.Deserialize<IChartObject>(node.ToJsonString(), Options));
        Assert.Equal(ChannelMode.Parallel, old.ChannelMode);
    }
}
