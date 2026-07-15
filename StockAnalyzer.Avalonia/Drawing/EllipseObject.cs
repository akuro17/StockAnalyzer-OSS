using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class EllipseObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Ellipse;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 2.0;
    public bool IsSelected { get; set; }

    public EllipseObject(ChartPoint p1, ChartPoint p2)
    {
        Points.Add(p1);
        Points.Add(p2);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Calculate Bounding Box
        var left = Math.Min(p1.X, p2.X);
        var top = Math.Min(p1.Y, p2.Y);
        var right = Math.Max(p1.X, p2.X);
        var bottom = Math.Max(p1.Y, p2.Y);

        var rect = new SKRect((float)left, (float)top, (float)right, (float)bottom);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        
        // Fill functionality could be added later, currently spec says "Ellipse Drawing"
        // Usually shapes might have fill. For now, stroke only as per spec "Draw (Canvas/SVG)".
        
        canvas.DrawOval(rect, paint);

        if (IsSelected)
        {
            using var handlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 4, handlePaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 4, handlePaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Ellipse Equation: (x-h)^2/a^2 + (y-k)^2/b^2 = 1
        // Center (h, k)
        double h = (p1.X + p2.X) / 2.0;
        double k = (p1.Y + p2.Y) / 2.0;
        double a = Math.Abs(p1.X - p2.X) / 2.0;
        double b = Math.Abs(p1.Y - p2.Y) / 2.0;

        if (a <= 0 || b <= 0) return false; // Degenerate

        // Check distance to ellipse boundary.
        // Simplification: Check if point satisfies equation roughly = 1 +/- tolerance.
        
        double normalizedX = (screenPoint.X - h) / a;
        double normalizedY = (screenPoint.Y - k) / b;
        
        double distSq = normalizedX * normalizedX + normalizedY * normalizedY;
        
        // Simple fallback: If it's "reasonably close" to 1.
        // First, a quick bounding box check with tolerance.
        double left = Math.Min(p1.X, p2.X) - tolerance;
        double top = Math.Min(p1.Y, p2.Y) - tolerance;
        double right = Math.Max(p1.X, p2.X) + tolerance;
        double bottom = Math.Max(p1.Y, p2.Y) + tolerance;
        
        if (screenPoint.X < left || screenPoint.X > right || screenPoint.Y < top || screenPoint.Y > bottom)
            return false;

        // Convert unit diff back to pixels roughly
        double minR = Math.Min(a, b);
        if (minR < 1) minR = 1; // Avoid division by zero or very small numbers
        
        double dist = Math.Sqrt(distSq);
        double diff = Math.Abs(dist - 1.0);
        
        double pixelDiff = diff * minR; 
        
        // Use a slightly larger tolerance for hit testing to make it easier to select.
        return pixelDiff < tolerance * 2;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
