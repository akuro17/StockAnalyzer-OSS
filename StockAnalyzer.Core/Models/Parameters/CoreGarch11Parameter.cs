using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreGarch11Parameter : CoreAnnualizableVolatilityParameterBase
{
    private int _period = IndicatorDefaultConstants.Garch11DefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.Garch11MinPeriod, IndicatorDefaultConstants.Garch11MaxPeriod)]
    [DisplayName("Period")]
    [Description("Estimation window (bars). Each re-estimation fits the model on the latest Period returns; the first value appears at bar Period, so earlier bars stay empty. A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _refitInterval = IndicatorDefaultConstants.Garch11DefaultRefitInterval;

    [CoreParameterRange(IndicatorDefaultConstants.Garch11MinRefitInterval, IndicatorDefaultConstants.Garch11MaxRefitInterval)]
    [DisplayName("Refit Interval")]
    [Description("Bars between re-estimations. Between refits the coefficients are kept fixed and the variance is updated with each new return.")]
    [Category("Periods")]
    public int RefitInterval
    {
        get => _refitInterval;
        set => SetProperty(ref _refitInterval, value);
    }

    private int _maxIterations = IndicatorDefaultConstants.Garch11DefaultMaxIterations;

    [CoreParameterRange(IndicatorDefaultConstants.Garch11MinMaxIterations, IndicatorDefaultConstants.Garch11MaxMaxIterations)]
    [DisplayName("Max Iterations")]
    [Description("Optimizer iteration limit per estimation run. When the optimizer does not converge or leaves the constraints, the fixed starting coefficients of the model are used for that window.")]
    [Category("Estimation")]
    public int MaxIterations
    {
        get => _maxIterations;
        set => SetProperty(ref _maxIterations, value);
    }

    private bool _enableGjrLeverage;

    [DisplayName("Enable GJR Leverage")]
    [Description("When true, estimates Glosten-Jagannathan-Runkle GJR-GARCH(1,1) capturing asymmetric leverage (gamma) on negative return shocks.")]
    [Category("Model")]
    public bool EnableGjrLeverage
    {
        get => _enableGjrLeverage;
        set => SetProperty(ref _enableGjrLeverage, value);
    }

    public override string GetDisplayName(string type) => EnableGjrLeverage ? $"{type} ({Period}, GJR)" : $"{type} ({Period})";

    // The first value is at bar index Period.
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.Garch11MinPeriod, IndicatorDefaultConstants.Garch11MaxPeriod, nameof(Period));
        ThrowIfOutOfRange(RefitInterval, IndicatorDefaultConstants.Garch11MinRefitInterval, IndicatorDefaultConstants.Garch11MaxRefitInterval, nameof(RefitInterval));
        ThrowIfOutOfRange(MaxIterations, IndicatorDefaultConstants.Garch11MinMaxIterations, IndicatorDefaultConstants.Garch11MaxMaxIterations, nameof(MaxIterations));
        ValidateAnnualization();
    }
}
