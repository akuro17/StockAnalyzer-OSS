using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class SsaDynamicEnvelopesBehavior : TwoClickBehavior<SsaDynamicEnvelopesObject>
{
    protected override SsaDynamicEnvelopesObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new SsaDynamicEnvelopesObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
