using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class ParallelChannelObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.ParallelChannel;
    
    // P1, P2 Define the main trend line. P3 defines the parallel line position.
    public List<ChartPoint> Points { get; private set; }

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;
    public bool IsFilled { get; set; } = true;

    public ParallelChannelObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
    {
        Points = new List<ChartPoint> { p1, p2, p3 };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 3) return;

        var s1 = transform.ChartToScreen(Points[0]);
        var s2 = transform.ChartToScreen(Points[1]);
        var s3 = transform.ChartToScreen(Points[2]);

        // Calculate generic polygon
        // Line 1: P1 -> P2
        // Line 2: Parallel to Line 1, passing through P3
        // Vertices: s1, s2, s2', s1'
        // Where s1', s2' are on Line 2 with same X as s1, s2 (Visual Vertical alignment)

        // Slope m = dy / dx
        float dx = (float)(s2.X - s1.X);
        float dy = (float)(s2.Y - s1.Y);

        // Avoid divide by zero (vertical line)
        bool isVertical = Math.Abs(dx) < 0.001;
        
        global::Avalonia.Point s2_prime, s1_prime;

        if (isVertical)
        {
            // If vertical, parallel line is just shifted by (s3.X - s1.X)
            float xShift = (float)(s3.X - s1.X);
            s2_prime = new global::Avalonia.Point(s2.X + xShift, s2.Y);
            s1_prime = new global::Avalonia.Point(s1.X + xShift, s1.Y); 
        }
        else
        {
            float m = dy / dx;
            // Line 2 equation: y - y3 = m(x - x3) => y = m(x - x3) + y3
            
            // Calc s2_prime (x = s2.X)
            float y2_prime = m * (float)(s2.X - s3.X) + (float)s3.Y;
            s2_prime = new global::Avalonia.Point(s2.X, y2_prime);

            // Calc s1_prime (x = s1.X)
            float y1_prime = m * (float)(s1.X - s3.X) + (float)s3.Y;
            s1_prime = new global::Avalonia.Point(s1.X, y1_prime);
        }

        // Draw Filled Path
        using var path = new SKPath();
        path.MoveTo((float)s1.X, (float)s1.Y);
        path.LineTo((float)s2.X, (float)s2.Y);
        path.LineTo((float)s2_prime.X, (float)s2_prime.Y);
        path.LineTo((float)s1_prime.X, (float)s1_prime.Y);
        path.Close();

        if (IsFilled)
        {
            using var fillPaint = new SKPaint
            {
                Color = new SKColor(SkiaColor.Red, SkiaColor.Green, SkiaColor.Blue, 30),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawPath(path, fillPaint);
        }

        using var strokePaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        canvas.DrawPath(path, strokePaint);

        // Center dashed line (optional, user req "Center line"?)
        // Prompt 35-2 says "Band and Center line".
        // Parallel Channel usually has a center line.
        // Midpoints: (s1+s1')/2 ...
        var m1 = new global::Avalonia.Point((s1.X + s1_prime.X) / 2, (s1.Y + s1_prime.Y) / 2);
        var m2 = new global::Avalonia.Point((s2.X + s2_prime.X) / 2, (s2.Y + s2_prime.Y) / 2);
        
        using var centerPaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };
        canvas.DrawLine((float)m1.X, (float)m1.Y, (float)m2.X, (float)m2.Y, centerPaint);

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, s1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, s2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, s3, AnchorPointIndex == 2 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        // Simple bounding box or Polygon containment
        // Polygon test
        if (Points.Count < 3) return false;
        
        // Re-calculate polygon points (logic duplication, could refactor)
        var s1 = transform.ChartToScreen(Points[0]);
        var s2 = transform.ChartToScreen(Points[1]);
        var s3 = transform.ChartToScreen(Points[2]);

        // ... Same logic ...
        float dx = (float)(s2.X - s1.X);
        float dy = (float)(s2.Y - s1.Y);
        
        global::Avalonia.Point s2_prime, s1_prime;
        // Assume non-vertical for HitTest ease or copy logic
        if (Math.Abs(dx) < 0.001) {
             float xShift = (float)(s3.X - s1.X);
             s2_prime = new global::Avalonia.Point(s2.X + xShift, s2.Y);
             s1_prime = new global::Avalonia.Point(s1.X + xShift, s1.Y);
        } else {
             float m = dy / dx;
             float y2_prime = m * (float)(s2.X - s3.X) + (float)s3.Y;
             s2_prime = new global::Avalonia.Point(s2.X, y2_prime);
             float y1_prime = m * (float)(s1.X - s3.X) + (float)s3.Y;
             s1_prime = new global::Avalonia.Point(s1.X, y1_prime);
        }

        // Check if inside Polygon (s1, s2, s2_prime, s1_prime)
        // Ray casting algorithm or SKPath.Contains
        using var path = new SKPath();
        path.MoveTo((float)s1.X, (float)s1.Y);
        path.LineTo((float)s2.X, (float)s2.Y);
        path.LineTo((float)s2_prime.X, (float)s2_prime.Y);
        path.LineTo((float)s1_prime.X, (float)s1_prime.Y);
        path.Close();

        return path.Contains((float)screenPoint.X, (float)screenPoint.Y) || 
               // Or near lines
               IsNearLine(s1, s2, screenPoint, tolerance) ||
               IsNearLine(s2, s2_prime, screenPoint, tolerance) ||
               IsNearLine(s2_prime, s1_prime, screenPoint, tolerance) ||
               IsNearLine(s1_prime, s1, screenPoint, tolerance);
    }

    private bool IsNearLine(global::Avalonia.Point p1, global::Avalonia.Point p2, global::Avalonia.Point test, double tolerance)
    {
        // Distance from point to line segment
        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        if (dx == 0 && dy == 0) return false;

        double t = ((test.X - p1.X) * dx + (test.Y - p1.Y) * dy) / (dx * dx + dy * dy);
        
        if (t < 0) return PointDistance(p1, test) < tolerance;
        if (t > 1) return PointDistance(p2, test) < tolerance;
        global::Avalonia.Point projection = new global::Avalonia.Point(p1.X + t * dx, p1.Y + t * dy);
        return PointDistance(projection, test) < tolerance;
    }

    private double PointDistance(global::Avalonia.Point a, global::Avalonia.Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}

