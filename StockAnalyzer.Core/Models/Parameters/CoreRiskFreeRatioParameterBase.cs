using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The settings shared by the risk ratios that subtract a risk-free rate and may be annualised (Rolling Sharpe, Treynor): <see cref="Annualize"/> (default off, so the per-bar value does not
/// depend on the chart time frame), <see cref="PeriodsPerYear"/> and <see cref="AnnualRiskFreeRate"/>. <see cref="PeriodsPerYear"/> is also the divisor of the per-bar risk-free rate
/// (<c>rate / PeriodsPerYear</c>), so it matters when the rate is not zero even if <see cref="Annualize"/> is off. It is a user setting and is never derived from the chart time frame.
/// </summary>
public abstract class CoreRiskFreeRatioParameterBase : CoreIndicatorParameterBase
{
    private bool _annualize = IndicatorDefaultConstants.RiskRatioAnnualizeDefault;
    private int _periodsPerYear = IndicatorDefaultConstants.PeriodsPerYear;
    private decimal _annualRiskFreeRate = IndicatorDefaultConstants.RiskRatioDefaultAnnualRiskFreeRate;

    [DisplayName("Annualize")]
    [Description("Scale the ratio to a yearly figure (Sharpe: times the square root of Periods Per Year; Treynor: times Periods Per Year). Off (default) = the value per bar, whose meaning does not change with the chart time frame.")]
    [Category("Estimation")]
    public bool Annualize
    {
        get => _annualize;
        set => SetProperty(ref _annualize, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.MinPeriodsPerYear, IndicatorDefaultConstants.MaxPeriodsPerYear)]
    [DisplayName("Periods Per Year")]
    [Description("Bars per year (252 daily, 52 weekly, 12 monthly). Divides the annual risk-free rate into a per-bar rate and, when Annualize is on, scales the ratio.")]
    [Category("Estimation")]
    public int PeriodsPerYear
    {
        get => _periodsPerYear;
        set => SetProperty(ref _periodsPerYear, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.RiskRatioMinAnnualRiskFreeRate, IndicatorDefaultConstants.RiskRatioMaxAnnualRiskFreeRate)]
    [DisplayName("Annual Risk-Free Rate")]
    [Description("Annual risk-free rate as a fraction (0.03 = 3 %). Converted to a per-bar rate by dividing by Periods Per Year.")]
    [Category("Estimation")]
    public decimal AnnualRiskFreeRate
    {
        get => _annualRiskFreeRate;
        set => SetProperty(ref _annualRiskFreeRate, value);
    }

    /// <summary>Range check of the three shared settings for the <c>Validate()</c> of the derived classes.</summary>
    protected void ValidateRiskFreeAnnualization()
    {
        ThrowIfOutOfRange(PeriodsPerYear, IndicatorDefaultConstants.MinPeriodsPerYear, IndicatorDefaultConstants.MaxPeriodsPerYear, nameof(PeriodsPerYear));
        ThrowIfOutOfRange(AnnualRiskFreeRate, IndicatorDefaultConstants.RiskRatioMinAnnualRiskFreeRate, IndicatorDefaultConstants.RiskRatioMaxAnnualRiskFreeRate, nameof(AnnualRiskFreeRate));
    }
}
