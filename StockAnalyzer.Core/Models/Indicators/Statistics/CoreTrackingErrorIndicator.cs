using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Rolling tracking error of the chart symbol against a comparison symbol over the last <see cref="CoreCrossTickerReturnStatisticIndicatorBase.Period"/> return pairs
/// (<see cref="DslFinanceMath.TrackingError"/>): the sample standard deviation (divisor n - 1) of the active return <c>r_a - r_b</c>, times <c>sqrt(PeriodsPerYear)</c> when
/// <see cref="Annualize"/> is on, then times 100 with <see cref="AsPercent"/>. Unit: a fraction or percent of return. A constant active return has a tracking error of 0 (a value, not null).
/// Null where the window is incomplete (see the base class).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TrackingError)]
public class CoreTrackingErrorIndicator : CoreCrossTickerReturnStatisticIndicatorBase
{
    public CoreTrackingErrorIndicator()
    {
        Period = IndicatorDefaultConstants.TrackingErrorDefaultPeriod;
    }

    public bool Annualize { get; set; } = IndicatorDefaultConstants.RiskRatioAnnualizeDefault;

    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    public bool AsPercent { get; set; } = IndicatorDefaultConstants.TrackingErrorDefaultAsPercent;

    public override string Name => $"Tracking Error ({Period}{ComparisonLabel()}{(Annualize ? ", Ann" : string.Empty)})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreTrackingErrorParameter p)
        {
            p.Validate();
            Period = p.Period;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
            AsPercent = p.AsPercent;
            ConfigureComparison(p);
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreTrackingErrorParameter
    {
        Period = Period,
        Annualize = Annualize,
        PeriodsPerYear = PeriodsPerYear,
        AsPercent = AsPercent,
    };

    protected override decimal? Compute(ReadOnlySpan<double> returnsA, ReadOnlySpan<double> returnsB)
    {
        double kernelPeriodsPerYear = Annualize ? PeriodsPerYear : IndicatorDefaultConstants.UnannualizedPeriodsPerYear;
        MathResult<double> result = DslFinanceMath.TrackingError(returnsA, returnsB, kernelPeriodsPerYear);
        if (!result.IsOk)
        {
            return null;
        }

        decimal? error = IndicatorCalculationHelper.ToDecimalOrNull(result.Value);
        return AsPercent && error.HasValue ? error.Value * IndicatorDefaultConstants.PercentFactor : error;
    }
}
