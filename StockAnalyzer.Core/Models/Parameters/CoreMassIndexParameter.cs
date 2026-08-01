using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMassIndexParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 25;

    [CoreParameterRange(1, 1000)]
    public int EmaPeriod { get; set; } = 9;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {EmaPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
    }
}
