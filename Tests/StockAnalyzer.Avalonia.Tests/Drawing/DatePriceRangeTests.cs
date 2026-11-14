using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

[Collection("DrawingThemeContext State")]
public sealed class DatePriceRangeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    [Fact]
    public void Enums_AreAppendedAfterDisjointChannel_AndToolIsRegistered()
    {
        Assert.Equal((int)ChartObjectType.DisjointChannel + 1, (int)ChartObjectType.DatePriceRange);
        Assert.Equal((int)DrawingTool.DisjointChannel + 1, (int)DrawingTool.DatePriceRange);

        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.DatePriceRange);
        Assert.IsType<DatePriceRangeBehavior>(behavior);
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var categories = DrawingToolCategoryService.GetCategories();
        Assert.Contains(
            categories.SelectMany(c => c.Tools),
            t => t.Tool == DrawingTool.DatePriceRange && t.NameKey == "DrawTool_DatePriceRange");

        Assert.False(DrawingValueCatalog.TryGet("DatePriceRange", out _));
        Assert.Equal(15, DrawingValueCatalog.All.Count);
    }

    [Fact]
    public void Serialization_RoundTripsPointsAndLabelToggles()
    {
        using var original = new DatePriceRangeObject(
            PixelCoordinateTransform.Chart(100, 200),
            PixelCoordinateTransform.Chart(300, 100))
        {
            Color = Colors.Teal,
            Thickness = 2.0,
            CustomName = "My Range",
            ShowPriceLabel = false,
            ShowBarLabel = true,
            ShowVolumeLabel = false
        };

        string json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        using var restored = Assert.IsType<DatePriceRangeObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));

        Assert.Equal(original.Points.Count, restored.Points.Count);
        Assert.Equal(original.Points[0].Time, restored.Points[0].Time);
        Assert.Equal(original.Points[0].Price, restored.Points[0].Price);
        Assert.Equal(original.Points[1].Time, restored.Points[1].Time);
        Assert.Equal(original.Points[1].Price, restored.Points[1].Price);
        Assert.Equal("My Range", restored.CustomName);
        Assert.False(restored.ShowPriceLabel);
        Assert.True(restored.ShowBarLabel);
        Assert.False(restored.ShowVolumeLabel);
    }

    [Fact]
    public void GetCalculatedValues_ReturnsSixDerivedMetrics_AndRecalculatorUpdatesThem()
    {
        var p1 = PixelCoordinateTransform.Chart(100, 200);
        var p2 = PixelCoordinateTransform.Chart(200, 100);
        using var obj = new DatePriceRangeObject(p1, p2);

        DateTime tMin = p1.Time <= p2.Time ? p1.Time : p2.Time;
        DateTime tMax = p1.Time <= p2.Time ? p2.Time : p1.Time;
        var candles = new[]
        {
            new CoreCandleData(tMin, 100m, 110m, 95m, 105m, 1_000L),
            new CoreCandleData(tMin + ((tMax - tMin) / 2), 105m, 115m, 100m, 112m, 2_000L),
            new CoreCandleData(tMax, 112m, 120m, 108m, 118m, 3_000L)
        };

        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));

        var values = obj.GetCalculatedValues(tMin);
        Assert.Equal(6, values.Count);
        Assert.Equal(p2.Price - p1.Price, values[0].NumericValue);
        Assert.Equal(3m, values[2].NumericValue);
        Assert.Equal(6_000m, values[4].NumericValue);
        Assert.Equal(2_000m, values[5].NumericValue);
        Assert.DoesNotContain(values, v => v.Key is "P1" or "P2" or "Start" or "End");

        obj.Points.Clear();
        Assert.Empty(obj.GetCalculatedValues(tMin));
    }

    [Fact]
    public void HitTest_MatchesInsideAndBorderTolerance_AndExcludesOutsideOrHidden()
    {
        var transform = new PixelCoordinateTransform();
        using var obj = new DatePriceRangeObject(
            PixelCoordinateTransform.Chart(100, 100),
            PixelCoordinateTransform.Chart(300, 200));

        Assert.True(obj.HitTest(new Point(200, 150), transform, tolerance: 5.0)); // inside
        Assert.True(obj.HitTest(new Point(100, 150), transform, tolerance: 5.0)); // border
        Assert.True(obj.HitTest(new Point(96, 150), transform, tolerance: 5.0));  // within tolerance
        Assert.False(obj.HitTest(new Point(80, 150), transform, tolerance: 5.0)); // outside

        obj.IsVisible = false;
        Assert.False(obj.HitTest(new Point(200, 150), transform, tolerance: 5.0));
    }

    [Fact]
    public void Translate_ShiftsBothPoints()
    {
        var p1 = PixelCoordinateTransform.Chart(100, 200);
        var p2 = PixelCoordinateTransform.Chart(300, 100);
        using var obj = new DatePriceRangeObject(p1, p2);

        obj.Translate(TimeSpan.FromDays(2), 50m);

        Assert.Equal(p1.Time.AddDays(2), obj.Points[0].Time);
        Assert.Equal(p1.Price + 50m, obj.Points[0].Price);
        Assert.Equal(p2.Time.AddDays(2), obj.Points[1].Time);
        Assert.Equal(p2.Price + 50m, obj.Points[1].Price);
    }

    [Fact]
    public void Render_SmokeTest_RendersVisibleAndSkipsWhenHidden()
    {
        const int width = 400;
        const int height = 300;
        var transform = new PixelCoordinateTransform();

        using var obj = new DatePriceRangeObject(
            PixelCoordinateTransform.Chart(100, 200),
            PixelCoordinateTransform.Chart(300, 100))
        {
            IsSelected = true
        };

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Black);
        obj.Render(surface.Canvas, transform);
        using var rendered = surface.Snapshot();
        byte[] renderedBytes = rendered.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        // Downward drag + no labels + degenerate single point should also render safely without throwing.
        obj.Points[1] = PixelCoordinateTransform.Chart(300, 260);
        obj.ShowPriceLabel = false;
        obj.ShowBarLabel = false;
        obj.ShowVolumeLabel = false;
        obj.Render(surface.Canvas, transform);

        obj.Points[1] = obj.Points[0];
        obj.Render(surface.Canvas, transform);

        // Hidden object leaves clean canvas unchanged.
        using var hiddenSurface = SKSurface.Create(new SKImageInfo(width, height));
        hiddenSurface.Canvas.Clear(SKColors.Black);
        obj.IsVisible = false;
        obj.Render(hiddenSurface.Canvas, transform);
        using var hiddenSnapshot = hiddenSurface.Snapshot();
        byte[] hiddenBytes = hiddenSnapshot.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        Assert.NotEqual(hiddenBytes, renderedBytes);
    }

    [Fact]
    public void Render_SteadyState_ZeroManagedAllocationWithoutText_AndBoundedWithLabels_AndAfterDisposeIsNoOp()
    {
        var transform = new PixelCoordinateTransform();
        using var obj = new DatePriceRangeObject(
            PixelCoordinateTransform.Chart(100, 200),
            PixelCoordinateTransform.Chart(300, 100));
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var canvas = surface.Canvas;

        // Warmup GetCalculatedValues and Render with labels on.
        _ = obj.GetCalculatedValues(PixelCoordinateTransform.Chart(150, 0).Time);
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        long valuesBytes = AllocationProbe.InCollectionFreeWindow(
            () => _ = obj.GetCalculatedValues(PixelCoordinateTransform.Chart(150, 0).Time));
        Assert.Equal(0L, valuesBytes);

        // SkiaSharp's MeasureText/DrawText(string) converts strings to short-lived UTF-8 buffers,
        // while our label/array/paint/path caching itself adds zero steady-state allocations.
        long labeledRenderBytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));
        Assert.InRange(labeledRenderBytes, 0L, 1024L);

        // Without text labels, Render allocates 0 managed bytes.
        obj.ShowPriceLabel = false;
        obj.ShowBarLabel = false;
        obj.ShowVolumeLabel = false;
        obj.Render(canvas, transform);

        long unlabeledRenderBytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));
        Assert.Equal(0L, unlabeledRenderBytes);

        obj.Dispose();
        obj.Dispose(); // idempotent
        obj.Render(canvas, transform); // safe no-op after Dispose
    }

    [Fact]
    public void Constructor_InheritsThemeDefaults_AndNullRenderInputsAreSafe_AndStrokeStyleDisplayNameIsStandardized()
    {
        var transform = new PixelCoordinateTransform();
        using var obj = new DatePriceRangeObject(
            PixelCoordinateTransform.Chart(100, 200),
            PixelCoordinateTransform.Chart(250, 120));

        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);

        var displayName = typeof(DatePriceRangeObject)
            .GetProperty(nameof(DatePriceRangeObject.StrokeStyle))?
            .GetCustomAttributes(typeof(System.ComponentModel.DisplayNameAttribute), false)
            .OfType<System.ComponentModel.DisplayNameAttribute>()
            .SingleOrDefault()?.DisplayName;
        Assert.Equal("Stroke Style", displayName);

        using var surface = SKSurface.Create(new SKImageInfo(320, 240));
        obj.Render(null!, transform);
        obj.Render(surface.Canvas, null!);
    }

    [Fact]
    public void HitTest_ReturnsFalse_WhenTransformIsNullOrDisposed()
    {
        var transform = new PixelCoordinateTransform();
        var obj = new DatePriceRangeObject(
            PixelCoordinateTransform.Chart(100, 200),
            PixelCoordinateTransform.Chart(250, 120));

        Assert.True(obj.HitTest(new Point(150, 160), transform));
        Assert.False(obj.HitTest(new Point(150, 160), null!));

        obj.Dispose();
        Assert.False(obj.HitTest(new Point(150, 160), transform));
    }
}
