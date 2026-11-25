using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Harmonics;

/// <summary>
/// Common base for the three Harmonic flag indicators (<see cref="HarmonicPatternEngine"/>; independent per-type pools).
/// Initializes <see cref="Parameter"/> with the type-specific specification defaults (Gartley / Butterfly / Bat).
/// </summary>
public abstract class CoreHarmonicIndicatorBase : CorePatternEngineIndicatorBase
{
    private readonly HarmonicPatternKind _kind;

    protected CoreHarmonicIndicatorBase(IndicatorType type, HarmonicPatternKind kind)
        : base(PatternFlagIndicatorCatalog.Get(type))
    {
        _kind = kind;
        Parameter = CoreHarmonicPatternParameter.CreateFor(type);
    }

    protected CoreHarmonicPatternParameter Parameter { get; private set; }

    /// <summary>The <c>Main</c> series (0m / 1m) under its own name; hidden on the chart and in the Data tab.</summary>
    public IReadOnlyList<decimal?> Main => _values;

    /// <summary>The <c>Direction</c> series (-1m, 0m, +1m): tie-broken side for Screener and Backtest (hidden on the chart and in the Data tab).</summary>
    public IReadOnlyList<decimal?> Direction => DirectionValues;

    protected override PatternDirectionMode DirectionMode => Parameter.DirectionMode;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);
        if (parameters is CoreHarmonicPatternParameter p)
        {
            Parameter = p;
        }
    }

    protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        => HarmonicPatternEngine.Run(context, Parameter, _kind, cancellationToken);
}

/// <summary>Gartley Pattern (222) harmonic flag evaluated by <see cref="HarmonicPatternEngine"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.HarmonicGartley)]
public sealed class CoreHarmonicGartleyIndicator : CoreHarmonicIndicatorBase
{
    public CoreHarmonicGartleyIndicator()
        : base(IndicatorType.HarmonicGartley, HarmonicPatternKind.Gartley)
    {
    }

    /// <summary>Bullish Gartley (+1m on active bars).</summary>
    public IReadOnlyList<decimal?> BullishGartley => BullishValues;

    /// <summary>Bearish Gartley (-1m on active bars).</summary>
    public IReadOnlyList<decimal?> BearishGartley => BearishValues;
}

/// <summary>Butterfly Pattern harmonic flag evaluated by <see cref="HarmonicPatternEngine"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.HarmonicButterfly)]
public sealed class CoreHarmonicButterflyIndicator : CoreHarmonicIndicatorBase
{
    public CoreHarmonicButterflyIndicator()
        : base(IndicatorType.HarmonicButterfly, HarmonicPatternKind.Butterfly)
    {
    }

    /// <summary>Bullish Butterfly (+1m on active bars).</summary>
    public IReadOnlyList<decimal?> BullishButterfly => BullishValues;

    /// <summary>Bearish Butterfly (-1m on active bars).</summary>
    public IReadOnlyList<decimal?> BearishButterfly => BearishValues;
}

/// <summary>Bat Pattern harmonic flag evaluated by <see cref="HarmonicPatternEngine"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.HarmonicBat)]
public sealed class CoreHarmonicBatIndicator : CoreHarmonicIndicatorBase
{
    public CoreHarmonicBatIndicator()
        : base(IndicatorType.HarmonicBat, HarmonicPatternKind.Bat)
    {
    }

    /// <summary>Bullish Bat (+1m on active bars).</summary>
    public IReadOnlyList<decimal?> BullishBat => BullishValues;

    /// <summary>Bearish Bat (-1m on active bars).</summary>
    public IReadOnlyList<decimal?> BearishBat => BearishValues;
}
