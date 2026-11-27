using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>The curve family of the Nonlinear Regression indicator.</summary>
public enum NonlinearRegressionModel
{
    [Description("Exponential: a exp(b x)")]
    Exponential = 0,

    [Description("Logarithmic: a + b ln(x)")]
    Logarithmic = 1,

    [Description("Power: a x^b")]
    Power = 2,

    [Description("Logistic: window mapped to (0, 1)")]
    Logistic = 3,
}

public class CoreNonlinearRegressionParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.NonlinearRegressionDefaultPeriod;
    private NonlinearRegressionModel _modelType = NonlinearRegressionModel.Exponential;

    [CoreParameterRange(IndicatorDefaultConstants.NonlinearRegressionMinPeriod, IndicatorDefaultConstants.NonlinearRegressionMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of prices in the window (bars). A window containing an unusable price gives no value; Exponential and Power also need every price above zero.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Model Type")]
    [Description("Curve fitted to the window, with x = 1 .. Period: Exponential a exp(b x), Logarithmic a + b ln(x), Power a x^b, or Logistic (the window is mapped linearly into the open interval (0, 1), fitted with a logistic regression and mapped back to price).")]
    [Category("Estimation")]
    public NonlinearRegressionModel ModelType
    {
        get => _modelType;
        set => SetProperty(ref _modelType, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {ModelType})";

    // The first value is at bar index Period - 1.
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.NonlinearRegressionMinPeriod, IndicatorDefaultConstants.NonlinearRegressionMaxPeriod, nameof(Period));
        if (!System.Enum.IsDefined(ModelType))
        {
            throw new ArgumentOutOfRangeException(nameof(ModelType));
        }
    }
}
