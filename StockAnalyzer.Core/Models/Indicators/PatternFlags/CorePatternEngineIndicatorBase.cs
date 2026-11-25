using System.Threading;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags;

/// <summary>
/// Base of the chart-pattern flags that are evaluated by a pattern engine of <c>Services/Analysis/PatternEngines</c> (plan ChartPatterns56 v2, model P): the engine returns one <see cref="PatternBarResult"/> per bar
/// for the whole series, and this base writes it into the series of the indicator. The two sides of a bidirectional type are set independently, so the visible bullish and bearish series can both be lit on one bar
/// (integration spec 4.2); <c>Main</c> is 1m when any side fires and <c>Direction</c> is the tie-broken side (score, then the later confirmation, then bullish). The bars before the minimum history are null.
/// </summary>
public abstract class CorePatternEngineIndicatorBase : CorePatternFlagFullSeriesIndicatorBase
{
    protected CorePatternEngineIndicatorBase(PatternFlagSpec spec)
        : base(spec)
    {
    }

    /// <summary>Runs the engine over the whole series held by <paramref name="context"/>; one result per bar.</summary>
    protected abstract PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken);

    protected sealed override void FillSeries(CandleData[] bars, int count, int minBars, decimal?[] main, decimal?[]? direction, decimal?[]? bullish, decimal?[]? bearish, CancellationToken cancellationToken)
    {
        var context = new PatternSeriesContext(bars, count);
        PatternBarResult[] results = RunEngine(context, cancellationToken);

        for (int t = 0; t < count; t++)
        {
            if (t + 1 < minBars)
            {
                // main[t], direction[t] and the side series stay null (not computable yet).
                continue;
            }

            PatternBarResult r = results[t];
            main[t] = r.Fires ? 1m : 0m;
            if (direction != null)
            {
                direction[t] = r.Fires ? r.Direction : 0m;
                WriteSides(t, bullish, bearish, r.BullishFires, r.BearishFires);
            }
        }
    }
}
