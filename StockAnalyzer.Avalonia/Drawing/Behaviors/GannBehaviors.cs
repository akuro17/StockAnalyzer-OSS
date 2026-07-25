using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Gann DragToDraw (2-point) ---

public sealed class GannFanBehavior : DragToDrawBehavior<GannFanObject>
{
    protected override GannFanObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new GannFanObject(p, p);
}

public sealed class GannBoxBehavior : DragToDrawBehavior<GannBoxObject>
{
    protected override GannBoxObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new GannBoxObject(p, p);
}

public sealed class GannSquareBehavior : DragToDrawBehavior<GannSquareObject>
{
    protected override GannSquareObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new GannSquareObject(p, p);
}

public sealed class GannGridBehavior : DragToDrawBehavior<GannGridObject>
{
    protected override GannGridObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new GannGridObject(p, p);
}

public sealed class GannSquare144Behavior : DragToDrawBehavior<GannSquare144Object>
{
    protected override GannSquare144Object CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new GannSquare144Object(p, p);
}

public sealed class GannWheelBehavior : DragToDrawBehavior<GannWheelObject>
{
    protected override GannWheelObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new GannWheelObject(p, p);
}
