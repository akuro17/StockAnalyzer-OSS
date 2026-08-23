using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMassIndexParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Overall summation period for Mass Index.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 25;

    [DisplayName("EMA Period")]
    [Description("EMA period for high-low range smoothing.")]
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod { get; set; } = 9;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {EmaPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
    }
}
