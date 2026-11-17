using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Attributes;
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
    public const string GjrGammaSeriesName = nameof(GjrGamma);

    public override string Name => EnableGjrLeverage ? "GJR-GARCH(1,1)" : "GARCH(1,1)";

    public override bool IsOverlay => false;

    /// <summary>Estimation window in bars (number of returns).</summary>
    public int Period { get; set; } = IndicatorDefaultConstants.Garch11DefaultPeriod;

    /// <summary>Bars between re-estimations.</summary>
    public int RefitInterval { get; set; } = IndicatorDefaultConstants.Garch11DefaultRefitInterval;

    /// <summary>Optimizer iteration cap per estimation.</summary>
    public int MaxIterations { get; set; } = IndicatorDefaultConstants.Garch11DefaultMaxIterations;

    /// <summary>When true, estimates GJR-GARCH(1,1) with asymmetric leverage gamma.</summary>
    public bool EnableGjrLeverage { get; set; }

    /// <summary>When true the value is multiplied by sqrt(<see cref="PeriodsPerYear"/>).</summary>
    public bool Annualize { get; set; }

    /// <summary>Bars per year for the annualisation; used only when <see cref="Annualize"/> is true.</summary>
    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    /// <summary>Estimated asymmetric leverage coefficient (gamma) when <see cref="EnableGjrLeverage"/> is enabled.</summary>
    [IndicatorResultIgnore]
    public List<decimal?> GjrGamma { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreGarch11Parameter p)
        {
            p.Validate();
            Period = p.Period;
            RefitInterval = p.RefitInterval;
            MaxIterations = p.MaxIterations;
            EnableGjrLeverage = p.EnableGjrLeverage;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        GjrGamma.Clear();

        var parameters = new CoreGarch11Parameter
        {
            Period = Period,
            RefitInterval = RefitInterval,
            MaxIterations = MaxIterations,
            EnableGjrLeverage = EnableGjrLeverage,
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

        int count = candles?.Count ?? 0;
        if (count == 0)
        {
            return EnableGjrLeverage
                ? IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
                {
                    [IndicatorResult.MainSeriesName] = _values,
                    [GjrGammaSeriesName] = GjrGamma
                })
                : IndicatorResult.Success(_values);
        }

        decimal[] prices = ArrayPool<decimal>.Shared.Rent(count);
        double[] returns = ArrayPool<double>.Shared.Rent(count);
        try
        {
            PriceDataHelper.FillPrices(candles!, 0, prices.AsSpan(0, count), PriceSource);

            // returns[k] = 100 ln(P_k / P_(k-1)) in percent; NaN when a price is not positive (returns[0] has no predecessor).
            returns[0] = double.NaN;
            for (int k = 1; k < count; k++)
            {
                returns[k] = EgarchMath.PercentLogReturn((double)prices[k - 1], (double)prices[k]);
            }

            double annualFactor = IndicatorCalculationHelper.AnnualizationFactor(Annualize, PeriodsPerYear);
            int firstFitBar = Period;
            return EnableGjrLeverage
                ? CalculateGjrSeries(returns, count, firstFitBar, annualFactor)
                : CalculateStandardSeries(returns, count, firstFitBar, annualFactor);
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(prices);
            ArrayPool<double>.Shared.Return(returns);
        }
    }

    private IIndicatorResult CalculateStandardSeries(double[] returns, int count, int firstFitBar, double annualFactor)
    {
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

    private IIndicatorResult CalculateGjrSeries(double[] returns, int count, int firstFitBar, double annualFactor)
    {
        bool hasEstimate = false;
        GjrGarchFit fit = default;
        double variance = 0.0;

        for (int j = 0; j < count; j++)
        {
            if (j < firstFitBar)
            {
                _values.Add(null);
                GjrGamma.Add(null);
                continue;
            }

            if ((j - firstFitBar) % RefitInterval == 0)
            {
                hasEstimate = Garch11Math.TryFitGjr(returns, j - Period + 1, Period, MaxIterations, out fit);
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
                    variance = Garch11Math.NextVarianceStepGjr(fit.Omega, fit.Alpha, fit.Beta, fit.Gamma, residual, variance);
                    hasEstimate = double.IsFinite(variance) && variance > 0.0;
                }
                else
                {
                    hasEstimate = false;
                }
            }

            if (hasEstimate)
            {
                _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt(variance) * annualFactor));
                GjrGamma.Add(IndicatorCalculationHelper.ToDecimalOrNull(fit.Gamma));
            }
            else
            {
                _values.Add(null);
                GjrGamma.Add(null);
            }
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [IndicatorResult.MainSeriesName] = _values,
            [GjrGammaSeriesName] = GjrGamma
        });
    }
}
