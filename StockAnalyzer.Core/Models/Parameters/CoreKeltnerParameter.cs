using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKeltnerParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod { get; set; } = 20;

    [CoreParameterRange(1, 1000)]
    public int AtrPeriod { get; set; } = 10;

    public decimal Multiplier { get; set; } = 2.0m;

    public override string GetDisplayName(string type) => $"{type} ({EmaPeriod}, {AtrPeriod}, {Multiplier})";

    public override void Validate()
    {
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
        if (AtrPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(AtrPeriod));
    }
}
