using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Drawing;

public class HorizontalLineObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.HorizontalLine;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public HorizontalLineObject(ChartPoint point)
    {
        Points.Add(point);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count == 0) return;

        var pt = transform.ChartToScreen(Points[0]);
        float y = (float)pt.Y;

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Use CanvasWidth from the transform to represent the actual chart drawing area
        // to prevent the line from extending into the Y-axis margin.
        float width = (float)transform.CanvasWidth;
        canvas.DrawLine(0, y, width, y, paint);

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(pt.X, y), AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0) return false;
        var pt = transform.ChartToScreen(Points[0]);
        return Math.Abs(screenPoint.Y - pt.Y) <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            // Horizontal line only cares about Price translation
            Points[0] = new ChartPoint(Points[0].Time, Points[0].Price + priceDelta);
        }
    }
}
