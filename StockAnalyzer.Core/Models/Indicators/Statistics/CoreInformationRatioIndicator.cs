using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Rolling information ratio of the chart symbol against a comparison symbol over the last <see cref="CoreCrossTickerReturnStatisticIndicatorBase.Period"/> return pairs
/// (<see cref="DslFinanceMath.InfoRatio"/>): the mean of the active return <c>r_a - r_b</c> over its sample standard deviation (the tracking error, divisor n - 1), times
/// <c>sqrt(PeriodsPerYear)</c> when <see cref="Annualize"/> is on. Unit: dimensionless ratio. Null where the window is incomplete or where the active return is constant (no tracking error).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.InformationRatio)]
public class CoreInformationRatioIndicator : CoreCrossTickerReturnStatisticIndicatorBase
{
    public CoreInformationRatioIndicator()
    {
        Period = IndicatorDefaultConstants.InformationRatioDefaultPeriod;
    }

    public bool Annualize { get; set; } = IndicatorDefaultConstants.RiskRatioAnnualizeDefault;

    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    public override string Name => $"Information Ratio ({Period}{ComparisonLabel()}{(Annualize ? ", Ann" : string.Empty)})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreInformationRatioParameter p)
        {
            p.Validate();
            Period = p.Period;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
            ConfigureComparison(p);
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreInformationRatioParameter
    {
        Period = Period,
        Annualize = Annualize,
        PeriodsPerYear = PeriodsPerYear,
    };

    protected override decimal? Compute(ReadOnlySpan<double> returnsA, ReadOnlySpan<double> returnsB)
    {
        double kernelPeriodsPerYear = Annualize ? PeriodsPerYear : IndicatorDefaultConstants.UnannualizedPeriodsPerYear;
        MathResult<double> result = DslFinanceMath.InfoRatio(returnsA, returnsB, kernelPeriodsPerYear);
        return result.IsOk ? IndicatorCalculationHelper.ToDecimalOrNull(result.Value) : null;
    }
}
