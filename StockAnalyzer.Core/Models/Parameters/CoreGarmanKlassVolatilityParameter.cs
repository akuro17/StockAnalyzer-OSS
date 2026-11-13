using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreGarmanKlassVolatilityParameter : CoreIndicatorParameterBase
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

    private bool _annualize;

    [DisplayName("Annualize")]
    [Description("Multiply the value by the square root of Periods Per Year. Off (default) = a ratio per bar, whose meaning does not change with the chart time frame. For an annual figure use 252 on daily, 52 on weekly and 12 on monthly bars.")]
    public bool Annualize
    {
        get => _annualize;
        set => SetProperty(ref _annualize, value);
    }

    private int _periodsPerYear = IndicatorDefaultConstants.PeriodsPerYear;

    [DisplayName("Periods Per Year")]
    [Description("Bars per year for the annualisation (252 daily, 52 weekly, 12 monthly). Used only when Annualize is on.")]
    [CoreParameterRange(IndicatorDefaultConstants.MinPeriodsPerYear, IndicatorDefaultConstants.MaxPeriodsPerYear)]
    public int PeriodsPerYear
    {
        get => _periodsPerYear;
        set => SetProperty(ref _periodsPerYear, value);
    }

    public override string GetDisplayName(string type) => Period == IndicatorDefaultConstants.GarmanKlassPeriod && !Annualize ? type : $"{type} ({Period})";

    public override void Validate()
    {
        if (Period < IndicatorDefaultConstants.GarmanKlassMinPeriod || Period > IndicatorDefaultConstants.GarmanKlassMaxPeriod)
            throw new ArgumentOutOfRangeException(nameof(Period));
        if (PeriodsPerYear < IndicatorDefaultConstants.MinPeriodsPerYear || PeriodsPerYear > IndicatorDefaultConstants.MaxPeriodsPerYear)
            throw new ArgumentOutOfRangeException(nameof(PeriodsPerYear));
    }
}
