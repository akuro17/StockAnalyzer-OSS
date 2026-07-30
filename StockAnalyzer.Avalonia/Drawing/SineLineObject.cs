using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Sine Line Tool
/// Draws a sine wave overlaid on the chart.
/// P1: Start Point (Phase 0, Middle Price)
/// P2: Defines Period (Time Diff) and Amplitude (Price Diff)
/// </summary>
public class SineLineObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.SineLine;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    public SineLineObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Parameters
        float startX = (float)p1.X;
        float midY = (float)p1.Y;
        
        float periodPx = Math.Abs((float)(p2.X - p1.X));
        if (periodPx < 2f) periodPx = 2f;
        
        // Amplitude: Difference in Y from MidLine (P1.Y)
        float amplitudePx = Math.Abs((float)(p2.Y - p1.Y));
        
        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        var bounds = canvas.LocalClipBounds;
        using var path = new SKPath();

        // Draw wide enough to cover screen
        // Need to find start X relative to screen left
        // x(t) = startX + t
        // phi such that sine passes through startX at phase 0?
        // Form: y = midY + Amp * sin(2*PI * (x - startX) / periodPx)
        // Adjust for coordinate system: Screen Y increases downwards.
        // If we want standard sine visual, -sin maybe? Let's just stick to math, user can invert by dragging down vs up if we preserve sign.
        // But amplitude is abs.
        
        // Optimization: Start drawing from bounds.Left
        // x_start = bounds.Left.
        // We want integers or small steps.
        
        float step = 2f; // pixels
        float x = bounds.Left;
        
        // Move to first point
        bool first = true;
        
        // Extend slightly beyond bounds
        for (x = bounds.Left - periodPx; x <= bounds.Right + periodPx; x += step)
        {
            // Calculate Y
            double angle = 2.0 * Math.PI * (x - startX) / periodPx;
            float y = midY + amplitudePx * (float)Math.Sin(angle); // + because Y is down? 
            // If dragging Up (y < midY), we want peak... 
            // If P2 is above P1 (Screen Y is smaller), Amp is + distance.
            // If we want P2 to be a Peak?
            // If P2 is at (startX + periodPx/4), sin is 1.
            // y = midY + Amp * 1 = midY + Amp (Below midY).
            // So dragging down creates a "peak" at bottom.
            // Dragging up (P2.Y < P1.Y) implies we want peak at top (Y < MidY).
            // So if P2.Y < P1.Y, we probably want y = midY - Amp * sin...
            
            // Let's use signed amplitude based on P2
            float signedAmp = (float)(p2.Y - p1.Y); 
            // But wait, Period is X diff.
            // If P2 is at 1/4 period, Sin is 1. Y = Mid + SignedAmp. Correct.
            
            // Re-calc using signed amp
             y = midY + signedAmp * (float)Math.Sin(angle);

            if (first)
            {
                path.MoveTo(x, y);
                first = false;
            }
            else
            {
                path.LineTo(x, y);
            }
        }
        
        canvas.DrawPath(path, paint);

        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 5, selPaint);
            canvas.DrawCircle((float)p2.X, (float)p2.Y, 5, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
         if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        float startX = (float)p1.X;
        float midY = (float)p1.Y;
        float periodPx = Math.Abs((float)(p2.X - p1.X));
        if (periodPx < 0.1f) return false;
        
        float signedAmp = (float)(p2.Y - p1.Y);

        // Calculate Y at screenPoint.X
        double angle = 2.0 * Math.PI * (screenPoint.X - startX) / periodPx;
        float y = midY + signedAmp * (float)Math.Sin(angle);
        
        return Math.Abs(screenPoint.Y - y) <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
