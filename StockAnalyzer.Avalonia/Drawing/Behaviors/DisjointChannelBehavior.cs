using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// 4-click placement of a <see cref="DisjointChannelObject"/>: P1, P2 (edge 1), then P3, P4 (edge 2). Points not yet fixed follow the cursor.
/// Step <see cref="SecondPointStep"/>: P2 (and the not yet placed P3, P4) follow the cursor. Step <see cref="ThirdPointStep"/>: P3 (and P4) follow.
/// Any later step: P4 follows. The last click finishes; Esc before that cancels (generic multi-step handling of the interaction controller).
/// </summary>
public sealed class DisjointChannelBehavior : IDrawingToolBehavior
{
    /// <summary>Drawing step while the second point is chosen (the interaction controller starts at 1 after the first click).</summary>
    private const int SecondPointStep = 1;

    /// <summary>Drawing step while the third point is chosen.</summary>
    private const int ThirdPointStep = 2;

    private const int SecondPointIndex = 1;
    private const int ThirdPointIndex = 2;
    private const int FourthPointIndex = 3;

    public int RequiredSteps => DisjointChannelObject.PointCount;
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
        => new DisjointChannelObject(chartPoint, chartPoint, chartPoint, chartPoint);

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is not DisjointChannelObject channel || channel.Points.Count < DisjointChannelObject.PointCount) return;

        if (drawingStep <= SecondPointStep)
        {
            channel.Points[SecondPointIndex] = chartPoint;
            channel.Points[ThirdPointIndex] = chartPoint;
            channel.Points[FourthPointIndex] = chartPoint;
        }
        else if (drawingStep == ThirdPointStep)
        {
            channel.Points[ThirdPointIndex] = chartPoint;
            channel.Points[FourthPointIndex] = chartPoint;
        }
        else
        {
            channel.Points[FourthPointIndex] = chartPoint;
        }
    }
}
