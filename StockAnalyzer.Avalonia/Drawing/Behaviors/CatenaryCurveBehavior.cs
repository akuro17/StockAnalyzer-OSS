using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// 3-Step placement behavior for CatenaryCurveObject:
/// Step 1: Click sets P0 (start), drag updates P1 (end), and P2 (sag point) is automatically placed at midpoint.
/// Step 2: Click fixes P1 (end), drag updates P2 (sag price) with Time locked at midpoint between P0 and P1.
/// Step 3: Click fixes P2 (sag point) and finishes tool.
/// </summary>
public sealed class CatenaryCurveBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 3;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        return new CatenaryCurveObject(chartPoint, chartPoint, chartPoint);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is not CatenaryCurveObject cat || cat.Points.Count < 3) return;

        if (drawingStep <= 1)
        {
            // Step 1: Dragging P1 (endpoint)
            cat.Points[1] = chartPoint;
            // Automatically place P2 (sag point) at the exact midpoint in time and price between P0 and P1
            long midTicks = (cat.Points[0].Time.Ticks + chartPoint.Time.Ticks) / 2;
            decimal midPrice = (cat.Points[0].Price + chartPoint.Price) / 2m;
            cat.Points[2] = new ChartPoint(new DateTime(midTicks), midPrice);
        }
        else if (drawingStep == 2)
        {
            // Step 2: Dragging P2 (sag point)
            // Lock P2.Time to the exact midpoint between P0 and P1
            long midTicks = (cat.Points[0].Time.Ticks + cat.Points[1].Time.Ticks) / 2;
            cat.Points[2] = new ChartPoint(new DateTime(midTicks), chartPoint.Price);
        }
    }
}
