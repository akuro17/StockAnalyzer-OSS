using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreZigZagParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(0.1, 100.0)]
    public decimal Threshold { get; set; } = 5.0m;

    public override string GetDisplayName(string type) => $"{type} ({Threshold}%)";

    public override void Validate()
    {
        if (Threshold <= 0) throw new ArgumentOutOfRangeException(nameof(Threshold));
    }
}
