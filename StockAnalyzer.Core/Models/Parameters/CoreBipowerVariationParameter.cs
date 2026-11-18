using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreBipowerVariationParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.BipowerVariationDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.BipowerVariationMinPeriod, IndicatorDefaultConstants.BipowerVariationMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of log returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.BipowerVariationMinPeriod, IndicatorDefaultConstants.BipowerVariationMaxPeriod, nameof(Period));
    }
}
