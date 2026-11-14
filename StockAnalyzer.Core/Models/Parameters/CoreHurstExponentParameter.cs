using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreHurstExponentParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.HurstDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.HurstMinPeriod, IndicatorDefaultConstants.HurstMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of log returns in the window (bars). The minimum keeps at least three sub-interval lengths, so that the regression is defined. A window containing an unusable price gives no value.")]
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
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.HurstMinPeriod, IndicatorDefaultConstants.HurstMaxPeriod, nameof(Period));
    }
}
