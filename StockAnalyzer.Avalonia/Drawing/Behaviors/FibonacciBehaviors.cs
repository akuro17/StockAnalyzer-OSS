using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Fibonacci TwoClick (2-point) ---

public sealed class FibRetracementBehavior : TwoClickBehavior<FibonacciRetracementObject>
{
    protected override FibonacciRetracementObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciRetracementObject(p, p);
}

public sealed class FibTimeZoneBehavior : TwoClickBehavior<FibonacciTimeZoneObject>
{
    protected override FibonacciTimeZoneObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciTimeZoneObject(p, p);
}

public sealed class FibArcBehavior : TwoClickBehavior<FibonacciArcObject>
{
    protected override FibonacciArcObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciArcObject(p, p);
}

public sealed class FibCircleBehavior : TwoClickBehavior<FibonacciCircleObject>
{
    protected override FibonacciCircleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciCircleObject(p, p);
}

public sealed class FibFanBehavior : TwoClickBehavior<FibonacciFanObject>
{
    protected override FibonacciFanObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciFanObject(p, p);
}

public sealed class FibSpiralBehavior : TwoClickBehavior<FibonacciSpiralObject>
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

public sealed class FibEllipseBehavior : ThreePointBehavior<FibonacciEllipseObject>
{
    protected override FibonacciEllipseObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FibonacciEllipseObject(p, p, p);
}

