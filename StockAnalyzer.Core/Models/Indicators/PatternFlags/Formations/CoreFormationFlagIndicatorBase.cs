using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Formations;

/// <summary>
/// Base of Head and Shoulders and Double Top / Bottom (plan ChartPatterns56 v2, run V2-3). The value of every bar comes from the pattern engine of the type (<c>HeadAndShouldersEngine</c>,
/// <c>DoubleTopBottomEngine</c>), which replaced the thin adapter over the legacy <c>MarketStructureDetector</c> predicates: those lack the checks of the v2 specifications (prior trend, price zone, peak interval,
/// neckline depth, close breakout), which is what produced the reported false positives. A top is bearish (-1m), a bottom bullish (+1m).
/// </summary>
public abstract class CoreFormationFlagIndicatorBase : CorePatternEngineIndicatorBase
{
    protected CoreFormationFlagIndicatorBase(IndicatorType type)
        : base(PatternFlagIndicatorCatalog.Get(type))
    {
    }

    /// <summary>The <c>Main</c> series (0m / 1m) under its own name; output-name discovery lists the public series properties and recognises the Main series by this alias (hidden on the chart and in the data window).</summary>
    public IReadOnlyList<decimal?> Main => _values;

    /// <summary>The <c>Direction</c> series (-1m, 0m, +1m): the tie-broken side, for the Screener and the Backtest (hidden on the chart and in the data window).</summary>
    public IReadOnlyList<decimal?> Direction => DirectionValues;
}
