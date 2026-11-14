using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// GARCH(1,1) conditional volatility (Bollerslev 1986) on percent log returns r_k = 100 ln(P_k / P_(k-1)), estimated by Gaussian QMLE with variance targeting
/// (see <see cref="Garch11Math"/>) on a rolling window of <see cref="Period"/> returns and refitted every <see cref="RefitInterval"/> bars.
/// <c>Main</c> at bar j = the conditional standard deviation FOR THE NEXT BAR, sqrt(sigma^2_(j+1)), computed from returns up to bar j only (causal).
/// Between refits the last coefficients stay fixed and sigma^2 is advanced with each new return; a bar whose return is unusable has no value, and no value follows
/// until the next refit bar. Bars before bar <see cref="Period"/> and bars whose window has an unusable price (or a zero window variance) are null.
/// Unit: percentage per bar by default (the unit does not change with the chart time frame); annual percentage when <see cref="Annualize"/> is on.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Garch11)]
public class CoreGarch11Indicator : CoreIndicatorBase
{
    public override string Name => "GARCH(1,1)";

    public override bool IsOverlay => false;

    /// <summary>Estimation window in bars (number of returns).</summary>
    public int Period { get; set; } = IndicatorDefaultConstants.Garch11DefaultPeriod;

    /// <summary>Bars between re-estimations.</summary>
    public int RefitInterval { get; set; } = IndicatorDefaultConstants.Garch11DefaultRefitInterval;

    /// <summary>Optimizer iteration cap per estimation.</summary>
    public int MaxIterations { get; set; } = IndicatorDefaultConstants.Garch11DefaultMaxIterations;

    /// <summary>When true the value is multiplied by sqrt(<see cref="PeriodsPerYear"/>).</summary>
    public bool Annualize { get; set; }

    /// <summary>Bars per year for the annualisation; used only when <see cref="Annualize"/> is true.</summary>
    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreGarch11Parameter p)
        {
            p.Validate();
            Period = p.Period;
            RefitInterval = p.RefitInterval;
            MaxIterations = p.MaxIterations;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var parameters = new CoreGarch11Parameter
        {
            Period = Period,
            RefitInterval = RefitInterval,
            MaxIterations = MaxIterations,
            Annualize = Annualize,
            PeriodsPerYear = PeriodsPerYear
        };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        double[] prices = PriceDataHelper.ExtractDoubleSeries(candles, PriceSource);
        int count = prices.Length;
        if (count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        // returns[k] = 100 ln(P_k / P_(k-1)) in percent; NaN when a price is not positive (returns[0] has no predecessor).
        var returns = new double[count];
        returns[0] = double.NaN;
        for (int k = 1; k < count; k++)
        {
            returns[k] = EgarchMath.PercentLogReturn(prices[k - 1], prices[k]);
        }

        double annualFactor = IndicatorCalculationHelper.AnnualizationFactor(Annualize, PeriodsPerYear);
        int firstFitBar = Period;
        bool hasEstimate = false;
        Garch11Fit fit = default;
        double variance = 0.0;

        for (int j = 0; j < count; j++)
        {
            if (j < firstFitBar)
            {
                _values.Add(null);
                continue;
            }

            if ((j - firstFitBar) % RefitInterval == 0)
            {
                hasEstimate = Garch11Math.TryFit(returns, j - Period + 1, Period, MaxIterations, out fit);
                if (hasEstimate)
                {
                    variance = fit.NextVariance;
                }
            }
            else if (hasEstimate)
            {
                double r = returns[j];
                if (double.IsFinite(r))
                {
                    double residual = r - fit.Mean;
                    variance = Garch11Math.NextVarianceStep(fit.Omega, fit.Alpha, fit.Beta, residual * residual, variance);
                    hasEstimate = double.IsFinite(variance) && variance > 0.0;
                }
                else
                {
                    hasEstimate = false;
                }
            }

            _values.Add(hasEstimate ? IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt(variance) * annualFactor) : null);
        }

        return IndicatorResult.Success(_values);
    }
}
