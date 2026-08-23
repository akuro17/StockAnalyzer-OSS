using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEgarchParameter : CoreIndicatorParameterBase
{
    [DisplayName("Period")]
    [Description("Estimation window period for EGARCH.")]
    [Category("Periods")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period { get; set; } = 14;

    [DisplayName("Omega (ω)")]
    [Description("Constant term in log-variance equation.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-10.0, 10.0)]
    public double Omega { get; set; } = 0.1;

    [DisplayName("Alpha (α)")]
    [Description("Magnitude coefficient of standardized residuals.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-5.0, 5.0)]
    public double Alpha { get; set; } = 0.2;

    [DisplayName("Beta (β)")]
    [Description("Persistence coefficient of log-variance.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-1.0, 1.0)]
    public double Beta { get; set; } = 0.7;

    [DisplayName("Gamma (γ)")]
    [Description("Asymmetry / leverage effect coefficient.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-5.0, 5.0)]
    public double Gamma { get; set; } = 0.1;

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
