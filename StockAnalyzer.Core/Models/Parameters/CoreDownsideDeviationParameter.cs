using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDownsideDeviationParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.DownsideDeviationDefaultPeriod;
    private double _targetReturn = IndicatorDefaultConstants.DownsideDeviationDefaultTargetReturn;

    [CoreParameterRange(IndicatorDefaultConstants.DownsideDeviationMinPeriod, IndicatorDefaultConstants.DownsideDeviationMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of simple returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.DownsideDeviationMinTargetReturn, IndicatorDefaultConstants.DownsideDeviationMaxTargetReturn)]
    [DisplayName("Target Return")]
    [Description("Minimum acceptable return per bar as a ratio (0.01 is 1 %). Returns below it count as downside, returns above it as upside.")]
    [Category("Estimation")]
    public double TargetReturn
    {
        get => _targetReturn;
        set => SetProperty(ref _targetReturn, value);
    }

    public override string GetDisplayName(string type) =>
        TargetReturn == IndicatorDefaultConstants.DownsideDeviationDefaultTargetReturn ? $"{type} ({Period})" : $"{type} ({Period}, {TargetReturn})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.DownsideDeviationMinPeriod, IndicatorDefaultConstants.DownsideDeviationMaxPeriod, nameof(Period));
        if (!(TargetReturn >= IndicatorDefaultConstants.DownsideDeviationMinTargetReturn && TargetReturn <= IndicatorDefaultConstants.DownsideDeviationMaxTargetReturn))
        {
            throw new ArgumentOutOfRangeException(nameof(TargetReturn));
        }
    }
}
