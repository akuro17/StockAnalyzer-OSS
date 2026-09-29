using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class SsaAnomalyHighlightBehavior : TwoClickBehavior<SsaAnomalyHighlightObject>
{
    protected override SsaAnomalyHighlightObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new SsaAnomalyHighlightObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
