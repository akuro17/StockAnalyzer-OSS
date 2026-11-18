using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSkewnessKurtosisParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.SkewnessKurtosisDefaultPeriod;
    private bool _useLogReturn = IndicatorDefaultConstants.SkewnessKurtosisDefaultUseLogReturn;

    [CoreParameterRange(IndicatorDefaultConstants.SkewnessKurtosisMinPeriod, IndicatorDefaultConstants.SkewnessKurtosisMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of one-bar returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Use Log Return")]
    [Description("On: log returns ln(P / P[-1]). Off: simple returns (P - P[-1]) / P[-1].")]
    [Category("Estimation")]
    public bool UseLogReturn
    {
        get => _useLogReturn;
        set => SetProperty(ref _useLogReturn, value);
    }

    public override string GetDisplayName(string type) => UseLogReturn ? $"{type} ({Period})" : $"{type} ({Period}, simple)";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.SkewnessKurtosisMinPeriod, IndicatorDefaultConstants.SkewnessKurtosisMaxPeriod, nameof(Period));
    }
}
