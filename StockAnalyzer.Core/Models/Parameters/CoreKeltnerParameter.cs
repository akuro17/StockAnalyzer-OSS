using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKeltnerParameter : CoreIndicatorParameterBase
{
    private int _emaPeriod = 20;

    [DisplayName("EMA Period")]
    [Description("Exponential moving average period for center line.")]
    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    public int EmaPeriod
    {
        get => _emaPeriod;
        set => SetProperty(ref _emaPeriod, value);
    }

    private int _atrPeriod = 10;

    [DisplayName("ATR Period")]
    [Description("Average True Range calculation period for channel width.")]
    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    public int AtrPeriod
    {
        get => _atrPeriod;
        set => SetProperty(ref _atrPeriod, value);
    }

    private decimal _multiplier = 2.0m;

    [DisplayName("Multiplier")]
    [Description("ATR multiplier for channel bands.")]
    [CoreParameterRange(IndicatorDefaultConstants.KeltnerMinMultiplier, IndicatorDefaultConstants.KeltnerMaxMultiplier)]
    public decimal Multiplier
    {
        get => _multiplier;
        set => SetProperty(ref _multiplier, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({EmaPeriod}, {AtrPeriod}, {Multiplier})";

    // The first non-null output: the middle line (SMA seed) starts at index EmaPeriod - 1 and the ATR at index AtrPeriod; the channel needs both
    // (the WebAiExpansion convention: the first non-null index, not a convergence length).
    public override int GetRequiredWarmupBars() => Math.Max(EmaPeriod - 1, AtrPeriod);

    public override void Validate()
    {
        ThrowIfOutOfRange(EmaPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(EmaPeriod));
        ThrowIfOutOfRange(AtrPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(AtrPeriod));
        ThrowIfOutOfRange(Multiplier, IndicatorDefaultConstants.KeltnerMinMultiplier, IndicatorDefaultConstants.KeltnerMaxMultiplier, nameof(Multiplier));
    }
}
