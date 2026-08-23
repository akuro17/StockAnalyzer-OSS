using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreChaikinVolatilityParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Moving average period for high-low spread.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 10;

    [DisplayName("ROC Period")]
    [Description("Rate of change calculation period for volatility.")]
    [CoreParameterRange(1, 1000)]
    public int RocPeriod { get; set; } = 10;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {RocPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (RocPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(RocPeriod));
    }
}
