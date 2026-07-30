using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Rectangle object implementation (defined by 2 points)
/// </summary>
public class RectangleObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Rectangle;
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }
    
    // Additional property for Fill
    public bool IsFilled { get; set; } = true;

    public RectangleObject(ChartPoint p1, ChartPoint p2)
    {
        Points = new List<ChartPoint> { p1, p2 };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        var left = (float)Math.Min(p1.X, p2.X);
        var top = (float)Math.Min(p1.Y, p2.Y);
        var right = (float)Math.Max(p1.X, p2.X);
        var bottom = (float)Math.Max(p1.Y, p2.Y);
        var rect = new SKRect(left, top, right, bottom);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        canvas.DrawRect(rect, paint);

        if (IsFilled)
        {
            using var fillPaint = new SKPaint
            {
                Color = new SKColor(SkiaColor.Red, SkiaColor.Green, SkiaColor.Blue, 30), // Low opacity
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(rect, fillPaint);
        }

        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            // Draw handles at corners
            canvas.DrawCircle(left, top, 4, selPaint);
            canvas.DrawCircle(right, top, 4, selPaint);
            canvas.DrawCircle(right, bottom, 4, selPaint);
            canvas.DrawCircle(left, bottom, 4, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        var rect = new global::Avalonia.Rect(p1, p2);

        // Hit test border or inside?
        if (rect.Contains(screenPoint)) return true;
        
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
