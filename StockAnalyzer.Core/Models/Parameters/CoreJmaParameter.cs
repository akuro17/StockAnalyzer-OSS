using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreJmaParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 10000)]
    public int Period { get; set; } = 7;

    [CoreParameterRange(-100.0, 100.0)]
    public double Phase { get; set; } = 0;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Phase})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        // Phase can be negative, standard range often -100 to 100
    }
}
