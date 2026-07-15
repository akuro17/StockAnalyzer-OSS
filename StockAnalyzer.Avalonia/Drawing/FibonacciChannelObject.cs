using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

public class FibonacciChannelObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciChannel;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    private readonly float[] _levels = { 0.0f, 0.618f, 1.0f, 1.618f, 2.618f };

    public FibonacciChannelObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
    {
        Points.Add(p1);
        Points.Add(p2);
        Points.Add(p3);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 3) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        var p3 = transform.ChartToScreen(Points[2]);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // 1. Calculate Trend Line Slope (m) and Intercept (b) in Screen Coordinates
        // y = mx + b
        
        float dx = (float)(p2.X - p1.X);
        float dy = (float)(p2.Y - p1.Y);
        
        if (Math.Abs(dx) < 0.001) dx = 0.001f; // Avoid division by zero
        float m = dy / dx;
        float b = (float)p1.Y - (m * (float)p1.X);

        // 2. Calculate Channel Width (vertical offset or perpendicular?)
        // Spec says "Vertical offset is easiest".
        // Let's use Vertical Offset (Parallel lines in Y direction).
        // Line through P3: y = mx + b3.
        // b3 = P3.Y - m * P3.X
        // Width W = b3 - b. (Vertical distance constant)
        
        float b3 = (float)p3.Y - (m * (float)p3.X);
        float w = b3 - b;

        // Draw Lines
        float xStart = 0;
        float xEnd = 10000; // Canvas width?
        // Let's try to match usage. Usually extends infinite right?
        xStart = (float)Math.Min(p1.X, p2.X);
        if (p1.X > xStart) xStart = -1000; // extend left too? Usually channels do.
        
        // Let's extend full screen width roughly
        // We really need Canvas Bounds. Assuming xStart=0, xEnd=CanvasWidth for now is tricky without Bounds passed.
        // We will just draw "long enough" lines based on points range x10.
        
        float minX = (float)Math.Min(p1.X, Math.Min(p2.X, p3.X));
        float maxX = (float)Math.Max(p1.X, Math.Max(p2.X, p3.X));
        float range = maxX - minX;
        if (range < 100) range = 100;
        
        xStart = minX - range * 2;
        xEnd = maxX + range * 5;

        foreach (var level in _levels)
        {
            // y = mx + b + (w * level)
            // b_level = b + (w * level)
            float b_level = b + (w * level);
            
            float yStart = (m * xStart) + b_level;
            float yEnd = (m * xEnd) + b_level;

            // Optional: Clip to reasonable Y values? skia handles it.
            
            canvas.DrawLine(xStart, yStart, xEnd, yEnd, paint);
        }

        if (IsSelected)
        {
            using var handlePaint = new SKPaint { Color = SKColors.BlueViolet, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 4, handlePaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 4, handlePaint);
            canvas.DrawCircle((float)p3.X, (float)p3.Y, 4, handlePaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
         if (Points.Count < 3) return false;
         
         var p1 = transform.ChartToScreen(Points[0]);
         var p2 = transform.ChartToScreen(Points[1]);
         var p3 = transform.ChartToScreen(Points[2]);
         
         if (GetDistance(screenPoint, p1) < tolerance * 2) return true;
         if (GetDistance(screenPoint, p2) < tolerance * 2) return true;
         if (GetDistance(screenPoint, p3) < tolerance * 2) return true;

         // Hit test closest line?
         return false;
    }
    
    private double GetDistance(global::Avalonia.Point a, global::Avalonia.Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
