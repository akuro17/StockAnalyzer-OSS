using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreGarmanKlassVolatilityParameter : CoreAnnualizableVolatilityParameterBase
{
    private int _period = IndicatorDefaultConstants.GarmanKlassPeriod;

    [DisplayName("Period")]
    [Description("Bars aggregated: 1 = the value of each bar alone (default). Above 1 = the square root of the mean of the per-bar Garman-Klass variances over the last Period bars (not the mean of the per-bar standard deviations); a window containing a bar with a non-positive price gives no value.")]
    [CoreParameterRange(IndicatorDefaultConstants.GarmanKlassMinPeriod, IndicatorDefaultConstants.GarmanKlassMaxPeriod)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => Period == IndicatorDefaultConstants.GarmanKlassPeriod && !Annualize ? type : $"{type} ({Period})";

    public override void Validate()
    {
        if (Period < IndicatorDefaultConstants.GarmanKlassMinPeriod || Period > IndicatorDefaultConstants.GarmanKlassMaxPeriod)
            throw new ArgumentOutOfRangeException(nameof(Period));
        ValidateAnnualization();
    }
}
