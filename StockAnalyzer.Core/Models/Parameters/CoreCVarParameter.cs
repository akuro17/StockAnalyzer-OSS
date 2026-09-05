using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreCVarParameter : CoreIndicatorParameterBase
{
    private int _period = 20;

    [DisplayName("Period")]
    [Description("Lookback period for CVaR / Expected Shortfall estimation.")]
    [CoreParameterRange(1, 1000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private double _confidenceLevel = 0.95;

    [DisplayName("Confidence Level")]
    [Description("Statistical confidence level (e.g. 0.95 for 95%).")]
    [CoreParameterRange(0.01, 1.0)]
    public double ConfidenceLevel
    {
        get => _confidenceLevel;
        set => SetProperty(ref _confidenceLevel, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {ConfidenceLevel:P})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (ConfidenceLevel <= 0 || ConfidenceLevel >= 1) throw new ArgumentOutOfRangeException(nameof(ConfidenceLevel), "ConfidenceLevel must be between 0 and 1");
    }
}
