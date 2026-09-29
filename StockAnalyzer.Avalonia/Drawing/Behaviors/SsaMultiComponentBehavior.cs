using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class SsaMultiComponentBehavior : TwoClickBehavior<SsaMultiComponentObject>
{
    protected override SsaMultiComponentObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new SsaMultiComponentObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
