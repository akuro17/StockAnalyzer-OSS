using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class HoughMagneticLineBehavior : TwoClickBehavior<HoughMagneticLineObject>
{
    protected override HoughMagneticLineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new HoughMagneticLineObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
