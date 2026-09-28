using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class SsaProjectedTargetsBehavior : TwoClickBehavior<SsaProjectedTargetsObject>
{
    protected override SsaProjectedTargetsObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new SsaProjectedTargetsObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
