using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Cycle Tools (DragToDraw 2-point) ---

public sealed class CyclicLinesBehavior : DragToDrawBehavior<CyclicLinesObject>
{
    protected override CyclicLinesObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new CyclicLinesObject(p, p);
}

public sealed class SineLineBehavior : DragToDrawBehavior<SineLineObject>
{
    protected override SineLineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new SineLineObject(p, p);
}

public sealed class TimeCyclesBehavior : DragToDrawBehavior<TimeCyclesObject>
{
    protected override TimeCyclesObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new TimeCyclesObject(p, p);
}
