using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Structure;

/// <summary>
/// Base of Break of Structure and Change of Character (plan ChartPatterns56 v2, run V2-5, model P). Both are evaluated by <see cref="StructureShiftEngine"/>: ONE causal pass over the loaded series with a trend
/// state and a protected swing (the same state machine for the two indicators; each one reads the shifts of its own kind). The first value is at bar 20 (<c>N_min</c>). The legacy Screener criterion read the last shift
/// of the non-causal <c>Detect</c>; this one does not.
/// </summary>
public abstract class CoreStructureShiftIndicatorBase : CorePatternEngineIndicatorBase
{
    private CoreStructureShiftParameter _parameter;

    protected CoreStructureShiftIndicatorBase(IndicatorType type)
        : base(PatternFlagIndicatorCatalog.Get(type))
    {
        _parameter = CoreStructureShiftParameter.CreateFor(type);
    }

    /// <summary>The <c>Main</c> series (0m / 1m) under its own name; output-name discovery lists the public series properties and recognises the Main series by this alias (hidden on the chart and in the data window).</summary>
    public IReadOnlyList<decimal?> Main => _values;

    /// <summary>The <c>Direction</c> series (-1m, 0m, +1m): the tie-broken side, for the Screener and the Backtest (hidden on the chart and in the data window).</summary>
    public IReadOnlyList<decimal?> Direction => DirectionValues;

    /// <summary>The kind of shift this indicator reports.</summary>
    protected abstract StructureShiftKind Kind { get; }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);

        if (parameters is CoreStructureShiftParameter p)
        {
            _parameter = p;
        }
    }

    protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        => StructureShiftEngine.Run(context, _parameter, Kind, cancellationToken);
}

/// <summary>Break of Structure: a close beyond the last confirmed pivot in the direction of the current trend.</summary>
[StockAnalyzerIndicator(IndicatorType.MarketStructureBos)]
public sealed class CoreMarketStructureBosIndicator : CoreStructureShiftIndicatorBase
{
    public CoreMarketStructureBosIndicator()
        : base(IndicatorType.MarketStructureBos)
    {
    }

    /// <summary>Bullish Break of Structure: +1m on the bars the flag is set.</summary>
    public IReadOnlyList<decimal?> BullishBos => BullishValues;

    /// <summary>Bearish Break of Structure: -1m on the bars the flag is set.</summary>
    public IReadOnlyList<decimal?> BearishBos => BearishValues;

    protected override StructureShiftKind Kind => StructureShiftKind.Bos;
}

/// <summary>Change of Character: a close beyond the last confirmed pivot against the current trend.</summary>
[StockAnalyzerIndicator(IndicatorType.MarketStructureChoch)]
public sealed class CoreMarketStructureChochIndicator : CoreStructureShiftIndicatorBase
{
    public CoreMarketStructureChochIndicator()
        : base(IndicatorType.MarketStructureChoch)
    {
    }

    /// <summary>Bullish Change of Character: +1m on the bars the flag is set.</summary>
    public IReadOnlyList<decimal?> BullishChoch => BullishValues;

    /// <summary>Bearish Change of Character: -1m on the bars the flag is set.</summary>
    public IReadOnlyList<decimal?> BearishChoch => BearishValues;

    protected override StructureShiftKind Kind => StructureShiftKind.Choch;
}

/// <summary>Base of directional Market Structure trend state flags (<see cref="HigherHighHigherLowEngine"/>).</summary>
public abstract class CoreStructureTrendIndicatorBase : CorePatternFlagFullSeriesIndicatorBase
{
    private CoreStructureTrendParameter _parameter = new();

    protected CoreStructureTrendIndicatorBase(IndicatorType type)
        : base(PatternFlagIndicatorCatalog.Get(type))
    {
    }

    public decimal ZigZagThresholdPercent => _parameter.ZigZagThresholdPercent;

    protected abstract int TrendSide { get; }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);

        if (parameters is CoreStructureTrendParameter p)
        {
            _parameter = p;
        }
    }

    protected override void FillSeries(CandleData[] bars, int count, int minBars, decimal?[] main, decimal?[]? direction, decimal?[]? bullish, decimal?[]? bearish, CancellationToken cancellationToken)
    {
        var context = new PatternSeriesContext(bars, count);
        PatternBarResult[] results = HigherHighHigherLowEngine.Run(context, _parameter, TrendSide, cancellationToken);
        for (int t = 0; t < count; t++)
        {
            if (t + 1 < minBars)
            {
                continue;
            }

            main[t] = results[t].Fires ? 1m : 0m;
        }
    }
}

/// <summary>Higher High / Higher Low: a causal state of the latest 4 confirmed ZigZag swings (<see cref="HigherHighHigherLowEngine"/>, side = +1).</summary>
[StockAnalyzerIndicator(IndicatorType.MarketStructureHhHl)]
public sealed class CoreMarketStructureHhHlIndicator : CoreStructureTrendIndicatorBase
{
    public CoreMarketStructureHhHlIndicator()
        : base(IndicatorType.MarketStructureHhHl)
    {
    }

    protected override int TrendSide => 1;
}

/// <summary>Lower Low / Lower High: bearish mirror of <see cref="CoreMarketStructureHhHlIndicator"/> (<see cref="HigherHighHigherLowEngine"/>, side = -1).</summary>
[StockAnalyzerIndicator(IndicatorType.MarketStructureLlLh)]
public sealed class CoreMarketStructureLlLhIndicator : CoreStructureTrendIndicatorBase
{
    public CoreMarketStructureLlLhIndicator()
        : base(IndicatorType.MarketStructureLlLh)
    {
    }

    protected override int TrendSide => -1;
}
