namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

/// <summary>
/// 3-Step interactive placement behavior for CurveTrendObject (Bézier trend curve).
/// Step 1: Click sets P0 (start), drag updates P1 (end) with P2 initialized to the midpoint.
/// Step 2: Click fixes P1 (end), drag updates P2 (curvature control point).
/// Step 3: Click fixes P2 (curvature) and finishes tool.
/// </summary>
public sealed class CurveTrendBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 3;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        return new CurveTrendObject(chartPoint, chartPoint, chartPoint);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is not CurveTrendObject trend || trend.Points.Count < 3) return;

        if (drawingStep <= 1)
        {
            // Step 1: Dragging P1 (endpoint)
            trend.Points[1] = chartPoint;
            long midTicks = trend.Points[0].Time.Ticks + (chartPoint.Time.Ticks - trend.Points[0].Time.Ticks) / 2;
            decimal midPrice = (trend.Points[0].Price + chartPoint.Price) / 2m;
            trend.Points[2] = new ChartPoint(new DateTime(midTicks), midPrice);
        }
        else if (drawingStep == 2)
        {
            // Step 2: Dragging P2 (curvature control point)
            trend.Points[2] = chartPoint;
        }
    }
}

/// <summary>
/// 4-Step interactive placement behavior for CurveChannelObject (Bézier curve channel).
/// Step 1: Click sets P0 (start), drag updates P1 (end) with P2 & P3 initialized to the midpoint.
/// Step 2: Click fixes P1 (end), drag updates P2 & P3 (curvature control point).
/// Step 3: Click fixes P2 (curvature), drag updates P3 (channel offset/width).
/// Step 4: Click fixes P3 and finishes tool.
/// </summary>
public sealed class CurveChannelBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 4;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        return new CurveChannelObject(chartPoint, chartPoint, chartPoint, chartPoint);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is not CurveChannelObject channel || channel.Points.Count < 4) return;

        if (drawingStep <= 1)
        {
            // Step 1: Dragging P1 (endpoint)
            channel.Points[1] = chartPoint;
            long midTicks = channel.Points[0].Time.Ticks + (chartPoint.Time.Ticks - channel.Points[0].Time.Ticks) / 2;
            decimal midPrice = (channel.Points[0].Price + chartPoint.Price) / 2m;
            var midPt = new ChartPoint(new DateTime(midTicks), midPrice);
            channel.Points[2] = midPt;
            channel.Points[3] = midPt;
        }
        else if (drawingStep == 2)
        {
            // Step 2: Dragging P2 (curvature control point)
            channel.Points[2] = chartPoint;
            channel.Points[3] = chartPoint;
        }
        else if (drawingStep == 3)
        {
            // Step 3: Dragging P3 (offset point)
            channel.Points[3] = chartPoint;
        }
    }
}
