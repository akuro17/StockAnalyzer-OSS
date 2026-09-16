namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

using System.Collections.Generic;
using StockAnalyzer.Core.Models;

public sealed class GoldenSpiralBehavior : TwoClickBehavior<GoldenSpiralObject>
{
    protected override GoldenSpiralObject CreateInstance(ChartPoint point, IEnumerable<CoreCandleData>? _) => new(point, point);
}

public sealed class LogarithmicSpiralBehavior : TwoClickBehavior<LogarithmicSpiralObject>
{
    protected override LogarithmicSpiralObject CreateInstance(ChartPoint point, IEnumerable<CoreCandleData>? _) => new(point, point);
}

public sealed class ArchimedeanSpiralBehavior : TwoClickBehavior<ArchimedeanSpiralObject>
{
    protected override ArchimedeanSpiralObject CreateInstance(ChartPoint point, IEnumerable<CoreCandleData>? _) => new(point, point);
}
