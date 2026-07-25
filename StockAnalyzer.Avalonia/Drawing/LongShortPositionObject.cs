using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Drawing;

public class LongShortPositionObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type { get; private set; }
    public List<ChartPoint> Points { get; private set; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }
    
    // New Settings Properties
    public Color TargetColor { get; set; } = Colors.Green;
    public Color StopColor { get; set; } = Colors.Red;
    public double AreaOpacity { get; set; } = 0.15; // default ~40/255 -> 0.15

    public LongShortPositionObject(ChartPoint entry, ChartPoint stop, ChartPoint target, bool isLong)
    {
        Type = isLong ? ChartObjectType.LongPosition : ChartObjectType.ShortPosition;
        Points.Add(entry);
        Points.Add(stop);
        Points.Add(target);
    }
    
    // Additional Property
    public bool IsLong => Type == ChartObjectType.LongPosition;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 3) return;

        var entryPt = Points[0];
        var stopPt = Points[1];
        var targetPt = Points[2];

        // Convert to Screen
        var pEntry = transform.ChartToScreen(entryPt);
        var pStop = transform.ChartToScreen(stopPt);
        var pTarget = transform.ChartToScreen(targetPt);

        float x = (float)pEntry.X;
        float yEntry = (float)pEntry.Y;
        float yStop = (float)pStop.Y;
        float yTarget = (float)pTarget.Y;
        
        float width = ChartConstants.LongShortBoxWidth; // Fixed width for visibility
        float rightX = x + width;

        using var entryPaint = new SKPaint
        {
            Color = new SKColor(Color.R, Color.G, Color.B, Color.A), // Use main Color or Gray? Originally Gray, let's keep Gray for Entry Line or user Color.
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash(new float[] { 2, 2 }, 0),
            IsAntialias = true
        };
        
        byte alpha = (byte)Math.Clamp(AreaOpacity * 255, 0, 255);

        using var targetPaint = new SKPaint
        {
            Color = new SKColor(TargetColor.R, TargetColor.G, TargetColor.B, alpha),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        
        using var stopPaint = new SKPaint
        {
            Color = new SKColor(StopColor.R, StopColor.G, StopColor.B, alpha),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        // Draw Rects
        // Note: Skia coordinates, Y increases downwards.
        // Min/Max ensures correct Rect construction regardless of Up/Down.
        var targetRect = new SKRect(x, Math.Min(yEntry, yTarget), rightX, Math.Max(yEntry, yTarget));
        var stopRect = new SKRect(x, Math.Min(yEntry, yStop), rightX, Math.Max(yEntry, yStop));
        
        // Logic: Target is always Target color. Stop is always Stop color.
        canvas.DrawRect(targetRect, targetPaint);
        canvas.DrawRect(stopRect, stopPaint);
        
        // Draw Entry Line
        canvas.DrawLine(x, yEntry, rightX, yEntry, entryPaint);
        
        // Draw Stats
        decimal risk = Math.Abs(entryPt.Price - stopPt.Price);
        decimal reward = Math.Abs(targetPt.Price - entryPt.Price);
        decimal ratio = risk != 0 ? reward / risk : 0;
        
        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, TextSize = ChartConstants.LongShortTextSize };
        using var font = new SKFont(SKTypeface.Default, ChartConstants.LongShortTextSize);
        
        string FormatDiff(decimal diff) => diff > 0 ? $"+{diff:F2}" : $"{diff:F2}";

        string targetStr = $"{targetPt.Price:F2} / {FormatDiff(targetPt.Price - entryPt.Price)}";
        string entryStr  = $"{entryPt.Price:F2} / R/R {ratio:F2}";
        string stopStr   = $"{stopPt.Price:F2} / {FormatDiff(stopPt.Price - entryPt.Price)}";

        // Vertical adjustment to center text vertically with the line
        float textYOffset = ChartConstants.LongShortTextOffsetY;

        canvas.DrawText(targetStr, rightX + ChartConstants.LongShortTextOffsetX, yTarget + textYOffset, font, textPaint);
        canvas.DrawText(entryStr, rightX + ChartConstants.LongShortTextOffsetX, yEntry + textYOffset, font, textPaint);
        canvas.DrawText(stopStr, rightX + ChartConstants.LongShortTextOffsetX, yStop + textYOffset, font, textPaint);
        
        if (IsSelected)
        {
             // Draw handles
             float r = ChartConstants.DefaultHandleRadius;
             using var entryHandlePaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
             using var stopHandlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
             using var targetHandlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };

             canvas.DrawCircle(x, yEntry, r, entryHandlePaint); // Entry Handle
             canvas.DrawCircle(x, yStop, r, stopHandlePaint); // Stop Handle
             canvas.DrawCircle(x, yTarget, r, targetHandlePaint); // Target Handle
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
         if (Points.Count < 3) return false;
         
         var p1 = transform.ChartToScreen(Points[0]);
         var p2 = transform.ChartToScreen(Points[1]);
         var p3 = transform.ChartToScreen(Points[2]);

         double x = p1.X;
         double y1 = p1.Y;
         double y2 = p2.Y;
         double y3 = p3.Y;
         double rightX = x + ChartConstants.LongShortBoxWidth;

         if (screenPoint.X >= x && screenPoint.X <= rightX)
         {
             double minY = Math.Min(Math.Min(y1, y2), y3);
             double maxY = Math.Max(Math.Max(y1, y2), y3);
             if (screenPoint.Y >= minY && screenPoint.Y <= maxY) return true;
         }

         // Also check handles specifically just in case
         if (Math.Abs(p1.X - screenPoint.X) < ChartConstants.DefaultHitProximity && Math.Abs(p1.Y - screenPoint.Y) < ChartConstants.DefaultHitProximity) return true;
         if (Math.Abs(p2.X - screenPoint.X) < ChartConstants.DefaultHitProximity && Math.Abs(p2.Y - screenPoint.Y) < ChartConstants.DefaultHitProximity) return true;
         if (Math.Abs(p3.X - screenPoint.X) < ChartConstants.DefaultHitProximity && Math.Abs(p3.Y - screenPoint.Y) < ChartConstants.DefaultHitProximity) return true;
         
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
