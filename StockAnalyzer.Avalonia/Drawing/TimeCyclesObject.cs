using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Time Cycles Tool (Cycle Brackets)
/// Draws semi-circular brackets connecting time intervals.
/// </summary>
public class TimeCyclesObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.TimeCycles;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    public TimeCyclesObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        float startX = (float)p1.X;
        float baseY = (float)p1.Y; // Use P1 Y as the base line for brackets?
        float intervalX = Math.Abs((float)(p2.X - p1.X));
        if (intervalX < 5f) return;

        var bounds = canvas.LocalClipBounds;

        // Draw Forward
        float currentX = startX;
        int count = 0;
        
        // Draw brackets. A bracket connects currentX to currentX + intervalX.
        // It's a semi-ellipse? Or semi-circle (if aspect ratio allows).
        // Standard Cycle Bracket is a semi-circle visually.
        // Bounds: Rect(currentX, baseY - radius, intervalX, radius*2) ? 
        // Arc start 180 sweep 180?
        
        while (count < 100)
        {
            if (currentX > bounds.Right) break;
            
            float nextX = currentX + intervalX;
            
            if (nextX >= bounds.Left)
            {
                // Draw arc
                var rect = new SKRect(currentX, baseY - intervalX / 2, nextX, baseY + intervalX / 2);
                // We want to draw the TOP half or BOTTOM half?
                // Usually brackets go UP if Price is above? Or Down?
                // Let's assume standard is UP (Arch).
                // Screen Y is down. So Top half is negative Y relative to center.
                // Draw Arc: StartAngle 180, Sweep 180.
                
                using var path = new SKPath();
                path.AddArc(rect, 180, 180);
                canvas.DrawPath(path, paint);
            }
            
            currentX = nextX;
            count++;
        }
        
        // Draw Backward
        currentX = startX;
        count = 0;
        while (count < 100)
        {
            if (currentX < bounds.Left) break;
            float prevX = currentX - intervalX;
            
             if (currentX >= bounds.Left || prevX <= bounds.Right) // Visible
            {
                 var rect = new SKRect(prevX, baseY - intervalX / 2, currentX, baseY + intervalX / 2);
                 using var path = new SKPath();
                 path.AddArc(rect, 180, 180);
                 canvas.DrawPath(path, paint);
            }
            
            currentX = prevX;
            count++;
        }
        
        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 5, selPaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 5, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        float startX = (float)p1.X;
        float baseY = (float)p1.Y;
        float intervalX = Math.Abs((float)(p2.X - p1.X));
        
        if (intervalX < 1f) return false;

        // Check which Cycle we are in
        // cycle_index = floor((x - startX) / interval)
        
        double k = Math.Floor((screenPoint.X - startX) / intervalX);
        float cycleStartX = startX + (float)k * intervalX;
        float cycleEndX = cycleStartX + intervalX;
        float centerX = (cycleStartX + cycleEndX) / 2f;
        float radius = intervalX / 2f;
        
        // Check distance to arc
        // Arc center is (centerX, baseY).
        // Distance from center
        double dx = screenPoint.X - centerX;
        double dy = screenPoint.Y - baseY;
        double dist = Math.Sqrt(dx*dx + dy*dy);
        
        // Check if dist is close to radius
        if (Math.Abs(dist - radius) <= tolerance)
        {
            // Also need to check if Y is correct (Upper half).
            // Screen Y: Upper half is Y < baseY.
            if (screenPoint.Y <= baseY + tolerance) // Allow tolerance near base
                return true;
        }
        
        return false;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
