using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSmiParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 14;

    [CoreParameterRange(1, 1000)]
    public int Smooth1 { get; set; } = 5;

    [CoreParameterRange(1, 1000)]
    public int Smooth2 { get; set; } = 3;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Smooth1}, {Smooth2})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Smooth1 <= 0) throw new ArgumentOutOfRangeException(nameof(Smooth1));
        if (Smooth2 <= 0) throw new ArgumentOutOfRangeException(nameof(Smooth2));
    }
}
