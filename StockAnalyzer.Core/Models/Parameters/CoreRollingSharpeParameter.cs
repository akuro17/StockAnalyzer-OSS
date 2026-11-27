using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreRollingSharpeParameter : CoreRiskFreeRatioParameterBase
{
    private int _period = IndicatorDefaultConstants.RollingSharpeDefaultPeriod;
    private bool _useLogReturn = IndicatorDefaultConstants.RollingSharpeDefaultUseLogReturn;

    [CoreParameterRange(IndicatorDefaultConstants.RollingSharpeMinPeriod, IndicatorDefaultConstants.RollingSharpeMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of returns in the window (bars). A window containing an unusable price, or a flat window, gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Use Log Return")]
    [Description("Off (default): simple returns (P - P_prev) / P_prev. On: log returns ln(P / P_prev).")]
    [Category("Estimation")]
    public bool UseLogReturn
    {
        get => _useLogReturn;
        set => SetProperty(ref _useLogReturn, value);
    }

    public override string GetDisplayName(string type) => Annualize ? $"{type} ({Period}, Ann)" : $"{type} ({Period})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.RollingSharpeMinPeriod, IndicatorDefaultConstants.RollingSharpeMaxPeriod, nameof(Period));
        ValidateRiskFreeAnnualization();
    }
}
