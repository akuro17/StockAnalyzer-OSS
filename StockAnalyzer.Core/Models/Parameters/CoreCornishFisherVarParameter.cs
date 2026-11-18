using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreCornishFisherVarParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.CornishFisherVarDefaultPeriod;
    private double _confidenceLevel = IndicatorDefaultConstants.CornishFisherVarDefaultConfidenceLevel;

    [CoreParameterRange(IndicatorDefaultConstants.CornishFisherVarMinPeriod, IndicatorDefaultConstants.CornishFisherVarMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of simple returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.CornishFisherVarMinConfidenceLevel, IndicatorDefaultConstants.CornishFisherVarMaxConfidenceLevel)]
    [DisplayName("Confidence Level")]
    [Description("Confidence level c of the one-bar Value at Risk; the VaR is the (1 - c) quantile of the return (a loss is negative).")]
    [Category("Estimation")]
    public double ConfidenceLevel
    {
        get => _confidenceLevel;
        set => SetProperty(ref _confidenceLevel, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {ConfidenceLevel})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.CornishFisherVarMinPeriod, IndicatorDefaultConstants.CornishFisherVarMaxPeriod, nameof(Period));
        if (!(ConfidenceLevel >= IndicatorDefaultConstants.CornishFisherVarMinConfidenceLevel && ConfidenceLevel <= IndicatorDefaultConstants.CornishFisherVarMaxConfidenceLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(ConfidenceLevel));
        }
    }
}
