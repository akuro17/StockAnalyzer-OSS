using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class TextObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Text;
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; }
    public double Thickness { get; set; } = 1.0;
    public double FontSize { get; set; } = 12.0;
    public bool IsSelected { get; set; }
    
    public string Text { get; set; } = "Text";

    public TextObject(ChartPoint point, string text = "Text")
    {
        Points = new List<ChartPoint> { point };
        Color = Colors.Green;
        FontSize = 12.0;
        Text = text;
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count == 0) return;

        var pt = transform.ChartToScreen(Points[0]);

        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            TextSize = (float)FontSize,
            Typeface = SKTypeface.Default
        };

        SKRect textBounds = new SKRect();
        textPaint.MeasureText(Text, ref textBounds);

        float padding = ChartConstants.DefaultTextBackgroundPadding;
        float w = textBounds.Width + padding * 2;
        float h = textBounds.Height + padding * 2;
        
        float x = (float)pt.X;
        float y = (float)pt.Y;
        
        // Centered on Point
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
            canvas.DrawCircle((float)pt.X, (float)pt.Y, ChartConstants.DefaultHandleRadius, handlePaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0 || string.IsNullOrEmpty(Text)) return false;
        var pt = transform.ChartToScreen(Points[0]);

        using var paint = new SKPaint
        {
            TextSize = (float)FontSize,
            Typeface = SKTypeface.Default
        };

        var bounds = new SKRect();
        paint.MeasureText(Text, ref bounds);

        float padding = ChartConstants.DefaultTextBackgroundPadding;
        float w = bounds.Width + padding * 2;
        float h = bounds.Height + padding * 2;
        
        float x = (float)pt.X;
        float y = (float)pt.Y;
        
        // Match Render logic: Centered on Point
        var rect = new global::Avalonia.Rect(x - w/2, y - h/2, w, h);
        
        return rect.Contains(screenPoint);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            Points[0] = new ChartPoint(Points[0].Time.Add(timeDelta), Points[0].Price + priceDelta);
        }
    }
}
