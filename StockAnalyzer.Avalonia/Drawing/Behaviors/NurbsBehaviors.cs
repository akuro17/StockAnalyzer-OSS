namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

using System.Collections.Generic;
using StockAnalyzer.Core.Models;

/// <summary>
/// Variable-point interactive behavior for NurbsTrendCurveObject.
/// Clicking adds control points dynamically, mouse move updates the active control point,
/// and double-click or finish key completes placement.
/// </summary>
public sealed class NurbsTrendCurveBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 0; // Variable points
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        var points = new List<ChartPoint> { chartPoint, chartPoint };
        return new NurbsTrendCurveObject(points);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is NurbsTrendCurveObject curve && curve.Points.Count > 0)
        {
            curve.Points[curve.Points.Count - 1] = chartPoint;
        }
    }
}

/// <summary>
/// 2-Step interactive drag-to-draw behavior for NurbsConicObject.
/// Mouse down places Center, mouse drag updates radius, mouse up finishes placement.
/// </summary>
public sealed class NurbsConicBehavior : DragToDrawBehavior<NurbsConicObject>
{
    protected override NurbsConicObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new NurbsConicObject(p, p);
}

/// <summary>
/// 2-Step interactive drag-to-draw behavior for NurbsEllipseObject.
/// Mouse down places Center, mouse drag updates corner extent (Rx, Ry), mouse up finishes placement.
/// </summary>
public sealed class NurbsEllipseBehavior : DragToDrawBehavior<NurbsEllipseObject>
{
    protected override NurbsEllipseObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new NurbsEllipseObject(p, p);
}

/// <summary>
/// 3-Step interactive placement behavior for NurbsParabolaObject.
/// Step 1: Click sets P0 (start), drag updates P1 & P2.
/// Step 2: Click sets P1 (vertex/apex), drag updates P2 (end).
/// Step 3: Click fixes P2 and completes tool.
/// </summary>
public sealed class NurbsParabolaBehavior : ThreePointBehavior<NurbsParabolaObject>
{
    protected override NurbsParabolaObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new NurbsParabolaObject(p, p, p);
}

/// <summary>
/// 3-Step interactive placement behavior for NurbsHyperbolaObject.
/// Step 1: Click sets P0 (start), drag updates P1 & P2.
/// Step 2: Click sets P1 (asymptote intersection), drag updates P2 (end).
/// Step 3: Click fixes P2 and completes tool.
/// </summary>
public sealed class NurbsHyperbolaBehavior : ThreePointBehavior<NurbsHyperbolaObject>
{
    protected override NurbsHyperbolaObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new NurbsHyperbolaObject(p, p, p);
}
