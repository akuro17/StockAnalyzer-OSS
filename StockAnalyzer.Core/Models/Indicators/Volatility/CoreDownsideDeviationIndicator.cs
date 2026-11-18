using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Rolling downside and upside deviation of the last <see cref="Period"/> one-bar simple returns against <see cref="TargetReturn"/> (<see cref="AsymmetricRiskMath"/>, computed in decimal).
/// <c>Main</c> = sigma-, the square root of the mean of min(0, r - target)^2; <c>UpsideDeviation</c> = sigma+; both are ratios per bar (0.01 is 1 %), not annualised,
/// and are 0 for a window in which no return is on that side of the target. <c>RollingSortino</c> = (mean(r) - target) / sigma- (dimensionless; null when sigma- is below
/// <see cref="ComparisonSeriesDefaults.FlatWindowDeviationEpsilon"/>) and <c>SemivarianceAsymmetry</c> = (sigma+^2 - sigma-^2) / (sigma+^2 + sigma-^2) in [-1, 1]
/// (null when the denominator is not above <see cref="ReturnStatistics.DecimalVarianceEpsilon"/>); both are chart-suppressed (scale), see <see cref="IndicatorChartSuppression"/>.
/// Null for bars before bar <see cref="Period"/> and for a window with an unusable price. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.DownsideDeviation)]
public class CoreDownsideDeviationIndicator : CoreIndicatorBase
{
    public const string UpsideDeviationSeriesName = nameof(UpsideDeviation);
    public const string RollingSortinoSeriesName = nameof(RollingSortino);
    public const string SemivarianceAsymmetrySeriesName = nameof(SemivarianceAsymmetry);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.DownsideDeviationDefaultPeriod;

    public double TargetReturn { get; set; } = IndicatorDefaultConstants.DownsideDeviationDefaultTargetReturn;

    public List<decimal?> UpsideDeviation { get; } = new();

    public List<decimal?> RollingSortino { get; } = new();

    public List<decimal?> SemivarianceAsymmetry { get; } = new();

    public override string Name =>
        TargetReturn == IndicatorDefaultConstants.DownsideDeviationDefaultTargetReturn ? $"Downside Deviation ({Period})" : $"Downside Deviation ({Period}, {TargetReturn})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreDownsideDeviationParameter p)
        {
            p.Validate();
            Period = p.Period;
            TargetReturn = p.TargetReturn;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        UpsideDeviation.Clear();
        RollingSortino.Clear();
        SemivarianceAsymmetry.Clear();

        var parameters = new CoreDownsideDeviationParameter { Period = Period, TargetReturn = TargetReturn };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        // Validate has already rejected a non-finite or out-of-range value; a null here is refused instead of silently becoming a target of 0.
        if (IndicatorCalculationHelper.ToDecimalOrNull(TargetReturn) is not decimal target)
        {
            return IndicatorResult.Failure($"{nameof(TargetReturn)} is not a finite value inside the decimal range.");
        }

        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), useLogReturn: false);
        decimal[] values = returns.DecimalReturns;
        for (int i = 0; i < returns.Count; i++)
        {
            if (i < Period || !returns.IsValid(i - Period + 1, i)
                || !AsymmetricRiskMath.TryComputeDownsideStatistics(values.AsSpan(i - Period + 1, Period), target, out DownsideStatistics statistics))
            {
                _values.Add(null);
                UpsideDeviation.Add(null);
                RollingSortino.Add(null);
                SemivarianceAsymmetry.Add(null);
                continue;
            }

            _values.Add(statistics.DownsideDeviation);
            UpsideDeviation.Add(statistics.UpsideDeviation);
            RollingSortino.Add(statistics.RollingSortino);
            SemivarianceAsymmetry.Add(statistics.SemivarianceAsymmetry);
        }

        return CreateAutomaticResult();
    }
}
