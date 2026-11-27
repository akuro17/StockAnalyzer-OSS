using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The opt-in annualisation of a two-symbol risk ratio: <see cref="Annualize"/> (default off, so the per-bar value does not depend on the chart time frame) and
/// <see cref="PeriodsPerYear"/> (a user setting, never derived from the chart time frame).
/// </summary>
public abstract class CoreAnnualizedCrossTickerParameterBase : CoreCrossTickerReturnParameterBase
{
    private bool _annualize = IndicatorDefaultConstants.RiskRatioAnnualizeDefault;
    private int _periodsPerYear = IndicatorDefaultConstants.PeriodsPerYear;

    [DisplayName("Annualize")]
    [Description("Scale the value to a yearly figure (Information Ratio and Tracking Error: times the square root of Periods Per Year; Treynor: times Periods Per Year). Off (default) = the value per bar, whose meaning does not change with the chart time frame.")]
    [Category("Estimation")]
    public bool Annualize
    {
        get => _annualize;
        set => SetProperty(ref _annualize, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.MinPeriodsPerYear, IndicatorDefaultConstants.MaxPeriodsPerYear)]
    [DisplayName("Periods Per Year")]
    [Description("Bars per year (252 daily, 52 weekly, 12 monthly). Scales the value when Annualize is on and divides the annual risk-free rate into a per-bar rate.")]
    [Category("Estimation")]
    public int PeriodsPerYear
    {
        get => _periodsPerYear;
        set => SetProperty(ref _periodsPerYear, value);
    }

    /// <summary>Range check of <see cref="PeriodsPerYear"/> for the <c>Validate()</c> of the derived classes.</summary>
    protected void ValidateAnnualization()
    {
        ThrowIfOutOfRange(PeriodsPerYear, IndicatorDefaultConstants.MinPeriodsPerYear, IndicatorDefaultConstants.MaxPeriodsPerYear, nameof(PeriodsPerYear));
    }

    /// <summary>The label suffix of the annualisation.</summary>
    protected string AnnualizationSuffix() => Annualize ? ", Ann" : string.Empty;
}
