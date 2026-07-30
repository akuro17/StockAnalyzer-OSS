using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Defines the behavior for a specific drawing tool.
/// Each implementation encapsulates creation, point update, and completion logic
/// for a single tool type, replacing the monolithic switch/if-else chains
/// in ChartInteractionController.
/// </summary>
public interface IDrawingToolBehavior
{
    /// <summary>
    /// Creates a new chart object at the given chart point.
    /// For multi-step tools, this creates the initial object with placeholder points.
    /// </summary>
    /// <param name="chartPoint">The starting chart coordinate.</param>
    /// <param name="candles">Optional candle data for tools that need it (e.g., RegressionTrend).</param>
    /// <returns>A new chart object instance.</returns>
    IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null);

    /// <summary>
    /// Updates the drawing object's points during mouse movement.
    /// Called while the user is actively drawing/placing the object.
    /// </summary>
    /// <param name="obj">The object being drawn.</param>
    /// <param name="drawingStep">Current step for multi-step tools (0-based).</param>
    /// <param name="chartPoint">The current chart coordinate.</param>
    /// <param name="candles">Optional candle data for recalculation.</param>
    void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null);

    /// <summary>
    /// Number of steps required to complete the drawing.
    /// 1 = click-to-place, 2 = drag-to-draw (two points), 3+ = multi-step.
    /// 0 = variable (e.g., Polyline).
    /// </summary>
    int RequiredSteps { get; }

    /// <summary>
    /// Whether the drawing should be finalized when the pointer is released.
    /// True for drag-to-draw tools (Rectangle, TrendLine, etc.).
    /// False for click-based tools or multi-step tools that advance on click.
    /// </summary>
    bool FinishesOnRelease { get; }
}
