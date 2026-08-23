using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAcceleratorOscillatorParameter : CoreIndicatorParameterBase
{
    [DisplayName("Fast Period")]
    [Description("Fast SMA period for Accelerator Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int FastPeriod { get; set; } = 5;

    [DisplayName("Slow Period")]
    [Description("Slow SMA period for Accelerator Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int SlowPeriod { get; set; } = 34;

    [DisplayName("Smooth Period")]
    [Description("Smoothing period for Accelerator Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int SmoothPeriod { get; set; } = 5;

    public override string GetDisplayName(string type) => $"{type} ({FastPeriod}, {SlowPeriod}, {SmoothPeriod})";

    public override void Validate()
    {
        if (FastPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(FastPeriod));
        if (SlowPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(SlowPeriod));
        if (SmoothPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(SmoothPeriod));
    }
}
