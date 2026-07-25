using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Behavior for the Target Price Projection tool.
/// Uses ThreePointBehavior for 3-click anchor placement (P1, P2, P3).
/// </summary>
public sealed class TargetPriceProjectionBehavior : ThreePointBehavior<TargetPriceProjectionObject>
{
    protected override TargetPriceProjectionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new TargetPriceProjectionObject(p, p, p);
}
