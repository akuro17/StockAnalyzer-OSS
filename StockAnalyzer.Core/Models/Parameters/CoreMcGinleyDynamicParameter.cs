using System.Globalization;
using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMcGinleyDynamicParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.McGinleyDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Period")]
    [Description("Period N (bars): the first value is the mean of the first N positive prices, and N scales the speed of the adjustment.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private decimal _constant = IndicatorDefaultConstants.McGinleyDefaultConstant;

    [CoreParameterRange(IndicatorDefaultConstants.McGinleyMinConstant, IndicatorDefaultConstants.McGinleyMaxConstant)]
    [DisplayName("Constant")]
    [Description("McGinley constant k in the divisor k x N x (P / MD)^4. The divisor contains (P / MD)^4, so when a rally continues and the price moves far above MD the divisor grows rapidly and MD stops following the price. To follow a volatile symbol, lower the Constant to about 0.3 - 0.1.")]
    [Category("Smoothing")]
    public decimal Constant
    {
        get => _constant;
        set => SetProperty(ref _constant, value);
    }

    public override string GetDisplayName(string type) => string.Create(CultureInfo.InvariantCulture, $"{type} ({Period}, {Constant})");

    // The first value is at the bar where N positive prices have been seen: index Period - 1 for a series of positive prices.
    public override int GetRequiredWarmupBars() => Period - 1;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(Period));
        ThrowIfOutOfRange(Constant, IndicatorDefaultConstants.McGinleyMinConstant, IndicatorDefaultConstants.McGinleyMaxConstant, nameof(Constant));
    }
}
