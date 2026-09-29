using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class HoughResonantFanBehavior : TwoClickBehavior<HoughResonantFanObject>
{
    protected override HoughResonantFanObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new HoughResonantFanObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
