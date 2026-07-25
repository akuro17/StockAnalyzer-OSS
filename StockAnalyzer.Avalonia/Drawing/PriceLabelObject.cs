using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class PriceLabelObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.PriceLabel;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Blue;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }
    public double FontSize { get; set; } = 12.0;

    public PriceLabelObject(ChartPoint position)
    {
        Points.Add(position); // Anchor
        Points.Add(position); // Label Display Position (Initially same)
    }

    public PriceLabelObject(ChartPoint anchor, ChartPoint labelPos)
    {
        Points.Add(anchor);
        Points.Add(labelPos);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]); // Anchor (Fixed Point)
        var p2 = transform.ChartToScreen(Points[1]); // Label Position (Movable)
        
        // Price is determined by the ANCHOR point
        var price = Points[0].Price;

        string text = $"{price:F2}";

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            IsAntialias = true,
            TextSize = (float)FontSize
        };

        SKRect textBounds = new SKRect();
        paint.MeasureText(text, ref textBounds);
        
        float padding = ChartConstants.DefaultPriceLabelPadding;
        float w = textBounds.Width + padding * 2;
        float h = textBounds.Height + padding * 2;
        
        float x = (float)p2.X;
        float y = (float)p2.Y;
        
        // Draw dashed line from Anchor to Label
        using var linePaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
        };
        canvas.DrawLine((float)p1.X, (float)p1.Y, x, y, linePaint);

        // Background
        using var bgPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(200),
            Style = SKPaintStyle.Fill
        };
        
        // Center label at P2
        SKRect rect = new SKRect(x - w/2, y - h/2, x + w/2, y + h/2);
        canvas.DrawRoundRect(rect, ChartConstants.DefaultDrawingCornerRadius, ChartConstants.DefaultDrawingCornerRadius, bgPaint);
        
        // Border
        using var borderPaint = new SKPaint
        {
            Color = SkiaColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, ChartConstants.DefaultDrawingCornerRadius, ChartConstants.DefaultDrawingCornerRadius, borderPaint);

        // Text
        paint.Color = SKColors.Black; // Text color
        canvas.DrawText(text, x - textBounds.MidX, y - textBounds.MidY, paint);

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

        var p1 = transform.ChartToScreen(Points[0]); // Anchor
        var p2 = transform.ChartToScreen(Points[1]); // Label
        
        // 1. Check Anchor (P0)
        if (Distance(screenPoint, p1) <= tolerance * 2) return true;
        
        // 2. Check Label Handle (P2) - Small radius for the Dot
        if (Distance(screenPoint, p2) <= tolerance * 2) return true;

        // 3. Check Text Rectangle (Body)
        // Re-measure text to get bounds
        var price = Points[0].Price;
        string text = $"{price:F2}";

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = (float)FontSize,
            IsAntialias = true
        };
        SKRect textBounds = new SKRect();
        paint.MeasureText(text, ref textBounds);

        float padding = ChartConstants.DefaultPriceLabelPadding;
        float w = textBounds.Width + padding * 2;
        float h = textBounds.Height + padding * 2;
        float x = (float)p2.X;
        float y = (float)p2.Y;
        
        // Rect centered at P2
        // Logic from Render: SKRect rect = new SKRect(x - w/2, y - h/2, x + w/2, y + h/2);
        // Note: Avalonia Point is (X,Y), SKRect is (Left,Top,Right,Bottom)
        
        double left = x - w/2;
        double top = y - h/2;
        double right = x + w/2;
        double bottom = y + h/2;
        
        if (screenPoint.X >= left && screenPoint.X <= right &&
            screenPoint.Y >= top && screenPoint.Y <= bottom)
        {
            return true;
        }
        
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
        // Price label updates automatically because Points[0].Price changes
    }
}
