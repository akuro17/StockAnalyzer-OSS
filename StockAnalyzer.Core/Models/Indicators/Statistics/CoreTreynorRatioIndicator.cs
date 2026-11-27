using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Rolling Treynor ratio of the chart symbol against a comparison symbol over the last <see cref="CoreCrossTickerReturnStatisticIndicatorBase.Period"/> return pairs
/// (<see cref="DslFinanceMath.TreynorRatio"/>): <c>(mean(r_a) - rf) x F / beta</c> with the per-bar risk-free rate <c>rf = AnnualRiskFreeRate / PeriodsPerYear</c>, the OLS beta
/// <c>cov(r_a, r_b) / var(r_b)</c> and a LINEAR annualisation <c>F = PeriodsPerYear</c> when <see cref="Annualize"/> is on, otherwise 1 (the value per bar). Unit: return per unit of beta.
/// Null where the window is incomplete (see the base class), where the beta is not above 1e-12 in magnitude, or where the benchmark has no variance.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TreynorRatio)]
public class CoreTreynorRatioIndicator : CoreCrossTickerReturnStatisticIndicatorBase
{
    public CoreTreynorRatioIndicator()
    {
        Period = IndicatorDefaultConstants.TreynorRatioDefaultPeriod;
    }

    public bool Annualize { get; set; } = IndicatorDefaultConstants.RiskRatioAnnualizeDefault;

    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    public decimal AnnualRiskFreeRate { get; set; } = IndicatorDefaultConstants.RiskRatioDefaultAnnualRiskFreeRate;

    public override string Name => $"Treynor ({Period}{ComparisonLabel()}{(Annualize ? ", Ann" : string.Empty)})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreTreynorRatioParameter p)
        {
            p.Validate();
            Period = p.Period;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
            AnnualRiskFreeRate = p.AnnualRiskFreeRate;
            ConfigureComparison(p);
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreTreynorRatioParameter
    {
        Period = Period,
        Annualize = Annualize,
        PeriodsPerYear = PeriodsPerYear,
        AnnualRiskFreeRate = AnnualRiskFreeRate,
    };

    protected override decimal? Compute(ReadOnlySpan<double> returnsA, ReadOnlySpan<double> returnsB)
    {
        double riskFreePerBar = (double)(AnnualRiskFreeRate / PeriodsPerYear);
        double kernelPeriodsPerYear = Annualize ? PeriodsPerYear : IndicatorDefaultConstants.UnannualizedPeriodsPerYear;
        MathResult<double> result = DslFinanceMath.TreynorRatio(returnsA, returnsB, riskFreePerBar, kernelPeriodsPerYear);
        return result.IsOk ? IndicatorCalculationHelper.ToDecimalOrNull(result.Value) : null;
    }
}
