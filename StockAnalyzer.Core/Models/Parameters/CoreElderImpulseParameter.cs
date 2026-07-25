using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreElderImpulseParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod { get; set; } = 13;

    [CoreParameterRange(1, 1000)]
    public int MacdFastPeriod { get; set; } = 12;

    [CoreParameterRange(1, 1000)]
    public int MacdSlowPeriod { get; set; } = 26;

    [CoreParameterRange(1, 1000)]
    public int MacdSignalPeriod { get; set; } = 9;

    public override string GetDisplayName(string type) => $"{type} ({EmaPeriod}, {MacdFastPeriod}, {MacdSlowPeriod}, {MacdSignalPeriod})";

    public override void Validate()
    {
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
        if (MacdFastPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(MacdFastPeriod));
        if (MacdSlowPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(MacdSlowPeriod));
        if (MacdSignalPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(MacdSignalPeriod));
    }
}
