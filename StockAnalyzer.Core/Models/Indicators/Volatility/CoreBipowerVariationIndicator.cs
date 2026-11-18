using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Rolling realized bipower variation of the last <see cref="Period"/> log returns (<see cref="JumpVariationMath"/>). All volatilities are log-return standard deviations PER BAR
/// (ratios, not percentages, not annualised): <c>Main</c> = sqrt(BPV), the continuous volatility; <c>TotalVolatility</c> = sqrt(RV); <c>JumpComponentHistogram</c> = sqrt(J) with
/// J = max(0, RV - BPV) (the name contains "Histogram", so the chart draws it as bars from the zero baseline with the existing renderer). A window of zero returns gives 0 for all three.
/// <c>JumpRatio</c> = J / RV in [0, 1] (null when RV is not above <see cref="ReturnStatistics.VarianceEpsilon"/>) is chart-suppressed (scale), see <see cref="IndicatorChartSuppression"/>.
/// Null for bars before bar <see cref="Period"/> and for a window with an unusable price. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.BipowerVariation)]
public class CoreBipowerVariationIndicator : CoreIndicatorBase
{
    public const string TotalVolatilitySeriesName = nameof(TotalVolatility);
    public const string JumpComponentHistogramSeriesName = nameof(JumpComponentHistogram);
    public const string JumpRatioSeriesName = nameof(JumpRatio);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.BipowerVariationDefaultPeriod;

    public List<decimal?> TotalVolatility { get; } = new();

    public List<decimal?> JumpComponentHistogram { get; } = new();

    public List<decimal?> JumpRatio { get; } = new();

    public override string Name => $"Bipower Variation ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreBipowerVariationParameter p)
        {
            p.Validate();
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        TotalVolatility.Clear();
        JumpComponentHistogram.Clear();
        JumpRatio.Clear();

        var parameters = new CoreBipowerVariationParameter { Period = Period };
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
            if (i < Period || !returns.IsValid(i - Period + 1, i)
                || !JumpVariationMath.TryCompute(returns.Returns.AsSpan(i - Period + 1, Period), out JumpVariation variation))
            {
                _values.Add(null);
                TotalVolatility.Add(null);
                JumpComponentHistogram.Add(null);
                JumpRatio.Add(null);
                continue;
            }

            _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(variation.BipowerVolatility));
            TotalVolatility.Add(IndicatorCalculationHelper.ToDecimalOrNull(variation.TotalVolatility));
            JumpComponentHistogram.Add(IndicatorCalculationHelper.ToDecimalOrNull(variation.JumpVolatility));
            JumpRatio.Add(IndicatorCalculationHelper.ToDecimalOrNull(variation.JumpRatio));
        }

        return CreateAutomaticResult();
    }
}
