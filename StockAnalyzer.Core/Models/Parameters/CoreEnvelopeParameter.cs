using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEnvelopeParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Moving average period for envelope center line.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 20;

    [DisplayName("Deviation")]
    [Description("Percentage band deviation from the moving average.")]
    [CoreParameterRange(0, 1.0)]
    public decimal Deviation { get; set; } = 0.025m;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Deviation:P1})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Deviation < 0) throw new ArgumentOutOfRangeException(nameof(Deviation));
    }
}
