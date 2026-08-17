using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class GradientAndBlendingTests
{
    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 600.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(600.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 600.0 - (double)price;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    [Theory]
    [InlineData(DrawingBlendMode.Normal, SKBlendMode.SrcOver)]
    [InlineData(DrawingBlendMode.Multiply, SKBlendMode.Multiply)]
    [InlineData(DrawingBlendMode.Screen, SKBlendMode.Screen)]
    [InlineData(DrawingBlendMode.Overlay, SKBlendMode.Overlay)]
    [InlineData(DrawingBlendMode.Darken, SKBlendMode.Darken)]
    [InlineData(DrawingBlendMode.Lighten, SKBlendMode.Lighten)]
    [InlineData(DrawingBlendMode.ColorDodge, SKBlendMode.ColorDodge)]
    [InlineData(DrawingBlendMode.ColorBurn, SKBlendMode.ColorBurn)]
    [InlineData(DrawingBlendMode.SoftLight, SKBlendMode.SoftLight)]
    [InlineData(DrawingBlendMode.HardLight, SKBlendMode.HardLight)]
    [InlineData(DrawingBlendMode.Difference, SKBlendMode.Difference)]
    [InlineData(DrawingBlendMode.Exclusion, SKBlendMode.Exclusion)]
    [InlineData((DrawingBlendMode)255, SKBlendMode.SrcOver)]
    public void DrawingBlendModeExtensions_MapsToExpectedSkBlendMode(DrawingBlendMode mode, SKBlendMode expected)
    {
        Assert.Equal(expected, mode.ToSkBlendMode());
    }

    [Theory]
    [InlineData(DrawingGradientType.None)]
    [InlineData(DrawingGradientType.LinearVertical)]
    [InlineData(DrawingGradientType.LinearHorizontal)]
    [InlineData(DrawingGradientType.LinearDiagonal)]
    [InlineData(DrawingGradientType.Radial)]
    public void RectangleObject_Render_SupportsAllGradientTypes(DrawingGradientType gradientType)
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(100, 100));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        var rect = new RectangleObject(p1, p2)
        {
            IsFilled = true,
            GradientType = gradientType,
            Color = Colors.Blue,
            GradientEndColor = Colors.Red,
            FillAlpha = 60,
            GradientEndAlpha = 180,
            BlendMode = DrawingBlendMode.Multiply
        };

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        var exception = Record.Exception(() => rect.Render(canvas, transform));
        Assert.Null(exception);
    }

    [Fact]
    public void RectangleObject_Render_SupportsAllBlendModes()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(100, 100));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        foreach (DrawingBlendMode mode in Enum.GetValues(typeof(DrawingBlendMode)))
        {
            var rect = new RectangleObject(p1, p2)
            {
                IsFilled = true,
                BlendMode = mode,
                GradientType = DrawingGradientType.LinearDiagonal,
                Color = Colors.Green
            };

            var exception = Record.Exception(() => rect.Render(canvas, transform));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void RectangleObject_Render_DegenerateAndFiniteGuards()
    {
        var transform = new DummyCoordinateTransform();
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        // Same point (W=0, H=0)
        var pZero = transform.ScreenToChart(new Point(150, 150));
        var rectZero = new RectangleObject(pZero, pZero)
        {
            IsFilled = true,
            GradientType = DrawingGradientType.Radial
        };
        var ex1 = Record.Exception(() => rectZero.Render(canvas, transform));
        Assert.Null(ex1);

        // Vertical line (W=0, H>0)
        var pV1 = transform.ScreenToChart(new Point(150, 100));
        var pV2 = transform.ScreenToChart(new Point(150, 300));
        var rectV = new RectangleObject(pV1, pV2) { IsFilled = true, GradientType = DrawingGradientType.LinearVertical };
        var ex2 = Record.Exception(() => rectV.Render(canvas, transform));
        Assert.Null(ex2);

        // Horizontal line (W>0, H=0)
        var pH1 = transform.ScreenToChart(new Point(100, 150));
        var pH2 = transform.ScreenToChart(new Point(300, 150));
        var rectH = new RectangleObject(pH1, pH2) { IsFilled = true, GradientType = DrawingGradientType.LinearHorizontal };
        var ex3 = Record.Exception(() => rectH.Render(canvas, transform));
        Assert.Null(ex3);
    }

    [Fact]
    public void RectangleObject_HitTest_DualMode()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(100, 100));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        var rectFilled = new RectangleObject(p1, p2) { IsFilled = true };
        var rectOutline = new RectangleObject(p1, p2) { IsFilled = false };

        var insideCenter = new Point(200, 150);
        var onTopBorder = new Point(200, 100);
        var farOutside = new Point(500, 500);

        // Filled: inside is hit
        Assert.True(rectFilled.HitTest(insideCenter, transform));
        Assert.True(rectFilled.HitTest(onTopBorder, transform));
        Assert.False(rectFilled.HitTest(farOutside, transform));

        // Outline: inside center is NOT hit, border IS hit
        Assert.False(rectOutline.HitTest(insideCenter, transform));
        Assert.True(rectOutline.HitTest(onTopBorder, transform));
        Assert.False(rectOutline.HitTest(farOutside, transform));
    }

    [Theory]
    [InlineData(DrawingGradientType.None)]
    [InlineData(DrawingGradientType.LinearVertical)]
    [InlineData(DrawingGradientType.LinearHorizontal)]
    [InlineData(DrawingGradientType.LinearDiagonal)]
    [InlineData(DrawingGradientType.Radial)]
    public void EllipseObject_Render_SupportsAllGradientTypes(DrawingGradientType gradientType)
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(100, 100));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        var ellipse = new EllipseObject(p1, p2)
        {
            IsFilled = true,
            GradientType = gradientType,
            Color = Colors.Purple,
            GradientEndColor = Colors.Cyan,
            FillAlpha = 40,
            GradientEndAlpha = 200,
            BlendMode = DrawingBlendMode.Screen
        };

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        var exception = Record.Exception(() => ellipse.Render(canvas, transform));
        Assert.Null(exception);
    }

    [Fact]
    public void EllipseObject_Render_SupportsAllBlendModes()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(100, 100));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        foreach (DrawingBlendMode mode in Enum.GetValues(typeof(DrawingBlendMode)))
        {
            var ellipse = new EllipseObject(p1, p2)
            {
                IsFilled = true,
                BlendMode = mode,
                GradientType = DrawingGradientType.Radial,
                Color = Colors.Teal
            };

            var exception = Record.Exception(() => ellipse.Render(canvas, transform));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void EllipseObject_Render_DegenerateAndFiniteGuards()
    {
        var transform = new DummyCoordinateTransform();
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        // Same point (a=0, b=0)
        var pZero = transform.ScreenToChart(new Point(200, 200));
        var ellipseZero = new EllipseObject(pZero, pZero) { IsFilled = true };
        var ex = Record.Exception(() => ellipseZero.Render(canvas, transform));
        Assert.Null(ex);
    }

    [Fact]
    public void EllipseObject_HitTest_DualMode()
    {
        var transform = new DummyCoordinateTransform();
        // Ellipse bounded by (100, 100) and (300, 200) => Center (200, 150), a = 100, b = 50
        var p1 = transform.ScreenToChart(new Point(100, 100));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        var ellipseFilled = new EllipseObject(p1, p2) { IsFilled = true };
        var ellipseOutline = new EllipseObject(p1, p2) { IsFilled = false };

        var center = new Point(200, 150);
        var corner = new Point(105, 105); // Inside bounding box corner, but outside ellipse
        var onRightVertex = new Point(300, 150); // (h + a, k)
        var farOutside = new Point(500, 500);

        // Filled: center is hit, corner outside ellipse is NOT hit, boundary vertex is hit
        Assert.True(ellipseFilled.HitTest(center, transform));
        Assert.False(ellipseFilled.HitTest(corner, transform));
        Assert.True(ellipseFilled.HitTest(onRightVertex, transform));
        Assert.False(ellipseFilled.HitTest(farOutside, transform));

        // Outline: center is NOT hit, boundary vertex IS hit
        Assert.False(ellipseOutline.HitTest(center, transform));
        Assert.True(ellipseOutline.HitTest(onRightVertex, transform));
        Assert.False(ellipseOutline.HitTest(farOutside, transform));
    }

    [Fact]
    public void Persistence_RectangleObject_RoundTripsAllProperties()
    {
        var original = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 10), 150m))
        {
            Color = Colors.DodgerBlue,
            Thickness = 3.0,
            IsFilled = true,
            BlendMode = DrawingBlendMode.Overlay,
            GradientType = DrawingGradientType.LinearDiagonal,
            GradientEndColor = Colors.Coral,
            FillAlpha = 55,
            GradientEndAlpha = 210,
            IsSelected = true
        };

        var json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<IChartObject>(json, JsonOptions);

        var restoredRect = Assert.IsType<RectangleObject>(restored);
        Assert.Equal(ChartObjectType.Rectangle, restoredRect.Type);
        Assert.Equal(2, restoredRect.Points.Count);
        Assert.Equal(original.Points[0].Time, restoredRect.Points[0].Time);
        Assert.Equal(original.Points[0].Price, restoredRect.Points[0].Price);
        Assert.Equal(original.Points[1].Time, restoredRect.Points[1].Time);
        Assert.Equal(original.Points[1].Price, restoredRect.Points[1].Price);

        Assert.Equal(Colors.DodgerBlue, restoredRect.Color);
        Assert.Equal(3.0, restoredRect.Thickness);
        Assert.True(restoredRect.IsFilled);
        Assert.Equal(DrawingBlendMode.Overlay, restoredRect.BlendMode);
        Assert.Equal(DrawingGradientType.LinearDiagonal, restoredRect.GradientType);
        Assert.Equal(Colors.Coral, restoredRect.GradientEndColor);
        Assert.Equal(55, restoredRect.FillAlpha);
        Assert.Equal(210, restoredRect.GradientEndAlpha);
        Assert.True(restoredRect.IsSelected);
    }

    [Fact]
    public void Persistence_EllipseObject_RoundTripsAllProperties()
    {
        var original = new EllipseObject(
            new ChartPoint(new DateTime(2025, 2, 1), 200m),
            new ChartPoint(new DateTime(2025, 2, 15), 250m))
        {
            Color = Colors.MediumPurple,
            Thickness = 2.5,
            IsFilled = true,
            BlendMode = DrawingBlendMode.Multiply,
            GradientType = DrawingGradientType.Radial,
            GradientEndColor = Colors.Gold,
            FillAlpha = 70,
            GradientEndAlpha = 190,
            IsSelected = false
        };

        var json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<IChartObject>(json, JsonOptions);

        var restoredEllipse = Assert.IsType<EllipseObject>(restored);
        Assert.Equal(ChartObjectType.Ellipse, restoredEllipse.Type);
        Assert.Equal(2, restoredEllipse.Points.Count);
        Assert.Equal(original.Points[0].Time, restoredEllipse.Points[0].Time);
        Assert.Equal(original.Points[0].Price, restoredEllipse.Points[0].Price);
        Assert.Equal(original.Points[1].Time, restoredEllipse.Points[1].Time);
        Assert.Equal(original.Points[1].Price, restoredEllipse.Points[1].Price);

        Assert.Equal(Colors.MediumPurple, restoredEllipse.Color);
        Assert.Equal(2.5, restoredEllipse.Thickness);
        Assert.True(restoredEllipse.IsFilled);
        Assert.Equal(DrawingBlendMode.Multiply, restoredEllipse.BlendMode);
        Assert.Equal(DrawingGradientType.Radial, restoredEllipse.GradientType);
        Assert.Equal(Colors.Gold, restoredEllipse.GradientEndColor);
        Assert.Equal(70, restoredEllipse.FillAlpha);
        Assert.Equal(190, restoredEllipse.GradientEndAlpha);
        Assert.False(restoredEllipse.IsSelected);
    }
}
