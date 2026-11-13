using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreTsiParameter : CoreIndicatorParameterBase
{
    private int _longPeriod = IndicatorDefaultConstants.TsiDefaultLongPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Long Period")]
    [Description("Period (bars) of the first (longer) EMA of the price change.")]
    [Category("Periods")]
    public int LongPeriod
    {
        get => _longPeriod;
        set => SetProperty(ref _longPeriod, value);
    }

    private int _shortPeriod = IndicatorDefaultConstants.TsiDefaultShortPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Short Period")]
    [Description("Period (bars) of the second (shorter) EMA applied to the first EMA.")]
    [Category("Periods")]
    public int ShortPeriod
    {
        get => _shortPeriod;
        set => SetProperty(ref _shortPeriod, value);
    }

    private int _signalPeriod = IndicatorDefaultConstants.TsiDefaultSignalPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Signal Period")]
    [Description("Period (bars) of the EMA of the TSI that forms the Signal line.")]
    [Category("Signal")]
    public int SignalPeriod
    {
        get => _signalPeriod;
        set => SetProperty(ref _signalPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({LongPeriod}, {ShortPeriod}, {SignalPeriod})";

    // Main needs 1 + (LongPeriod - 1) + (ShortPeriod - 1) bars; the Signal (an EMA of Main) needs SignalPeriod - 1 more.
    public override int GetRequiredWarmupBars() => 1 + (LongPeriod - 1) + (ShortPeriod - 1) + (SignalPeriod - 1);

    public override void Validate()
    {
        ThrowIfOutOfRange(LongPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(LongPeriod));
        ThrowIfOutOfRange(ShortPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(ShortPeriod));
        ThrowIfOutOfRange(SignalPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(SignalPeriod));
    }
}
