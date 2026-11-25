using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Formations;

/// <summary>Head and Shoulders (top = bearish, inverse = bullish) evaluated by <see cref="HeadAndShouldersEngine"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.PatternHeadAndShoulders)]
public sealed class CorePatternHeadAndShouldersIndicator : CoreFormationFlagIndicatorBase
{
    private CoreHeadAndShouldersParameter _parameter = new();

    public CorePatternHeadAndShouldersIndicator()
        : base(IndicatorType.PatternHeadAndShoulders)
    {
    }

    /// <summary>Inverse Head and Shoulders (bullish): +1m on the bars the pattern is present.</summary>
    public IReadOnlyList<decimal?> InverseBottom => BullishValues;

    /// <summary>Head and Shoulders top (bearish): -1m on the bars the pattern is present.</summary>
    public IReadOnlyList<decimal?> Top => BearishValues;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);

        if (parameters is CoreHeadAndShouldersParameter p)
        {
            _parameter = p;
        }
    }

    protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        => HeadAndShouldersEngine.Run(context, _parameter, cancellationToken);
}

/// <summary>Double Top (bearish) / Double Bottom (bullish) evaluated by <see cref="DoubleTopBottomEngine"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.PatternDoubleTopBottom)]
public sealed class CorePatternDoubleTopBottomIndicator : CoreFormationFlagIndicatorBase
{
    private CoreDoubleTopBottomParameter _parameter = new();

    public CorePatternDoubleTopBottomIndicator()
        : base(IndicatorType.PatternDoubleTopBottom)
    {
    }

    /// <summary>Double Bottom (bullish): +1m on the bars the pattern is present.</summary>
    public IReadOnlyList<decimal?> DoubleBottom => BullishValues;

    /// <summary>Double Top (bearish): -1m on the bars the pattern is present.</summary>
    public IReadOnlyList<decimal?> DoubleTop => BearishValues;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);

        if (parameters is CoreDoubleTopBottomParameter p)
        {
            _parameter = p;
        }
    }

    protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        => DoubleTopBottomEngine.Run(context, _parameter, cancellationToken);
}

/// <summary>Bullish / Bearish Flag evaluated by <see cref="FlagEngine"/> (pole, Theil-Sen channel, break; decision D-V2-9).</summary>
[StockAnalyzerIndicator(IndicatorType.PatternFlag)]
public sealed class CorePatternFlagIndicator : CorePatternEngineIndicatorBase
{
    private CoreFlagPatternParameter _parameter = new();

    public CorePatternFlagIndicator()
        : base(PatternFlagIndicatorCatalog.Get(IndicatorType.PatternFlag))
    {
    }

    /// <summary>The <c>Main</c> series (0m / 1m) under its own name; output-name discovery lists the public series properties and recognises the Main series by this alias (hidden on the chart and in the data window).</summary>
    public IReadOnlyList<decimal?> Main => _values;

    /// <summary>The <c>Direction</c> series (-1m, 0m, +1m): the tie-broken side, for the Screener and the Backtest (hidden on the chart and in the data window).</summary>
    public IReadOnlyList<decimal?> Direction => DirectionValues;

    /// <summary>Bullish Flag: +1m on the bars the pattern is present.</summary>
    public IReadOnlyList<decimal?> BullishFlag => BullishValues;

    /// <summary>Bearish Flag: -1m on the bars the pattern is present.</summary>
    public IReadOnlyList<decimal?> BearishFlag => BearishValues;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);

        if (parameters is CoreFlagPatternParameter p)
        {
            _parameter = p;
        }
    }

    protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        => FlagEngine.Run(context, _parameter, cancellationToken);
}
