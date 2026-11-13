using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreStochRsiParameter : CoreIndicatorParameterBase
{
    private int _rsiPeriod = IndicatorDefaultConstants.StochRsiDefaultRsiPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("RSI Period")]
    [Description("Period (bars) of the Wilder RSI the stochastic is applied to.")]
    [Category("Periods")]
    public int RsiPeriod
    {
        get => _rsiPeriod;
        set => SetProperty(ref _rsiPeriod, value);
    }

    private int _stochPeriod = IndicatorDefaultConstants.StochRsiDefaultStochPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Stochastic Period")]
    [Description("Number of bars of RSI over which the highest and lowest RSI are taken.")]
    [Category("Periods")]
    public int StochPeriod
    {
        get => _stochPeriod;
        set => SetProperty(ref _stochPeriod, value);
    }

    private int _kPeriod = IndicatorDefaultConstants.StochRsiDefaultKPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("%K Smoothing")]
    [Description("Period (bars) of the simple moving average of the raw StochRSI that forms %K (the Main series).")]
    [Category("Smoothing")]
    public int KPeriod
    {
        get => _kPeriod;
        set => SetProperty(ref _kPeriod, value);
    }

    private int _dPeriod = IndicatorDefaultConstants.StochRsiDefaultDPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("%D Smoothing")]
    [Description("Period (bars) of the simple moving average of %K that forms %D (the PercentD series).")]
    [Category("Smoothing")]
    public int DPeriod
    {
        get => _dPeriod;
        set => SetProperty(ref _dPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({RsiPeriod}, {StochPeriod}, {KPeriod}, {DPeriod})";

    // The first RSI is at bar index RsiPeriod; %K needs StochPeriod - 1 and KPeriod - 1 more bars, %D another DPeriod - 1.
    public override int GetRequiredWarmupBars() => RsiPeriod + (StochPeriod - 1) + (KPeriod - 1) + (DPeriod - 1);

    public override void Validate()
    {
        ThrowIfOutOfRange(RsiPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(RsiPeriod));
        ThrowIfOutOfRange(StochPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(StochPeriod));
        ThrowIfOutOfRange(KPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(KPeriod));
        ThrowIfOutOfRange(DPeriod, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(DPeriod));
    }
}
