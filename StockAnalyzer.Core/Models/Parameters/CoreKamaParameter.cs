using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKamaParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 10000)]
    public int Period { get; set; } = 10;

    [CoreParameterRange(1, 1000)]
    public int Fast { get; set; } = 2;

    [CoreParameterRange(1, 10000)]
    public int Slow { get; set; } = 30;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Fast}, {Slow})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Fast <= 0) throw new ArgumentOutOfRangeException(nameof(Fast));
        if (Slow <= 0) throw new ArgumentOutOfRangeException(nameof(Slow));
    }
}
