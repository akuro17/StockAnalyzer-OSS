using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class AngleObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.AngleTool;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    public AngleObject(ChartPoint p1, ChartPoint p2)
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

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Draw Line
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint);

        // Draw Horizontal Reference (Optional, but helpful for "Angle from Horizontal")
        // dashed line
        using var dashPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(128),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p1.X + ChartConstants.AngleHorizontalRefLength, (float)p1.Y, dashPaint);


        // Calculate Angle
        // Screen Coordinates: Y increases downwards.
        // Math standard: Y increases upwards.
        // We want visual angle.
        // DeltaY = -(p2.Y - p1.Y) [Standard Cartesian]
        // DeltaX = p2.X - p1.X
        
        double deltaX = p2.X - p1.X;
        double deltaY = -(p2.Y - p1.Y);
        double angleRad = Math.Atan2(deltaY, deltaX);
        double angleDeg = angleRad * (180.0 / Math.PI);

        // Draw Angle Text
        string text = $"{angleDeg:F1}°";
        using var textPaint = new SKPaint
        {
            Color = SkiaColor,
            TextSize = 14,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        // Text Position: Slightly offset from P1 or Midpoint?
        // Let's put it near P1 but offset
        float textX = (float)p1.X + ChartConstants.AngleTextOffset;
        float textY = (float)p1.Y - ChartConstants.AngleTextOffset;

        canvas.DrawText(text, textX, textY, textPaint);

        // Draw Arc
        // Radius for arc
        float radius = ChartConstants.AngleArcRadius;
        using var arcPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        var rect = new SKRect((float)p1.X - radius, (float)p1.Y - radius, (float)p1.X + radius, (float)p1.Y + radius);
        // Sweep angle is simply -angleDeg because Skia angles are clockwise (positive Y down)
        // Wait, Skia DrawArc:
        // startAngle: 0 is right. Positive is clockwise.
        // Our calculated angleDeg is standard (CCW positive).
        // So Skia Angle = -angleDeg.
        canvas.DrawArc(rect, 0, (float)(-angleDeg), false, arcPaint);

        if (IsSelected)
        {
            using var handlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, ChartConstants.DefaultHandleRadius, handlePaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, ChartConstants.DefaultHandleRadius, handlePaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        return DistancePointToSegment(screenPoint, p1, p2) <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    private double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = Math.Pow(v.X - w.X, 2) + Math.Pow(v.Y - w.Y, 2);
        if (l2 == 0) return Math.Sqrt(Math.Pow(p.X - v.X, 2) + Math.Pow(p.Y - v.Y, 2));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        global::Avalonia.Point projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt(Math.Pow(p.X - projection.X, 2) + Math.Pow(p.Y - projection.Y, 2));
    }
}
