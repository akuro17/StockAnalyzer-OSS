using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Simple Line Tools (TwoClick 2-point) ---

public sealed class TrendLineBehavior : TwoClickBehavior<TrendLineObject>
{
    protected override TrendLineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new TrendLineObject(p, p);
}

public sealed class RayBehavior : TwoClickBehavior<RayObject>
{
    protected override RayObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new RayObject(p, p);
}

public sealed class AngleBehavior : TwoClickBehavior<AngleObject>
{
    protected override AngleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new AngleObject(p, p);
}

public sealed class ArrowBehavior : TwoClickBehavior<ArrowObject>
{
    protected override ArrowObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new ArrowObject(p, p) { Color = DrawingThemeContext.DefaultColor, Thickness = DrawingThemeContext.DefaultStrokeThickness };
}

public sealed class PriceLabelBehavior : TwoClickBehavior<PriceLabelObject>
{
    protected override PriceLabelObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new PriceLabelObject(p, p) { Color = DrawingThemeContext.DefaultColor, Thickness = DrawingThemeContext.DefaultStrokeThickness, FontSize = DrawingThemeContext.DrawingFontSize };
}

public sealed class CalloutBehavior : TwoClickBehavior<CalloutObject>
{
    protected override CalloutObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new CalloutObject(p, p) { Color = DrawingThemeContext.DefaultColor, Thickness = DrawingThemeContext.DefaultStrokeThickness, FontSize = DrawingThemeContext.DrawingFontSize };
}

public sealed class LineTextBehavior : TwoClickBehavior<LineTextObject>
{
    protected override LineTextObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new LineTextObject(p, p) { Color = DrawingThemeContext.DefaultColor, Thickness = DrawingThemeContext.DefaultStrokeThickness };
}

// --- Shape Tools (TwoClick 2-point) ---

public sealed class RectangleBehavior : TwoClickBehavior<RectangleObject>
{
    protected override RectangleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new RectangleObject(p, p);
}

public sealed class EllipseBehavior : TwoClickBehavior<EllipseObject>
{
    protected override EllipseObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new EllipseObject(p, p);

    /// <summary>
    /// Keeps the circumference control points (Points[2]/[3]) tracking the boundary point opposite the
    /// corner, live, throughout the 2-click placement drag. Without this, they stay frozen at the
    /// constructor's default — computed from the degenerate center==corner state at the very first
    /// click, before the user has dragged anywhere, which collapses to the center itself rather than a
    /// point opposite the (not-yet-placed) corner. Since Points[0]/[2]/[3] never move relative to each
    /// other afterward, the two circumference handles would end up rendered at the same angle as the
    /// corner handle once placement finishes, defeating EllipseObject's opposite-side default entirely.
    /// </summary>
    protected override void OnPointUpdated(EllipseObject obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        if (obj.Points.Count < 4) return;
        var boundaryPoint = EllipseArcGeometry.ComputeChartSpaceOppositeBoundaryPoint(obj.Points[0], obj.Points[1]);
        obj.Points[2] = boundaryPoint;
        obj.Points[3] = boundaryPoint;
    }
}

// --- Click-to-Place Tools ---

public sealed class HorizontalLineBehavior : ClickToPlaceBehavior<HorizontalLineObject>
{
    protected override HorizontalLineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new HorizontalLineObject(p);
}

public sealed class VerticalLineBehavior : ClickToPlaceBehavior<VerticalLineObject>
{
    protected override VerticalLineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new VerticalLineObject(p);
}

public sealed class GannSquareOfNineBehavior : ClickToPlaceBehavior<GannSquareOfNineObject>
{
    protected override GannSquareOfNineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new GannSquareOfNineObject(p);
}

public sealed class AnchoredVwapBehavior : ClickToPlaceBehavior<AnchoredVwapObject>
{
    protected override AnchoredVwapObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? candles)
    {
        var avwap = new AnchoredVwapObject(p);
        if (candles != null) avwap.Recalculate(candles);
        return avwap;
    }
}
