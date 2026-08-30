using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

public sealed class KalmanFilterProjectionBehavior : TwoClickBehavior<KalmanFilterProjectionObject>
{
    protected override KalmanFilterProjectionObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
    {
        var obj = new KalmanFilterProjectionObject();
        obj.Points.Add(p);
        obj.Points.Add(p);
        return obj;
    }
}
