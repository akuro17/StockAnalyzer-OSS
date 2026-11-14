using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The opt-in annualisation shared by the volatility indicators with a per-bar output (range estimators, GARCH(1,1), Garman-Klass): <see cref="Annualize"/> (default off) and
/// <see cref="PeriodsPerYear"/> (used only when <see cref="Annualize"/> is on). The factor itself is <c>IndicatorCalculationHelper.AnnualizationFactor</c>.
/// </summary>
public abstract class CoreAnnualizableVolatilityParameterBase : CoreIndicatorParameterBase
{
    private bool _annualize;

    [DisplayName("Annualize")]
    [Description("Multiply the value by the square root of Periods Per Year. Off (default) = the value per bar, whose meaning does not change with the chart time frame. For an annual figure use 252 on daily, 52 on weekly and 12 on monthly bars.")]
    public bool Annualize
    {
        get => _annualize;
        set => SetProperty(ref _annualize, value);
    }

    private int _periodsPerYear = IndicatorDefaultConstants.PeriodsPerYear;

    [CoreParameterRange(IndicatorDefaultConstants.MinPeriodsPerYear, IndicatorDefaultConstants.MaxPeriodsPerYear)]
    [DisplayName("Periods Per Year")]
    [Description("Bars per year for the annualisation (252 daily, 52 weekly, 12 monthly). Used only when Annualize is on.")]
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
}
