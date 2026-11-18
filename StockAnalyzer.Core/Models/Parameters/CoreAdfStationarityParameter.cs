using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAdfStationarityParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.AdfStationarityDefaultPeriod;
    private int _lagOrder = IndicatorDefaultConstants.AdfStationarityDefaultLagOrder;
    private bool _useLogPrice = IndicatorDefaultConstants.AdfStationarityDefaultUseLogPrice;

    [CoreParameterRange(IndicatorDefaultConstants.AdfStationarityMinPeriod, IndicatorDefaultConstants.AdfStationarityMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of prices in the window (bars). A window shorter than 2 x Lag Order + 5 has no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.AdfStationarityMinLagOrder, IndicatorDefaultConstants.AdfStationarityMaxLagOrder)]
    [DisplayName("Lag Order")]
    [Description("Number of lagged price differences added to the Dickey-Fuller regression (0 = plain Dickey-Fuller test with a constant).")]
    [Category("Estimation")]
    public int LagOrder
    {
        get => _lagOrder;
        set => SetProperty(ref _lagOrder, value);
    }

    [DisplayName("Use Log Price")]
    [Description("Test the natural logarithm of the price (a price that is not positive gives no value). Off tests the price itself.")]
    [Category("Estimation")]
    public bool UseLogPrice
    {
        get => _useLogPrice;
        set => SetProperty(ref _useLogPrice, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {LagOrder})";

    // The first value is at bar index Period - 1 (price input).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.AdfStationarityMinPeriod, IndicatorDefaultConstants.AdfStationarityMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(LagOrder, IndicatorDefaultConstants.AdfStationarityMinLagOrder, IndicatorDefaultConstants.AdfStationarityMaxLagOrder, nameof(LagOrder));
    }
}
