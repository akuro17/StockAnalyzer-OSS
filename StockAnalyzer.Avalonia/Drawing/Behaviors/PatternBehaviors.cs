using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Multi-Step Pattern Tools ---

public sealed class ParallelChannelBehavior : ThreePointBehavior<ParallelChannelObject>
{
    protected override ParallelChannelObject CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? _)
        => new ParallelChannelObject(chartPoint, chartPoint, chartPoint);
}

public sealed class TriangleBehavior : ThreePointBehavior<TriangleObject>
{
    protected override TriangleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new TriangleObject(p, p, p);
}

public sealed class PitchforkBehavior : ThreePointBehavior<PitchforkObject>
{
    protected override PitchforkObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new PitchforkObject(p, p, p);
}

/// <summary>
/// Harmonic pattern: 5-point multi-step tool (XABCD).
/// </summary>


/// <summary>
/// Polyline and ElliottWave: variable-point tool.
/// Points are added dynamically on each click.
/// </summary>
public sealed class PolylineBehavior : IDrawingToolBehavior
{
    private readonly PolylineLabelType _labelType;

    public PolylineBehavior(PolylineLabelType labelType = PolylineLabelType.None)
    {
        _labelType = labelType;
    }

    public int RequiredSteps => 0; // Variable
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        var points = new System.Collections.Generic.List<ChartPoint> { chartPoint, chartPoint };
        return new PolylineObject(points) { LabelType = _labelType };
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is PolylineObject poly && poly.Points.Count > 0)
        {
            poly.Points[poly.Points.Count - 1] = chartPoint;
        }
    }
}

/// <summary>
/// CurveLineText: variable-point curved line with independent Top/Bottom path-following text.
/// Points are added dynamically on each click, identical wiring to PolylineBehavior.
/// </summary>
public sealed class CurveLineTextBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 0; // Variable
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        var points = new System.Collections.Generic.List<ChartPoint> { chartPoint, chartPoint };
        return new CurveLineTextObject(points) { Color = DrawingThemeContext.DefaultColor, Thickness = DrawingThemeContext.DefaultStrokeThickness };
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is CurveLineTextObject curve && curve.Points.Count > 0)
        {
            curve.Points[curve.Points.Count - 1] = chartPoint;
        }
    }
}

/// <summary>
/// Bar Pattern: special multi-step tool with data extraction at step 2.
/// </summary>
public sealed class BarPatternBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 3; // P1, P2 (extract data), P3 (anchor placement)
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        var barPattern = new BarPatternObject(chartPoint);
        barPattern.Points.Add(chartPoint); // Placeholder P2
        return barPattern;
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is BarPatternObject barPattern && barPattern.Points.Count > 0)
        {
            barPattern.Points[barPattern.Points.Count - 1] = chartPoint;
        }
    }
}
