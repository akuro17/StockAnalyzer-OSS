using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class HoughAutoLinesBehavior : TwoClickBehavior<HoughAutoLinesObject>
{
    protected override HoughAutoLinesObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new HoughAutoLinesObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
