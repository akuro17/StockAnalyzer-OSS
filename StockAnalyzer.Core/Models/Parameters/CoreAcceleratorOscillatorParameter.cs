using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAcceleratorOscillatorParameter : CoreIndicatorParameterBase
{
    private int _fastPeriod = 5;

    [DisplayName("Fast Period")]
    [Description("Fast SMA period for Accelerator Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int FastPeriod
    {
        get => _fastPeriod;
        set => SetProperty(ref _fastPeriod, value);
    }

    private int _slowPeriod = 34;

    [DisplayName("Slow Period")]
    [Description("Slow SMA period for Accelerator Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int SlowPeriod
    {
        get => _slowPeriod;
        set => SetProperty(ref _slowPeriod, value);
    }

    private int _smoothPeriod = 5;

    [DisplayName("Smooth Period")]
    [Description("Smoothing period for Accelerator Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int SmoothPeriod
    {
        get => _smoothPeriod;
        set => SetProperty(ref _smoothPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({FastPeriod}, {SlowPeriod}, {SmoothPeriod})";

    public override void Validate()
    {
        if (FastPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(FastPeriod));
        if (SlowPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(SlowPeriod));
        if (SmoothPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(SmoothPeriod));
    }
}
