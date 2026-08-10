using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreChaikinVolatilityParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 10;

    [CoreParameterRange(1, 1000)]
    public int RocPeriod { get; set; } = 10;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {RocPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (RocPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(RocPeriod));
    }
}
