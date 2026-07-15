using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CorePpoParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int FastPeriod { get; set; } = 12;

    [CoreParameterRange(1, 1000)]
    public int SlowPeriod { get; set; } = 26;

    public override string GetDisplayName(string type) => $"{type} ({FastPeriod}, {SlowPeriod})";

    public override void Validate()
    {
        if (FastPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(FastPeriod));
        if (SlowPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(SlowPeriod));
    }
}
