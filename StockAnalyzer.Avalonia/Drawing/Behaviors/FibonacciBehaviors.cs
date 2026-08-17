using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Fibonacci DragToDraw (2-point) ---

public sealed class FibRetracementBehavior : DragToDrawBehavior<FibonacciRetracementObject>
{
    protected override FibonacciRetracementObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciRetracementObject(p, p);
}

public sealed class FibTimeZoneBehavior : DragToDrawBehavior<FibonacciTimeZoneObject>
{
    protected override FibonacciTimeZoneObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciTimeZoneObject(p, p);
}

public sealed class FibArcBehavior : DragToDrawBehavior<FibonacciArcObject>
{
    protected override FibonacciArcObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciArcObject(p, p);
}

public sealed class FibCircleBehavior : DragToDrawBehavior<FibonacciCircleObject>
{
    protected override FibonacciCircleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciCircleObject(p, p);
}

public sealed class FibFanBehavior : DragToDrawBehavior<FibonacciFanObject>
{
    protected override FibonacciFanObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciFanObject(p, p);
}

public sealed class FibSpiralBehavior : DragToDrawBehavior<FibonacciSpiralObject>
{
    protected override FibonacciSpiralObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciSpiralObject(p, p);
}

// --- Fibonacci MultiStep (3-point) ---

public sealed class FibExpansionBehavior : ThreePointBehavior<FibonacciExpansionObject>
{
    protected override FibonacciExpansionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciExpansionObject(p, p, p);
}

public sealed class FibChannelBehavior : ThreePointBehavior<FibonacciChannelObject>
{
    protected override FibonacciChannelObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciChannelObject(p, p, p);
}
