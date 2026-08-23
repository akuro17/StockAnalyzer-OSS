using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreExpectedMoveParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Historical volatility calculation period.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 14;

    [DisplayName("Multiplier")]
    [Description("Expected Move standard deviation multiplier.")]
    [CoreParameterRange(0.1, 10.0)]
    public decimal Multiplier { get; set; } = 1.0m;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Multiplier})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
