using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class AutoTimeCycleBehavior : TwoClickBehavior<AutoTimeCycleObject>
{
    protected override AutoTimeCycleObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new AutoTimeCycleObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
