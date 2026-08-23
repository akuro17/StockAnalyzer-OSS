using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKamaParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Efficiency Ratio (ER) lookback period for KAMA.")]
    [CoreParameterRange(1, 10000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 10;

    [DisplayName("Fast EMA Period")]
    [Description("Fastest EMA constant period for KAMA.")]
    [CoreParameterRange(1, 1000)]
    public int Fast { get; set; } = 2;

    [DisplayName("Slow EMA Period")]
    [Description("Slowest EMA constant period for KAMA.")]
    [CoreParameterRange(1, 10000)]
    public int Slow { get; set; } = 30;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Fast}, {Slow})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Fast <= 0) throw new ArgumentOutOfRangeException(nameof(Fast));
        if (Slow <= 0) throw new ArgumentOutOfRangeException(nameof(Slow));
    }
}
