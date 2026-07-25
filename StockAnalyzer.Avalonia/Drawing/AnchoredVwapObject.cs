using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Drawing;

public class AnchoredVwapObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.AnchoredVWAP;

    // Point[0] is the Anchor. Point[1] is just visual handle or optional end?
    // Usually Anchored VWAP goes to end of chart (Right Infinite).
    // Let's say Point[0] is anchor. 
    public List<ChartPoint> Points { get; private set; }
    
    public Color Color { get; set; } = Colors.Orange;
    public double Thickness { get; set; } = 2.0;
    public bool IsSelected { get; set; }

    // Calculated curve
    public List<(DateTime Time, decimal Price)> VwapCurve { get; private set; } = new List<(DateTime, decimal)>();

    public AnchoredVwapObject(ChartPoint anchor)
    {
        Points = new List<ChartPoint> { anchor };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        if (Points.Count < 1) return;
        
        var anchorTime = Points[0].Time;
        
        // Filter from anchor onwards
        var relevantCandles = candles.Where(c => c.Timestamp >= anchorTime).OrderBy(c => c.Timestamp).ToList();
        
        if (!relevantCandles.Any()) 
        {
            VwapCurve.Clear();
            return;
        }

        VwapCurve = VolumeAnalysis.CalculateAnchoredVwap(relevantCandles);
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (VwapCurve == null || VwapCurve.Count < 2) return;

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        using var path = new SKPath();
        bool isFirst = true;

        foreach (var (time, price) in VwapCurve)
        {
            var screenPt = transform.ChartToScreen(new ChartPoint(time, price));
            if (isFirst)
            {
                path.MoveTo((float)screenPt.X, (float)screenPt.Y);
                isFirst = false;
            }
            else
            {
                path.LineTo((float)screenPt.X, (float)screenPt.Y);
            }
        }

        canvas.DrawPath(path, paint);

        if (IsSelected)
        {
             // Draw Anchor Point
             var anchor = transform.ChartToScreen(Points[0]);
             using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
             canvas.DrawCircle((float)anchor.X, (float)anchor.Y, 5, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        // 1. Check anchor point first (fast)
        if (Points.Count > 0)
        {
             var anchor = transform.ChartToScreen(Points[0]);
             double dist = Math.Sqrt(Math.Pow(anchor.X - screenPoint.X, 2) + Math.Pow(anchor.Y - screenPoint.Y, 2));
             if (dist < tolerance * 2) return true;
        }

        // 2. Check curve segments
        if (VwapCurve == null || VwapCurve.Count < 2) return false;

        // Optimization: Only check visible or simplified segments?
        // Basic implementation: Iterate all segments.
        global::Avalonia.Point? p1 = null;

        foreach (var (time, price) in VwapCurve)
        {
            var p2 = transform.ChartToScreen(new ChartPoint(time, price));
            
            if (p1.HasValue)
            {
                if (HitTestHelper.IsPointNearLine(screenPoint, p1.Value, p2, tolerance))
                {
                    return true;
                }
            }
            p1 = p2;
        }

        return false;
    }

    // Helper for HitTest logic (consider moving to a shared utility class if reused)
    private static class HitTestHelper
    {
        public static bool IsPointNearLine(global::Avalonia.Point p, global::Avalonia.Point a, global::Avalonia.Point b, double tolerance)
        {
             double l2 = Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);
             if (l2 == 0) return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2)) <= tolerance;

             double t = ((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / l2;
             t = Math.Max(0, Math.Min(1, t));

             global::Avalonia.Point projection = new global::Avalonia.Point(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
             return Math.Sqrt(Math.Pow(p.X - projection.X, 2) + Math.Pow(p.Y - projection.Y, 2)) <= tolerance;
        }
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            Points[0] = new ChartPoint(Points[0].Time.Add(timeDelta), Points[0].Price); // Price anchor isn't used for calc but for drawing anchor logic?
            // Actually AVWAP calculates from Time. Price is irrelevant for calculation.
        }
    }
}
