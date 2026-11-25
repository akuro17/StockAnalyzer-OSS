using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Waves;

/// <summary>Elliott Wave Impulse (1-2-3-4-5) flag evaluated by <see cref="ElliottWaveEngine.RunImpulse"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.ElliottWaveImpulse)]
public sealed class CoreElliottWaveImpulseIndicator : CorePatternEngineIndicatorBase
{
    private CoreElliottImpulseParameter _parameter = new();

    public CoreElliottWaveImpulseIndicator()
        : base(PatternFlagIndicatorCatalog.Get(IndicatorType.ElliottWaveImpulse))
    {
    }

    /// <summary>The <c>Main</c> series (0m / 1m) under its own name (hidden on the chart and in the Data tab).</summary>
    public IReadOnlyList<decimal?> Main => _values;

    /// <summary>The <c>Direction</c> series (-1m, 0m, +1m): tie-broken side for Screener and Backtest (hidden on the chart and in the Data tab).</summary>
    public IReadOnlyList<decimal?> Direction => DirectionValues;

    /// <summary>Bullish Impulse 1-5 (+1m on active bars).</summary>
    public IReadOnlyList<decimal?> BullishImpulse => BullishValues;

    /// <summary>Bearish Impulse 1-5 (-1m on active bars).</summary>
    public IReadOnlyList<decimal?> BearishImpulse => BearishValues;

    protected override PatternDirectionMode DirectionMode => _parameter.DirectionMode;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);
        if (parameters is CoreElliottImpulseParameter p)
        {
            _parameter = p;
        }
    }

    protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        => ElliottWaveEngine.RunImpulse(context, _parameter, cancellationToken);
}

/// <summary>Elliott Wave Corrective (A-B-C) flag evaluated by <see cref="ElliottWaveEngine.RunCorrective"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.ElliottWaveCorrective)]
public sealed class CoreElliottWaveCorrectiveIndicator : CorePatternEngineIndicatorBase
{
    private CoreElliottCorrectiveParameter _parameter = new();

    public CoreElliottWaveCorrectiveIndicator()
        : base(PatternFlagIndicatorCatalog.Get(IndicatorType.ElliottWaveCorrective))
    {
    }

    /// <summary>The <c>Main</c> series (0m / 1m) under its own name (hidden on the chart and in the Data tab).</summary>
    public IReadOnlyList<decimal?> Main => _values;

    /// <summary>The <c>Direction</c> series (-1m, 0m, +1m): tie-broken side for Screener and Backtest (hidden on the chart and in the Data tab).</summary>
    public IReadOnlyList<decimal?> Direction => DirectionValues;

    /// <summary>Bullish Corrective ABC (+1m on active bars).</summary>
    public IReadOnlyList<decimal?> BullishCorrective => BullishValues;

    /// <summary>Bearish Corrective ABC (-1m on active bars).</summary>
    public IReadOnlyList<decimal?> BearishCorrective => BearishValues;

    protected override PatternDirectionMode DirectionMode => _parameter.DirectionMode;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);
        if (parameters is CoreElliottCorrectiveParameter p)
        {
            _parameter = p;
        }
    }

    protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        => ElliottWaveEngine.RunCorrective(context, _parameter, cancellationToken);
}
