using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class FibonacciRetracementObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FibonacciRetracement;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    private readonly float[] _levels = { 0f, 0.236f, 0.382f, 0.5f, 0.618f, 0.786f, 1.0f };

    public FibonacciRetracementObject(ChartPoint p1, ChartPoint p2)
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

        // Draw Trend Line
        using var dashPaint = new SKPaint
        {
            Color = SkiaColor.WithAlpha(128),
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, dashPaint);


        // Calculate Levels
        // Vertical range is P1.Y to P2.Y
        // Note: Y coordinates are screen coordinates.
        // Usually, 0% is Start, 100% is End? Or 0% is High, 100% is Low?
        // Standard Retracement: 
        // If uptrend (Low -> High), 0% is High, 100% is Low in terms of Retracement?
        // Actually, usually we draw line from Swing Low (100% or 0%) to Swing High (0% or 100%).
        // Let's assume P1 is 100% (Start) and P2 is 0% (End). 
        // Wait, normally if I drag from Low to High:
        // Retracement levels appear below High.
        // So P2 (High) is 0 Retracement. P1 (Low) is 100 Retracement.
        // Level Y = P2.Y + (P1.Y - P2.Y) * Level
        
        float yDiff = (float)p1.Y - (float)p2.Y;
        
        // Horizontal extent: Should it cover the whole chart or just the width of the line?
        // Standard implementations usually extend right or cover segment width.
        // Let's cover the segment X range but extended? Or just canvas width?
        // User usually expects lines to extend to the right.
        // For now, let's use the X range of the points but maybe extended?
        // Let's just use the min/max X of the two points for the segment style, 
        // OR extend right if requested. 
        // Most tools extend to the right edge or infinity.
        // Let's allow X range to be P1.X to P2.X for now, similar to legacy.
        
        float x1 = (float)Math.Min(p1.X, p2.X);
        float x2 = (float)Math.Max(p1.X, p2.X);
        
        // If we want to emulate "Trend Based", we project levels based on Y diff.
        
        using var textPaint = new SKPaint
        {
            Color = SkiaColor,
            TextSize = 10,
            IsAntialias = true
        };

        foreach (var level in _levels)
        {
            float y = (float)p2.Y + (yDiff * level);
            
            canvas.DrawLine(x1, y, x2, y, paint);
            
            canvas.DrawLine(x1, y, x2, y, paint);
            
            // Draw Text (Legacy Layout: Right side of line, Top: %, Bottom: Value)
            // Align to x2 + padding
            float textX = x2 + 5;
            float textY_Percent = y - 3;
            float textY_Value = y + 10;

            string textPercent = $"{level:P1}";
            string textValue = $"{GetPrice(y, transform):F2}";

            canvas.DrawText(textPercent, textX, textY_Percent, textPaint);
            canvas.DrawText(textValue, textX, textY_Value, textPaint);
        }

        if (IsSelected)
        {
            using var handlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 4, handlePaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 4, handlePaint);
        }
    }
    
    private decimal GetPrice(float y, ICoordinateTransform transform)
    {
        // Inverse transform dummy or use transform if available
        // transform usually has ScreenToChart logic
        // We can create a point at (0, y) and convert
        return transform.ScreenToChart(new global::Avalonia.Point(0, y)).Price;
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;
        
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        float x1 = (float)Math.Min(p1.X, p2.X);
        float x2 = (float)Math.Max(p1.X, p2.X);
        
        if (screenPoint.X < x1 || screenPoint.X > x2) return false;

        // Check levels
         float yDiff = (float)p1.Y - (float)p2.Y;
         foreach (var level in _levels)
         {
             float y = (float)p2.Y + (yDiff * level);
             if (Math.Abs(screenPoint.Y - y) <= tolerance) return true;
         }
         
         // Check diagonal
         // Should we? Maybe not. Usually just levels.
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
