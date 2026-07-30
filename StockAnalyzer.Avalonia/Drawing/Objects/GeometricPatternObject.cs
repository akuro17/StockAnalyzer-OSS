using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Represents a drawn Geometric Pattern (channel, flag, pennant, etc.).
/// Holds the detected formations and renders them on the chart using the coordinate transform.
/// </summary>
public class GeometricPatternObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GeometricPattern;
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Blue;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    /// <summary>
    /// The detected geometric formations to render.
    /// </summary>
    public IReadOnlyList<DetectedFormation>? Formations { get; set; }

    // Display Filters
    public bool ShowChannels { get; set; } = true;
    public bool ShowWedges { get; set; } = true;
    public bool ShowTriangles { get; set; } = true;
    public bool ShowPennantsAndFlags { get; set; } = true;
    public bool ShowMegaphone { get; set; } = true;
    
    // Detection Settings
    /// <summary>
    /// If set to a value, overrides multi-scale detection and uses this fixed threshold.
    /// If null, uses automated multi-scale detection.
    /// </summary>
    public decimal? ZigZagThreshold { get; set; } = null;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public GeometricPatternObject()
    {
        Points = new List<ChartPoint>();
        var themeColor = AppTheme.GeometricResistanceLine;
        Color = Color.FromArgb(themeColor.Alpha, themeColor.Red, themeColor.Green, themeColor.Blue);
    }

    public GeometricPatternObject(ChartPoint anchor)
    {
        Points = new List<ChartPoint> { anchor };
        var themeColor = AppTheme.GeometricResistanceLine;
        Color = Color.FromArgb(themeColor.Alpha, themeColor.Red, themeColor.Green, themeColor.Blue);
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (transform == null) return;

        // Render each formation
        if (Formations != null && Formations.Count > 0)
        {
            foreach (var formation in Formations)
            {
                if (!ShouldRender(formation.Type)) continue;
                if (!formation.IsBrokenOut) continue;
                
                RenderFormation(canvas, transform, formation);
            }
        }

        // Draw a selection handle at the anchor point if selected
        if (IsSelected && Points.Count > 0)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p1.X, (float)p1.Y, 4, selPaint);
        }
    }

    private void RenderFormation(SKCanvas canvas, ICoordinateTransform transform, DetectedFormation formation)
    {
        // 1. Calculate screen coordinates for the bounded lines
        var resStartScreen = transform.ChartToScreen(new ChartPoint(formation.StartTime, (decimal)formation.UpperPriceAt(formation.StartIndex)));
        var resEndScreen = transform.ChartToScreen(new ChartPoint(formation.EndTime, (decimal)formation.UpperPriceAt(formation.EndIndex)));
        
        var supStartScreen = transform.ChartToScreen(new ChartPoint(formation.StartTime, (decimal)formation.LowerPriceAt(formation.StartIndex)));
        var supEndScreen = transform.ChartToScreen(new ChartPoint(formation.EndTime, (decimal)formation.LowerPriceAt(formation.EndIndex)));

        // 2. Draw Fill Area (Polygon between the 4 points)
        using (var fillPaint = new SKPaint
        {
            Color = AppTheme.GeometricFormationFill,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            using var path = new SKPath();
            path.MoveTo((float)resStartScreen.X, (float)resStartScreen.Y);
            path.LineTo((float)resEndScreen.X, (float)resEndScreen.Y);
            path.LineTo((float)supEndScreen.X, (float)supEndScreen.Y);
            path.LineTo((float)supStartScreen.X, (float)supStartScreen.Y);
            path.Close();
            canvas.DrawPath(path, fillPaint);
        }

        // 3. Draw Resistance Line (Dashed Red)
        using (var resPaint = new SKPaint
        {
            Color = AppTheme.GeometricResistanceLine,
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
        })
        {
            canvas.DrawLine((float)resStartScreen.X, (float)resStartScreen.Y, (float)resEndScreen.X, (float)resEndScreen.Y, resPaint);
        }

        // 4. Draw Support Line (Dashed Green)
        using (var supPaint = new SKPaint
        {
            Color = AppTheme.GeometricSupportLine,
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
        })
        {
            canvas.DrawLine((float)supStartScreen.X, (float)supStartScreen.Y, (float)supEndScreen.X, (float)supEndScreen.Y, supPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        // The "red dot" (Anchor point) hit test
        if (Points.Count > 0)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            if (Math.Abs(p1.X - screenPoint.X) <= tolerance && Math.Abs(p1.Y - screenPoint.Y) <= tolerance)
            {
                return true;
            }
        }
        
        if (Formations == null || Formations.Count == 0) return false;

        // A full polygon hit-test or distance to line segments could be implemented here.
        // For simplicity, we just return true if it's within the bounding box of any formation.
        foreach(var formation in Formations)
        {
            if (!ShouldRender(formation.Type)) continue;
            
            var resStart = transform.ChartToScreen(new ChartPoint(formation.StartTime, (decimal)formation.UpperPriceAt(formation.StartIndex)));
            var supEnd = transform.ChartToScreen(new ChartPoint(formation.EndTime, (decimal)formation.LowerPriceAt(formation.EndIndex)));
            
            double minX = Math.Min(resStart.X, supEnd.X);
            double maxX = Math.Max(resStart.X, supEnd.X);
            double minY = Math.Min(resStart.Y, supEnd.Y);
            double maxY = Math.Max(resStart.Y, supEnd.Y);
            
            if (screenPoint.X >= minX - tolerance && screenPoint.X <= maxX + tolerance &&
                screenPoint.Y >= minY - tolerance && screenPoint.Y <= maxY + tolerance)
            {
                return true;
            }
        }

        return false;
    }

    public void Recalculate(IEnumerable<StockAnalyzer.Core.Models.CandleData> candles)
    {
        if (Points.Count == 0 || candles == null) return;
        
        try
        {
            var candleDataList = candles
                .Where(c => c.Timestamp >= Points[0].Time)
                .ToList();
                
            var thresholds = ZigZagThreshold.HasValue ? new[] { ZigZagThreshold.Value } : null;
            var formations = StockAnalyzer.Core.Models.GeometricPattern.GeometricPatternDetector.Detect(candleDataList, thresholds);
            Formations = formations;
        }
        catch
        {
            // Ignore detection errors, Formations might be empty.
            if (Formations == null)
            {
                Formations = Array.Empty<StockAnalyzer.Core.Models.GeometricPattern.DetectedFormation>();
            }
        }
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        // Translate the anchor point
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
        
        // Re-detection happens via Recalculate called by the controller
    }
    
    private bool ShouldRender(GeometricFormationType type)
    {
        switch (type)
        {
            case GeometricFormationType.HorizontalBox:
            case GeometricFormationType.AscendingChannel:
            case GeometricFormationType.DescendingChannel:
                return ShowChannels;
                
            case GeometricFormationType.SymmetricalTriangle:
            case GeometricFormationType.AscendingTriangle:
            case GeometricFormationType.DescendingTriangle:
                return ShowTriangles;
                
            case GeometricFormationType.Megaphone:
                return ShowMegaphone;
                
            case GeometricFormationType.BullishFlag:
            case GeometricFormationType.BearishFlag:
            case GeometricFormationType.Pennant:
                return ShowPennantsAndFlags;
                
            case GeometricFormationType.RisingWedge:
            case GeometricFormationType.FallingWedge:
                return ShowWedges;
                
            default:
                return true;
        }
    }
}
