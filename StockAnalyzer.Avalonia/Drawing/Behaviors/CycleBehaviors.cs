using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Cycle Tools (TwoClick 2-point) ---

public sealed class CyclicLinesBehavior : TwoClickBehavior<CyclicLinesObject>
{
    protected override CyclicLinesObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new CyclicLinesObject(p, p);
}

public sealed class SineLineBehavior : TwoClickBehavior<SineLineObject>
{
    protected override SineLineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new SineLineObject(p, p);
}

public sealed class TimeCyclesBehavior : TwoClickBehavior<TimeCyclesObject>
{
    protected override TimeCyclesObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new TimeCyclesObject(p, p);
}
