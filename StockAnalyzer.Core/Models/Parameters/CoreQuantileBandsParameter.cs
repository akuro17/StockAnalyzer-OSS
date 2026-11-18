using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreQuantileBandsParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.QuantileBandsDefaultPeriod;
    private double _upperQuantile = IndicatorDefaultConstants.QuantileBandsDefaultUpperQuantile;
    private double _lowerQuantile = IndicatorDefaultConstants.QuantileBandsDefaultLowerQuantile;

    [CoreParameterRange(IndicatorDefaultConstants.QuantileBandsMinPeriod, IndicatorDefaultConstants.QuantileBandsMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of prices in the window (bars). A window containing a missing price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.QuantileBandsMinUpperQuantile, IndicatorDefaultConstants.QuantileBandsMaxUpperQuantile)]
    [DisplayName("Upper Quantile")]
    [Description("Probability of the upper band (type 7 quantile of the window prices), above 0.5.")]
    [Category("Estimation")]
    public double UpperQuantile
    {
        get => _upperQuantile;
        set => SetProperty(ref _upperQuantile, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.QuantileBandsMinLowerQuantile, IndicatorDefaultConstants.QuantileBandsMaxLowerQuantile)]
    [DisplayName("Lower Quantile")]
    [Description("Probability of the lower band (type 7 quantile of the window prices), below 0.5.")]
    [Category("Estimation")]
    public double LowerQuantile
    {
        get => _lowerQuantile;
        set => SetProperty(ref _lowerQuantile, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    // The first value is at bar index Period - 1 (price input).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.QuantileBandsMinPeriod, IndicatorDefaultConstants.QuantileBandsMaxPeriod, nameof(Period));
        if (!(UpperQuantile >= IndicatorDefaultConstants.QuantileBandsMinUpperQuantile && UpperQuantile <= IndicatorDefaultConstants.QuantileBandsMaxUpperQuantile))
        {
            throw new ArgumentOutOfRangeException(nameof(UpperQuantile));
        }

        if (!(LowerQuantile >= IndicatorDefaultConstants.QuantileBandsMinLowerQuantile && LowerQuantile <= IndicatorDefaultConstants.QuantileBandsMaxLowerQuantile))
        {
            throw new ArgumentOutOfRangeException(nameof(LowerQuantile));
        }
    }
}
