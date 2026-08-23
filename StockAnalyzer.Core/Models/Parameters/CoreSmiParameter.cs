using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSmiParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Stochastic Momentum Index calculation period.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 14;

    [DisplayName("First Smoothing Period")]
    [Description("First EMA smoothing period for SMI.")]
    [CoreParameterRange(1, 1000)]
    public int Smooth1 { get; set; } = 5;

    [DisplayName("Second Smoothing Period")]
    [Description("Second EMA smoothing period for SMI.")]
    [CoreParameterRange(1, 1000)]
    public int Smooth2 { get; set; } = 3;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Smooth1}, {Smooth2})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Smooth1 <= 0) throw new ArgumentOutOfRangeException(nameof(Smooth1));
        if (Smooth2 <= 0) throw new ArgumentOutOfRangeException(nameof(Smooth2));
    }
}
