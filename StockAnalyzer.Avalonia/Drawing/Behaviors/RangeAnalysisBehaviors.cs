using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Placement of the range-analysis tools (<see cref="IRangeAnalysisDrawing"/>): click 1 sets the first range edge, moving shows the second edge, click 2 fixes it.
/// The heavy calculation is NOT run while the edge follows the pointer: it runs when the placement has finished (ChartViewModel.OnDrawingFinished). Every tool of the family is one
/// thin subclass that only names its object.
/// </summary>
public abstract class RangeAnalysisBehavior<T> : TwoClickBehavior<T> where T : RangeAnalysisObjectBase
{
    // The base class already does everything a range tool needs while the pointer moves: it moves Points[1]. OnPointUpdated stays empty on purpose (no live recalculation).
}

public sealed class GarchVolatilityConeBehavior : RangeAnalysisBehavior<GarchVolatilityConeObject>
{
    protected override GarchVolatilityConeObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}

public sealed class PolynomialRegressionProjectionBehavior : RangeAnalysisBehavior<PolynomialRegressionProjectionObject>
{
    protected override PolynomialRegressionProjectionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}

public sealed class NonlinearRegressionCurveBehavior : RangeAnalysisBehavior<NonlinearRegressionCurveObject>
{
    protected override NonlinearRegressionCurveObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}

public sealed class RobustRegressionTrendBehavior : RangeAnalysisBehavior<RobustRegressionTrendObject>
{
    protected override RobustRegressionTrendObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}

public sealed class WeightedRegressionTrendBehavior : RangeAnalysisBehavior<WeightedRegressionTrendObject>
{
    protected override WeightedRegressionTrendObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}

public sealed class RangeRegimeDiagnosticCardBehavior : RangeAnalysisBehavior<RangeRegimeDiagnosticCardObject>
{
    protected override RangeRegimeDiagnosticCardObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}

public sealed class BootstrapConfidenceBandBehavior : RangeAnalysisBehavior<BootstrapConfidenceBandObject>
{
    protected override BootstrapConfidenceBandObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}

/// <summary>
/// Placement of the anchor-curve tools (PchipCurve, CubicSplineCurve): variable number of anchors, exactly like the polyline. Click 1 creates the object with the first anchor and a second one that
/// follows the pointer, every further click adds an anchor (<c>ChartInteractionController</c> calls <see cref="InterpolationCurveObjectBase.AddPoint"/>), a double click, Enter, Ctrl or Shift
/// finishes (the generic variable-shape commit removes the trailing preview anchor).
/// </summary>
public abstract class InterpolationCurveBehavior<T> : IDrawingToolBehavior where T : InterpolationCurveObjectBase
{
    public int RequiredSteps => 0; // variable

    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        // A newly placed curve follows the placement order and may loop; the class default (TimeSeries) is what a file without the setting keeps.
        T curve = Create(new List<ChartPoint> { chartPoint, chartPoint });
        curve.CurveMode = InterpolationCurveMode.Path;
        return curve;
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is T curve && curve.Points.Count > 0)
        {
            curve.Points[curve.Points.Count - 1] = chartPoint;
        }
    }

    protected abstract T Create(List<ChartPoint> points);
}

public sealed class PchipCurveBehavior : InterpolationCurveBehavior<PchipCurveObject>
{
    protected override PchipCurveObject Create(List<ChartPoint> points) => new(points);
}

public sealed class CubicSplineCurveBehavior : InterpolationCurveBehavior<CubicSplineCurveObject>
{
    protected override CubicSplineCurveObject Create(List<ChartPoint> points) => new(points);
}
