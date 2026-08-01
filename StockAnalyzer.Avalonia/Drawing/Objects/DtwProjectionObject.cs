using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class DtwProjectionObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.DtwProjection;
    
    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);
    
    // Core properties
    public Color Color { get; set; } = Colors.Orange;
    public double Thickness { get; set; } = 1.0;
    public double HandleSize { get; set; } = 4.0;
    public bool IsSelected { get; set; }
    
    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);

    // Projected path data populated by the ML service
    // Point: X = timestamp (ticks), Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Matched pattern location in the historical data (populated by ML search)
    // Stored as timestamps for coordinate-transform compatibility in the renderer.
    public DateTime? MatchedStartTime { get; set; }
    public DateTime? MatchedEndTime { get; set; }

    public DtwProjectionObject()
    {
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        var renderer = new StockAnalyzer.Avalonia.Drawing.Renderers.DtwProjectionRenderer();
        renderer.Render(canvas, this, transform, IsSelected);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        // Hit-test the boundary rectangle for selection
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        var rect = new global::Avalonia.Rect(p1, p2);
        
        // Expand rect by tolerance for easier selection
        rect = rect.Inflate(tolerance);

        return rect.Contains(screenPoint);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(
                Points[i].Time + timeDelta,
                Points[i].Price + priceDelta
            );
        }

        if (ProjectedPath != null)
        {
            var newPath = new List<StockAnalyzer.Core.Models.Point>();
            foreach (var p in ProjectedPath)
            {
                newPath.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            ProjectedPath = newPath;
        }
    }
}
