using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAwesomeOscillatorParameter : CoreIndicatorParameterBase
{
    private int _fastPeriod = 5;

    [DisplayName("Fast Period")]
    [Description("Fast SMA period for Awesome Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int FastPeriod
    {
        get => _fastPeriod;
        set => SetProperty(ref _fastPeriod, value);
    }

    private int _slowPeriod = 34;

    [DisplayName("Slow Period")]
    [Description("Slow SMA period for Awesome Oscillator.")]
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
