using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAdxrParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Period for Average Directional Index (ADX).")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 14;

    [DisplayName("ADXR Period")]
    [Description("Smoothing period for Average Directional Movement Index Rating (ADXR).")]
    [CoreParameterRange(1, 1000)]
    public int AdxrPeriod { get; set; } = 14;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {AdxrPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (AdxrPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(AdxrPeriod));
    }
}
