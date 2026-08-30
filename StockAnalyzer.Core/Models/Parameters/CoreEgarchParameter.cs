using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEgarchParameter : CoreIndicatorParameterBase
{
    private int _period = 14;

    [DisplayName("Period")]
    [Description("Estimation window period for EGARCH.")]
    [Category("Periods")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private double _omega = 0.1;

    [DisplayName("Omega (ω)")]
    [Description("Constant term in log-variance equation.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-10.0, 10.0)]
    public double Omega
    {
        get => _omega;
        set => SetProperty(ref _omega, value);
    }

    private double _alpha = 0.2;

    [DisplayName("Alpha (α)")]
    [Description("Magnitude coefficient of standardized residuals.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-5.0, 5.0)]
    public double Alpha
    {
        get => _alpha;
        set => SetProperty(ref _alpha, value);
    }

    private double _beta = 0.7;

    [DisplayName("Beta (β)")]
    [Description("Persistence coefficient of log-variance.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-1.0, 1.0)]
    public double Beta
    {
        get => _beta;
        set => SetProperty(ref _beta, value);
    }

    private double _gamma = 0.1;

    [DisplayName("Gamma (γ)")]
    [Description("Asymmetry / leverage effect coefficient.")]
    [Category("GARCH Coefficients")]
    [CoreParameterRange(-5.0, 5.0)]
    public double Gamma
    {
        get => _gamma;
        set => SetProperty(ref _gamma, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
