using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gann Fan Tool
/// Draws a set of lines from a starting point at various angles based on 1x1, 1x2, 2x1 etc. geometric relationships.
/// </summary>
public class GannFanObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GannFan;
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    // Ratios: Price Units per Time Unit (Slope)
    // 1x1 is 45 degrees (geometrically in square scaling), but here we map units.
    // Standard Gann Angles: 1x8, 1x4, 1x3, 1x2, 1x1, 2x1, 3x1, 4x1, 8x1
    private readonly (int time, int price)[] _ratios = new[]
    {
        (1, 8), (1, 4), (1, 3), (1, 2),
        (1, 1),
        (2, 1), (3, 1), (4, 1), (8, 1)
    };

    public GannFanObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var startScreen = transform.ChartToScreen(Points[0]);
        var endScreen = transform.ChartToScreen(Points[1]);

        // Calculate unit step based on the second point.
        // In some Gann tools, the second point defines the 1x1 line.
        // Let's assume P1 is Origin, P2 lies on the 1x1 line.
        
        float dx = (float)(endScreen.X - startScreen.X);
        float dy = (float)(endScreen.Y - startScreen.Y);
        
        // Direction multipliers
        float xDir = dx >= 0 ? 1 : -1;
        float yDir = dy >= 0 ? 1 : -1; // Screen Y is down usually, but let's calculate slope relative to absolute.
        
        // Actually, P2 defines the 1x1 vector.
        // So 1x1 slope = dy/dx.
        // 1x2 means half the slope? Or double?
        // Gann Angles: 
        // 1x1 = 45 deg
        // 2x1 (2 Time x 1 Price) = Flatter
        // 1x2 (1 Time x 2 Price) = Steeper
        
        // Slope m = dy / dx (for 1x1)
        // If dx is Time, dy is Price.
        
        // Let's use the visual P2 as the reference for 1x1.
        // Slope 1x1 = dy / dx.
        
        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        
        using var textPaint = new SKPaint
        {
            Color = SkiaColor,
            IsAntialias = true,
            TextSize = 10,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        // Extend lines to screen edge or sufficient length
        float maxExtension = 5000f; // Pixels

        foreach (var ratio in _ratios)
        {
            // ratio = (Time, Price)
            // 1x1 = ratio 1/1
            // 2x1 = ratio 2 time / 1 price = slope * 0.5 (flatter)
            // 1x2 = ratio 1 time / 2 price = slope * 2.0 (steeper)
            
            float slopeFactor = (float)ratio.price / (float)ratio.time;
            
            // Current Slope based on P2 (1x1)
            // screen_dy = slope * screen_dx
            // modified_dy = screen_dy * slopeFactor?
            // Yes, if P2 is 1x1.
            
            // NOTE: Screen Y increases downwards.
            // If dragging UP, dy is negative.
            
            // Target Vector
            float vecX = dx;
            float vecY = dy * slopeFactor; // Check math. 
            // example: 1x1 -> mul 1.
            // 1x2 (Steeper) -> mul 2.
            // 2x1 (Flatter) -> mul 0.5.
            
            // We want to extend this vector.
            float len = (float)Math.Sqrt(vecX * vecX + vecY * vecY);
            if (len < 0.1f) continue;
            
            float scale = maxExtension / len;
            
            float x2 = (float)startScreen.X + vecX * scale;
            float y2 = (float)startScreen.Y + vecY * scale;

            canvas.DrawLine((float)startScreen.X, (float)startScreen.Y, x2, y2, paint);
            
            // Draw Label near the "end" (or P2 X-axis projection?)
            // Let's draw near P2's X coordinate if possible, or just extended.
            // Better: Draw slightly offset from start to avoid clutter?
            // Draw at the edge of the screen? Hard without Bounds.
            // Draw at fixed distance.
            
            float labelDist = 200f; 
            float lx = (float)startScreen.X + vecX * (labelDist / len);
            float ly = (float)startScreen.Y + vecY * (labelDist / len);

            if (ratio.time == 1 && ratio.price == 1)
                paint.StrokeWidth = (float)Thickness + 1; // Bold 1x1
            else
                paint.StrokeWidth = (float)Thickness;

            // Determine label text: "1x1", "1x2" etc.
            string label = $"{ratio.time}x{ratio.price}";
            canvas.DrawText(label, lx, ly, textPaint);
        }
        
        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)startScreen.X, (float)startScreen.Y, 5, selPaint);
            canvas.DrawCircle((float)endScreen.X, (float)endScreen.Y, 5, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;
        
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        float dx = (float)(p2.X - p1.X);
        float dy = (float)(p2.Y - p1.Y);

        foreach (var ratio in _ratios)
        {
             float slopeFactor = (float)ratio.price / (float)ratio.time;
             float vecX = dx;
             float vecY = dy * slopeFactor;
             
             // Check distance from point to line (Infinite line or Ray)
             // Ray from p1
             
             var pt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);
             var lineStart = new SKPoint((float)p1.X, (float)p1.Y);
             var lineEnd = new SKPoint((float)p1.X + vecX, (float)p1.Y + vecY);
             
             if (MathUtils.DistancePointToLine(pt, lineStart, lineEnd) <= tolerance)
             {
                 return true;
             }
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

// Simple Helper for Math if not available
internal static class MathUtils
{
    public static float DistancePointToLine(SKPoint p, SKPoint a, SKPoint b)
    {
        float num = Math.Abs((b.Y - a.Y) * p.X - (b.X - a.X) * p.Y + b.X * a.Y - b.Y * a.X);
        float den = (float)Math.Sqrt(Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.X - a.X, 2));
        if (den == 0) return 0;
        return num / den;
    }
}
