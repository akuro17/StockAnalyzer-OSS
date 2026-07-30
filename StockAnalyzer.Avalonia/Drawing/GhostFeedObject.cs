using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Avalonia.Drawing;

public class GhostFeedObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GhostFeed;
    public List<ChartPoint> Points { get; private set; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    public List<GhostCandle> Candles { get; set; } = new();

    public GhostFeedObject(ChartPoint anchor, ChartPoint endPoint)
    {
        Points.Add(anchor);
        Points.Add(endPoint);
    }

    public void UpdateEndPoint(ChartPoint endPoint, IEnumerable<CoreCandleData>? candles)
    {
        if (Points.Count > 1)
        {
            Points[1] = endPoint;
            ExtractCandles(Points[0], endPoint, candles);
        }
    }
    
    private void ExtractCandles(ChartPoint start, ChartPoint end, IEnumerable<CoreCandleData>? candles)
    {
        Candles.Clear();
        if (candles == null) return;

        DateTime t1 = start.Time;
        DateTime t2 = end.Time;
        if (t1 > t2) (t1, t2) = (t2, t1); // Ensure correct range regardless of drag direction

        var sourceData = candles
            .Where(c => c.Timestamp >= t1 && c.Timestamp <= t2)
            .OrderBy(c => c.Timestamp)
            .ToList();

        if (sourceData.Count == 0) return;

        // Base price and time to anchor the ghost feed
        decimal basePrice = start.Price;
        DateTime baseTime = start.Time;

        foreach (var data in sourceData)
        {
            long ticksOffset = data.Timestamp.Ticks - baseTime.Ticks;

            Candles.Add(new GhostCandle 
            { 
                TicksOffset = ticksOffset,
                Open = data.Open - basePrice, 
                High = data.High - basePrice, 
                Low = data.Low - basePrice, 
                Close = data.Close - basePrice 
            });
        }
    }

    public SKColor SkiaColor => new SKColor(100, 100, 100, 128); // Semi-transparent Gray

    private ChartPoint GetVisualAnchor()
    {
        if (Points.Count < 2) return Points[0];
        DateTime t1 = Points[0].Time;
        DateTime t2 = Points[1].Time;
        DateTime startT = t1 < t2 ? t1 : t2;
        DateTime endT = t1 > t2 ? t1 : t2;
        return new ChartPoint(endT, Points[0].Price);
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count == 0) return;

        // Visual selection hint before extraction is complete, similar to BarPattern
        if (Candles.Count == 0 && Points.Count > 1)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            var p2 = transform.ChartToScreen(Points[1]);
            
            var left = Math.Min(p1.X, p2.X);
            var right = Math.Max(p1.X, p2.X);
            
            var rect = new SKRect((float)left, 0, (float)right, canvas.LocalClipBounds.Height);
            using var bandPaint = new SKPaint { Color = new SKColor(100, 100, 100, 50), Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, bandPaint);
            return;
        }

        if (Candles.Count == 0) return;

        var visualAnchor = GetVisualAnchor();
        DateTime baseTime = visualAnchor.Time;
        decimal basePrice = visualAnchor.Price;
        
        using var paint = new SKPaint { Color = SkiaColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var wickPaint = new SKPaint { Color = SkiaColor, StrokeWidth = 1, IsAntialias = true };
        
        // Dynamic body width: derive from the time interval between adjacent ghost candles via the transform,
        // exactly like CandleStickRenderer. This ensures zoom-synchronized sizing.
        float bodyWidth = ChartConstants.BarPatternBodyWidth; // fallback
        if (Candles.Count >= 2)
        {
            var t0 = baseTime.AddTicks(Candles[0].TicksOffset);
            var t1 = baseTime.AddTicks(Candles[1].TicksOffset);
            double sx0 = transform.ChartToScreen(new ChartPoint(t0, basePrice)).X;
            double sx1 = transform.ChartToScreen(new ChartPoint(t1, basePrice)).X;
            float candleWidth = (float)Math.Abs(sx1 - sx0);
            bodyWidth = Math.Max(1f, candleWidth * 0.8f);
        }
        
        foreach (var c in Candles)
        {
            DateTime t = baseTime.AddTicks(c.TicksOffset);
            
            decimal open = basePrice + c.Open;
            decimal close = basePrice + c.Close;
            decimal high = basePrice + c.High;
            decimal low = basePrice + c.Low;

            var pHigh = transform.ChartToScreen(new ChartPoint(t, high));
            var pLow = transform.ChartToScreen(new ChartPoint(t, low));
            var pOpen = transform.ChartToScreen(new ChartPoint(t, open));
            var pClose = transform.ChartToScreen(new ChartPoint(t, close));
            
            float x = (float)pHigh.X;
            float yHigh = (float)pHigh.Y;
            float yLow = (float)pLow.Y;
            
            // Draw Wick
            canvas.DrawLine(x, yHigh, x, yLow, wickPaint);
            
            // Draw Body
            float width = bodyWidth;
            float top = (float)Math.Min(pOpen.Y, pClose.Y);
            float bottom = (float)Math.Max(pOpen.Y, pClose.Y);
            
            var bodyRect = new SKRect(x - width/2, top, x + width/2, bottom);
            
            paint.Color = (close >= open) ? new SKColor(0, 200, 0, 128) : new SKColor(200, 0, 0, 128);
            canvas.DrawRect(bodyRect, paint);
        }
        
        if (IsSelected)
        {
             // Draw Anchor Handle
             var p = transform.ChartToScreen(visualAnchor);
             using var selPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
             canvas.DrawCircle((float)p.X, (float)p.Y, ChartConstants.DefaultHandleRadius, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0 || Candles.Count == 0) return false;

        var visualAnchor = GetVisualAnchor();
        DateTime baseTime = visualAnchor.Time;
        decimal basePrice = visualAnchor.Price;

        foreach (var c in Candles)
        {
            DateTime t = baseTime.AddTicks(c.TicksOffset);
            decimal high = basePrice + c.High;
            decimal low = basePrice + c.Low;

            var pHigh = transform.ChartToScreen(new ChartPoint(t, high));
            var pLow = transform.ChartToScreen(new ChartPoint(t, low));

            float x = (float)pHigh.X;
            float width = ChartConstants.BarPatternHitTestWidth;

            if (Math.Abs(screenPoint.X - x) <= width / 2)
            {
                if (screenPoint.Y >= Math.Min(pHigh.Y, pLow.Y) && screenPoint.Y <= Math.Max(pHigh.Y, pLow.Y))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            Points[0] = new ChartPoint(Points[0].Time.Add(timeDelta), Points[0].Price + priceDelta);
            if (Points.Count > 1) 
            {
                Points[1] = new ChartPoint(Points[1].Time.Add(timeDelta), Points[1].Price + priceDelta);
            }
        }
    }
}

public class GhostCandle
{
    public long TicksOffset { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
}
