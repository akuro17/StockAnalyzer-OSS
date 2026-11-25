using System;
using System.Threading;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags;

/// <summary>
/// Base of the whole-series flags (<see cref="PatternFlagEvaluationModel.FullSeries"/>, model P of plan ChartPatterns56 S-3): one pass over the loaded series gives every bar.
/// </summary>
public abstract class CorePatternFlagFullSeriesIndicatorBase : CorePatternFlagIndicatorBase
{
    protected CorePatternFlagFullSeriesIndicatorBase(PatternFlagSpec spec)
        : base(spec)
    {
        if (spec.Model != PatternFlagEvaluationModel.FullSeries)
        {
            throw new ArgumentException($"{spec.Type} is not a whole-series type.", nameof(spec));
        }
    }
}
