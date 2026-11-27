using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreTrackingErrorParameter : CoreAnnualizedCrossTickerParameterBase
{
    private int _period = IndicatorDefaultConstants.TrackingErrorDefaultPeriod;
    private bool _asPercent = IndicatorDefaultConstants.TrackingErrorDefaultAsPercent;

    [CoreParameterRange(IndicatorDefaultConstants.TrackingErrorMinPeriod, IndicatorDefaultConstants.TrackingErrorMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of return pairs in the window (bars). A window with a missing price of either symbol gives no value; a constant active return gives 0.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("As Percent")]
    [Description("On (default): the tracking error in percent. Off: as a fraction (0.02 = 2 %).")]
    [Category("Display")]
    public bool AsPercent
    {
        get => _asPercent;
        set => SetProperty(ref _asPercent, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}{ComparisonSuffix()}{AnnualizationSuffix()})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.TrackingErrorMinPeriod, IndicatorDefaultConstants.TrackingErrorMaxPeriod, nameof(Period));
        ValidateAnnualization();
    }
}
