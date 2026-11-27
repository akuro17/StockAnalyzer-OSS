using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreInformationRatioParameter : CoreAnnualizedCrossTickerParameterBase
{
    private int _period = IndicatorDefaultConstants.InformationRatioDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.InformationRatioMinPeriod, IndicatorDefaultConstants.InformationRatioMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of return pairs in the window (bars). A window with a missing price of either symbol, or with a constant active return, gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}{ComparisonSuffix()}{AnnualizationSuffix()})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.InformationRatioMinPeriod, IndicatorDefaultConstants.InformationRatioMaxPeriod, nameof(Period));
        ValidateAnnualization();
    }
}
