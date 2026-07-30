using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreExpectedMoveParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 14;

    public decimal Multiplier { get; set; } = 1.0m;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Multiplier})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
