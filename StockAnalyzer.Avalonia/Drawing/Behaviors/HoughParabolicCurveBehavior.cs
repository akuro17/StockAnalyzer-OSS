using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class HoughParabolicCurveBehavior : TwoClickBehavior<HoughParabolicCurveObject>
{
    protected override HoughParabolicCurveObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new HoughParabolicCurveObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
