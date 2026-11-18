using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreVarianceRatioParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.VarianceRatioDefaultPeriod;
    private int _aggregationHorizon = IndicatorDefaultConstants.VarianceRatioDefaultAggregationHorizon;

    [CoreParameterRange(IndicatorDefaultConstants.VarianceRatioMinPeriod, IndicatorDefaultConstants.VarianceRatioMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of log returns in the window (bars). A window shorter than 2 x Aggregation Horizon + 2 has no value; a window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.VarianceRatioMinAggregationHorizon, IndicatorDefaultConstants.VarianceRatioMaxAggregationHorizon)]
    [DisplayName("Aggregation Horizon")]
    [Description("Number of one-bar returns q summed into one q-bar return of the variance ratio VR(q).")]
    [Category("Estimation")]
    public int AggregationHorizon
    {
        get => _aggregationHorizon;
        set => SetProperty(ref _aggregationHorizon, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {AggregationHorizon})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.VarianceRatioMinPeriod, IndicatorDefaultConstants.VarianceRatioMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(AggregationHorizon, IndicatorDefaultConstants.VarianceRatioMinAggregationHorizon, IndicatorDefaultConstants.VarianceRatioMaxAggregationHorizon, nameof(AggregationHorizon));
    }
}
