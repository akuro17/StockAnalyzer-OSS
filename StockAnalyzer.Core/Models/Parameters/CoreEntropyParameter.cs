using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEntropyParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.EntropyDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.EntropyMinPeriod, IndicatorDefaultConstants.EntropyMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of log returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _binCount = IndicatorDefaultConstants.EntropyDefaultBinCount;

    [CoreParameterRange(IndicatorDefaultConstants.EntropyMinBinCount, IndicatorDefaultConstants.EntropyMaxBinCount)]
    [DisplayName("Bin Count")]
    [Description("Number of equal-width bins between the smallest and the largest return of the window. The result is divided by ln(Bin Count), so it is 0 to 1 whatever the bin count.")]
    [Category("Distribution")]
    public int BinCount
    {
        get => _binCount;
        set => SetProperty(ref _binCount, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {BinCount})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.EntropyMinPeriod, IndicatorDefaultConstants.EntropyMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(BinCount, IndicatorDefaultConstants.EntropyMinBinCount, IndicatorDefaultConstants.EntropyMaxBinCount, nameof(BinCount));
    }
}
