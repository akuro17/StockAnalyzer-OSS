using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKvoParameter : CoreIndicatorParameterBase
{
    private int _fastPeriod = IndicatorDefaultConstants.KvoDefaultFastPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Fast Period")]
    [Description("Period (bars) of the fast EMA of the volume force.")]
    [Category("Periods")]
    public int FastPeriod
    {
        get => _fastPeriod;
        set => SetProperty(ref _fastPeriod, value);
    }

    private int _slowPeriod = IndicatorDefaultConstants.KvoDefaultSlowPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Slow Period")]
    [Description("Period (bars) of the slow EMA of the volume force.")]
    [Category("Periods")]
    public int SlowPeriod
    {
        get => _slowPeriod;
        set => SetProperty(ref _slowPeriod, value);
    }

    private int _signalPeriod = IndicatorDefaultConstants.KvoDefaultSignalPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Signal Period")]
    [Description("Period (bars) of the EMA of the KVO that forms the Signal line.")]
    [Category("Signal")]
    public int SignalPeriod
    {
        get => _signalPeriod;
        set => SetProperty(ref _signalPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({FastPeriod}, {SlowPeriod}, {SignalPeriod})";

    // The volume force starts at bar index 1; Main needs the slower of the two EMAs (index max(Fast, Slow)); the Signal needs SignalPeriod - 1 more bars.
    public override int GetRequiredWarmupBars() => Math.Max(FastPeriod, SlowPeriod) + (SignalPeriod - 1);

    public override void Validate()
    {
        ThrowIfOutOfRange(FastPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(FastPeriod));
        ThrowIfOutOfRange(SlowPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(SlowPeriod));
        ThrowIfOutOfRange(SignalPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(SignalPeriod));
    }
}
