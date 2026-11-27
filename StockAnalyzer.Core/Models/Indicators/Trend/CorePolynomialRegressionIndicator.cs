using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// Rolling least-squares polynomial of the last <see cref="Period"/> prices (<see cref="DslRegressionMath.PolyProject"/>): a polynomial of degree <see cref="Degree"/> is fitted on the window mapped to
/// [-1, 1] and evaluated <see cref="ForecastHorizon"/> bars after the newest bar (0 = the fitted value at the newest bar). <c>Main</c> is a price, drawn at the newest bar of the window (it is not shifted
/// into the future, so the series keeps one value per chart bar and uses bars &lt;= t only). The prices are converted to double for the QR solve and the result is narrowed once; every bar gets a fresh
/// execution budget. Null for bars before bar <c>Period - 1</c>, for a window with a missing price, and for every bar when <c>Period &lt; Degree + 2</c> (the kernel refuses; no cross-field check
/// of the parameters, like the other statistical indicators).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.PolynomialRegression)]
public class CorePolynomialRegressionIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.PolynomialRegressionDefaultPeriod;

    public int Degree { get; set; } = IndicatorDefaultConstants.PolynomialRegressionDefaultDegree;

    public int ForecastHorizon { get; set; } = IndicatorDefaultConstants.PolynomialRegressionDefaultForecastHorizon;

    public override string Name => ForecastHorizon > 0 ? $"Polynomial Regression ({Period}, {Degree}, +{ForecastHorizon})" : $"Polynomial Regression ({Period}, {Degree})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePolynomialRegressionParameter p)
        {
            p.Validate();
            Period = p.Period;
            Degree = p.Degree;
            ForecastHorizon = p.ForecastHorizon;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var parameters = new CorePolynomialRegressionParameter { Period = Period, Degree = Degree, ForecastHorizon = ForecastHorizon };
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

        for (int i = 0; i < levels.Length; i++)
        {
            decimal? value = null;
            int first = i - Period + 1;
            if (first >= 0 && prices.IsComplete(first, i))
            {
                var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
                MathResult<double> result = DslRegressionMath.PolyProject(levels.AsSpan(first, Period), Degree, ForecastHorizon, ref budget);
                if (result.IsOk)
                {
                    value = IndicatorCalculationHelper.ToDecimalOrNull(result.Value);
                }
            }

            _values.Add(value);
        }

        return CreateAutomaticResult();
    }
}
