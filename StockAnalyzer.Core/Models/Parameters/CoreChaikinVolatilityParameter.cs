using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreChaikinVolatilityParameter : CoreIndicatorParameterBase
{
    private int _period = 10;

    [DisplayName("Period")]
    [Description("Moving average period for high-low spread.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _rocPeriod = 10;

    [DisplayName("ROC Period")]
    [Description("Rate of change calculation period for volatility.")]
    [CoreParameterRange(1, 1000)]
    public int RocPeriod
    {
        get => _rocPeriod;
        set => SetProperty(ref _rocPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {RocPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (RocPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(RocPeriod));
    }
}
