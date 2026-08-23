using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDarvasBoxSignalParameter : CoreIndicatorParameterBase
{
    [DisplayName("High Period")]
    [Description("Period used to establish highest high anchor.")]
    [CoreParameterRange(1, 10000)]
    public int HighPeriod { get; set; } = 20;

    [DisplayName("Confirmation Period")]
    [Description("Consecutive periods required to confirm box boundary.")]
    [CoreParameterRange(1, 100)]
    public int ConfirmationPeriod { get; set; } = 3;

    public override string GetDisplayName(string type) => $"{type} ({HighPeriod}, {ConfirmationPeriod})";

    public override void Validate()
    {
        if (HighPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(HighPeriod));
        if (ConfirmationPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(ConfirmationPeriod));
    }
}
