using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class FrechetProjectionBehavior : TwoClickBehavior<FrechetProjectionObject>
{
    protected override FrechetProjectionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new FrechetProjectionObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
