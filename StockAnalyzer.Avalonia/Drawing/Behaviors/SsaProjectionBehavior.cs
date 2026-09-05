using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class SsaProjectionBehavior : TwoClickBehavior<SsaProjectionObject>
{
    protected override SsaProjectionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new SsaProjectionObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
