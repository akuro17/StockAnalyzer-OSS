using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Behavior for geometric pattern tool.
/// It detects geometric patterns using the current chart data and immediately
/// creates a GeometricPatternObject upon a single click.
/// </summary>
public sealed class GeometricPatternBehavior : IDrawingToolBehavior
{
    // One click triggers the creation of the object based on the whole visible chart context.
    // However, we want to allow the user to select the starting point / anchor point for the object itself.
    // We will make it a 2-point step to define a rough start/end anchor OR snap to the detected patterns.
    // Actually, pattern detection usually acts globally or over an extracted segment.
    // Let's implement it like DtwProjectionBehavior which is click-based.
    
    // Instead of drawing, a single click will calculate and instantiate the object.
    
    // 1 step: click to calculate and place the tool.
    public int RequiredSteps => 1; 

    // Finishes on the click down (not drag release).
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        var patternObj = new GeometricPatternObject(chartPoint);

        if (candles != null)
        {
            try
            {
                // Filter candles to only analyze data on or after the anchor point (clicked point)
                var candleDataList = candles
                    .Where(c => c.Timestamp >= chartPoint.Time)
                    .Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume))
                    .ToList();
                
                // Detect patterns using multi-scale thresholds. patternObj.ZigZagThreshold is ignored for detection
                // as the detector now automatically extracts and merges macro and micro patterns.
                var formations = GeometricPatternDetector.Detect(candleDataList, null);
                
                if (formations != null && formations.Count > 0)
                {
                    patternObj.Formations = formations;
                }
            }
            catch
            {
                // Ignore detection errors, the object will just have empty Formations.
            }
        }

        return patternObj;
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        // One click tool, no update needed while drawing.
        if (obj is GeometricPatternObject go)
        {
            go.Points[0] = chartPoint;
        }
    }
}
