using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>Extended Line (TrendLineObject.ExtendMode) and Cross Line.</summary>
public sealed class ExtendedLineAndCrossLineTests
{
    private const int Width = 800;
    private const int Height = 600;
    private const double EdgeTolerancePx = 0.5;

    private static readonly Rect Clip = new(0, 0, Width, Height);

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private static T RoundTrip<T>(T original) where T : IChartObject
    {
        string json = JsonSerializer.Serialize<IChartObject>(original, Options);
        return Assert.IsType<T>(JsonSerializer.Deserialize<IChartObject>(json, Options));
    }

    private static TrendLineObject Line(double x1, double y1, double x2, double y2, LineExtendMode mode)
        => new(PixelCoordinateTransform.Chart(x1, y1), PixelCoordinateTransform.Chart(x2, y2))
        {
            ExtendMode = mode,
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

    /// <summary>True when any pixel within one pixel of (x, y) differs from the black background.</summary>
    private static bool IsDrawn(SKBitmap bitmap, int x, int y)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                int px = Math.Clamp(x + dx, 0, Width - 1);
                int py = Math.Clamp(y + dy, 0, Height - 1);
                if (bitmap.GetPixel(px, py) != SKColors.Black) return true;
            }
        }
        return false;
    }

    private static void AssertPoint(double expectedX, double expectedY, Point actual)
    {
        Assert.InRange(actual.X, expectedX - EdgeTolerancePx, expectedX + EdgeTolerancePx);
        Assert.InRange(actual.Y, expectedY - EdgeTolerancePx, expectedY + EdgeTolerancePx);
    }

    // ---------------- T1: Extended Line ----------------

    [Fact]
    public void LineExtendMode_Values_AreFixed()
    {
        Assert.Equal(0, (int)LineExtendMode.None);
        Assert.Equal(1, (int)LineExtendMode.Right);
        Assert.Equal(2, (int)LineExtendMode.Left);
        Assert.Equal(3, (int)LineExtendMode.Both);
    }

    [Fact]
    public void NewEnumMembers_AreAppendedAfterTheFormerLastMembers()
    {
        Assert.Equal((int)DrawingTool.Eyedropper + 1, (int)DrawingTool.ExtendedLine);
        Assert.Equal((int)DrawingTool.ExtendedLine + 1, (int)DrawingTool.CrossLine);
        Assert.Equal((int)ChartObjectType.StepBadge + 1, (int)ChartObjectType.CrossLine);
    }

    [Fact]
    public void GetExtendedSegment_Both_EndsLieOnTheClipBorder()
    {
        // y = 100 + 0.5 x : left border at (0, 100), bottom border (y = 600) is reached at x = 1000 > 800, so the right border (800, 500) is hit.
        var (start, end) = TrendLineObject.GetExtendedSegment(new Point(100, 150), new Point(300, 250), Clip, LineExtendMode.Both);

        AssertPoint(0, 100, start);
        AssertPoint(800, 500, end);
    }

    [Fact]
    public void GetExtendedSegment_RightAndLeft_ExtendOnlyTheirOwnEndInClickOrder()
    {
        var s1 = new Point(100, 150);
        var s2 = new Point(300, 250);

        var right = TrendLineObject.GetExtendedSegment(s1, s2, Clip, LineExtendMode.Right);
        Assert.Equal(s1, right.Start);
        AssertPoint(800, 500, right.End);

        var left = TrendLineObject.GetExtendedSegment(s1, s2, Clip, LineExtendMode.Left);
        AssertPoint(0, 100, left.Start);
        Assert.Equal(s2, left.End);

        // Reversed click order: "Right" now means beyond the point that was clicked second (the left one on screen).
        var reversedRight = TrendLineObject.GetExtendedSegment(s2, s1, Clip, LineExtendMode.Right);
        Assert.Equal(s2, reversedRight.Start);
        AssertPoint(0, 100, reversedRight.End);
    }

    [Fact]
    public void GetExtendedSegment_None_ReturnsTheSegmentUnchanged()
    {
        var s1 = new Point(100, 150);
        var s2 = new Point(300, 250);

        var (start, end) = TrendLineObject.GetExtendedSegment(s1, s2, Clip, LineExtendMode.None);

        Assert.Equal(s1, start);
        Assert.Equal(s2, end);
    }

    [Fact]
    public void GetExtendedSegment_VerticalLine_ReachesTopAndBottom()
    {
        var (start, end) = TrendLineObject.GetExtendedSegment(new Point(400, 100), new Point(400, 200), Clip, LineExtendMode.Both);

        AssertPoint(400, 0, start);
        AssertPoint(400, Height, end);
    }

    [Fact]
    public void GetExtendedSegment_HorizontalLine_ReachesLeftAndRight()
    {
        var (start, end) = TrendLineObject.GetExtendedSegment(new Point(100, 300), new Point(200, 300), Clip, LineExtendMode.Both);

        AssertPoint(0, 300, start);
        AssertPoint(Width, 300, end);
    }

    [Fact]
    public void GetExtendedSegment_AnchorsCloserThanTheMinimumSpan_AreNotExtended()
    {
        var s1 = new Point(100, 100);
        var s2 = new Point(100 + ChartConstants.ExtendedLineMinSpanPixels * 0.5, 100);

        var (start, end) = TrendLineObject.GetExtendedSegment(s1, s2, Clip, LineExtendMode.Both);

        Assert.Equal(s1, start);
        Assert.Equal(s2, end);
    }

    [Fact]
    public void GetExtendedSegment_LineMissingTheClip_IsNotExtended()
    {
        // Horizontal line 300 px above the clip: the infinite line never meets the rectangle.
        var s1 = new Point(100, -300);
        var s2 = new Point(300, -300);

        var (start, end) = TrendLineObject.GetExtendedSegment(s1, s2, Clip, LineExtendMode.Both);

        Assert.Equal(s1, start);
        Assert.Equal(s2, end);
    }

    [Fact]
    public void GetExtendedSegment_AnchorOutsideTheClip_KeepsThatEnd()
    {
        // P2 is already beyond the right border: "Right" has nothing to add, "Left" still reaches the left border.
        var s1 = new Point(400, 300);
        var s2 = new Point(900, 300);

        var right = TrendLineObject.GetExtendedSegment(s1, s2, Clip, LineExtendMode.Right);
        Assert.Equal(s2, right.End);

        var left = TrendLineObject.GetExtendedSegment(s1, s2, Clip, LineExtendMode.Left);
        AssertPoint(0, 300, left.Start);
    }

    [Fact]
    public void Render_ModeNone_IsPixelIdenticalToThePlainSegment()
    {
        using var obj = Line(100, 150, 300, 250, LineExtendMode.None);
        using SKBitmap actual = RenderBitmap(obj);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        using (var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            Color = obj.SkiaColor,
            StrokeWidth = (float)obj.Thickness,
            PathEffect = obj.StrokeStyle.ToSkPathEffect()
        })
        {
            surface.Canvas.DrawLine(100, 150, 300, 250, paint);
        }
        using var image = surface.Snapshot();
        using SKBitmap expected = SKBitmap.FromImage(image);

        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));   // span compare: no 2 MB array copies (they would trigger collections that disturb the allocation probes of other tests)
    }

    [Fact]
    public void Render_Both_DrawsTheExtendedPartsAndNoneDoesNot()
    {
        using var extended = Line(100, 150, 300, 250, LineExtendMode.Both);
        using var plain = Line(100, 150, 300, 250, LineExtendMode.None);

        using SKBitmap extendedBitmap = RenderBitmap(extended);
        using SKBitmap plainBitmap = RenderBitmap(plain);

        // Left extension at x = 50 (y = 125) and right extension at x = 600 (y = 400).
        Assert.True(IsDrawn(extendedBitmap, 50, 125));
        Assert.True(IsDrawn(extendedBitmap, 600, 400));
        Assert.False(IsDrawn(plainBitmap, 50, 125));
        Assert.False(IsDrawn(plainBitmap, 600, 400));
        // The body is drawn in both.
        Assert.True(IsDrawn(extendedBitmap, 200, 200));
        Assert.True(IsDrawn(plainBitmap, 200, 200));
    }

    [Fact]
    public void ExtendedEnds_LieOnTheScreenLineThroughTheAnchors_OnALogarithmicPriceAxis()
    {
        // Decision record (spec 0016, L-6 A): the extension is a straight continuation in SCREEN space. On a logarithmic price axis the
        // screen line is not the straight-line-in-price line of TrendLinePrice, but the extended ends must lie exactly on the line
        // through the two projected anchors, on the clip border, with no kink at the anchors.
        var log = new PixelCoordinateTransform(logarithmicPrice: true);
        using var obj = Line(100, 100, 300, 200, LineExtendMode.Both);
        Point s1 = log.ChartToScreen(obj.Points[0]);
        Point s2 = log.ChartToScreen(obj.Points[1]);

        var (start, end) = TrendLineObject.GetExtendedSegment(s1, s2, log.ScreenRect, LineExtendMode.Both);

        // Both ends are on the infinite screen line through s1 and s2 (distance from the line, in pixels).
        double dx = s2.X - s1.X;
        double dy = s2.Y - s1.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        foreach (Point end0 in new[] { start, end })
        {
            double distance = Math.Abs(dx * (end0.Y - s1.Y) - dy * (end0.X - s1.X)) / length;
            Assert.True(distance < 1e-6, $"The end ({end0.X}, {end0.Y}) is {distance} px away from the anchor line.");
        }

        // Both ends are on the border of the drawing area and outside (beyond) the anchors in click order.
        foreach (Point end0 in new[] { start, end })
        {
            bool onBorder = Math.Abs(end0.X - log.ScreenRect.Left) <= EdgeTolerancePx || Math.Abs(end0.X - log.ScreenRect.Right) <= EdgeTolerancePx
                || Math.Abs(end0.Y - log.ScreenRect.Top) <= EdgeTolerancePx || Math.Abs(end0.Y - log.ScreenRect.Bottom) <= EdgeTolerancePx;
            Assert.True(onBorder, $"The end ({end0.X}, {end0.Y}) is not on the clip border.");
        }
        Assert.True((end.X - s2.X) * dx + (end.Y - s2.Y) * dy > 0);     // Right end is beyond P2
        Assert.True((start.X - s1.X) * dx + (start.Y - s1.Y) * dy < 0); // Left end is beyond P1

        // On screen the drawn pixels follow that line (not the data-space line, which is about 12 px away at x = 400).
        double screenLineY = s1.Y + dy / dx * (400 - s1.X);
        double dataSpaceY = log.GetYFromPrice(250m);   // TrendLinePrice at day 400: 100 + (200 - 100) / (300 - 100) * (400 - 100) = 250
        Assert.True(Math.Abs(screenLineY - dataSpaceY) > 10, "The probe would not tell the two definitions apart.");

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        obj.Render(surface.Canvas, log);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        Assert.True(IsDrawn(bitmap, 400, (int)Math.Round(screenLineY)));
        Assert.False(IsDrawn(bitmap, 400, (int)Math.Round(dataSpaceY)));
    }

    [Fact]
    public void Render_Both_AllocatesZeroManagedBytes()
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        var transform = new PixelCoordinateTransform();
        using var obj = Line(100, 150, 300, 250, LineExtendMode.Both);

        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        long bytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void HitTest_ExtendedPartCountsLikeTheBody()
    {
        var transform = new PixelCoordinateTransform();
        using var extended = Line(100, 150, 300, 250, LineExtendMode.Both);
        using var plain = Line(100, 150, 300, 250, LineExtendMode.None);

        var onRightExtension = new Point(600, 400);
        var onLeftExtension = new Point(50, 125);
        var nearRightExtension = new Point(600, 400 + ChartConstants.DefaultHitTestTolerance - 0.5);
        var farFromRightExtension = new Point(600, 400 + ChartConstants.DefaultHitTestTolerance + 5);

        Assert.True(extended.HitTest(onRightExtension, transform));
        Assert.True(extended.HitTest(onLeftExtension, transform));
        Assert.True(extended.HitTest(nearRightExtension, transform));
        Assert.False(extended.HitTest(farFromRightExtension, transform));

        Assert.False(plain.HitTest(onRightExtension, transform));
        Assert.False(plain.HitTest(onLeftExtension, transform));
        Assert.True(plain.HitTest(new Point(200, 200), transform));
    }

    [Theory]
    [InlineData(LineExtendMode.None)]
    [InlineData(LineExtendMode.Right)]
    [InlineData(LineExtendMode.Left)]
    [InlineData(LineExtendMode.Both)]
    public void CalculatedValues_AreTheStraightLinePriceOutsideTheSegmentForEveryMode(LineExtendMode mode)
    {
        // The trend line value is already the extrapolated straight-line price on both sides of the segment (golden-pinned),
        // so the extension mode must not change it (TrendLineValues is intentionally not parameterised by the extension mode).
        using var obj = new TrendLineObject(PixelCoordinateTransform.Chart(0, 100), PixelCoordinateTransform.Chart(10, 200)) { ExtendMode = mode };
        DateTime epoch = PixelCoordinateTransform.Chart(0, 0).Time;

        decimal right = Assert.Single(obj.GetCalculatedValues(epoch.AddDays(15))).NumericValue!.Value;
        decimal left = Assert.Single(obj.GetCalculatedValues(epoch.AddDays(-5))).NumericValue!.Value;

        Assert.Equal(250m, right);
        Assert.Equal(50m, left);
    }

    [Fact]
    public void ExtendedLineTool_PresetStartsWithBoth_TrendLineToolStaysNone()
    {
        var presets = new DrawingToolPresetService();

        var extendedBehavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.ExtendedLine);
        var trendBehavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.TrendLine);
        Assert.NotNull(extendedBehavior);
        Assert.NotNull(trendBehavior);
        Assert.Equal(2, extendedBehavior!.RequiredSteps);
        Assert.False(extendedBehavior.FinishesOnRelease);

        var fromExtendedTool = Assert.IsType<TrendLineObject>(extendedBehavior.CreateObject(PixelCoordinateTransform.Chart(1, 1)));
        var fromTrendTool = Assert.IsType<TrendLineObject>(trendBehavior!.CreateObject(PixelCoordinateTransform.Chart(1, 1)));
        // The behaviour itself creates the plain object; the preset is what gives the tool its initial value.
        Assert.Equal(LineExtendMode.None, fromExtendedTool.ExtendMode);

        presets.ApplyPresetToNewObject(DrawingTool.ExtendedLine, fromExtendedTool);
        presets.ApplyPresetToNewObject(DrawingTool.TrendLine, fromTrendTool);

        Assert.Equal(ChartObjectType.TrendLine, fromExtendedTool.Type);
        Assert.Equal(TrendLineObject.ExtendedLineToolDefaultMode, fromExtendedTool.ExtendMode);
        Assert.Equal(LineExtendMode.Both, fromExtendedTool.ExtendMode);
        Assert.Equal(LineExtendMode.None, fromTrendTool.ExtendMode);
    }

    [Fact]
    public void ExtendedLineTool_UserEditedPresetWins()
    {
        var presets = new DrawingToolPresetService();
        var prototype = Assert.IsType<TrendLineObject>(presets.GetOrCreatePresetObject(DrawingTool.ExtendedLine));
        Assert.Equal(LineExtendMode.Both, prototype.ExtendMode);

        prototype.ExtendMode = LineExtendMode.Right;

        var created = new TrendLineObject(PixelCoordinateTransform.Chart(1, 1), PixelCoordinateTransform.Chart(2, 2));
        presets.ApplyPresetToNewObject(DrawingTool.ExtendedLine, created);

        Assert.Equal(LineExtendMode.Right, created.ExtendMode);
    }

    [Fact]
    public void ExtendMode_RoundTripsThroughPersistence_AndOldJsonWithoutItReadsAsNone()
    {
        var original = Line(100, 150, 300, 250, LineExtendMode.Left);

        var restored = RoundTrip(original);
        Assert.Equal(LineExtendMode.Left, restored.ExtendMode);
        Assert.Equal(original.Points[1].Price, restored.Points[1].Price);

        var json = JsonNode.Parse(JsonSerializer.Serialize<IChartObject>(Line(100, 150, 300, 250, LineExtendMode.Both), Options))!.AsObject();
        Assert.True(json.Remove("extendMode"));
        var old = Assert.IsType<TrendLineObject>(JsonSerializer.Deserialize<IChartObject>(json.ToJsonString(), Options));
        Assert.Equal(LineExtendMode.None, old.ExtendMode);
    }

    [Fact]
    public void ExtendMode_IsAParametersCategoryAnalysisTaggedProperty_NotHiddenByTheSharedEditor()
    {
        var property = typeof(TrendLineObject).GetProperty(nameof(TrendLineObject.ExtendMode))!;

        var category = (System.ComponentModel.CategoryAttribute)Attribute.GetCustomAttribute(property, typeof(System.ComponentModel.CategoryAttribute))!;
        var tags = Attribute.GetCustomAttributes(property, typeof(ParameterTagAttribute)).Cast<ParameterTagAttribute>().Select(t => t.Tag);

        Assert.Equal("Parameters", category.Category);
        // "Common" is hidden by the fallback settings editor and by the preset editor; a tool-specific parameter must use "Analysis".
        Assert.Contains(DrawingParameterTags.Analysis, tags);
        Assert.DoesNotContain(DrawingParameterTags.Common, tags);
    }

    // ---------------- T2: Cross Line ----------------

    private static CrossLineObject Cross(double x, double y)
        => new(PixelCoordinateTransform.Chart(x, y)) { Color = Colors.White, Thickness = 2 };

    [Fact]
    public void CrossLine_Metadata_RegistryAndToolbar()
    {
        using var obj = Cross(100, 200);
        Assert.Equal(ChartObjectType.CrossLine, obj.Type);
        Assert.IsAssignableFrom<IDrawingCalculatedValuesProvider>(obj);

        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.CrossLine);
        Assert.NotNull(behavior);
        Assert.Equal(1, behavior!.RequiredSteps);
        Assert.IsType<CrossLineObject>(behavior.CreateObject(PixelCoordinateTransform.Chart(3, 4)));

        var lines = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Lines");
        Assert.Contains(lines.Tools, t => t.Tool == DrawingTool.CrossLine && t.NameKey == "DrawTool_CrossLine");
        Assert.Contains(lines.Tools, t => t.Tool == DrawingTool.ExtendedLine && t.NameKey == "DrawTool_ExtendedLine");
    }

    [Fact]
    public void CrossLine_Defaults_MatchTheHorizontalLine()
    {
        using var horizontal = new HorizontalLineObject(PixelCoordinateTransform.Chart(1, 1));
        using var cross = new CrossLineObject(PixelCoordinateTransform.Chart(1, 1));

        Assert.Equal(horizontal.Color, cross.Color);
        Assert.Equal(horizontal.Thickness, cross.Thickness);
        Assert.Equal(horizontal.StrokeStyle, cross.StrokeStyle);
        Assert.Equal(horizontal.MoveAxisMode, cross.MoveAxisMode);
        Assert.Equal(horizontal.PanelIndex, cross.PanelIndex);
    }

    [Fact]
    public void CrossLineValues_AreTheHorizontalValuesFollowedByTheVerticalValues()
    {
        var anchor = new DrawingAnchor(new DateTime(2026, 1, 10, 6, 0, 0), 123.456m);
        var timestamp = new DateTime(2026, 1, 15, 18, 30, 0);
        var color = new IndicatorColor(255, 10, 20, 30);

        var expected = HorizontalLineValues.Calculate(anchor, color).Concat(VerticalLineValues.Calculate(anchor, timestamp, color)).ToArray();
        var actual = CrossLineValues.Calculate(anchor, timestamp, color);

        Assert.IsType<DrawingCalculatedValue[]>(actual);
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
        Assert.Equal(new[] { "Price", "LineDate", "Offset" }, actual.Select(v => v.Key).ToArray());
    }

    [Fact]
    public void CrossLineObject_GetCalculatedValues_DelegatesAndGuardsAnEmptyPointList()
    {
        using var obj = Cross(100, 200);
        DateTime timestamp = PixelCoordinateTransform.Chart(105, 0).Time;
        var color = new IndicatorColor(Colors.White.A, Colors.White.R, Colors.White.G, Colors.White.B);

        var values = obj.GetCalculatedValues(timestamp);
        var expected = CrossLineValues.Calculate(obj.Points[0].ToAnchor(), timestamp, color);

        Assert.Equal(expected.Count, values.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], values[i]);

        obj.Points.Clear();
        Assert.Empty(obj.GetCalculatedValues(timestamp));
    }

    [Fact]
    public void CrossLine_Render_DrawsFullWidthAndFullHeightLines()
    {
        using var obj = Cross(100, 200);
        using SKBitmap bitmap = RenderBitmap(obj);

        Assert.True(IsDrawn(bitmap, 700, 200));  // horizontal arm, far right
        Assert.True(IsDrawn(bitmap, 2, 200));    // horizontal arm, far left
        Assert.True(IsDrawn(bitmap, 100, 550));  // vertical arm, bottom
        Assert.True(IsDrawn(bitmap, 100, 5));    // vertical arm, top
        Assert.False(IsDrawn(bitmap, 500, 400)); // away from both arms
    }

    [Fact]
    public void CrossLine_Render_AllocatesZeroManagedBytes()
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        var transform = new PixelCoordinateTransform();
        using var obj = Cross(100, 200);

        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        long bytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void CrossLine_HitTest_OnEitherArmCrossingAndToleranceEdge()
    {
        var transform = new PixelCoordinateTransform();
        using var obj = Cross(100, 200);
        double tolerance = ChartConstants.DefaultHitTestTolerance;

        Assert.True(obj.HitTest(new Point(500, 200), transform));                 // on the horizontal arm
        Assert.True(obj.HitTest(new Point(100, 500), transform));                 // on the vertical arm
        Assert.True(obj.HitTest(new Point(100, 200), transform));                 // crossing
        Assert.True(obj.HitTest(new Point(500, 200 + tolerance - 0.5), transform));
        Assert.True(obj.HitTest(new Point(100 + tolerance - 0.5, 500), transform));
        Assert.False(obj.HitTest(new Point(500, 200 + tolerance + 5), transform));
        Assert.False(obj.HitTest(new Point(100 + tolerance + 5, 500), transform));
        Assert.False(obj.HitTest(new Point(500, 400), transform));

        obj.Points.Clear();
        Assert.False(obj.HitTest(new Point(500, 200), transform));
    }

    [Fact]
    public void CrossLine_Render_AfterDispose_IsANoOp_NotANativeCrash()
    {
        var obj = Cross(100, 200);
        obj.Dispose();
        obj.Dispose();   // idempotent

        using SKBitmap bitmap = RenderBitmap(obj);

        Assert.False(IsDrawn(bitmap, 700, 200));
    }

    [Fact]
    public void CrossLine_Translate_MovesTimeAndPrice()
    {
        using var obj = Cross(100, 200);
        ChartPoint before = obj.Points[0];

        obj.Translate(TimeSpan.FromDays(3), 25m);

        Assert.Equal(before.Time.AddDays(3), obj.Points[0].Time);
        Assert.Equal(before.Price + 25m, obj.Points[0].Price);
    }

    [Fact]
    public void CrossLine_RoundTripsThroughPersistence()
    {
        var original = Cross(100, 200);
        original.Color = Colors.Orange;
        original.Thickness = 3.5;
        original.StrokeStyle = DrawingStrokeStyle.Dash;
        original.ZIndex = 4;
        original.PanelIndex = 2;

        var restored = RoundTrip(original);

        Assert.Equal(ChartObjectType.CrossLine, restored.Type);
        Assert.Single(restored.Points);
        Assert.Equal(original.Points[0].Time, restored.Points[0].Time);
        Assert.Equal(original.Points[0].Price, restored.Points[0].Price);
        Assert.Equal(Colors.Orange, restored.Color);
        Assert.Equal(3.5, restored.Thickness);
        Assert.Equal(DrawingStrokeStyle.Dash, restored.StrokeStyle);
        Assert.Equal(4, restored.ZIndex);
        Assert.Equal(2, restored.PanelIndex);
    }
}
