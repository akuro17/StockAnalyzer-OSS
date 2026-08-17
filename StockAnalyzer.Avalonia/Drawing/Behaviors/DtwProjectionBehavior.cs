using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class DtwProjectionBehavior : DragToDrawBehavior<DtwProjectionObject>
{
    protected override DtwProjectionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        return new DtwProjectionObject
        {
            Points = { p, p }, // Initial start and end points
            Color = Colors.Green
        };
    }
}
