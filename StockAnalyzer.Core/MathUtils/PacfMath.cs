using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// High-performance, zero-allocation mathematical engine for Partial Autocorrelation Function (PACF)
/// computation using the Levinson-Durbin recursion.
/// </summary>
public static class PacfMath
{
    private const double ZeroVarianceTolerance = 1e-12;
    private const double PacfClampLimit = 0.999999;
    public const double BartlettZ95 = 1.959963984540054; // z_0.975 for two-tailed 95% confidence

    /// <summary>
    /// Computes partial autocorrelation coefficients for lags 1 through targetLag.
    /// Operates with zero heap allocations for typical indicator window sizes (targetLag &lt;= 128).
    /// </summary>
    /// <param name="series">Stationary input time series (length N).</param>
    /// <param name="targetLag">Maximum lag order to estimate (1 &lt;= targetLag &lt; N).</param>
    /// <param name="outPacfCoefficients">
    /// Destination span of length &gt;= targetLag + 1. Index k contains PACF value at lag k.
    /// Index 0 is set to 1.0.
    /// </param>
    /// <param name="zeroLagVariance">Output sample variance (gamma[0]).</param>
    /// <returns>True if successfully solved; false if zero variance, non-finite input, or invalid arguments.</returns>
    public static bool TryCalculatePacf(
        ReadOnlySpan<double> series,
        int targetLag,
        Span<double> outPacfCoefficients,
        out double zeroLagVariance)
    {
        zeroLagVariance = 0.0;

        int n = series.Length;
        if (targetLag < 1 || n <= targetLag || outPacfCoefficients.Length < targetLag + 1)
        {
            return false;
        }

        outPacfCoefficients.Slice(0, targetLag + 1).Clear();

        // 1. Verify finite inputs
        for (int t = 0; t < n; t++)
        {
            if (!double.IsFinite(series[t]))
            {
                return false;
            }
        }

        // 2. Compute sample mean
        double sum = 0.0;
        for (int t = 0; t < n; t++)
        {
            sum += series[t];
        }
        double mean = sum / n;

        // 3. Compute sample autocovariances for lags 0..targetLag
        Span<double> gamma = targetLag <= 128 ? stackalloc double[targetLag + 1] : new double[targetLag + 1];
        for (int j = 0; j <= targetLag; j++)
        {
            double covSum = 0.0;
            int limit = n - j;
            for (int t = 0; t < limit; t++)
            {
                covSum += (series[t] - mean) * (series[t + j] - mean);
            }
            gamma[j] = covSum / n;
        }

        zeroLagVariance = gamma[0];

        // Guard against zero variance / non-finite variance using negated comparison
        if (!(gamma[0] > ZeroVarianceTolerance) || !double.IsFinite(gamma[0]))
        {
            return false;
        }

        // 4. Levinson-Durbin Recursion
        Span<double> phiPrev = targetLag <= 128 ? stackalloc double[targetLag + 1] : new double[targetLag + 1];
        Span<double> phiCurr = targetLag <= 128 ? stackalloc double[targetLag + 1] : new double[targetLag + 1];

        outPacfCoefficients[0] = 1.0;

        // Order k = 1
        double phi11 = Math.Clamp(gamma[1] / gamma[0], -PacfClampLimit, PacfClampLimit);
        phiPrev[1] = phi11;
        outPacfCoefficients[1] = phi11;
        double v = gamma[0] * (1.0 - phi11 * phi11);

        // Orders k = 2..targetLag
        for (int k = 2; k <= targetLag; k++)
        {
            if (!(v > ZeroVarianceTolerance) || !double.IsFinite(v))
            {
                // Degenerate variance: zero out remaining orders and break
                for (int rem = k; rem <= targetLag; rem++)
                {
                    outPacfCoefficients[rem] = 0.0;
                }
                break;
            }

            double num = gamma[k];
            for (int j = 1; j < k; j++)
            {
                num -= phiPrev[j] * gamma[k - j];
            }

            double phikk = Math.Clamp(num / v, -PacfClampLimit, PacfClampLimit);
            phiCurr[k] = phikk;

            for (int j = 1; j < k; j++)
            {
                phiCurr[j] = phiPrev[j] - phikk * phiPrev[k - j];
            }

            phiCurr.Slice(1, k).CopyTo(phiPrev.Slice(1, k));
            v *= (1.0 - phikk * phikk);
            outPacfCoefficients[k] = phikk;
        }

        return true;
    }
}
