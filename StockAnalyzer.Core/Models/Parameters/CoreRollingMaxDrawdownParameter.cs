using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreRollingMaxDrawdownParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.RollingMaxDrawdownDefaultPeriod;
    private bool _asPercent = IndicatorDefaultConstants.RollingMaxDrawdownDefaultAsPercent;

    [CoreParameterRange(IndicatorDefaultConstants.RollingMaxDrawdownMinPeriod, IndicatorDefaultConstants.RollingMaxDrawdownMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of prices in the window (bars). A window containing an unusable or non-positive price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("As Percent")]
    [Description("On (default): the drawdown in percent (0 to 100). Off: the drawdown as a ratio of the window peak (0 to 1). The value is a positive depth.")]
    [Category("Display")]
    public bool AsPercent
    {
        get => _asPercent;
        set => SetProperty(ref _asPercent, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    // The first value is at bar index Period - 1.
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.RollingMaxDrawdownMinPeriod, IndicatorDefaultConstants.RollingMaxDrawdownMaxPeriod, nameof(Period));
    }
}
