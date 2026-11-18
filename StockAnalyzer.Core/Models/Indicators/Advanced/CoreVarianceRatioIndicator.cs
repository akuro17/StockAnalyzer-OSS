using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling Lo-MacKinlay variance ratio VR(q) of the last <see cref="Period"/> log returns with the aggregation horizon <see cref="AggregationHorizon"/> = q
/// (<see cref="SerialDependenceMath.VarianceRatio"/>, which calls the one existing <see cref="HurstMath.VarianceRatio"/>: no second formula, so <c>Main</c> is bit-identical to the
/// <c>VarianceRatio</c> series of the Hurst exponent with the same Period and lag). <c>Main</c> = VR (dimensionless; 1 = random walk, above 1 = momentum, below 1 = mean reversion).
/// <c>RandomWalkLine</c> = 1 as a constant reference line, present exactly where <c>Main</c> has a value. <c>ZStarStatistic</c> = the heteroskedasticity-robust Z*(q) and <c>PValue</c> = the two-sided
/// normal p-value of Z* (<c>erfc(|Z*| / sqrt 2)</c>); both are chart-suppressed (scale), see <see cref="IndicatorChartSuppression"/>.
/// Null for bars before bar <see cref="Period"/>, for a window shorter than <c>2 q + 2</c>, for a window with an unusable price and for a constant window. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.VarianceRatio)]
public class CoreVarianceRatioIndicator : CoreIndicatorBase
{
    public const string RandomWalkLineSeriesName = nameof(RandomWalkLine);
    public const string ZStarStatisticSeriesName = nameof(ZStarStatistic);
    public const string PValueSeriesName = nameof(PValue);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.VarianceRatioDefaultPeriod;

    public int AggregationHorizon { get; set; } = IndicatorDefaultConstants.VarianceRatioDefaultAggregationHorizon;

    public List<decimal?> RandomWalkLine { get; } = new();

    public List<decimal?> ZStarStatistic { get; } = new();

    public List<decimal?> PValue { get; } = new();

    public override string Name => $"Variance Ratio ({Period}, {AggregationHorizon})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreVarianceRatioParameter p)
        {
            p.Validate();
            Period = p.Period;
            AggregationHorizon = p.AggregationHorizon;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        RandomWalkLine.Clear();
        ZStarStatistic.Clear();
        PValue.Clear();

        var parameters = new CoreVarianceRatioParameter { Period = Period, AggregationHorizon = AggregationHorizon };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), useLogReturn: true);
        for (int i = 0; i < returns.Count; i++)
        {
            decimal? ratio = null;
            decimal? zStar = null;
            decimal? pValue = null;
            if (i >= Period && returns.IsValid(i - Period + 1, i))
            {
                VarianceRatioTestResult test = SerialDependenceMath.VarianceRatio(returns.Returns.AsSpan(i - Period + 1, Period), AggregationHorizon);
                ratio = IndicatorCalculationHelper.ToDecimalOrNull(test.VarianceRatio);
                if (ratio.HasValue)
                {
                    zStar = IndicatorCalculationHelper.ToDecimalOrNull(test.ZStar);
                    pValue = IndicatorCalculationHelper.ToDecimalOrNull(test.PValue);
                }
            }

            _values.Add(ratio);
            RandomWalkLine.Add(ratio.HasValue ? IndicatorDefaultConstants.VarianceRatioRandomWalkLevel : null);
            ZStarStatistic.Add(zStar);
            PValue.Add(pValue);
        }

        return CreateAutomaticResult();
    }
}
