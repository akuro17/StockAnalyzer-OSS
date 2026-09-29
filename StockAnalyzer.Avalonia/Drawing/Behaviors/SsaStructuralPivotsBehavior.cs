using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class SsaStructuralPivotsBehavior : TwoClickBehavior<SsaStructuralPivotsObject>
{
    protected override SsaStructuralPivotsObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new SsaStructuralPivotsObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
