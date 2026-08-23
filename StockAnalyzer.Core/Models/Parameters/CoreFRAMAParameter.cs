using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFRAMAParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Fractal calculation window (must be an even number).")]
    [CoreParameterRange(2, 200)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 16;

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period < 2) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
