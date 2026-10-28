namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Pair Ratio: A / B per bar (A = chart symbol, B = comparison symbol). Unit: ratio (1 means A equals B).
/// No value where B is 0. The formula is the one of the Relative Performance Ratio mode, called with the roles (A, B):
/// that mode divides the comparison symbol by the chart symbol, so this value is its reciprocal.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.PairRatio)]
public sealed class CorePairRatioIndicator : CorePairIndicatorBase
{
    public CorePairRatioIndicator() : base(IndicatorType.PairRatio)
    {
    }

    // ComparisonSeriesCalculator names its arguments (close, primaryClose); here they are (A, B).
    protected override decimal? Combine(decimal a, decimal b) => ComparisonSeriesCalculator.RatioValue(a, b);
}
