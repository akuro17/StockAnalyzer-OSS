using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Analysis; // Using the new analysis namespace

namespace StockAnalyzer.Avalonia.Drawing;

public class FixedRangeVolumeProfileObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FixedRangeVolumeProfile;

    // Defined by 2 points (Box corners) to define Time Range and Vertical Range (optional, usually full height of candles? No, Fixed Range usually means Time and Price range? 
    // "Fixed Range Volume Profile" typically means Time Range. Price range is usually determined by the High/Low of the candles in that time range.
    // However, the box allows limiting the price range too if desired. 
    // Let's stick to standard FRVP: Time Range defined by box width. Price range defined by max/min of included candles OR box height?
    // User expectation: Box defines the area. If box is drawn, maybe filters volume only within that price range too?
    // Standard implementation: All volume in time range.
    // Let's use the Box to define Time Range. Vertical range of the box can be used to constrain calculation, or just visual.
    // Let's implement specific logic: Filter candles by Time Range. Calculate Profile. Render Profile *inside* the box or extending from Left/Right of box?
    // Usually "Fixed Range" means "Over this time period".
    // I will use Time Range from Points[0].X to Points[1].X.
    // Vertical placement: I'll auto-scale to the Price Range of the candles, ignoring Box Y for calculation (unless user wants to filter price).
    // Box Y can be used for visual clipping? 
    // Let's start simplest: Time Range determines candles. Profile computed on Candle High/Low. Rendered across the time width.
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Blue; // User req: Blue
    public Color ValueAreaColor { get; set; } = Colors.Green; // User req: Green
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    public List<VolumeBin> ProfileData { get; private set; } = new List<VolumeBin>();
    public decimal VAH { get; private set; }
    public decimal VAL { get; private set; }
    
    // Config
    public bool RightToLeft { get; set; } = false; // Draw from right side of box?
    public double Opacity { get; set; } = 0.25; // User req: 25%

    public FixedRangeVolumeProfileObject(ChartPoint p1, ChartPoint p2)
    {
        Points = new List<ChartPoint> { p1, p2 };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, (byte)(255 * Opacity));
    public SKColor SkiaValueColor => new SKColor(ValueAreaColor.R, ValueAreaColor.G, ValueAreaColor.B, (byte)(255 * Opacity));

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        if (Points.Count < 2) return;

        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var start = t1 < t2 ? t1 : t2;
        var end = t1 < t2 ? t2 : t1;

        var range = candles.Where(c => c.Timestamp >= start && c.Timestamp <= end).ToList();
        
        // Calculate Profile
        // Row Size: Auto-calculate based on nice visual? or fixed?
        // Let's use 50 rows for decent resolution.
        ProfileData = VolumeAnalysis.CalculateProfile(range, 50);
        
        // Calculate Value Area
        var va = VolumeAnalysis.CalculateValueArea(ProfileData);
        VAH = va.VAH;
        VAL = va.VAL;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;
        if (ProfileData == null || !ProfileData.Any()) return;

        // Visual Bounds
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        float left = (float)Math.Min(p1.X, p2.X);
        float right = (float)Math.Max(p1.X, p2.X);
        float width = right - left;
        
        // Draw Histogram
        using var paintProfile = new SKPaint { Color = SkiaColor, Style = SKPaintStyle.Fill };
        using var paintVA = new SKPaint { Color = SkiaValueColor, Style = SKPaintStyle.Fill };
        using var paintBorder = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        foreach (var bin in ProfileData)
        {
            float yTop = (float)transform.ChartToScreen(new ChartPoint(DateTime.Now, bin.UpperBound)).Y;
            float yBottom = (float)transform.ChartToScreen(new ChartPoint(DateTime.Now, bin.LowerBound)).Y;
            
            // Bar Width relative to Max Volume in this profile
            float barWidth = (float)(bin.WidthPercent * width);
            
            // Draw Direction
            float xStart = left;
            float xEnd = left + barWidth;

            var rect = new SKRect(xStart, yTop, xEnd, yBottom);
            
            // Choose color: Value Area vs Normal
            bool isValueArea = bin.Price >= VAL && bin.Price <= VAH;
            
            canvas.DrawRect(rect, isValueArea ? paintVA : paintProfile);
            // Optional: Draw Buy/Sell split? Complex.
        }

        // Draw Box outline
        float top = (float)Math.Min(p1.Y, p2.Y);
        float bottom = (float)Math.Max(p1.Y, p2.Y);
        
        // Actually, let's draw outline around Time Range and Candle Price Range? 
        // Or user defined box?
        // User defined box is useful for selection.
        var userRect = new SKRect(left, top, right, bottom);
        // canvas.DrawRect(userRect, new SKPaint { Color = SKColors.Gray, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) });

        if (IsSelected)
        {
             using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
             canvas.DrawCircle(left, top, 5, selPaint);
             canvas.DrawCircle(right, bottom, 5, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        // Default to user box
        double x1 = p1.X;
        double x2 = p2.X;
        double y1 = p1.Y;
        double y2 = p2.Y;

        // If we have profile data, use its price range for Y bounds
        if (ProfileData != null && ProfileData.Count > 0)
        {
            var minPrice = ProfileData.Min(b => b.LowerBound);
            var maxPrice = ProfileData.Max(b => b.UpperBound);
            
            var topPt = transform.ChartToScreen(new ChartPoint(Points[0].Time, maxPrice));
            var bottomPt = transform.ChartToScreen(new ChartPoint(Points[0].Time, minPrice));
            
            // Screen Y usually grows downwards. MaxPrice -> Lower Y value.
            y1 = topPt.Y;
            y2 = bottomPt.Y;
        }

        var rect = new global::Avalonia.Rect(new global::Avalonia.Point(x1, y1), new global::Avalonia.Point(x2, y2));
        return rect.Contains(screenPoint);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price); 
        }
    }
}
