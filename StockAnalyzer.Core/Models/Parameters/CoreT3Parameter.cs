using System.Globalization;
using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreT3Parameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.T3DefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Period")]
    [Description("Period (bars) of each of the six cascaded exponential moving averages.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private decimal _volumeFactor = IndicatorDefaultConstants.T3DefaultVolumeFactor;

    [CoreParameterRange(IndicatorDefaultConstants.T3MinVolumeFactor, IndicatorDefaultConstants.T3MaxVolumeFactor)]
    [DisplayName("Volume Factor")]
    [Description("Tillson volume factor v in [0, 1]. 0 gives the triple EMA, larger values reduce lag and increase overshoot.")]
    [Category("Smoothing")]
    public decimal VolumeFactor
    {
        get => _volumeFactor;
        set => SetProperty(ref _volumeFactor, value);
    }

    public override string GetDisplayName(string type) => string.Create(CultureInfo.InvariantCulture, $"{type} ({Period}, {VolumeFactor})");

    // The first value is at bar index (stage count) x (Period - 1) for a series without gaps.
    public override int GetRequiredWarmupBars() => IndicatorDefaultConstants.T3EmaStageCount * (Period - 1);

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(Period));
        ThrowIfOutOfRange(VolumeFactor, IndicatorDefaultConstants.T3MinVolumeFactor, IndicatorDefaultConstants.T3MaxVolumeFactor, nameof(VolumeFactor));
    }
}
