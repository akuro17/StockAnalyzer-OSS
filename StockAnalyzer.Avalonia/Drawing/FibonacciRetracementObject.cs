using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class FibonacciRetracementObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.FibonacciRetracement;

    private readonly float[] _levels = { 0f, 0.236f, 0.382f, 0.5f, 0.618f, 0.786f, 1.0f };

    private readonly SKPaint _dashPaint;
    private readonly SKPaint _textPaint;

    public FibonacciRetracementObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);

        _dashPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true
        };
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Draw Trend Line
        _dashPaint.Color = SkiaColor.WithAlpha(128);
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, _dashPaint);

        float yDiff = (float)p1.Y - (float)p2.Y;
        float x1 = (float)Math.Min(p1.X, p2.X);
        float x2 = (float)Math.Max(p1.X, p2.X);

        _textPaint.Color = SkiaColor;
        _textPaint.TextSize = DrawingThemeContext.DrawingFontSize;

        foreach (var level in _levels)
        {
            float y = (float)p2.Y + (yDiff * level);
            
            canvas.DrawLine(x1, y, x2, y, _cachedPaint);
            
            // Draw Text (Legacy Layout: Right side of line, Top: %, Bottom: Value)
            float textX = x2 + 5;
            float textY_Percent = y - 3;
            float textY_Value = y + 10;

            string textPercent = $"{level:P1}";
            string textValue = $"{GetPrice(y, transform):F2}";

            canvas.DrawText(textPercent, textX, textY_Percent, _textPaint);
            canvas.DrawText(textValue, textX, textY_Value, _textPaint);
        }
    }
    
    private static decimal GetPrice(float y, ICoordinateTransform transform)
    {
        return transform.ScreenToChart(new global::Avalonia.Point(0, y)).Price;
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
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
        
        return false;
    }

    public override void Dispose()
    {
        _dashPaint.Dispose();
        _textPaint.Dispose();
        base.Dispose();
    }
}
