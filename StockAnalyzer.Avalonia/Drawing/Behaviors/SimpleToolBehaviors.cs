using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Simple Line Tools (DragToDraw 2-point) ---

public sealed class TrendLineBehavior : DragToDrawBehavior<TrendLineObject>
{
    protected override TrendLineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new TrendLineObject(p, p);
}

public sealed class RayBehavior : DragToDrawBehavior<RayObject>
{
    protected override RayObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new RayObject(p, p);
}

public sealed class AngleBehavior : DragToDrawBehavior<AngleObject>
{
    protected override AngleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new AngleObject(p, p);
}

public sealed class ArrowBehavior : DragToDrawBehavior<ArrowObject>
{
    protected override ArrowObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new ArrowObject(p, p) { Color = Colors.Green, Thickness = 1.0 };
}

public sealed class PriceLabelBehavior : DragToDrawBehavior<PriceLabelObject>
{
    protected override PriceLabelObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new PriceLabelObject(p, p) { Color = Colors.Green, Thickness = 1.0, FontSize = 12.0 };
}

public sealed class CalloutBehavior : DragToDrawBehavior<CalloutObject>
{
    protected override CalloutObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new CalloutObject(p, p) { Color = Colors.Green, Thickness = 1.0, FontSize = 14.0 };
}

// --- Shape Tools (DragToDraw 2-point) ---

public sealed class RectangleBehavior : DragToDrawBehavior<RectangleObject>
{
    protected override RectangleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new RectangleObject(p, p);
}

public sealed class EllipseBehavior : DragToDrawBehavior<EllipseObject>
{
    protected override EllipseObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new EllipseObject(p, p);
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
