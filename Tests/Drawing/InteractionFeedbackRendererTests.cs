using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class InteractionFeedbackRendererTests
{
    /// <summary>Spy IChartObject that counts how often it is asked to render.</summary>
    private sealed class SpyChartObject : IChartObject
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ChartObjectType Type { get; set; } = ChartObjectType.TrendLine;
        public string? CustomName { get; set; }
        public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
        public bool IsMoveAxisModeExplicit { get; set; }
        public List<ChartPoint> Points { get; set; } = new();
        public Color Color { get; set; } = Colors.Black;
        public double Thickness { get; set; } = 1.0;
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; }
        public int ZIndex { get; set; }
        public int PanelIndex { get; set; } = -1;
        public SKColor SkiaColor => SKColors.Black;

        public int RenderCount { get; private set; }

        public void Render(SKCanvas canvas, ICoordinateTransform transform) => RenderCount++;
        public bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance) => false;
        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }
    }

    [Theory]
    [InlineData(-1, -1, 1)] // main-chart shape, main pass  -> drawn
    [InlineData(-1, 0, 0)]  // main-chart shape, panel-0 pass -> skipped
    [InlineData(2, 2, 1)]   // panel-2 shape, panel-2 pass   -> drawn
    [InlineData(2, -1, 0)]  // panel-2 shape, main pass      -> skipped (no leak onto the main chart)
    [InlineData(2, 1, 0)]   // panel-2 shape, panel-1 pass   -> skipped
    public void Render_DrawsPreview_OnlyForThePanelThePassOwns(int objectPanelIndex, int passPanelIndex, int expectedRenders)
    {
        var renderer = new InteractionFeedbackRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(64, 64));
        var spy = new SpyChartObject { PanelIndex = objectPanelIndex };

        renderer.Render(surface.Canvas, null, spy, new DummyCoordinateTransform(), passPanelIndex);

        Assert.Equal(expectedRenders, spy.RenderCount);
    }

    [Fact]
    public void Render_DefaultPanelIndex_TargetsMainChart()
    {
        var renderer = new InteractionFeedbackRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(64, 64));
        var mainShape = new SpyChartObject { PanelIndex = -1 };
        var panelShape = new SpyChartObject { PanelIndex = 0 };

        renderer.Render(surface.Canvas, null, mainShape, new DummyCoordinateTransform());
        renderer.Render(surface.Canvas, null, panelShape, new DummyCoordinateTransform());

        Assert.Equal(1, mainShape.RenderCount);
        Assert.Equal(0, panelShape.RenderCount);
    }

    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 1000;
        public double CanvasHeight => 800;
        public Rect ScreenRect => new Rect(0, 0, 1000, 800);
        public double ViewportX => 0;
        public double ViewportWidth => 1000;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint) => new Point(100, 100);
        public ChartPoint ScreenToChart(Point screenPoint) => new ChartPoint(DateTime.Now, 100m);
        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 100.0;
    }

    [Fact]
    public void Render_WithDrawingPreviewObject_DrawsWithoutErrors()
    {
        var renderer = new InteractionFeedbackRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800));
        var canvas = surface.Canvas;

        var previewObj = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 10), 200m));

        var transform = new DummyCoordinateTransform();

        // Act - should execute safely without exceptions
        renderer.Render(canvas, null, previewObj, transform);

        // Verify surface remains valid
        Assert.NotNull(surface);
    }

    [Fact]
    public void Render_WithNullPreviewObject_ExecutesSafely()
    {
        var renderer = new InteractionFeedbackRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800));
        var canvas = surface.Canvas;
        var transform = new DummyCoordinateTransform();

        // Act with null
        renderer.Render(canvas, null, null, transform);

        Assert.NotNull(surface);
    }
}
