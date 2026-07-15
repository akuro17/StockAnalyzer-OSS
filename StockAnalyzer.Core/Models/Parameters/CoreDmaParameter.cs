using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDmaParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 20;

    [CoreParameterRange(-1000, 1000)]
    public int Displacement { get; set; } = 5;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Displacement})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
