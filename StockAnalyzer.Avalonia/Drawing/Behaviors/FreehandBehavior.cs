using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Behavior metadata for the Freehand tool.
/// Note: Runtime freehand gestures bypass UpdateNewShape and are governed directly by FreehandInputController.
/// </summary>
public sealed class FreehandBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 0; // Variable continuous gesture

    public bool FinishesOnRelease => true;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        var obj = new FreehandObject();
        obj.Points.Add(chartPoint);
        return obj;
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        // Freehand input flows through FreehandInputController without magnet snapping or point replacement.
        if (obj is FreehandObject freehand)
        {
            freehand.Points.Add(chartPoint);
        }
    }
}
