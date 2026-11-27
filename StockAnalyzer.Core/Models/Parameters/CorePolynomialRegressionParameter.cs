using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CorePolynomialRegressionParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.PolynomialRegressionDefaultPeriod;
    private int _degree = IndicatorDefaultConstants.PolynomialRegressionDefaultDegree;
    private int _forecastHorizon = IndicatorDefaultConstants.PolynomialRegressionDefaultForecastHorizon;

    [CoreParameterRange(IndicatorDefaultConstants.PolynomialRegressionMinPeriod, IndicatorDefaultConstants.PolynomialRegressionMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of prices in the window (bars). Must be at least Degree + 2, otherwise no bar has a value. A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.PolynomialRegressionMinDegree, IndicatorDefaultConstants.PolynomialRegressionMaxDegree)]
    [DisplayName("Degree")]
    [Description("Degree of the least-squares polynomial fitted to the window (1 = a straight line).")]
    [Category("Estimation")]
    public int Degree
    {
        get => _degree;
        set => SetProperty(ref _degree, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.PolynomialRegressionMinForecastHorizon, IndicatorDefaultConstants.PolynomialRegressionMaxForecastHorizon)]
    [DisplayName("Forecast Horizon")]
    [Description("Bars after the newest bar at which the fitted polynomial is evaluated (0 = the fitted value at the newest bar). The value is drawn at the newest bar, not shifted to the future.")]
    [Category("Estimation")]
    public int ForecastHorizon
    {
        get => _forecastHorizon;
        set => SetProperty(ref _forecastHorizon, value);
    }

    public override string GetDisplayName(string type) => ForecastHorizon > 0 ? $"{type} ({Period}, {Degree}, +{ForecastHorizon})" : $"{type} ({Period}, {Degree})";

    // The first value is at bar index Period - 1.
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.PolynomialRegressionMinPeriod, IndicatorDefaultConstants.PolynomialRegressionMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(Degree, IndicatorDefaultConstants.PolynomialRegressionMinDegree, IndicatorDefaultConstants.PolynomialRegressionMaxDegree, nameof(Degree));
        ThrowIfOutOfRange(ForecastHorizon, IndicatorDefaultConstants.PolynomialRegressionMinForecastHorizon, IndicatorDefaultConstants.PolynomialRegressionMaxForecastHorizon, nameof(ForecastHorizon));
    }
}
