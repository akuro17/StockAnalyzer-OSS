using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Behavior for drawing an Auto Elliott Wave analysis region.
/// Uses two clicks to define the analysis window.
/// </summary>
public sealed class AutoElliottWaveBehavior : TwoClickBehavior<AutoElliottWaveObject>
{
    protected override AutoElliottWaveObject CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        var obj = new AutoElliottWaveObject();
        obj.Points.Add(chartPoint);
        obj.Points.Add(chartPoint);

        // Recalculate will be called by the interaction controller on mouse release or data update.
        return obj;
    }
}
