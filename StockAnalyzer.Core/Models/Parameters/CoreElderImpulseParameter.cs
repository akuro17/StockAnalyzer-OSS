using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreElderImpulseParameter : CoreIndicatorParameterBase
{
    [DisplayName("EMA Period")]
    [Description("Period for exponential moving average trend filter.")]
    [Category("EMA")]
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod { get; set; } = 13;

    [DisplayName("MACD Fast Period")]
    [Description("Fast EMA period for MACD histogram.")]
    [Category("MACD")]
    [CoreParameterRange(1, 1000)]
    public int MacdFastPeriod { get; set; } = 12;

    [DisplayName("MACD Slow Period")]
    [Description("Slow EMA period for MACD histogram.")]
    [Category("MACD")]
    [CoreParameterRange(1, 1000)]
    public int MacdSlowPeriod { get; set; } = 26;

    [DisplayName("MACD Signal Period")]
    [Description("Signal EMA period for MACD histogram.")]
    [Category("MACD")]
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
