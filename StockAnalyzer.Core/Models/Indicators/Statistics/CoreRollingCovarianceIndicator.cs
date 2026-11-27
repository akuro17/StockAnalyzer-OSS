using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Rolling covariance of the returns of the chart symbol and a comparison symbol over the last <see cref="CoreCrossTickerReturnStatisticIndicatorBase.Period"/> return pairs
/// (<see cref="DslBivariateAndTestMath.Covariance"/>): <c>sum((r_a - mean_a)(r_b - mean_b)) / d</c> with <c>d = n - 1</c> (<see cref="UseSampleCovariance"/>, default) or <c>d = n</c>.
/// Unit: return squared, raw and unscaled (for example 0.0001 for returns of about 1 %). A statistic of dimensionless returns, not of prices.
/// Null where the window is incomplete (see the base class).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RollingCovariance)]
public class CoreRollingCovarianceIndicator : CoreCrossTickerReturnStatisticIndicatorBase
{
    public CoreRollingCovarianceIndicator()
    {
        Period = IndicatorDefaultConstants.RollingCovarianceDefaultPeriod;
    }

    public bool UseSampleCovariance { get; set; } = IndicatorDefaultConstants.RollingCovarianceDefaultUseSample;

    public override string Name => $"Covariance ({Period}{ComparisonLabel()})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRollingCovarianceParameter p)
        {
            p.Validate();
            Period = p.Period;
            UseSampleCovariance = p.UseSampleCovariance;
            ConfigureComparison(p);
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreRollingCovarianceParameter
    {
        Period = Period,
        UseSampleCovariance = UseSampleCovariance,
    };

    protected override decimal? Compute(ReadOnlySpan<double> returnsA, ReadOnlySpan<double> returnsB)
    {
        MathResult<double> result = DslBivariateAndTestMath.Covariance(returnsA, returnsB, UseSampleCovariance);
        return result.IsOk ? IndicatorCalculationHelper.ToDecimalOrNull(result.Value) : null;
    }
}
