using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKeltnerParameter : CoreIndicatorParameterBase
{
    [DisplayName("EMA Period")]
    [Description("Exponential moving average period for center line.")]
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod { get; set; } = 20;

    [DisplayName("ATR Period")]
    [Description("Average True Range calculation period for channel width.")]
    [CoreParameterRange(1, 1000)]
    public int AtrPeriod { get; set; } = 10;

    [DisplayName("Multiplier")]
    [Description("ATR multiplier for channel bands.")]
    [CoreParameterRange(0.1, 10.0)]
    public decimal Multiplier { get; set; } = 2.0m;

    public override string GetDisplayName(string type) => $"{type} ({EmaPeriod}, {AtrPeriod}, {Multiplier})";

    public override void Validate()
    {
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
        if (AtrPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(AtrPeriod));
    }
}
