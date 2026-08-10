using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class RayObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Ray;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    public RayObject(ChartPoint p1, ChartPoint p2)
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

        // Calculate extension to screen edge
        // Vector v = P2 - P1
        var vX = p2.X - p1.X;
        var vY = p2.Y - p1.Y;

        // Normalize? Not needed if we find intersection with bounds.
        // Bounds: canvas.LocalClipBounds
        var bounds = canvas.LocalClipBounds;

        // Intersect ray P1->v with bounds
        // Simple logic: extend very far
        float extension = Math.Max(bounds.Width, bounds.Height) * 2;
        
        // Normalize vector for consistent extension
        double len = Math.Sqrt(vX * vX + vY * vY);
        if (len == 0) return; // Same point

        double exX = p1.X + (vX / len) * extension;
        double exY = p1.Y + (vY / len) * extension;

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Draw Ray
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)exX, (float)exY, paint);

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

        // Hit test on the infinitely extended line?
        // Or just the visible segment? 
        // User expects "Ray" meaning clicking anywhere on the extended line works.
        
        // Vector P1->P2
        double vX = p2.X - p1.X;
        double vY = p2.Y - p1.Y;
        double vLen = Math.Sqrt(vX * vX + vY * vY);
        
        if (vLen == 0) return false;

        // Vector P1->screenPoint
        double wX = screenPoint.X - p1.X;
        double wY = screenPoint.Y - p1.Y;
        
        // Project w onto v
        // dot product
        double dot = wX * vX + wY * vY;
        
        // If dot < 0, point is "behind" P1.
        if (dot < 0) return false;
        
        // Distance to line
        // |Det(v, w)| / |v|
        // Det(v, w) = v.x * w.y - v.y * w.x
        double det = vX * wY - vY * wX;
        double dist = Math.Abs(det) / vLen;
        
        return dist <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
