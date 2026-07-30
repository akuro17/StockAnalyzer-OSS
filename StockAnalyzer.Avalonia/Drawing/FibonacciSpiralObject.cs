using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

public class FibonacciSpiralObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciSpiral;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    // Golden Ratio Spiral Growth Factor
    // per 90 degrees (PI/2), radius grows by Phi (1.618)
    // r = a * e^(b * theta)
    // Phi = e^(b * PI/2) => ln(Phi) = b * PI/2 => b = ln(Phi) / (PI/2)
    private const double Phi = 1.61803398875;
    private readonly double _b;

    public FibonacciSpiralObject(ChartPoint center, ChartPoint startRadius)
    {
        Points.Add(center);
        Points.Add(startRadius);
        _b = Math.Log(Phi) / (Math.PI / 2.0);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]); // Center
        var p2 = transform.ChartToScreen(Points[1]); // Start

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // 1. Calculate Initial Polar Coordinates from Center
        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        double r_init = Math.Sqrt(dx * dx + dy * dy);
        double theta_init = Math.Atan2(dy, dx); 
        
        // 2. Calculate 'a'
        // r = a * e^(b*theta)  =>  a = r / e^(b*theta)
        double a = r_init / Math.Exp(_b * theta_init);

        // 3. Generate Spiral Path
        using var path = new SKPath();
        
        // Start from theta_init - 2PI (inner) to theta_init + 6PI (outer)?
        // Usually we want to see it growing OUT from P2.
        // Let's start exactly at P2 (theta_init) or go inward/outward?
        // Standard tool: Grows outwards from P2 (or P1->P2 is the unit).
        // Let's draw from theta_init outwards for 4-5 turns.
        
        double startTheta = theta_init;
        double endTheta = theta_init + (Math.PI * 2 * 4); // 4 full turns
        
        // Resolution
        double step = 0.1; // radians

        bool isFirst = true;

        for (double t = startTheta; t <= endTheta; t += step)
        {
            double r = a * Math.Exp(_b * t);
            
            double x = p1.X + r * Math.Cos(t);
            double y = p1.Y + r * Math.Sin(t);
            
            if (isFirst)
            {
                path.MoveTo((float)x, (float)y);
                isFirst = false;
            }
            else
            {
                path.LineTo((float)x, (float)y);
            }
            
            // Optimization: Stop if off-screen too far?
            if (r > 5000) break; // Arbitrary limit to prevent overflow
        }

        canvas.DrawPath(path, paint);

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
         
         if (GetDistance(screenPoint, p1) < tolerance * 2) return true;
         if (GetDistance(screenPoint, p2) < tolerance * 2) return true;
         
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
