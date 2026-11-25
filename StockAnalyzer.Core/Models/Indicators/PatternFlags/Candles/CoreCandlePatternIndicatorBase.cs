using System.Threading;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Candles;

/// <summary>
/// Base of the 43 candlestick flags: all 43 types run in full-series Model P mode through
/// <see cref="CandlestickPatternEngine"/> with dynamic <c>MinBars</c> (plan R-9).
/// </summary>
public abstract class CoreCandlePatternIndicatorBase : CorePatternFlagFullSeriesIndicatorBase
{
    private CoreCandlePatternParameter _parameter;

    protected CoreCandlePatternIndicatorBase(IndicatorType type)
        : this(CandlePatternFlagTable.Get(type))
    {
    }

    private CoreCandlePatternIndicatorBase(CandlePatternFlagEntry entry)
        : base(PatternFlagIndicatorCatalog.TryGet(entry.Type, out PatternFlagSpec spec)
            ? spec
            : throw new System.InvalidOperationException($"{entry.Type} is missing in the pattern flag catalog."))
    {
        Entry = entry;
        _parameter = CoreCandlePatternParameter.CreateFor(entry.Type);
    }

    /// <summary>The table entry of this type.</summary>
    public CandlePatternFlagEntry Entry { get; }

    protected CoreCandlePatternParameter Parameter => _parameter;

    protected override int MinBars => _parameter.ComputeMinBars(Entry.Length);

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);
        if (parameters is CoreCandlePatternParameter candle)
        {
            _parameter = candle;
        }
    }

    protected override void FillSeries(CandleData[] bars, int count, int minBars, decimal?[] main, decimal?[]? direction, decimal?[]? bullish, decimal?[]? bearish, CancellationToken cancellationToken)
    {
        var context = new PatternSeriesContext(bars, count);
        PatternBarResult[] results = CandlestickPatternEngine.Run(context, Entry.Type, _parameter, cancellationToken);
        for (int t = 0; t < count; t++)
        {
            if (t + 1 < minBars)
            {
                continue;
            }

            main[t] = results[t].BullishFires ? 1m : 0m;
        }
    }
}
