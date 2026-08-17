using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAdxParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 14;

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
