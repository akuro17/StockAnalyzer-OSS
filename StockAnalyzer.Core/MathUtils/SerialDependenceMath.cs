using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>Variance ratio <c>VR(q)</c>, its heteroskedasticity-robust statistic <c>Z*(q)</c> and the two-sided normal p-value of <c>Z*</c>; each undefined part is NaN.</summary>
public readonly record struct VarianceRatioTestResult(double VarianceRatio, double ZStar, double PValue);

/// <summary>
/// Serial-dependence tests of a window of (log) returns: the Ljung-Box Q test and the p-value of the Lo-MacKinlay variance ratio.
/// <c>double</c> functions of dimensionless arguments; an undefined result is <see cref="double.NaN"/> and the caller narrows with <c>IndicatorCalculationHelper.ToDecimalOrNull</c>.
/// Specification: docs/indicators_formulas.md, records indicators_formulas_0597 (Ljung-Box) and indicators_formulas_0599 (variance ratio).
/// </summary>
public static class SerialDependenceMath
{
    /// <summary>Divisor of the window length in the effective Ljung-Box lag <c>min(MaxLag, floor(W / 5))</c> (owner decision U-5 = A).</summary>
    public const int LjungBoxWindowPerLag = 5;

    /// <summary>The Ljung-Box lag actually tested: <c>min(maxLag, floor(windowLength / 5))</c>. Below 1 no test is possible (the caller outputs no value).</summary>
    public static int EffectiveLjungBoxLag(int windowLength, int maxLag) => Math.Min(maxLag, windowLength / LjungBoxWindowPerLag);

    /// <summary>
    /// Ljung-Box statistic <c>Q = W (W + 2) sum_{k=1..m} rho_k^2 / (W - k)</c> and its p-value, the upper tail of the chi-square law with <paramref name="lags"/> degrees of freedom.
    /// <c>rho_k = gamma_k / gamma_0</c> with <c>gamma_k = (1 / W) sum_{t=k}^{W-1} (x_t - mean)(x_(t-k) - mean)</c>. False (both NaN) for <c>lags &lt; 1</c>, <c>lags &gt;= W</c>,
    /// a non-finite element, or a flat window (<c>gamma_0 &lt;= ReturnStatistics.VarianceEpsilon</c>). Allocates nothing.
    /// </summary>
    public static bool TryLjungBox(ReadOnlySpan<double> window, int lags, out double statistic, out double pValue)
    {
        statistic = double.NaN;
        pValue = double.NaN;
        int n = window.Length;
        if (lags < 1 || lags >= n)
        {
            return false;
        }

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(window[i]))
            {
                return false;
            }

            sum += window[i];
        }

        double mean = sum / n;
        double gamma0 = 0.0;
        for (int i = 0; i < n; i++)
        {
            double deviation = window[i] - mean;
            gamma0 += deviation * deviation;
        }

        gamma0 /= n;
        if (!double.IsFinite(gamma0) || !(gamma0 > ReturnStatistics.VarianceEpsilon))
        {
            return false;
        }

        double weighted = 0.0;
        for (int k = 1; k <= lags; k++)
        {
            double covariance = 0.0;
            for (int t = k; t < n; t++)
            {
                covariance += (window[t] - mean) * (window[t - k] - mean);
            }

            double rho = covariance / n / gamma0;
            weighted += rho * rho / (n - k);
        }

        double q = n * (n + 2.0) * weighted;
        double p = SpecialFunctionsMath.ChiSquareUpperTail(lags, q);
        if (!double.IsFinite(q) || !double.IsFinite(p))
        {
            return false;
        }

        statistic = q;
        pValue = p;
        return true;
    }

    /// <summary>
    /// Lo-MacKinlay variance ratio of the existing <see cref="HurstMath.VarianceRatio"/> (overlapping estimator, heteroskedasticity-robust Z*; no second formula is defined)
    /// with the two-sided p-value <c>erfc(|Z*| / sqrt 2)</c>. The variance ratio is NaN where <see cref="HurstMath.VarianceRatio"/> is NaN (q &lt; 2, window shorter than 2q + 2,
    /// a non-finite or constant window); Z* and the p-value are NaN where that function leaves Z* undefined.
    /// </summary>
    public static VarianceRatioTestResult VarianceRatio(ReadOnlySpan<double> window, int aggregationHorizon)
    {
        double vr = HurstMath.VarianceRatio(window, aggregationHorizon, out double zStar);
        if (double.IsNaN(vr))
        {
            return new VarianceRatioTestResult(double.NaN, double.NaN, double.NaN);
        }

        return new VarianceRatioTestResult(vr, zStar, SpecialFunctionsMath.NormalTwoSidedPValue(zStar));
    }
}
