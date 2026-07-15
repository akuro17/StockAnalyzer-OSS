using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Avalonia.Drawing;

public class RegressionTrendObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.RegressionTrend;

    // Start and End define the Time Range. Price is ignored (calculated).
    // However, to support dragging handles, we store ChartPoint.
    // Price in Points[0]/[1] can be the regression value at that time?
    public List<ChartPoint> Points { get; private set; }

    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }
    
    // Regression State
    private RegressionService.RegressionResult _result;
    private DateTime _start;
    private DateTime _end;

    public RegressionTrendObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
        _start = start.Time;
        _end = end.Time;
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        // 1. Filter candles in range [Points[0].Time, Points[1].Time]
        var p1Time = Points[0].Time;
        var p2Time = Points[1].Time;

        var start = p1Time < p2Time ? p1Time : p2Time;
        var end = p1Time < p2Time ? p2Time : p1Time;

        _start = start;
        _end = end;

        var range = candles.Where(c => c.Timestamp >= start && c.Timestamp <= end).OrderBy(c => c.Timestamp).ToList();

        // 2. Calculate Regression
        var service = new RegressionService();
        _result = service.Calculate(range);
        
        // 3. Update Points Y values to match Regression Start/End for visual consistent handles?
        // Or keep user Y?
        // Usually Regression Trend is auto-y. Handles shouldn't drag Y. 
        // Handles only drag TIME.
        // But if I use generic Handle logic, user might drag Y.
        // I should ignore Y drag for this object or snap Y.
        // For now, let's just store the calc result.
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (!_result.IsValid || _result.Count < 2) return;

        // Render 3 Lines: Center, +2SD, -2SD
        // Start and End X in Screen Coordinates
        // To draw correctly we need the Screen Points corresponding to Index 0 and Index (Count-1) of the REGRESSION range.
        
        // Problem: _result is 0-based index relative to the RANGE.
        // We need to map Range Start Time to Screen.
        
        var startPt = new ChartPoint(_start, 0); // Price 0 placeholder
        var endPt = new ChartPoint(_end, 0);

        var s1 = transform.ChartToScreen(startPt);
        var s2 = transform.ChartToScreen(endPt);

        // Calculate Y in Screen Space?
        // NO. Regression is in Price (Y) vs Index (X).
        // Regression Y = mx + c.
        // Value At Start (x=0) = Intercept.
        // Value At End (x=Count-1) = Slope*(Count-1) + Intercept.
        
        decimal yAnalysisStart = _result.GetValueAt(0);
        decimal yAnalysisEnd = _result.GetValueAt(_result.Count - 1);

        // Convert these Prices to Screen Y
        var pStart = new ChartPoint(_start, yAnalysisStart);
        var pEnd = new ChartPoint(_end, yAnalysisEnd);
        
        var screenStart = transform.ChartToScreen(pStart);
        var screenEnd = transform.ChartToScreen(pEnd);
        
        // Adjust Screen X. s1.X should match screenStart.X
        
        // Center Line
        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true
        };
        canvas.DrawLine((float)screenStart.X, (float)screenStart.Y, (float)screenEnd.X, (float)screenEnd.Y, paint);
        
        // StdDev Bands
        decimal std = _result.StdDev * 2;
        
        var pStartUpper = new ChartPoint(_start, yAnalysisStart + std);
        var pEndUpper = new ChartPoint(_end, yAnalysisEnd + std);
        var sStartUpper = transform.ChartToScreen(pStartUpper);
        var sEndUpper = transform.ChartToScreen(pEndUpper);
        
        var pStartLower = new ChartPoint(_start, yAnalysisStart - std);
        var pEndLower = new ChartPoint(_end, yAnalysisEnd - std);
        var sStartLower = transform.ChartToScreen(pStartLower);
        var sEndLower = transform.ChartToScreen(pEndLower);
        
        using var bandPaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = 1,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
        };
        
        canvas.DrawLine((float)sStartUpper.X, (float)sStartUpper.Y, (float)sEndUpper.X, (float)sEndUpper.Y, bandPaint);
        canvas.DrawLine((float)sStartLower.X, (float)sStartLower.Y, (float)sEndLower.X, (float)sEndLower.Y, bandPaint);

        // Fill
        using var path = new SKPath();
        path.MoveTo((float)sStartUpper.X, (float)sStartUpper.Y);
        path.LineTo((float)sEndUpper.X, (float)sEndUpper.Y);
        path.LineTo((float)sEndLower.X, (float)sEndLower.Y);
        path.LineTo((float)sStartLower.X, (float)sStartLower.Y);
        path.Close();
        
        using var fillPaint = new SKPaint
        {
            Color = new SKColor(SkiaColor.Red, SkiaColor.Green, SkiaColor.Blue, 30),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawPath(path, fillPaint);
        
        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)screenStart.X, (float)screenStart.Y, 5, selPaint);
            canvas.DrawCircle((float)screenEnd.X, (float)screenEnd.Y, 5, selPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (!_result.IsValid) return false;
        
        // Similar bounding box + path contains logic
         // For simplicity, just check distance to Center Line?
         // Or check if inside Band?
         
         // We can reconstruct the polygon locally and test
        decimal yAnalysisStart = _result.Slope * 0 + _result.Intercept;
        decimal yAnalysisEnd = _result.Slope * (_result.Count - 1) + _result.Intercept;
        decimal std = _result.StdDev * 2;
        
        var pStartUpper = new ChartPoint(_start, yAnalysisStart + std);
        var pEndUpper = new ChartPoint(_end, yAnalysisEnd + std);
        var pStartLower = new ChartPoint(_start, yAnalysisStart - std);
        var pEndLower = new ChartPoint(_end, yAnalysisEnd - std);

        var s1 = transform.ChartToScreen(pStartUpper);
        var s2 = transform.ChartToScreen(pEndUpper);
        var s3 = transform.ChartToScreen(pEndLower);
        var s4 = transform.ChartToScreen(pStartLower);
        
        using var path = new SKPath();
        path.MoveTo((float)s1.X, (float)s1.Y);
        path.LineTo((float)s2.X, (float)s2.Y);
        path.LineTo((float)s3.X, (float)s3.Y);
        path.LineTo((float)s4.X, (float)s4.Y);
        path.Close();
        
        return path.Contains((float)screenPoint.X, (float)screenPoint.Y);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        // Translate start/end times
        // Ignore priceDelta as auto-calculated
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price);
        }
        // NOTE: Must trigger Recalculate externally after translate!
    }
}
