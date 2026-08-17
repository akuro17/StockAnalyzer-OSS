using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreCVarParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 20;

    [CoreParameterRange(0.01, 1.0)]
    public double ConfidenceLevel { get; set; } = 0.95;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {ConfidenceLevel:P})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (ConfidenceLevel <= 0 || ConfidenceLevel >= 1) throw new ArgumentOutOfRangeException(nameof(ConfidenceLevel), "ConfidenceLevel must be between 0 and 1");
    }
}
