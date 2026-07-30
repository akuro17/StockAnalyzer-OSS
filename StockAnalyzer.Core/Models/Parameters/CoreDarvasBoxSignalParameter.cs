using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDarvasBoxSignalParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 10000)]
    public int HighPeriod { get; set; } = 20;

    [CoreParameterRange(1, 100)]
    public int ConfirmationPeriod { get; set; } = 3;

    public override string GetDisplayName(string type) => $"{type} ({HighPeriod}, {ConfirmationPeriod})";

    public override void Validate()
    {
        if (HighPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(HighPeriod));
        if (ConfirmationPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(ConfirmationPeriod));
    }
}
