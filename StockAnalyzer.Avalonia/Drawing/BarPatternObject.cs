using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Avalonia.Drawing;

public class BarPatternObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.BarPattern;
    public List<ChartPoint> Points { get; private set; } = new List<ChartPoint>();
    public Color Color { get; set; } = Colors.Blue; // Unused for rendering, but kept for serialization
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness; // Unused for rendering, kept for IChartObject consistency
    public int Transparency { get; set; } = 25; // Default 25% Opacity
    public Color UpColor { get; set; } = Colors.Green;
    public Color DownColor { get; set; } = Colors.Red;
    public bool IsSelected { get; set; }

    public List<GhostCandle> Candles { get; set; } = new();

    // Store relative percentage changes from the first candle's Open
    // or relative to the previous candle?
    // "Bar Pattern" usually copies the shape.
    // Let's store absolute Open/High/Low/Close relative to the Start Price of the sequence,
    // so we can project it from a new Anchor Price.

    public BarPatternObject(ChartPoint anchor)
    {
        Points.Add(anchor);
    }

    public void Initialize(List<CoreCandleData> sourceData)
    {
        Candles.Clear();
        if (sourceData == null || sourceData.Count == 0) return;

        decimal basePrice = sourceData[0].Open;
        DateTime baseTime = sourceData[0].Timestamp;

        for (int i = 0; i < sourceData.Count; i++)
        {
            var data = sourceData[i];
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

    public SKColor SkiaColor => new SKColor(0, 100, 255, 150); // Semi-transparent Blue

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count == 0) return;

        // Selection Mode Visualization
        if (Candles.Count == 0 && Points.Count > 1)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            var p2 = transform.ChartToScreen(Points[1]);
            
            var left = Math.Min(p1.X, p2.X);
            var right = Math.Max(p1.X, p2.X);
            
            // Draw a vertical band to show time range selection
            var rect = new SKRect((float)left, 0, (float)right, canvas.LocalClipBounds.Height);
            using var bandPaint = new SKPaint { Color = new SKColor(0, 100, 255, 50), Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, bandPaint);
            return;
        }

        if (Candles.Count == 0) return;

        var anchor = Points[0];
        
        DateTime baseTime = anchor.Time;
        decimal basePrice = anchor.Price;

        using var paint = new SKPaint { Color = SkiaColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var wickPaint = new SKPaint { Color = SkiaColor, StrokeWidth = 1, IsAntialias = true };
        using var outlinePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        // Dynamic body width: derive from the time interval between adjacent ghost candles via the transform
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
            // We calculate X for Open/Close based on the same time T
            var pOpen = transform.ChartToScreen(new ChartPoint(t, open));
            var pClose = transform.ChartToScreen(new ChartPoint(t, close));

            float x = (float)pHigh.X;
            float yHigh = (float)pHigh.Y;
            float yLow = (float)pLow.Y;

            // Draw Wick
            canvas.DrawLine(x, yHigh, x, yLow, wickPaint);

            // Draw Body
            float width = bodyWidth;
            float yO = (float)pOpen.Y;
            float yC = (float)pClose.Y;
            
            float top = Math.Min(yO, yC);
            float bottom = Math.Max(yO, yC);
            float height = Math.Max(1, bottom - top);

            var bodyRect = new SKRect(x - width / 2, top, x + width / 2, bottom);

            // Color based on Bear/Bull
            // User Configurable Colors
            // Convert Avalonia Color to SKColor
            var upSkColor = new SKColor(UpColor.R, UpColor.G, UpColor.B);
            var downSkColor = new SKColor(DownColor.R, DownColor.G, DownColor.B);
            
            var candleColor = (close >= open) ? upSkColor : downSkColor;
            
            // Use Transparency property for alpha (0-100 mapping to 0-255)
            // 25 means 25% Opacity (faint), 100 means opaque? 
            // Wait, previous assumption: "Opacity 25" -> 25% Visible (Alpha ~64).
            // User requested "Default 25" for *Transparency*.
            // Note: If user says "Transparency 25", they might mean 25% transparent (75% opaque).
            // But in FRVP we used Opacity. 
            // Let's stick to "Value 25" -> Alpha ~64 (25% Opacity) because Ghost is meant to be faint.
            // If it's too faint, user can increase it.
            
            byte alpha = (byte)(255 * (Transparency / 100.0));
            paint.Color = candleColor.WithAlpha(alpha);
            
            canvas.DrawRect(bodyRect, paint);
            
            if (IsSelected)
            {
                 canvas.DrawRect(bodyRect, outlinePaint);
            }
        }

        if (IsSelected)
        {
             // Draw Anchor Handle
             var p = transform.ChartToScreen(anchor);
             SelectionHandleRenderer.Draw(canvas, p, SKColors.Yellow, ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0) return false;
        
        var anchor = Points[0];
        DateTime baseTime = anchor.Time;
        decimal basePrice = anchor.Price;

        // Check if point is inside any candle
        foreach (var c in Candles)
        {
             DateTime t = baseTime.AddTicks(c.TicksOffset);
             decimal high = basePrice + c.High;
             decimal low = basePrice + c.Low;
             
             var pHigh = transform.ChartToScreen(new ChartPoint(t, high));
             var pLow = transform.ChartToScreen(new ChartPoint(t, low));
             
             float x = (float)pHigh.X;
             float width = ChartConstants.BarPatternHitTestWidth; // Bit wider for hit test
             
             if (Math.Abs(screenPoint.X - x) <= width/2)
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
        }
    }
}
