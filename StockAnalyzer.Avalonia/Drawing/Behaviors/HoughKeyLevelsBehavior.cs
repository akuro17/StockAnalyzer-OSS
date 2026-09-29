using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class HoughKeyLevelsBehavior : TwoClickBehavior<HoughKeyLevelsObject>
{
    protected override HoughKeyLevelsObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new HoughKeyLevelsObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
