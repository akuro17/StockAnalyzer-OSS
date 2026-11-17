using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// The completed higher-frame bars an indicator is calculated on, in both forms the calculation needs. The leading period is already dropped
/// (see <see cref="IndicatorTimeframeFrameCache.Get"/>): its first bar may start before the chart's data and so be incomplete.
/// </summary>
public sealed record PeriodFrameBars(CoreCandleData[] Candles, ImmutableArray<CandleData> Bars);

/// <summary>
/// One pipeline call's cache of higher-frame bars: each frame is aggregated at most once however many indicators ask for it, and the chart's bars are
/// converted at most once. Created lazily by the pipeline (an all-Same call never allocates it); not thread-safe, like the pipeline call that owns it.
/// </summary>
public sealed class IndicatorTimeframeFrameCache
{
    private readonly IReadOnlyList<CoreCandleData> _chartCandles;
    private readonly Dictionary<TimeFrame, PeriodFrameBars?> _frames = new();
    private ImmutableArray<CandleData> _chartBars;
    private bool _chartBarsBuilt;

    public IndicatorTimeframeFrameCache(IReadOnlyList<CoreCandleData> chartCandles)
    {
        _chartCandles = chartCandles;
    }

    /// <summary>The chart's candles as <see cref="CandleData"/>, the base of the causal alignment.</summary>
    public ImmutableArray<CandleData> ChartBars
    {
        get
        {
            if (!_chartBarsBuilt)
            {
                var bars = new CandleData[_chartCandles.Count];
                for (int i = 0; i < bars.Length; i++)
                {
                    var c = _chartCandles[i];
                    bars[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
                }

                _chartBars = ImmutableCollectionsMarshal.AsImmutableArray(bars);
                _chartBarsBuilt = true;
            }

            return _chartBars;
        }
    }

    /// <summary>
    /// The bars of <paramref name="frame"/> after the leading period is dropped, or null when at most one period exists (nothing completed is left to calculate on).
    /// </summary>
    public PeriodFrameBars? Get(TimeFrame frame)
    {
        if (_frames.TryGetValue(frame, out var cached))
        {
            return cached;
        }

        var aggregated = TimeFrameAggregator.AggregateToPeriodStart(_chartCandles, frame);
        PeriodFrameBars? built = null;
        if (aggregated.Count > 1)
        {
            var candles = new CoreCandleData[aggregated.Count - 1];
            var bars = new CandleData[candles.Length];
            for (int i = 0; i < candles.Length; i++)
            {
                var c = aggregated[i + 1];
                candles[i] = c;
                bars[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
            }

            built = new PeriodFrameBars(candles, ImmutableCollectionsMarshal.AsImmutableArray(bars));
        }

        _frames[frame] = built;
        return built;
    }
}

/// <summary>
/// Calculates an indicator on higher-frame bars and carries every named series onto the chart's bars: at a chart bar the value is the one of the most recently
/// CLOSED higher-frame period (<see cref="TimeframeAlignment.Align"/>), so a value never depends on bars after the chart bar and the running period never leaks.
/// </summary>
public static class IndicatorTimeframeCalculator
{
    /// <summary>
    /// Runs <paramref name="indicator"/> on the bars of <paramref name="effectiveFrame"/> from <paramref name="cache"/> and aligns the result onto the chart.
    /// A failed calculation is returned unchanged; with no completed period the result is <see cref="IndicatorTimeframeMessages.InsufficientAggregatedBars"/>.
    /// </summary>
    public static async Task<IIndicatorResult> CalculateAsync(ICoreIndicator indicator, IExecutionContext context, IndicatorTimeframeFrameCache cache, TimeFrame effectiveFrame)
    {
        var own = cache.Get(effectiveFrame);
        if (own is null)
        {
            return IndicatorResult.Failure(IndicatorTimeframeMessages.InsufficientAggregatedBars);
        }

        var ownResult = await indicator.CalculateAsync(own.Candles, context).ConfigureAwait(false);
        if (!ownResult.IsSuccessful)
        {
            return ownResult;
        }

        var chartBars = cache.ChartBars;
        var names = ownResult.SeriesNamesList;
        var aligned = new Dictionary<string, IReadOnlyList<decimal?>>(names.Count, System.StringComparer.OrdinalIgnoreCase);
        var orderedNames = new List<string>(names.Count);
        for (int n = 0; n < names.Count; n++)
        {
            string name = names[n];
            var values = NormalizeLength(ownResult.GetSeries(name), own.Bars.Length);
            aligned[name] = ImmutableCollectionsMarshal.AsArray(TimeframeAlignment.Align(chartBars, own.Bars, values))!;
            orderedNames.Add(name);
        }

        return new TimeframeAlignedIndicatorResult(ownResult, aligned, orderedNames);
    }

    /// <summary>
    /// Exactly <paramref name="length"/> values: a surplus is cut (a forward projection such as Ichimoku's cloud lies past the last bar and has no bar to carry it)
    /// and a shortfall is padded with null.
    /// </summary>
    private static ImmutableArray<decimal?> NormalizeLength(IReadOnlyList<decimal?> series, int length)
    {
        var values = new decimal?[length];
        int copy = series.Count < length ? series.Count : length;
        for (int i = 0; i < copy; i++)
        {
            values[i] = series[i];
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(values);
    }
}
