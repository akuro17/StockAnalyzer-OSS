using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

public class FibonacciExpansionObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciExpansion;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    private readonly float[] _levels = { 0.618f, 1.0f, 1.618f, 2.618f, 4.236f };

    public FibonacciExpansionObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
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

        using var dashPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(128),
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };

        // Draw connecting lines: P1->P2, P2->P3
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, dashPaint);
        canvas.DrawLine((float)p2.X, (float)p2.Y, (float)p3.X, (float)p3.Y, dashPaint);

        // Calculate Height based on Price
        // P1 to P2 price diff
        float priceDiff = (float)Math.Abs(p2.Y - p1.Y); 
        // Note: Y is screen coordinates, so smaller Y is higher price usually.
        // It's safer to use Price directly if possible, but for screen rendering we need pixels.
        // "Height" in spec is |P2.Price - P1.Price|.
        // Let's rely on Screen Y difference because Transform handles Price->Y.
        
        bool isUpTrend = p2.Y < p1.Y; // Screen Y: Up is smaller. So P2 above P1 means P2.Y < P1.Y
        // Spec says: "Direction is standard usually positive".
        // Actually Expansion usually adds to P3.
        // Target = P3 + (H * Ratio)
        // If we project upwards, we subtract from P3.Y (since Y is inverted).
        // If we project downwards, we add to P3.Y.
        // Usually determination is if P2 > P1 (Price), then we project Up?
        // Let's check Price directly to be sure of direction.
        
        // However, we only have screen points here easily. 
        // Let's assume Screen Y Logic:
        // H (pixels) = Abs(P2.Y - P1.Y)
        // If P2.Y < P1.Y (Price P2 > P1, Uptrend), we project UP from P3? 
        // Standard FE:
        // A-B-C. A(Low), B(High), C(Low). we project Upwards from C.
        // Screen: A.Y > B.Y. H = A.Y - B.Y. TargetY = C.Y - (H * Ratio).
        
        // A(High), B(Low), C(High). we project Downwards from C.
        // Screen: A.Y < B.Y. H = B.Y - A.Y. TargetY = C.Y + (H * Ratio).
        
        float h = (float)(p1.Y - p2.Y); // Positive if P2 is higher price (lower Y)
        
        using var textPaint = new SKPaint
        {
            Color = SkiaColor,
            TextSize = 10,
            IsAntialias = true
        };

        // Screen width for Infinite Line
        float xEnd = 10000; // Large number or Canvas.LocalClipBounds.Right

        foreach (var level in _levels)
        {
            // Target Y
            // Calculation: P3.Price + ( (P2.Price - P1.Price) * ratio )
            // In Screen Y:
            // Y is linear to Price (usually).
            // dy = p1.y - p2.y (Magnitude of P2-P1 move in pixels, signed)
            // If P2 is Higher Price (Lower Y) than P1, dy is +ve.
            // We want target to be Higher Price than P3 -> Lower Y than P3.
            // TargetY = p3.y - (dy * ratio).
            // wait. 
            // P1(100), P2(200). dy = -100 (in Y? no Y is inverted). 
            // P1.Y=500, P2.Y=100. dy = 400.
            // P3(150), P3.Y=300.
            // Target Price = 150 + (100 * 1.0) = 250.
            // Target Y should be corresponding to 250.
            // If scale is constant: TargetY = 300 - (400 * 1.0) = -100. Correct.
            
            float targetY = (float)p3.Y - (h * level);

            canvas.DrawLine((float)p3.X, targetY, xEnd, targetY, paint);
            
            // Text
            string text = $"FE {level:P1}";
            canvas.DrawText(text, (float)p3.X + 10, targetY - 5, textPaint);
        }
        
        if (IsSelected)
        {
            using var handlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 4, handlePaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 4, handlePaint);
            canvas.DrawCircle((float)p3.X, (float)p3.Y, 4, handlePaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
         if (Points.Count < 3) return false;
         // Hit test logic: simplistically check near P1, P2, P3 handles
         // Or lines?
         // For now, handles are best for selection.
         
         var p1 = transform.ChartToScreen(Points[0]);
         var p2 = transform.ChartToScreen(Points[1]);
         var p3 = transform.ChartToScreen(Points[2]);
         
         if (GetDistance(screenPoint, p1) < tolerance * 2) return true;
         if (GetDistance(screenPoint, p2) < tolerance * 2) return true;
         if (GetDistance(screenPoint, p3) < tolerance * 2) return true;
         
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
