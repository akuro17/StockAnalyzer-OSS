using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreRollingCovarianceParameter : CoreCrossTickerReturnParameterBase
{
    private int _period = IndicatorDefaultConstants.RollingCovarianceDefaultPeriod;
    private bool _useSampleCovariance = IndicatorDefaultConstants.RollingCovarianceDefaultUseSample;

    [CoreParameterRange(IndicatorDefaultConstants.RollingCovarianceMinPeriod, IndicatorDefaultConstants.RollingCovarianceMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of return pairs in the window (bars). A window with a missing price of either symbol gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Use Sample Covariance")]
    [Description("On (default): divide by n - 1. Off: divide by n. The value is in squared returns (unscaled).")]
    [Category("Estimation")]
    public bool UseSampleCovariance
    {
        get => _useSampleCovariance;
        set => SetProperty(ref _useSampleCovariance, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}{ComparisonSuffix()})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.RollingCovarianceMinPeriod, IndicatorDefaultConstants.RollingCovarianceMaxPeriod, nameof(Period));
    }
}
