using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameters shared by the range-based volatility estimators (Parkinson, Rogers-Satchell): window length; the opt-in annualisation comes from the base class.
/// Output unit: percentage per bar (annual percentage when <see cref="CoreAnnualizableVolatilityParameterBase.Annualize"/> is on).
/// </summary>
public abstract class CoreRangeVolatilityParameterBase : CoreAnnualizableVolatilityParameterBase
{
    private int _period = IndicatorDefaultConstants.RangeVolatilityDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.RangeVolatilityMinPeriod, IndicatorDefaultConstants.RangeVolatilityMaxPeriod)]
    [DisplayName("Period")]
    [Description("Window length in bars. A window containing a bar with an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    // The first value is at bar index Period - 1.
    public override int GetRequiredWarmupBars() => Period - 1;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.RangeVolatilityMinPeriod, IndicatorDefaultConstants.RangeVolatilityMaxPeriod, nameof(Period));
        ValidateAnnualization();
    }
}
