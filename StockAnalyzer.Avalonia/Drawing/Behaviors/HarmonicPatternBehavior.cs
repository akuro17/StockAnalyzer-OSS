using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.HarmonicPattern;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Behavior for drawing a Harmonic Pattern analysis region.
/// </summary>
public sealed class HarmonicPatternBehavior : TwoClickBehavior<HarmonicPatternObject>
{
    protected override HarmonicPatternObject CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        var obj = new HarmonicPatternObject
        {
            PivotMode = HarmonicPivotMode.LocalExtremaAmplitude
        };
        obj.Points.Add(chartPoint);
        obj.Points.Add(chartPoint);

        // Recalculate will be called by the interaction controller on mouse release or data update.
        return obj;
    }
}
