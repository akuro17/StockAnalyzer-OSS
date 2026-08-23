using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreCVarParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Lookback period for CVaR / Expected Shortfall estimation.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 20;

    [DisplayName("Confidence Level")]
    [Description("Statistical confidence level (e.g. 0.95 for 95%).")]
    [CoreParameterRange(0.01, 1.0)]
    public double ConfidenceLevel { get; set; } = 0.95;

    public override string GetDisplayName(string type) => $"{type} ({Period}, {ConfidenceLevel:P})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (ConfidenceLevel <= 0 || ConfidenceLevel >= 1) throw new ArgumentOutOfRangeException(nameof(ConfidenceLevel), "ConfidenceLevel must be between 0 and 1");
    }
}
