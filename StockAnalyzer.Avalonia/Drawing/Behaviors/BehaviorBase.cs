using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Base behavior for tools that create a 2-point object via drag
/// (e.g., TrendLine, Rectangle, FibRetracement).
/// Click sets point 1, drag updates point 2, release finishes.
/// </summary>
/// <typeparam name="T">The concrete IChartObject type.</typeparam>
public abstract class DragToDrawBehavior<T> : IDrawingToolBehavior where T : IChartObject
{
    public int RequiredSteps => 2;
    public bool FinishesOnRelease => true;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        return CreateInstance(chartPoint, candles);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj.Points.Count > 1)
        {
            obj.Points[1] = chartPoint;
            OnPointUpdated((T)obj, chartPoint, candles);
        }
    }

    /// <summary>
    /// Factory method to create the concrete object instance with two identical initial points.
    /// </summary>
    protected abstract T CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles);

    /// <summary>
    /// Optional hook called after point update (e.g., for recalculation).
    /// Default implementation does nothing.
    /// </summary>
    protected virtual void OnPointUpdated(T obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles) { }
}

/// <summary>
/// Base behavior for tools that are placed with a single click
/// (e.g., HorizontalLine, VerticalLine, AnchoredVWAP).
/// </summary>
/// <typeparam name="T">The concrete IChartObject type.</typeparam>
public abstract class ClickToPlaceBehavior<T> : IDrawingToolBehavior where T : IChartObject
{
    public int RequiredSteps => 1;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        return CreateInstance(chartPoint, candles);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        // Click-to-place tools don't update during mouse move
    }

    /// <summary>
    /// Factory method to create the concrete object instance.
    /// </summary>
    protected abstract T CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles);
}

/// <summary>
/// Base behavior for tools requiring 2 clicks (e.g., GhostFeed).
/// Click 1 sets P0, moving updates P1, Click 2 fixes P1 and finishes.
/// </summary>
/// <typeparam name="T">The concrete IChartObject type.</typeparam>
public abstract class TwoClickBehavior<T> : IDrawingToolBehavior where T : IChartObject
{
    public int RequiredSteps => 2;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        return CreateInstance(chartPoint, candles);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        // For a 2-step tool with FinishesOnRelease=false, step starts at 1
        if (drawingStep == 1 && obj.Points.Count > 1)
        {
            obj.Points[1] = chartPoint;
            OnPointUpdated((T)obj, chartPoint, candles);
        }
    }

    protected abstract T CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles);
    
    protected virtual void OnPointUpdated(T obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles) { }
}

/// <summary>
/// Base behavior for tools requiring 3 clicks to define 3 anchor points
/// (e.g., ParallelChannel, Triangle, Pitchfork, FibExpansion, FibChannel).
/// Step 1: Click sets P0, drag updates P1 (and P2 mirrors P1).
/// Step 2: Click fixes P1, drag updates P2.
/// Step 3: Click finishes.
/// </summary>
/// <typeparam name="T">The concrete IChartObject type.</typeparam>
public abstract class ThreePointBehavior<T> : IDrawingToolBehavior where T : IChartObject
{
    public int RequiredSteps => 3;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        return CreateInstance(chartPoint, candles);
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj.Points.Count < 3) return;

        if (drawingStep == 1)
        {
            // Dragging P1; P2 mirrors P1 as placeholder
            obj.Points[1] = chartPoint;
            obj.Points[2] = chartPoint;
        }
        else if (drawingStep == 2)
        {
            // Dragging P2 independently
            obj.Points[2] = chartPoint;
        }
    }

    /// <summary>
    /// Factory method to create the concrete 3-point object.
    /// </summary>
    protected abstract T CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles);
}
