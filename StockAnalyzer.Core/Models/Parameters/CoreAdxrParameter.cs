using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAdxrParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 14;

    [CoreParameterRange(1, 1000)]
    public int AdxrPeriod { get; set; } = 14;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {AdxrPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (AdxrPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(AdxrPeriod));
    }
}
