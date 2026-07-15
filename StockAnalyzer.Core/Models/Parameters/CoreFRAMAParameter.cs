using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFRAMAParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(2, 200)]
    public int Period { get; set; } = 16;

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period < 2) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
