using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Drawing;

public class LongShortPositionObject : IChartObject, IDisposable
{
    private readonly SKPaint _entryPaint;
    private readonly SKPathEffect _dashEffect;
    private readonly SKPaint _targetPaint;
    private readonly SKPaint _stopPaint;
    private readonly SKPaint _textPaint;
    private readonly SKFont _font;
    private bool _disposed;

    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type { get; private set; }
    public List<ChartPoint> Points { get; private set; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    
    // New Settings Properties
    public Color TargetColor { get; set; } = Colors.Green;
    public Color StopColor { get; set; } = Colors.Red;
    public double AreaOpacity { get; set; } = 0.15; // default ~40/255 -> 0.15

    /// <summary>
    /// Visual width of the Target/Stop box (pixels). User-resizable by dragging the
    /// Stop/Target handle (drawn at the box's right edge).
    /// </summary>
    public double BoxWidth { get; set; } = ChartConstants.LongShortBoxWidth;

    public LongShortPositionObject()
    {
        _dashEffect = SKPathEffect.CreateDash(new float[] { 2, 2 }, 0);
        _entryPaint = new SKPaint
        {
            StrokeWidth = 1,
            PathEffect = _dashEffect,
            IsAntialias = true
        };

        _targetPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _stopPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = DrawingThemeContext.DrawingFontSize
        };

        _font = new SKFont(SKTypeface.Default, DrawingThemeContext.DrawingFontSize);
    }

    public LongShortPositionObject(ChartPoint entry, ChartPoint stop, ChartPoint target, bool isLong)
        : this()
    {
        Type = isLong ? ChartObjectType.LongPosition : ChartObjectType.ShortPosition;
        Points.Add(entry);
        Points.Add(stop);
        Points.Add(target);
    }
    
    // Additional Property
    public bool IsLong => Type == ChartObjectType.LongPosition;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    /// <summary>
    /// Clamps a proposed Stop price so it stays on the correct side of Entry
    /// (below Entry for Long, above Entry for Short). Shared by the settings dialog
    /// and chart drag-handle interaction so both enforce the identical invariant.
    /// </summary>
    public static decimal ClampStopPrice(decimal proposedStop, decimal entryPrice, bool isLong)
    {
        if (isLong && proposedStop >= entryPrice) return entryPrice - ChartConstants.LongShortPriceClampEpsilon;
        if (!isLong && proposedStop <= entryPrice) return entryPrice + ChartConstants.LongShortPriceClampEpsilon;
        return proposedStop;
    }

    /// <summary>
    /// Clamps a proposed Target price so it stays on the correct side of Entry
    /// (above Entry for Long, below Entry for Short).
    /// </summary>
    public static decimal ClampTargetPrice(decimal proposedTarget, decimal entryPrice, bool isLong)
    {
        if (isLong && proposedTarget <= entryPrice) return entryPrice + ChartConstants.LongShortPriceClampEpsilon;
        if (!isLong && proposedTarget >= entryPrice) return entryPrice - ChartConstants.LongShortPriceClampEpsilon;
        return proposedTarget;
    }

    /// <summary>
    /// Clamps a proposed Entry price so it stays strictly between Stop and Target
    /// (Stop &lt; Entry &lt; Target for Long, Target &lt; Entry &lt; Stop for Short).
    /// </summary>
    public static decimal ClampEntryPrice(decimal proposedEntry, decimal stopPrice, decimal targetPrice, bool isLong)
    {
        decimal entry = proposedEntry;
        if (isLong)
        {
            if (entry <= stopPrice) entry = stopPrice + ChartConstants.LongShortPriceClampEpsilon;
            if (entry >= targetPrice) entry = targetPrice - ChartConstants.LongShortPriceClampEpsilon;
        }
        else
        {
            if (entry >= stopPrice) entry = stopPrice - ChartConstants.LongShortPriceClampEpsilon;
            if (entry <= targetPrice) entry = targetPrice + ChartConstants.LongShortPriceClampEpsilon;
        }
        return entry;
    }

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
        
        float width = (float)BoxWidth; // User-resizable width
        float rightX = x + width;

        _entryPaint.Color = new SKColor(Color.R, Color.G, Color.B, Color.A);
        
        byte alpha = (byte)Math.Clamp(AreaOpacity * 255, 0, 255);

        _targetPaint.Color = new SKColor(TargetColor.R, TargetColor.G, TargetColor.B, alpha);
        _stopPaint.Color = new SKColor(StopColor.R, StopColor.G, StopColor.B, alpha);

        // Draw Rects
        // Note: Skia coordinates, Y increases downwards.
        // Min/Max ensures correct Rect construction regardless of Up/Down.
        var targetRect = new SKRect(x, Math.Min(yEntry, yTarget), rightX, Math.Max(yEntry, yTarget));
        var stopRect = new SKRect(x, Math.Min(yEntry, yStop), rightX, Math.Max(yEntry, yStop));
        
        // Logic: Target is always Target color. Stop is always Stop color.
        canvas.DrawRect(targetRect, _targetPaint);
        canvas.DrawRect(stopRect, _stopPaint);
        
        // Draw Entry Line
        canvas.DrawLine(x, yEntry, rightX, yEntry, _entryPaint);
        
        // Draw Stats
        decimal risk = Math.Abs(entryPt.Price - stopPt.Price);
        decimal reward = Math.Abs(targetPt.Price - entryPt.Price);
        decimal ratio = risk != 0 ? reward / risk : 0;
        
        _textPaint.Color = DrawingThemeContext.TextColor;
        
        string FormatDiff(decimal diff) => diff > 0 ? $"+{diff:F2}" : $"{diff:F2}";

        string targetStr = $"{targetPt.Price:F2} / {FormatDiff(targetPt.Price - entryPt.Price)}";
        string entryStr  = $"{entryPt.Price:F2} / R/R {ratio:F2}";
        string stopStr   = $"{stopPt.Price:F2} / {FormatDiff(stopPt.Price - entryPt.Price)}";

        // Vertical adjustment to center text vertically with the line
        float textYOffset = ChartConstants.LongShortTextOffsetY;

        canvas.DrawText(targetStr, rightX + ChartConstants.LongShortTextOffsetX, yTarget + textYOffset, _font, _textPaint);
        canvas.DrawText(entryStr, rightX + ChartConstants.LongShortTextOffsetX, yEntry + textYOffset, _font, _textPaint);
        canvas.DrawText(stopStr, rightX + ChartConstants.LongShortTextOffsetX, yStop + textYOffset, _font, _textPaint);
        
        if (IsSelected)
        {
             // Draw handles. Entry stays at the box's left edge (its anchor point);
             // Stop/Target are drawn at the right edge, where dragging them
             // horizontally resizes BoxWidth (see ChartInteractionController).
             SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x, yEntry), SKColors.Red); // Entry Handle
             SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(rightX, yStop), SKColors.Red); // Stop Handle
             SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(rightX, yTarget), SKColors.Red); // Target Handle
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
         double rightX = x + BoxWidth;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entryPaint.Dispose();
        _dashEffect.Dispose();
        _targetPaint.Dispose();
        _stopPaint.Dispose();
        _textPaint.Dispose();
        _font.Dispose();
    }
}
