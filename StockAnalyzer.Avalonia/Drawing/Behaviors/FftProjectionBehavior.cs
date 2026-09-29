using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class FftProjectionBehavior : TwoClickBehavior<FftProjectionObject>
{
    protected override FftProjectionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new FftProjectionObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}