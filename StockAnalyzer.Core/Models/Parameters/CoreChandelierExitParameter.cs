using System.Globalization;
using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreChandelierExitParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.ChandelierExitDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Period")]
    [Description("Period (bars) of the highest high, the lowest low and the Wilder ATR.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private decimal _multiplier = IndicatorDefaultConstants.ChandelierExitDefaultMultiplier;

    [CoreParameterRange(IndicatorDefaultConstants.ChandelierExitMinMultiplier, IndicatorDefaultConstants.ChandelierExitMaxMultiplier)]
    [DisplayName("Multiplier")]
    [Description("ATR multiplier: the long stop is the highest high minus Multiplier x ATR, the short stop is the lowest low plus Multiplier x ATR.")]
    [Category("Multiplier")]
    public decimal Multiplier
    {
        get => _multiplier;
        set => SetProperty(ref _multiplier, value);
    }

    public override string GetDisplayName(string type) => string.Create(CultureInfo.InvariantCulture, $"{type} ({Period}, {Multiplier})");

    // The first value (Main and ShortStop) is at bar index Period (the Wilder ATR needs Period true ranges).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(Period));
        ThrowIfOutOfRange(Multiplier, IndicatorDefaultConstants.ChandelierExitMinMultiplier, IndicatorDefaultConstants.ChandelierExitMaxMultiplier, nameof(Multiplier));
    }
}
