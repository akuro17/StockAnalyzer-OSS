using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKpssStationarityParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.KpssStationarityDefaultPeriod;
    private bool _useReturns = IndicatorDefaultConstants.KpssStationarityDefaultUseReturns;

    [CoreParameterRange(IndicatorDefaultConstants.KpssStationarityMinPeriod, IndicatorDefaultConstants.KpssStationarityMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of observations in the window (bars; log returns when Use Returns is on). A window containing an unusable price, or a flat window, gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Use Returns")]
    [Description("Off (default): test the level stationarity of the price. On: test the level stationarity of the log return (the price series needs one more bar).")]
    [Category("Estimation")]
    public bool UseReturns
    {
        get => _useReturns;
        set => SetProperty(ref _useReturns, value);
    }

    public override string GetDisplayName(string type) => UseReturns ? $"{type} ({Period}, returns)" : $"{type} ({Period})";

    // The first value is at bar index Period - 1 (price input) or Period (returns input: Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => UseReturns ? Period + 1 : Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.KpssStationarityMinPeriod, IndicatorDefaultConstants.KpssStationarityMaxPeriod, nameof(Period));
    }
}
