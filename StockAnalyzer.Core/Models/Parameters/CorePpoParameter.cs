using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CorePpoParameter : CoreIndicatorParameterBase
{
    private int _fastPeriod = 12;

    [DisplayName("Fast Period")]
    [Description("Fast EMA period for Percentage Price Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int FastPeriod
    {
        get => _fastPeriod;
        set => SetProperty(ref _fastPeriod, value);
    }

    private int _slowPeriod = 26;

    [DisplayName("Slow Period")]
    [Description("Slow EMA period for Percentage Price Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int SlowPeriod
    {
        get => _slowPeriod;
        set => SetProperty(ref _slowPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({FastPeriod}, {SlowPeriod})";

    public override void Validate()
    {
        if (FastPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(FastPeriod));
        if (SlowPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(SlowPeriod));
    }
}
