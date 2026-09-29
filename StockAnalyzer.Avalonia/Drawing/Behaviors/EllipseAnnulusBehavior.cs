using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// 5-step interactive placement behavior for EllipseAnnulusObject.
/// Step 1: Click sets the outer bounding box start corner (Points[0]), drag updates the opposite
/// corner (Points[1]) — same convention as EllipseBehavior/EllipseArcBehavior.
/// Step 2: Click fixes the bounding box, drag places the start-angle control point on the outer
/// circumference (Points[2]).
/// Step 3: Click fixes the start angle, drag places the end-angle control point on the outer
/// circumference (Points[3]).
/// Step 4: Click fixes the end angle, drag places the inner-radius control point (Points[4]) and
/// finishes on the next click.
/// </summary>
public sealed class EllipseAnnulusBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 5;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
        => new EllipseAnnulusObject(chartPoint, chartPoint, chartPoint, chartPoint, chartPoint);

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is not EllipseAnnulusObject ring || ring.Points.Count < 5) return;

        if (drawingStep <= 1)
        {
            ring.Points[1] = chartPoint;
        }
        else if (drawingStep == 2)
        {
            ring.Points[2] = chartPoint;
        }
        else if (drawingStep == 3)
        {
            ring.Points[3] = chartPoint;
        }
        else if (drawingStep == 4)
        {
            ring.Points[4] = chartPoint;
        }
    }
}
