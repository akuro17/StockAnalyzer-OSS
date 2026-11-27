using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// Rolling nonlinear least-squares curve of the last <see cref="Period"/> prices with x = 1 .. Period inside the window (<see cref="ModelType"/>): Exponential <c>a exp(b x)</c> and Power <c>a x^b</c>
/// (fitted on the logarithm, every price must be above zero), Logarithmic <c>a + b ln(x)</c>, or Logistic (the window is mapped linearly into [eps, 1 - eps] with eps = 1e-4, fitted by a logistic
/// regression and mapped back to price; a window of equal prices has no value). <c>Main</c> = the fitted price at x = Period (the newest bar, no extrapolation). <c>ParameterB</c> = the shape
/// parameter b of the curve (Exponential: growth rate per bar, Logarithmic: the coefficient of ln x, Power: the exponent, Logistic: the slope of the logit per bar). <c>RSquared</c> = <c>1 - SSE / SST</c>
/// measured on the PRICE scale of the window against the curve in price (the same definition for every model), NOT clipped to [0, 1] (a poor fit can be negative); null where the window has no variance.
/// <c>ParameterB</c> and <c>RSquared</c> are chart-suppressed (scale), see <see cref="IndicatorChartSuppression"/>. The prices are converted to double for the fit and the results are narrowed once;
/// every bar gets a fresh execution budget. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.NonlinearRegression)]
public class CoreNonlinearRegressionIndicator : CoreIndicatorBase
{
    public const string ParameterBSeriesName = nameof(ParameterB);
    public const string RSquaredSeriesName = nameof(RSquared);

    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.NonlinearRegressionDefaultPeriod;

    public NonlinearRegressionModel ModelType { get; set; } = NonlinearRegressionModel.Exponential;

    public List<decimal?> ParameterB { get; } = new();

    public List<decimal?> RSquared { get; } = new();

    public override string Name => $"Nonlinear Regression ({Period}, {ModelType})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreNonlinearRegressionParameter p)
        {
            p.Validate();
            Period = p.Period;
            ModelType = p.ModelType;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        ParameterB.Clear();
        RSquared.Clear();

        var parameters = new CoreNonlinearRegressionParameter { Period = Period, ModelType = ModelType };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var prices = new PriceSeriesWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        double[] levels = new double[prices.Count];
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = (double)prices.Values[i];
        }

        double[] x = new double[Period];
        for (int j = 0; j < Period; j++)
        {
            x[j] = j + 1;
        }

        double[] fitted = new double[Period];
        double[] mapped = new double[Period];
        for (int i = 0; i < levels.Length; i++)
        {
            decimal? main = null;
            decimal? shape = null;
            decimal? rSquared = null;
            int first = i - Period + 1;
            if (first >= 0 && prices.IsComplete(first, i)
                && TryFitWindow(levels.AsSpan(first, Period), x, fitted, mapped, out double fittedLast, out double b, out double r2))
            {
                main = IndicatorCalculationHelper.ToDecimalOrNull(fittedLast);
                shape = main.HasValue ? IndicatorCalculationHelper.ToDecimalOrNull(b) : null;
                rSquared = main.HasValue && double.IsFinite(r2) ? IndicatorCalculationHelper.ToDecimalOrNull(r2) : null;
            }

            _values.Add(main);
            ParameterB.Add(shape);
            RSquared.Add(rSquared);
        }

        return CreateAutomaticResult();
    }

    /// <summary>Fits the window with the shared kernel (<see cref="NonlinearCurveFitMath"/>); <paramref name="fitted"/> receives the curve in price at every x, <paramref name="mapped"/> is scratch. <paramref name="r2"/> is NaN where the window has no variance.</summary>
    private bool TryFitWindow(ReadOnlySpan<double> y, double[] x, double[] fitted, double[] mapped, out double fittedLast, out double b, out double r2)
    {
        bool ok = NonlinearCurveFitMath.TryFit(ModelType, x, y, fitted, mapped, out NonlinearCurveFit fit, out fittedLast, out r2);
        b = ok ? fit.B : double.NaN;
        return ok;
    }
}
