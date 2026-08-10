using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class CalloutObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Callout;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Orange;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }
    public string Text { get; set; } = "Callout";
    public double FontSize { get; set; } = 14.0;

    public CalloutObject(ChartPoint anchor, ChartPoint body)
    {
        Points.Add(anchor);
        Points.Add(body);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]); // Anchor
        var p2 = transform.ChartToScreen(Points[1]); // Body (Text Box)

        using var linePaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) // Dotted line for pointer
        };

        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, linePaint);

        // Draw Text Box at P2
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            TextSize = (float)FontSize
        };

        SKRect textBounds = new SKRect();
        textPaint.MeasureText(Text, ref textBounds);

        float padding = ChartConstants.DefaultTextBackgroundPadding;
        float w = textBounds.Width + padding * 2;
        float h = textBounds.Height + padding * 2;
        
        float x = (float)p2.X;
        float y = (float)p2.Y;
        
        // Centered on P2
        SKRect rect = new SKRect(x - w/2, y - h/2, x + w/2, y + h/2);

        using var bgPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(220),
            Style = SKPaintStyle.Fill
        };
        
        canvas.DrawRoundRect(rect, ChartConstants.DefaultDrawingCornerRadius, ChartConstants.DefaultDrawingCornerRadius, bgPaint);
        
        using var borderPaint = new SKPaint
        {
            Color = SkiaColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, ChartConstants.DefaultDrawingCornerRadius, ChartConstants.DefaultDrawingCornerRadius, borderPaint);

        canvas.DrawText(Text, x - textBounds.MidX, y - textBounds.MidY, textPaint);

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

        var p2 = transform.ChartToScreen(Points[1]);
        
        // Hit test the text box area? 
        // Or just the points?
        // Let's do simple point distance for now defined by P1/P2.
        
        var p1 = transform.ChartToScreen(Points[0]);
        
        if (Distance(screenPoint, p1) <= tolerance * 2) return true;
        if (Distance(screenPoint, p2) <= tolerance * 4) return true; // Larger area for body
        
        return false;
    }
    
    private double Distance(global::Avalonia.Point p1, global::Avalonia.Point p2)
    {
        return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
