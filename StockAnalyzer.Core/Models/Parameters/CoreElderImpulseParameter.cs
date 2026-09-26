using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreElderImpulseParameter : CoreIndicatorParameterBase
{
    private int _emaPeriod = 13;

    [DisplayName("EMA Period")]
    [Description("Period for exponential moving average trend filter.")]
    [Category("EMA")]
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod
    {
        get => _emaPeriod;
        set => SetProperty(ref _emaPeriod, value);
    }

    private int _macdFastPeriod = 12;

    [DisplayName("MACD Fast Period")]
    [Description("Fast EMA period for MACD histogram.")]
    [Category("MACD")]
    [CoreParameterRange(1, 1000)]
    public int MacdFastPeriod
    {
        get => _macdFastPeriod;
        set => SetProperty(ref _macdFastPeriod, value);
    }

    private int _macdSlowPeriod = 26;

    [DisplayName("MACD Slow Period")]
    [Description("Slow EMA period for MACD histogram.")]
    [Category("MACD")]
    [CoreParameterRange(1, 1000)]
    public int MacdSlowPeriod
    {
        get => _macdSlowPeriod;
        set => SetProperty(ref _macdSlowPeriod, value);
    }

    private int _macdSignalPeriod = 9;

    [DisplayName("MACD Signal Period")]
    [Description("Signal EMA period for MACD histogram.")]
    [Category("MACD")]
    [CoreParameterRange(1, 1000)]
    public int MacdSignalPeriod
    {
        get => _macdSignalPeriod;
        set => SetProperty(ref _macdSignalPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({EmaPeriod}, {MacdFastPeriod}, {MacdSlowPeriod}, {MacdSignalPeriod})";

    public override void Validate()
    {
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
        if (MacdFastPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(MacdFastPeriod));
        if (MacdSlowPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(MacdSlowPeriod));
        if (MacdSignalPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(MacdSignalPeriod));
    }
}
