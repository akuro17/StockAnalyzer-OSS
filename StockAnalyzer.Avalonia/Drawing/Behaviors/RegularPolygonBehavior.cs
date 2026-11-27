using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>Placement of the regular polygon: click 1 sets the centre, moving shows vertex 0 (the radius and the rotation), click 2 fixes it.</summary>
public sealed class RegularPolygonBehavior : TwoClickBehavior<RegularPolygonObject>
{
    protected override RegularPolygonObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new(p, p);
}
