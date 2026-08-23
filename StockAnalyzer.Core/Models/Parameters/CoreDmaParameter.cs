using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDmaParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Calculation period for Displaced Moving Average.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 20;

    [DisplayName("Displacement")]
    [Description("Number of bars to displace the moving average forward or backward.")]
    [CoreParameterRange(-1000, 1000)]
    public int Displacement { get; set; } = 5;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Displacement})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
