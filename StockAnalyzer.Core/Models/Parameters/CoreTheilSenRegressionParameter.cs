using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreTheilSenRegressionParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.TheilSenDefaultPeriod;
    private double _bandMultiplier = IndicatorDefaultConstants.TheilSenDefaultBandMultiplier;
    private bool _useSiegelMedian = IndicatorDefaultConstants.TheilSenDefaultUseSiegelMedian;

    [CoreParameterRange(IndicatorDefaultConstants.TheilSenMinPeriod, IndicatorDefaultConstants.TheilSenMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of prices in the window (bars). The upper limit is set by the cost of the pair slopes (quadratic in the window). A window containing a missing price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.TheilSenMinBandMultiplier, IndicatorDefaultConstants.TheilSenMaxBandMultiplier)]
    [DisplayName("Band Multiplier")]
    [Description("Distance of the upper and lower band from the line in robust standard deviations of the residuals (1.4826 x median absolute deviation).")]
    [Category("Estimation")]
    public double BandMultiplier
    {
        get => _bandMultiplier;
        set => SetProperty(ref _bandMultiplier, value);
    }

    [DisplayName("Use Siegel Median")]
    [Description("Use the Siegel repeated median (breakdown point 50 %) instead of the Theil-Sen median of all pair slopes (about 29 %).")]
    [Category("Estimation")]
    public bool UseSiegelMedian
    {
        get => _useSiegelMedian;
        set => SetProperty(ref _useSiegelMedian, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    // The first value is at bar index Period - 1 (price input).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.TheilSenMinPeriod, IndicatorDefaultConstants.TheilSenMaxPeriod, nameof(Period));
        if (!(BandMultiplier >= IndicatorDefaultConstants.TheilSenMinBandMultiplier && BandMultiplier <= IndicatorDefaultConstants.TheilSenMaxBandMultiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(BandMultiplier));
        }
    }
}
