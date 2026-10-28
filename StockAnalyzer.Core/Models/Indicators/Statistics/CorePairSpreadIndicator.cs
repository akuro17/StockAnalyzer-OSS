namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Pair Spread: A - B per bar (A = chart symbol, B = comparison symbol). Unit: price difference in the price units of the two symbols (no currency conversion).
/// The formula is the one of the Relative Performance Spread mode, called with the roles (A, B):
/// that mode subtracts the chart symbol from the comparison symbol, so this value is its negation.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.PairSpread)]
public sealed class CorePairSpreadIndicator : CorePairIndicatorBase
{
    public CorePairSpreadIndicator() : base(IndicatorType.PairSpread)
    {
    }

    // ComparisonSeriesCalculator names its arguments (close, primaryClose); here they are (A, B).
    protected override decimal? Combine(decimal a, decimal b) => ComparisonSeriesCalculator.SpreadValue(a, b);
}
