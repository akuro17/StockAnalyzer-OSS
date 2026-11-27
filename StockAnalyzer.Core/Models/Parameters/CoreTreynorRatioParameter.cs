using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreTreynorRatioParameter : CoreAnnualizedCrossTickerParameterBase
{
    private int _period = IndicatorDefaultConstants.TreynorRatioDefaultPeriod;
    private decimal _annualRiskFreeRate = IndicatorDefaultConstants.RiskRatioDefaultAnnualRiskFreeRate;

    [CoreParameterRange(IndicatorDefaultConstants.TreynorRatioMinPeriod, IndicatorDefaultConstants.TreynorRatioMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of return pairs in the window (bars). A window with a missing price of either symbol, or a benchmark with no variance, gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
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

    public override string GetDisplayName(string type) => $"{type} ({Period}{ComparisonSuffix()}{AnnualizationSuffix()})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.TreynorRatioMinPeriod, IndicatorDefaultConstants.TreynorRatioMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(AnnualRiskFreeRate, IndicatorDefaultConstants.RiskRatioMinAnnualRiskFreeRate, IndicatorDefaultConstants.RiskRatioMaxAnnualRiskFreeRate, nameof(AnnualRiskFreeRate));
        ValidateAnnualization();
    }
}
