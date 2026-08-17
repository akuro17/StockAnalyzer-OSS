using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreUltimateOscillatorParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period1 { get; set; } = 7;

    [CoreParameterRange(1, 1000)]
    public int Period2 { get; set; } = 14;

    [CoreParameterRange(1, 1000)]
    public int Period3 { get; set; } = 28;

    public override string GetDisplayName(string type) => $"{type} ({Period1}, {Period2}, {Period3})";

    public override void Validate()
    {
        if (Period1 <= 0) throw new ArgumentOutOfRangeException(nameof(Period1));
        if (Period2 <= 0) throw new ArgumentOutOfRangeException(nameof(Period2));
        if (Period3 <= 0) throw new ArgumentOutOfRangeException(nameof(Period3));
    }
}
