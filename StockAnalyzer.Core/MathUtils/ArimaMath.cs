using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Pure C# mathematical engine for ARIMA (Autoregressive Integrated Moving Average) modeling.
/// Zero heap allocations on hot path, deterministic parameter estimation via Levinson-Durbin
/// and Hannan-Rissanen OLS regression.
/// </summary>
public static class ArimaMath
{
    private const double ZeroVarianceTolerance = 1e-9;
    private const double MatrixSingularityTolerance = 1e-12;
    private const double KpssCriticalValue5Percent = 0.463;
    private const int KpssMinSampleLength = 8;
    private const double KpssZeroVarianceThreshold = 1e-20;
    private const double KpssLongRunVarianceMinRatio = 1e-16;
    private const double AiccRegVarianceFloor = 1e-16;
    private const double AiccArSumMax = 0.99;
    private const double AiccCoefAbsMax = 1.5;
    private const double AiccImproveTolerance = 1e-9;
    private const int MaxStepwiseSearchIterations = 16;

    /// <summary>
    /// Computes the d-th order difference of an input series.
    /// d=0: output length N (copy)
    /// d=1: output length N-1, delta[t] = input[t+1] - input[t]
    /// d=2: output length N-2, delta2[t] = input[t+2] - 2*input[t+1] + input[t]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Difference(ReadOnlySpan<double> input, Span<double> output, int d)
    {
        int n = input.Length;
        if (d < 0 || d > 2) throw new ArgumentOutOfRangeException(nameof(d), "Differencing order must be 0, 1, or 2.");
        if (n <= d) throw new ArgumentException("Input length must be greater than differencing order d.", nameof(input));
        if (output.Length < n - d) throw new ArgumentException("Output buffer is too small.", nameof(output));

        switch (d)
        {
            case 0:
                input.CopyTo(output);
                break;
            case 1:
                for (int t = 0; t < n - 1; t++)
                {
                    output[t] = input[t + 1] - input[t];
                }
                break;
            case 2:
                for (int t = 0; t < n - 2; t++)
                {
                    output[t] = input[t + 2] - 2.0 * input[t + 1] + input[t];
                }
                break;
        }
    }

    /// <summary>
    /// Reverses the differencing operation to restore the 1-step-ahead forecast to the original price scale.
    /// d=0: forecast
    /// d=1: rawWindow[^1] + diffForecast
    /// d=2: 2 * rawWindow[^1] - rawWindow[^2] + diffForecast
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Undifference(double diffForecast, ReadOnlySpan<double> rawWindow, int d)
    {
        if (d < 0 || d > 2) throw new ArgumentOutOfRangeException(nameof(d), "Differencing order must be 0, 1, or 2.");
        if (rawWindow.Length < d + 1) throw new ArgumentException("rawWindow must have at least d + 1 points.", nameof(rawWindow));

        return d switch
        {
            0 => diffForecast,
            1 => rawWindow[^1] + diffForecast,
            2 => 2.0 * rawWindow[^1] - rawWindow[^2] + diffForecast,
            _ => diffForecast
        };
    }

    /// <summary>
    /// Solves the Yule-Walker equations for AR(p) coefficients using the Levinson-Durbin recursion.
    /// </summary>
    /// <param name="autocov">Autocovariances for lags 0 to p. Length must be >= p + 1.</param>
    /// <param name="phi">Destination span of length p for estimated AR coefficients.</param>
    /// <param name="sigmaSq">Residual innovation variance output.</param>
    /// <returns>True if successfully solved and stationary; false if singular or non-stationary.</returns>
    public static bool SolveLevinsonDurbin(ReadOnlySpan<double> autocov, Span<double> phi, out double sigmaSq)
    {
        int p = phi.Length;
        if (p == 0)
        {
            sigmaSq = autocov.Length > 0 ? Math.Max(0.0, autocov[0]) : 0.0;
            return true;
        }

        if (autocov.Length < p + 1 || autocov[0] <= MatrixSingularityTolerance || double.IsNaN(autocov[0]))
        {
            phi.Clear();
            sigmaSq = 0.0;
            return false;
        }

        Span<double> phiOld = stackalloc double[p];

        // Order k = 1
        double k1 = autocov[1] / autocov[0];
        if (double.IsNaN(k1) || double.IsInfinity(k1))
        {
            phi.Clear();
            sigmaSq = 0.0;
            return false;
        }

        k1 = Math.Clamp(k1, -0.999999, 0.999999);
        phi[0] = k1;
        double v = autocov[0] * (1.0 - k1 * k1);

        // Orders k = 2 to p
        for (int k = 2; k <= p; k++)
        {
            phi.Slice(0, k - 1).CopyTo(phiOld);

            double num = autocov[k];
            for (int j = 1; j < k; j++)
            {
                num -= phiOld[j - 1] * autocov[k - j];
            }

            if (v <= MatrixSingularityTolerance)
            {
                sigmaSq = Math.Max(0.0, v);
                return false;
            }

            double kk = num / v;
            if (double.IsNaN(kk) || double.IsInfinity(kk))
            {
                phi.Clear();
                sigmaSq = 0.0;
                return false;
            }

            kk = Math.Clamp(kk, -0.999999, 0.999999);
            phi[k - 1] = kk;

            for (int j = 1; j < k; j++)
            {
                phi[j - 1] = phiOld[j - 1] - kk * phiOld[k - 1 - j];
            }

            v *= (1.0 - kk * kk);
        }

        sigmaSq = Math.Max(0.0, v);
        return true;
    }

    /// <summary>
    /// Solves a linear system A * x = b of dimension n using Gaussian elimination with partial pivoting.
    /// Matrix A is given as a row-major flat span of length n * n.
    /// </summary>
    public static bool SolveLinearSystem(Span<double> a, Span<double> b, Span<double> x, int n)
    {
        if (n == 0) return true;
        if (a.Length < n * n || b.Length < n || x.Length < n) return false;

        // Forward elimination with partial pivoting
        for (int k = 0; k < n; k++)
        {
            int pivotRow = k;
            double maxPivot = Math.Abs(a[k * n + k]);

            for (int i = k + 1; i < n; i++)
            {
                double val = Math.Abs(a[i * n + k]);
                if (val > maxPivot)
                {
                    maxPivot = val;
                    pivotRow = i;
                }
            }

            if (maxPivot <= MatrixSingularityTolerance)
            {
                return false; // Singular matrix
            }

            if (pivotRow != k)
            {
                // Swap rows in A
                for (int j = k; j < n; j++)
                {
                    double tmpA = a[k * n + j];
                    a[k * n + j] = a[pivotRow * n + j];
                    a[pivotRow * n + j] = tmpA;
                }
                // Swap in b
                double tmpB = b[k];
                b[k] = b[pivotRow];
                b[pivotRow] = tmpB;
            }

            double diag = a[k * n + k];
            for (int i = k + 1; i < n; i++)
            {
                double factor = a[i * n + k] / diag;
                a[i * n + k] = 0.0;
                for (int j = k + 1; j < n; j++)
                {
                    a[i * n + j] -= factor * a[k * n + j];
                }
                b[i] -= factor * b[k];
            }
        }

        // Back substitution
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int j = i + 1; j < n; j++)
            {
                sum -= a[i * n + j] * x[j];
            }
            double diag = a[i * n + i];
            if (Math.Abs(diag) <= MatrixSingularityTolerance)
            {
                return false;
            }
            x[i] = sum / diag;
        }

        return true;
    }

    /// <summary>
    /// Estimates ARIMA(p, d, q) parameters on a rolling window and produces a strictly causal 1-step-ahead forecast.
    /// If estimation fails or variance is zero, returns false and outputs the last observed price window[^1].
    /// </summary>
    public static bool EstimateArimaForecast(
        ReadOnlySpan<double> window,
        int p, int d, int q,
        out double forecast)
    {
        int w = window.Length;

        // Fallback condition: insufficient samples
        if (w < p + d + q + 2 || p < 0 || p > 5 || d < 0 || d > 2 || q < 0 || q > 5)
        {
            forecast = w > 0 ? window[^1] : 0.0;
            return false;
        }

        // Check for non-finite values in raw window
        for (int i = 0; i < w; i++)
        {
            if (double.IsNaN(window[i]) || double.IsInfinity(window[i]))
            {
                forecast = window[^1];
                return false;
            }
        }

        // Check for zero-variance / flat series (scale-invariant: absolute or relative)
        double minPrice = window[0];
        double maxPrice = window[0];
        for (int i = 1; i < w; i++)
        {
            if (window[i] < minPrice) minPrice = window[i];
            if (window[i] > maxPrice) maxPrice = window[i];
        }
        double range = maxPrice - minPrice;
        if (range <= ZeroVarianceTolerance || (minPrice > 0.0 && range / minPrice <= ZeroVarianceTolerance))
        {
            forecast = window[^1];
            return false; // Deterministic fallback
        }

        // 1. Differencing
        int n = w - d;
        double[]? rentedDiff = null;
        Span<double> diff = (n <= MathBufferLimits.StackAllocThreshold)
            ? stackalloc double[n]
            : (rentedDiff = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

        try
        {
            Difference(window, diff, d);

            // 2. Mean centering
            double sumY = 0.0;
            for (int i = 0; i < n; i++) sumY += diff[i];
            double mu = sumY / n;

            double[]? rentedTilde = null;
            Span<double> yTilde = (n <= MathBufferLimits.StackAllocThreshold)
                ? stackalloc double[n]
                : (rentedTilde = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

            try
            {
                for (int i = 0; i < n; i++) yTilde[i] = diff[i] - mu;

                // Case A: ARIMA(0, d, 0) - Random Walk with drift
                if (p == 0 && q == 0)
                {
                    double diffForecast = mu;
                    forecast = Undifference(diffForecast, window, d);
                    return !double.IsNaN(forecast) && !double.IsInfinity(forecast);
                }

                // Case B: ARIMA(p, d, 0) - Pure AR via Levinson-Durbin
                if (q == 0)
                {
                    Span<double> gamma = stackalloc double[p + 1];
                    for (int k = 0; k <= p; k++)
                    {
                        double c = 0.0;
                        for (int i = 0; i < n - k; i++) c += yTilde[i] * yTilde[i + k];
                        gamma[k] = c / n;
                    }

                    Span<double> phi = stackalloc double[p];
                    if (!SolveLevinsonDurbin(gamma, phi, out _))
                    {
                        forecast = window[^1];
                        return false;
                    }

                    double predDiff = mu;
                    for (int i = 0; i < p; i++)
                    {
                        predDiff += phi[i] * yTilde[n - 1 - i];
                    }

                    forecast = Undifference(predDiff, window, d);
                    return !double.IsNaN(forecast) && !double.IsInfinity(forecast);
                }

                // Case C: ARIMA(p, d, q) with q > 0 - Hannan-Rissanen 2-stage regression
                // Step 1: Fit auxiliary high-order AR(m) to estimate residuals
                int m = Math.Min(n / 4, Math.Max(p + q + 2, 8));
                if (m < 1 || m >= n - 1)
                {
                    forecast = window[^1];
                    return false;
                }

                Span<double> gammaAux = stackalloc double[m + 1];
                for (int k = 0; k <= m; k++)
                {
                    double c = 0.0;
                    for (int i = 0; i < n - k; i++) c += yTilde[i] * yTilde[i + k];
                    gammaAux[k] = c / n;
                }

                Span<double> phiAux = stackalloc double[m];
                if (!SolveLevinsonDurbin(gammaAux, phiAux, out _))
                {
                    forecast = window[^1];
                    return false;
                }

                double[]? rentedResid = null;
                Span<double> residuals = (n <= MathBufferLimits.StackAllocThreshold)
                    ? stackalloc double[n]
                    : (rentedResid = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

                try
                {
                    residuals.Slice(0, m).Clear();
                    for (int t = m; t < n; t++)
                    {
                        double pred = 0.0;
                        for (int k = 1; k <= m; k++)
                        {
                            pred += phiAux[k - 1] * yTilde[t - k];
                        }
                        residuals[t] = yTilde[t] - pred;
                    }

                    // Step 2: Formulate OLS regression for ARMA(p, q)
                    int kTotal = p + q;
                    int t0 = Math.Max(p, m + q);
                    int sampleCount = n - t0;

                    if (sampleCount <= kTotal)
                    {
                        forecast = window[^1];
                        return false;
                    }

                    Span<double> ztz = stackalloc double[kTotal * kTotal];
                    ztz.Clear();
                    Span<double> zty = stackalloc double[kTotal];
                    zty.Clear();
                    Span<double> zRow = stackalloc double[kTotal];

                    for (int t = t0; t < n; t++)
                    {
                        // Explanatory variables: [yTilde(t-1..t-p), residuals(t-1..t-q)]
                        for (int i = 0; i < p; i++) zRow[i] = yTilde[t - 1 - i];
                        for (int j = 0; j < q; j++) zRow[p + j] = residuals[t - 1 - j];

                        double yVal = yTilde[t];
                        for (int r = 0; r < kTotal; r++)
                        {
                            zty[r] += zRow[r] * yVal;
                            for (int c = 0; c < kTotal; c++)
                            {
                                ztz[r * kTotal + c] += zRow[r] * zRow[c];
                            }
                        }
                    }

                    Span<double> beta = stackalloc double[kTotal];
                    if (!SolveLinearSystem(ztz, zty, beta, kTotal))
                    {
                        forecast = window[^1];
                        return false;
                    }

                    // Step 3: Compute 1-step-ahead forecast in differenced space
                    double predDiffFinal = mu;
                    for (int i = 0; i < p; i++)
                    {
                        double coef = beta[i];
                        if (Math.Abs(coef) > 2.0) coef = Math.Sign(coef) * 2.0; // Damping
                        predDiffFinal += coef * yTilde[n - 1 - i];
                    }
                    for (int j = 0; j < q; j++)
                    {
                        double coef = beta[p + j];
                        if (Math.Abs(coef) > 2.0) coef = Math.Sign(coef) * 2.0; // Damping
                        predDiffFinal += coef * residuals[n - 1 - j];
                    }

                    if (double.IsNaN(predDiffFinal) || double.IsInfinity(predDiffFinal))
                    {
                        forecast = window[^1];
                        return false;
                    }

                    forecast = Undifference(predDiffFinal, window, d);
                    if (double.IsNaN(forecast) || double.IsInfinity(forecast))
                    {
                        forecast = window[^1];
                        return false;
                    }

                    return true;
                }
                finally
                {
                    if (rentedResid != null) ArrayPool<double>.Shared.Return(rentedResid);
                }
            }
            finally
            {
                if (rentedTilde != null) ArrayPool<double>.Shared.Return(rentedTilde);
            }
        }
        finally
        {
            if (rentedDiff != null) ArrayPool<double>.Shared.Return(rentedDiff);
        }
    }

    /// <summary>
    /// Estimates ARIMA(p, d, q) parameters on an in-sample window and produces a multi-step future forecast
    /// with Box-Jenkins forecast error variances. Zero allocations on hot paths via stackalloc or ArrayPool.
    /// Preserves existing 1-step EstimateArimaForecast completely intact.
    /// </summary>
    public static bool EstimateArimaMultiStepForecast(
        ReadOnlySpan<double> window,
        int p, int d, int q,
        int futureSteps,
        Span<double> forecastedPrices,
        Span<double> errorVariances,
        out double innovationVariance,
        out double residualStdDev)
    {
        int w = window.Length;
        int steps = Math.Clamp(futureSteps, 1, 100);

        innovationVariance = 0.0;
        residualStdDev = 0.0;

        // Fallback condition: insufficient samples or invalid parameters
        if (w <= d || p < 0 || p > 5 || d < 0 || d > 2 || q < 0 || q > 5 ||
            forecastedPrices.Length < steps || errorVariances.Length < steps)
        {
            double fallbackPrice = w > 0 ? window[^1] : 0.0;
            forecastedPrices.Slice(0, steps).Fill(fallbackPrice);
            errorVariances.Slice(0, steps).Clear();
            return false;
        }

        // Check for non-finite values in raw window
        for (int i = 0; i < w; i++)
        {
            if (double.IsNaN(window[i]) || double.IsInfinity(window[i]))
            {
                forecastedPrices.Slice(0, steps).Fill(window[^1]);
                errorVariances.Slice(0, steps).Clear();
                return false;
            }
        }

        // Check for zero-variance / flat series (scale-invariant)
        double minPrice = window[0];
        double maxPrice = window[0];
        for (int i = 1; i < w; i++)
        {
            if (window[i] < minPrice) minPrice = window[i];
            if (window[i] > maxPrice) maxPrice = window[i];
        }
        double range = maxPrice - minPrice;
        if (range <= ZeroVarianceTolerance || (minPrice > 0.0 && range / minPrice <= ZeroVarianceTolerance))
        {
            forecastedPrices.Slice(0, steps).Fill(window[^1]);
            errorVariances.Slice(0, steps).Clear();
            innovationVariance = ZeroVarianceTolerance;
            residualStdDev = Math.Sqrt(ZeroVarianceTolerance);
            return true;
        }

        // 1. Differencing
        int n = w - d;
        double[]? rentedDiff = null;
        Span<double> diff = (n <= MathBufferLimits.StackAllocThreshold)
            ? stackalloc double[n]
            : (rentedDiff = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

        try
        {
            Difference(window, diff, d);

            // 2. Mean centering
            double sumY = 0.0;
            for (int i = 0; i < n; i++) sumY += diff[i];
            double mu = sumY / n;

            double[]? rentedTilde = null;
            Span<double> yTilde = (n <= MathBufferLimits.StackAllocThreshold)
                ? stackalloc double[n]
                : (rentedTilde = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

            try
            {
                for (int i = 0; i < n; i++) yTilde[i] = diff[i] - mu;

                Span<double> phi = stackalloc double[Math.Max(p, 1)];
                phi.Clear();
                Span<double> theta = stackalloc double[Math.Max(q, 1)];
                theta.Clear();

                Span<double> hatTildeY = stackalloc double[steps];
                hatTildeY.Clear();

                double sigmaSq = 0.0;
                bool isModelFitted = false;

                // Case A: ARIMA(0, d, 0)
                if (p == 0 && q == 0)
                {
                    double sumSq = 0.0;
                    for (int i = 0; i < n; i++) sumSq += yTilde[i] * yTilde[i];
                    sigmaSq = Math.Max(ZeroVarianceTolerance, sumSq / n);
                    isModelFitted = true;
                }
                // Case B: ARIMA(p, d, 0) - Pure AR via Levinson-Durbin
                else if (q == 0)
                {
                    Span<double> gamma = stackalloc double[p + 1];
                    for (int k = 0; k <= p; k++)
                    {
                        double c = 0.0;
                        for (int i = 0; i < n - k; i++) c += yTilde[i] * yTilde[i + k];
                        gamma[k] = c / n;
                    }

                    if (SolveLevinsonDurbin(gamma, phi.Slice(0, p), out sigmaSq))
                    {
                        isModelFitted = true;
                    }
                    else
                    {
                        return false;
                    }
                }
                // Case C: ARIMA(p, d, q) with q > 0 - Hannan-Rissanen 2-stage regression
                else
                {
                    int m = Math.Min(n / 4, Math.Max(p + q + 2, 8));
                    int kTotal = p + q;
                    int t0 = Math.Max(p, m + q);
                    int sampleCount = n - t0;

                    // Strict precondition: n >= 20, m >= 1, m < n - 1, and sampleCount > kTotal
                    if (m < 1 || m >= n - 1 || n < 20 || sampleCount <= kTotal)
                    {
                        // Strictly refuse silent fallback to AR(p) to preserve user model identity
                        return false;
                    }

                    Span<double> gammaAux = stackalloc double[m + 1];
                    for (int k = 0; k <= m; k++)
                    {
                        double c = 0.0;
                        for (int i = 0; i < n - k; i++) c += yTilde[i] * yTilde[i + k];
                        gammaAux[k] = c / n;
                    }

                    Span<double> phiAux = stackalloc double[m];
                    if (!SolveLevinsonDurbin(gammaAux, phiAux, out _))
                    {
                        return false;
                    }

                    double[]? rentedResid = null;
                    Span<double> residuals = (n <= MathBufferLimits.StackAllocThreshold)
                        ? stackalloc double[n]
                        : (rentedResid = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

                    try
                    {
                        residuals.Slice(0, m).Clear();
                        for (int t = m; t < n; t++)
                        {
                            double pred = 0.0;
                            for (int k = 1; k <= m; k++)
                            {
                                pred += phiAux[k - 1] * yTilde[t - k];
                            }
                            residuals[t] = yTilde[t] - pred;
                        }

                        Span<double> ztz = stackalloc double[kTotal * kTotal];
                        ztz.Clear();
                        Span<double> zty = stackalloc double[kTotal];
                        zty.Clear();
                        Span<double> zRow = stackalloc double[kTotal];

                        for (int t = t0; t < n; t++)
                        {
                            for (int i = 0; i < p; i++) zRow[i] = yTilde[t - 1 - i];
                            for (int j = 0; j < q; j++) zRow[p + j] = residuals[t - 1 - j];

                            double yVal = yTilde[t];
                            for (int r = 0; r < kTotal; r++)
                            {
                                zty[r] += zRow[r] * yVal;
                                for (int c = 0; c < kTotal; c++)
                                {
                                    ztz[r * kTotal + c] += zRow[r] * zRow[c];
                                }
                            }
                        }

                        Span<double> beta = stackalloc double[kTotal];
                        if (!SolveLinearSystem(ztz, zty, beta, kTotal))
                        {
                            return false;
                        }

                        // AR stability and MA invertibility clamp (|coef| <= 0.98)
                        for (int i = 0; i < p; i++)
                        {
                            double coef = beta[i];
                            if (Math.Abs(coef) > 0.98) coef = Math.Sign(coef) * 0.98;
                            phi[i] = coef;
                        }
                        for (int j = 0; j < q; j++)
                        {
                            double coef = beta[p + j];
                            if (Math.Abs(coef) > 0.98) coef = Math.Sign(coef) * 0.98;
                            theta[j] = coef;
                        }

                        // WebAI Review #1/#2: Compute recursive final ARMA residuals on all t in [0, n-1]
                        // e^{final}_t = yTilde_t - sum_{i=1}^p phi_i * yTilde_{t-i} - sum_{j=1}^q theta_j * e^{final}_{t-j}
                        double maxYTilde = 0.0;
                        for (int t = 0; t < n; t++)
                        {
                            if (Math.Abs(yTilde[t]) > maxYTilde) maxYTilde = Math.Abs(yTilde[t]);
                        }
                        double residThreshold = Math.Max(100.0, maxYTilde * 20.0);

                        for (int t = 0; t < n; t++)
                        {
                            double fitVal = 0.0;
                            for (int i = 1; i <= p; i++)
                            {
                                if (t - i >= 0) fitVal += phi[i - 1] * yTilde[t - i];
                            }
                            for (int j = 1; j <= q; j++)
                            {
                                if (t - j >= 0) fitVal += theta[j - 1] * residuals[t - j];
                            }
                            double r = yTilde[t] - fitVal;
                            if (Math.Abs(r) > residThreshold || double.IsNaN(r) || double.IsInfinity(r))
                            {
                                return false; // Non-invertible explosive filter detected -> refuse divergent forecast
                            }
                            residuals[t] = r;
                        }

                        double sumResidSq = 0.0;
                        for (int t = t0; t < n; t++)
                        {
                            double err = residuals[t];
                            sumResidSq += err * err;
                        }
                        sigmaSq = Math.Max(ZeroVarianceTolerance, sumResidSq / sampleCount);
                        isModelFitted = true;

                        // Multi-step forecast recurrence with recursive final residuals
                        for (int h = 1; h <= steps; h++)
                        {
                            double acc = 0.0;
                            for (int i = 1; i <= p; i++)
                            {
                                double yRef = (h - i > 0) ? hatTildeY[h - i - 1] : yTilde[n - 1 + h - i];
                                acc += phi[i - 1] * yRef;
                            }
                            for (int j = 1; j <= q; j++)
                            {
                                double epsRef = (h - j > 0) ? 0.0 : residuals[n - 1 + h - j];
                                acc += theta[j - 1] * epsRef;
                            }
                            hatTildeY[h - 1] = acc;
                        }
                    }
                    finally
                    {
                        if (rentedResid != null) ArrayPool<double>.Shared.Return(rentedResid);
                    }
                }

                if (!isModelFitted)
                {
                    return false;
                }

                // If recurrence was not run in Case C, run general recurrence for Case A / B:
                if (q == 0)
                {
                    for (int h = 1; h <= steps; h++)
                    {
                        double acc = 0.0;
                        for (int i = 1; i <= p; i++)
                        {
                            double yRef = (h - i > 0) ? hatTildeY[h - i - 1] : yTilde[n - 1 + h - i];
                            acc += phi[i - 1] * yRef;
                        }
                        hatTildeY[h - 1] = acc;
                    }
                }

                // 4. Stateful Cumulative Undifferencing
                double xPrev1 = window[^1];
                double xPrev2 = (w >= 2) ? window[^2] : window[^1];

                for (int h = 1; h <= steps; h++)
                {
                    double predY = mu + hatTildeY[h - 1];
                    double xH = d switch
                    {
                        0 => predY,
                        1 => xPrev1 + predY,
                        2 => 2.0 * xPrev1 - xPrev2 + predY,
                        _ => predY
                    };

                    if (d == 1)
                    {
                        xPrev1 = xH;
                    }
                    else if (d == 2)
                    {
                        xPrev2 = xPrev1;
                        xPrev1 = xH;
                    }

                    forecastedPrices[h - 1] = xH;
                }

                // 5. Box-Jenkins psi-weights & Error Variances
                // Generalized AR polynomial Phi*(B) = Phi(B) * (1 - B)^d
                // Degree of Phi*(B) is p + d
                int starDegree = p + d;
                Span<double> poly = stackalloc double[starDegree + 1];
                poly.Clear();
                poly[0] = 1.0;
                for (int i = 1; i <= p; i++)
                {
                    poly[i] = -phi[i - 1];
                }

                // Multiply by (1 - B) d times
                Span<double> tempPoly = stackalloc double[starDegree + 1];
                for (int iter = 0; iter < d; iter++)
                {
                    tempPoly.Clear();
                    for (int k = 0; k <= starDegree; k++)
                    {
                        double current = poly[k];
                        double prev = (k > 0) ? poly[k - 1] : 0.0;
                        tempPoly[k] = current - prev;
                    }
                    tempPoly.CopyTo(poly);
                }

                // Generalized AR coefficients: phiStar[k] = -poly[k] for k = 1..starDegree
                Span<double> phiStar = stackalloc double[starDegree + 1];
                phiStar.Clear();
                for (int k = 1; k <= starDegree; k++)
                {
                    phiStar[k] = -poly[k];
                }

                // Compute psi weights: psi_0 = 1, psi_j = theta_j + sum_{k=1}^{min(j, p+d)} phiStar[k] * psi_{j-k}
                Span<double> psi = stackalloc double[steps];
                psi.Clear();
                psi[0] = 1.0;

                for (int j = 1; j < steps; j++)
                {
                    double thetaVal = (j <= q) ? theta[j - 1] : 0.0;
                    double acc = 0.0;
                    int maxK = Math.Min(j, starDegree);
                    for (int k = 1; k <= maxK; k++)
                    {
                        acc += phiStar[k] * psi[j - k];
                    }
                    double psiVal = thetaVal + acc;
                    if (psiVal * psiVal > 1e15)
                    {
                        psiVal = Math.Sign(psiVal) * 1e7; // Guard against numerical overflow
                    }
                    psi[j] = psiVal;
                }

                // Cumulative forecast error variance: sigma^2(h) = sigmaSq * sum_{j=0}^{h-1} psi_j^2
                double sumPsiSq = 0.0;
                for (int h = 1; h <= steps; h++)
                {
                    double pWeight = psi[h - 1];
                    sumPsiSq += pWeight * pWeight;
                    if (sumPsiSq > 1e15)
                    {
                        sumPsiSq = 1e15; // Hard limit divergence guard
                        for (int rem = h; rem <= steps; rem++)
                        {
                            errorVariances[rem - 1] = Math.Max(0.0, sigmaSq * sumPsiSq);
                        }
                        break;
                    }
                    errorVariances[h - 1] = Math.Max(0.0, sigmaSq * sumPsiSq);
                }

                innovationVariance = sigmaSq;
                residualStdDev = Math.Sqrt(Math.Max(0.0, sigmaSq));
                return true;
            }
            finally
            {
                if (rentedTilde != null) ArrayPool<double>.Shared.Return(rentedTilde);
            }
        }
        finally
        {
            if (rentedDiff != null) ArrayPool<double>.Shared.Return(rentedDiff);
        }
    }

    /// <summary>
    /// Computes the level-stationarity KPSS unit-root test statistic with Bartlett / Newey-West long-run variance.
    /// Returns 0.0 when sample count &lt; 8 or variance is degenerate (<= 1e-20).
    /// </summary>
    public static double ComputeKpssStatistic(ReadOnlySpan<double> y)
    {
        int tLen = y.Length;
        if (tLen < KpssMinSampleLength)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (int t = 0; t < tLen; t++)
        {
            double val = y[t];
            if (!double.IsFinite(val))
            {
                return 0.0;
            }
            sum += val;
        }

        double mean = sum / tLen;
        double gamma0 = 0.0;
        double partialSum = 0.0;
        double sumPartialSq = 0.0;

        for (int t = 0; t < tLen; t++)
        {
            double e = y[t] - mean;
            gamma0 += e * e;
            partialSum += e;
            sumPartialSq += partialSum * partialSum;
        }

        gamma0 /= tLen;
        if (!(gamma0 > KpssZeroVarianceThreshold) || !double.IsFinite(gamma0))
        {
            return 0.0;
        }

        int lagMax = Math.Max(1, (int)Math.Floor(4.0 * Math.Pow(tLen / 100.0, 0.25)));
        if (lagMax >= tLen)
        {
            lagMax = tLen - 1;
        }

        double longRunVar = gamma0;
        for (int lag = 1; lag <= lagMax; lag++)
        {
            double cov = 0.0;
            for (int t = lag; t < tLen; t++)
            {
                cov += (y[t] - mean) * (y[t - lag] - mean);
            }

            cov /= tLen;
            double weight = 1.0 - (double)lag / (lagMax + 1);
            longRunVar += 2.0 * weight * cov;
        }

        if (!(longRunVar > KpssLongRunVarianceMinRatio * gamma0) || !double.IsFinite(longRunVar))
        {
            longRunVar = gamma0;
        }

        double numerator = sumPartialSq / ((double)tLen * tLen);
        return double.IsFinite(numerator) ? numerator / longRunVar : 0.0;
    }

    /// <summary>
    /// Selects differencing order d in [0, maxD] via sequential level KPSS tests against the 5% critical value (0.463).
    /// Reuses <see cref="Difference(ReadOnlySpan{double}, Span{double}, int)"/>.
    /// </summary>
    public static int SelectDifferenceOrderKpss(ReadOnlySpan<double> window, int maxD = 2)
    {
        int cappedMaxD = Math.Clamp(maxD, 0, 2);
        if (cappedMaxD == 0 || window.Length < KpssMinSampleLength)
        {
            return 0;
        }

        if (ComputeKpssStatistic(window) <= KpssCriticalValue5Percent)
        {
            return 0;
        }

        if (cappedMaxD == 1)
        {
            return 1;
        }

        int d1Len = window.Length - 1;
        double[]? rented = null;
        Span<double> diff1 = d1Len <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[d1Len]
            : (rented = ArrayPool<double>.Shared.Rent(d1Len)).AsSpan(0, d1Len);
        try
        {
            Difference(window, diff1, 1);
            return ComputeKpssStatistic(diff1) <= KpssCriticalValue5Percent ? 1 : 2;
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Evaluates small-sample corrected AICc for ARMA(p, q) on an already-differenced series using
    /// Yule-Walker / Hannan-Rissanen 2-stage estimation and stationarity/invertibility bounds.
    /// Returns <see cref="double.PositiveInfinity"/> when insufficient samples or invalid/unstable fit.
    /// </summary>
    public static double ComputeHannRissanenAicc(ReadOnlySpan<double> diffSeries, int p, int q)
    {
        if (p < 0 || p > 5 || q < 0 || q > 5)
        {
            return double.PositiveInfinity;
        }

        int n = diffSeries.Length;
        int k = p + q + 2;
        if (n <= k + 2 || n < Math.Max(p, q) + 6)
        {
            return double.PositiveInfinity;
        }

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double v = diffSeries[i];
            if (!double.IsFinite(v))
            {
                return double.PositiveInfinity;
            }
            sum += v;
        }

        double mean = sum / n;
        double[]? rentedCentered = null;
        Span<double> yTilde = n <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[n]
            : (rentedCentered = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                yTilde[i] = diffSeries[i] - mean;
            }

            double sigmaSq;
            if (p == 0 && q == 0)
            {
                double sq = 0.0;
                for (int i = 0; i < n; i++)
                {
                    sq += yTilde[i] * yTilde[i];
                }
                sigmaSq = sq / n;
            }
            else if (q == 0)
            {
                Span<double> gamma = stackalloc double[p + 1];
                ComputeSampleAutocovariance(yTilde, p, gamma);
                Span<double> phi = stackalloc double[p];
                if (!SolveLevinsonDurbin(gamma, phi, out _))
                {
                    return double.PositiveInfinity;
                }

                double phiSum = 0.0;
                for (int i = 0; i < p; i++)
                {
                    if (!double.IsFinite(phi[i]) || Math.Abs(phi[i]) >= AiccCoefAbsMax)
                    {
                        return double.PositiveInfinity;
                    }
                    phiSum += phi[i];
                }

                if (phiSum >= AiccArSumMax)
                {
                    return double.PositiveInfinity;
                }

                double rss = 0.0;
                int nEff = n - p;
                for (int t = p; t < n; t++)
                {
                    double pred = 0.0;
                    for (int i = 0; i < p; i++)
                    {
                        pred += phi[i] * yTilde[t - 1 - i];
                    }
                    double err = yTilde[t] - pred;
                    rss += err * err;
                }
                sigmaSq = rss / nEff;
            }
            else
            {
                int m = Math.Min(Math.Max(p + q + 2, 5), Math.Min(10, n / 3));
                if (m < 1 || m >= n - 1)
                {
                    return double.PositiveInfinity;
                }

                Span<double> gammaAux = stackalloc double[m + 1];
                ComputeSampleAutocovariance(yTilde, m, gammaAux);
                Span<double> phiAux = stackalloc double[m];
                if (!SolveLevinsonDurbin(gammaAux, phiAux, out _))
                {
                    return double.PositiveInfinity;
                }

                int t0 = Math.Max(p, m + q);
                int kTotal = p + q;
                int nEff = n - t0;
                if (nEff <= kTotal)
                {
                    return double.PositiveInfinity;
                }

                double[]? rentedResid = null;
                Span<double> residuals = n <= MathBufferLimits.StackAllocThreshold
                    ? stackalloc double[n]
                    : (rentedResid = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);
                try
                {
                    residuals.Slice(0, m).Clear();
                    for (int t = m; t < n; t++)
                    {
                        double pred = 0.0;
                        for (int j = 1; j <= m; j++)
                        {
                            pred += phiAux[j - 1] * yTilde[t - j];
                        }
                        residuals[t] = yTilde[t] - pred;
                    }

                    Span<double> ztz = stackalloc double[kTotal * kTotal];
                    ztz.Clear();
                    Span<double> zty = stackalloc double[kTotal];
                    zty.Clear();
                    Span<double> zRow = stackalloc double[kTotal];

                    for (int t = t0; t < n; t++)
                    {
                        for (int i = 0; i < p; i++) zRow[i] = yTilde[t - 1 - i];
                        for (int j = 0; j < q; j++) zRow[p + j] = residuals[t - 1 - j];

                        double yVal = yTilde[t];
                        for (int r = 0; r < kTotal; r++)
                        {
                            zty[r] += zRow[r] * yVal;
                            for (int c = 0; c < kTotal; c++)
                            {
                                ztz[r * kTotal + c] += zRow[r] * zRow[c];
                            }
                        }
                    }

                    Span<double> beta = stackalloc double[kTotal];
                    if (!SolveLinearSystem(ztz, zty, beta, kTotal))
                    {
                        return double.PositiveInfinity;
                    }

                    double phiSum = 0.0;
                    for (int i = 0; i < p; i++)
                    {
                        if (!double.IsFinite(beta[i]) || Math.Abs(beta[i]) >= AiccCoefAbsMax)
                        {
                            return double.PositiveInfinity;
                        }
                        phiSum += beta[i];
                    }

                    if (phiSum >= AiccArSumMax)
                    {
                        return double.PositiveInfinity;
                    }

                    for (int j = 0; j < q; j++)
                    {
                        if (!double.IsFinite(beta[p + j]) || Math.Abs(beta[p + j]) >= AiccCoefAbsMax)
                        {
                            return double.PositiveInfinity;
                        }
                    }

                    double rss = 0.0;
                    for (int t = t0; t < n; t++)
                    {
                        double pred = 0.0;
                        for (int i = 0; i < p; i++) pred += beta[i] * yTilde[t - 1 - i];
                        for (int j = 0; j < q; j++) pred += beta[p + j] * residuals[t - 1 - j];
                        double err = yTilde[t] - pred;
                        rss += err * err;
                    }

                    sigmaSq = rss / nEff;
                }
                finally
                {
                    if (rentedResid != null)
                    {
                        ArrayPool<double>.Shared.Return(rentedResid);
                    }
                }
            }

            if (!(sigmaSq > 0.0) || !double.IsFinite(sigmaSq))
            {
                return double.PositiveInfinity;
            }

            double regVar = Math.Max(sigmaSq, AiccRegVarianceFloor);
            double aic = n * Math.Log(regVar) + 2.0 * k;
            double aicc = aic + (2.0 * k * (k + 1)) / (n - k - 1);
            return double.IsFinite(aicc) ? aicc : double.PositiveInfinity;
        }
        finally
        {
            if (rentedCentered != null)
            {
                ArrayPool<double>.Shared.Return(rentedCentered);
            }
        }
    }

    /// <summary>
    /// Verifies MA invertibility for recursive error recovery <c>e_t + theta_1 e_{t-1} + ... + theta_q e_{t-q} = 0</c>
    /// using the Schur-Cohn reflection step-down test on <c>z^q + theta_1 z^{q-1} + ... + theta_q</c>.
    /// </summary>
    private static bool IsMaPolynomialInvertible(ReadOnlySpan<double> theta)
    {
        int q = theta.Length;
        if (q == 0)
        {
            return true;
        }

        if (q > 5)
        {
            return false;
        }

        Span<double> cur = stackalloc double[q];
        Span<double> next = stackalloc double[q];
        theta.CopyTo(cur);

        for (int m = q; m >= 1; m--)
        {
            double km = cur[m - 1];
            if (!double.IsFinite(km) || Math.Abs(km) >= AiccArSumMax)
            {
                return false;
            }

            if (m == 1)
            {
                break;
            }

            double denom = 1.0 - km * km;
            if (!(denom > 0.0))
            {
                return false;
            }

            for (int i = 0; i < m - 1; i++)
            {
                next[i] = (cur[i] - km * cur[m - 2 - i]) / denom;
            }

            next.Slice(0, m - 1).CopyTo(cur.Slice(0, m - 1));
        }

        return true;
    }

    /// <summary>
    /// Evaluates Hannan-Rissanen AICc using pre-centered <paramref name="yTilde"/> and
    /// precomputed long-AR <paramref name="auxResiduals"/> over an identical common sample
    /// window <c>[tStart .. yTilde.Length - 1]</c> of length <paramref name="nEff"/>.
    /// </summary>
    private static double ComputeHannRissanenAiccCommonWindow(
        ReadOnlySpan<double> yTilde,
        ReadOnlySpan<double> auxResiduals,
        int tStart,
        int nEff,
        int p,
        int q)
    {
        int n = yTilde.Length;
        int k = p + q + 2;
        if (p < 0 || q < 0 || nEff <= k + 1 || tStart < Math.Max(p, q) || tStart + nEff != n)
        {
            return double.PositiveInfinity;
        }

        double sigmaSq;
        if (p == 0 && q == 0)
        {
            double sq = 0.0;
            for (int t = tStart; t < n; t++)
            {
                sq += yTilde[t] * yTilde[t];
            }
            sigmaSq = sq / nEff;
        }
        else if (q == 0)
        {
            Span<double> gamma = stackalloc double[p + 1];
            ComputeSampleAutocovariance(yTilde, p, gamma);
            Span<double> phi = stackalloc double[p];
            if (!SolveLevinsonDurbin(gamma, phi, out _))
            {
                return double.PositiveInfinity;
            }

            double phiSum = 0.0;
            for (int i = 0; i < p; i++)
            {
                if (!double.IsFinite(phi[i]) || Math.Abs(phi[i]) >= AiccCoefAbsMax)
                {
                    return double.PositiveInfinity;
                }
                phiSum += phi[i];
            }

            if (phiSum >= AiccArSumMax)
            {
                return double.PositiveInfinity;
            }

            double rss = 0.0;
            for (int t = tStart; t < n; t++)
            {
                double pred = 0.0;
                for (int i = 0; i < p; i++)
                {
                    pred += phi[i] * yTilde[t - 1 - i];
                }
                double err = yTilde[t] - pred;
                rss += err * err;
            }
            sigmaSq = rss / nEff;
        }
        else
        {
            if (auxResiduals.Length < n)
            {
                return double.PositiveInfinity;
            }

            int kTotal = p + q;
            Span<double> ztz = stackalloc double[kTotal * kTotal];
            ztz.Clear();
            Span<double> zty = stackalloc double[kTotal];
            zty.Clear();
            Span<double> zRow = stackalloc double[kTotal];

            for (int t = tStart; t < n; t++)
            {
                for (int i = 0; i < p; i++) zRow[i] = yTilde[t - 1 - i];
                for (int j = 0; j < q; j++) zRow[p + j] = auxResiduals[t - 1 - j];

                double yVal = yTilde[t];
                for (int r = 0; r < kTotal; r++)
                {
                    zty[r] += zRow[r] * yVal;
                    for (int c = 0; c < kTotal; c++)
                    {
                        ztz[r * kTotal + c] += zRow[r] * zRow[c];
                    }
                }
            }

            Span<double> beta = stackalloc double[kTotal];
            if (!SolveLinearSystem(ztz, zty, beta, kTotal))
            {
                return double.PositiveInfinity;
            }

            double phiSum = 0.0;
            for (int i = 0; i < p; i++)
            {
                if (!double.IsFinite(beta[i]) || Math.Abs(beta[i]) >= AiccCoefAbsMax)
                {
                    return double.PositiveInfinity;
                }
                phiSum += beta[i];
            }

            if (phiSum >= AiccArSumMax)
            {
                return double.PositiveInfinity;
            }

            for (int j = 0; j < q; j++)
            {
                if (!double.IsFinite(beta[p + j]) || Math.Abs(beta[p + j]) >= AiccCoefAbsMax)
                {
                    return double.PositiveInfinity;
                }
            }

            // Ensure recursive MA residual recovery is invertible (all roots outside the unit disk)
            // so downstream Box-Jenkins forecasting does not diverge exponentially.
            if (!IsMaPolynomialInvertible(beta.Slice(p, q)))
            {
                return double.PositiveInfinity;
            }

            double rss = 0.0;
            for (int t = tStart; t < n; t++)
            {
                double pred = 0.0;
                for (int i = 0; i < p; i++) pred += beta[i] * yTilde[t - 1 - i];
                for (int j = 0; j < q; j++) pred += beta[p + j] * auxResiduals[t - 1 - j];
                double err = yTilde[t] - pred;
                rss += err * err;
            }

            sigmaSq = rss / nEff;
        }

        if (!(sigmaSq > 0.0) || !double.IsFinite(sigmaSq))
        {
            return double.PositiveInfinity;
        }

        double regVar = Math.Max(sigmaSq, AiccRegVarianceFloor);
        double aic = nEff * Math.Log(regVar) + 2.0 * k;
        double aicc = aic + (2.0 * k * (k + 1)) / (nEff - k - 1);
        return double.IsFinite(aicc) ? aicc : double.PositiveInfinity;
    }

    /// <summary>
    /// Selects ARIMA(p, d, q) order automatically using sequential KPSS unit-root testing and
    /// Hyndman-Khandakar stepwise AICc search over [0, maxP] x [0, maxQ] on a unified evaluation window.
    /// </summary>
    public static (int P, int D, int Q, double Aicc) SelectBestArimaOrder(
        ReadOnlySpan<double> window,
        int maxP = 3,
        int maxQ = 3,
        int maxD = 2)
    {
        int cappedMaxP = Math.Clamp(maxP, 0, 5);
        int cappedMaxQ = Math.Clamp(maxQ, 0, 5);
        int cappedMaxD = Math.Clamp(maxD, 0, 2);

        for (int i = 0; i < window.Length; i++)
        {
            if (!double.IsFinite(window[i]))
            {
                return (0, 0, 0, double.PositiveInfinity);
            }
        }

        int dStar = SelectDifferenceOrderKpss(window, cappedMaxD);
        int n = window.Length - dStar;
        if (n <= 2)
        {
            return (0, dStar, 0, double.PositiveInfinity);
        }

        double[]? rentedDiff = null;
        Span<double> yTilde = n <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[n]
            : (rentedDiff = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

        try
        {
            Difference(window, yTilde, dStar);

            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double v = yTilde[i];
                if (!double.IsFinite(v))
                {
                    return (0, dStar, 0, double.PositiveInfinity);
                }
                sum += v;
            }

            double mean = sum / n;
            if (!double.IsFinite(mean))
            {
                return (0, dStar, 0, double.PositiveInfinity);
            }

            for (int i = 0; i < n; i++)
            {
                yTilde[i] -= mean;
            }

            // Compute a single common auxiliary AR order m* and evaluation start offset tStart*
            // across all candidate (p, q) so AICc compares identical sample vectors [tStart* .. n - 1].
            int effMaxP = cappedMaxP;
            int effMaxQ = cappedMaxQ;
            int mStar = 0;
            int tStartStar = 0;
            int nEffStar = n;

            for (int step = 0; step <= cappedMaxP + cappedMaxQ + 1; step++)
            {
                int mCand;
                int tStartCand;
                if (effMaxQ > 0)
                {
                    mCand = Math.Min(Math.Max(effMaxP + effMaxQ + 2, 5), Math.Min(10, n / 3));
                    if (mCand < 1 || mCand >= n - 1)
                    {
                        effMaxQ = 0;
                        continue;
                    }
                    tStartCand = Math.Max(effMaxP, mCand + effMaxQ);
                }
                else
                {
                    mCand = 0;
                    tStartCand = effMaxP;
                }

                int nEffCand = n - tStartCand;
                int kMaxCand = effMaxP + effMaxQ + 2;
                if (nEffCand > 2 && nEffCand - kMaxCand - 1 >= 1)
                {
                    mStar = mCand;
                    tStartStar = tStartCand;
                    nEffStar = nEffCand;
                    break;
                }

                if (effMaxP == 0 && effMaxQ == 0)
                {
                    return (0, dStar, 0, double.PositiveInfinity);
                }

                if (effMaxQ >= effMaxP && effMaxQ > 0)
                {
                    effMaxQ--;
                }
                else
                {
                    effMaxP--;
                }
            }

            double[]? rentedAux = null;
            Span<double> auxResiduals = effMaxQ > 0 && mStar >= 1
                ? (n <= MathBufferLimits.StackAllocThreshold
                    ? stackalloc double[n]
                    : (rentedAux = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n))
                : Span<double>.Empty;

            try
            {
                if (effMaxQ > 0 && mStar >= 1)
                {
                    Span<double> gammaAux = stackalloc double[mStar + 1];
                    ComputeSampleAutocovariance(yTilde, mStar, gammaAux);
                    Span<double> phiAux = stackalloc double[mStar];
                    if (SolveLevinsonDurbin(gammaAux, phiAux, out _))
                    {
                        auxResiduals.Slice(0, mStar).Clear();
                        for (int t = mStar; t < n; t++)
                        {
                            double pred = 0.0;
                            for (int j = 1; j <= mStar; j++)
                            {
                                pred += phiAux[j - 1] * yTilde[t - j];
                            }
                            auxResiduals[t] = yTilde[t] - pred;
                        }
                    }
                    else
                    {
                        effMaxQ = 0;
                        tStartStar = effMaxP;
                        nEffStar = n - tStartStar;
                        while (effMaxP > 0 && (nEffStar <= 2 || nEffStar - (effMaxP + 2) - 1 < 1))
                        {
                            effMaxP--;
                            tStartStar = effMaxP;
                            nEffStar = n - tStartStar;
                        }

                        if (nEffStar <= 2 || nEffStar - (effMaxP + 2) - 1 < 1)
                        {
                            return (0, dStar, 0, double.PositiveInfinity);
                        }
                    }
                }

                Span<double> visitedAicc = stackalloc double[36];
                visitedAicc.Fill(double.NaN);

                int bestP = 0;
                int bestQ = 0;
                double bestAicc = double.PositiveInfinity;

                static void TryEvaluate(
                    ReadOnlySpan<double> yCentered,
                    ReadOnlySpan<double> precomputedAuxResiduals,
                    int tStart,
                    int nEff,
                    Span<double> memo,
                    int candP,
                    int candQ,
                    int maxPBound,
                    int maxQBound,
                    ref int bestPOut,
                    ref int bestQOut,
                    ref double bestAiccOut)
                {
                    if (candP < 0 || candP > maxPBound || candQ < 0 || candQ > maxQBound)
                    {
                        return;
                    }

                    int idx = candP * 6 + candQ;
                    double aicc = memo[idx];
                    if (double.IsNaN(aicc))
                    {
                        aicc = ComputeHannRissanenAiccCommonWindow(
                            yCentered, precomputedAuxResiduals, tStart, nEff, candP, candQ);
                        memo[idx] = aicc;
                    }

                    if (!double.IsInfinity(aicc) && aicc < bestAiccOut - AiccImproveTolerance)
                    {
                        bestPOut = candP;
                        bestQOut = candQ;
                        bestAiccOut = aicc;
                    }
                }

                // Phase 1: Initial 4 candidates in fixed deterministic order
                TryEvaluate(yTilde, auxResiduals, tStartStar, nEffStar, visitedAicc, Math.Min(2, effMaxP), Math.Min(2, effMaxQ), effMaxP, effMaxQ, ref bestP, ref bestQ, ref bestAicc);
                TryEvaluate(yTilde, auxResiduals, tStartStar, nEffStar, visitedAicc, 0, 0, effMaxP, effMaxQ, ref bestP, ref bestQ, ref bestAicc);
                if (effMaxP >= 1) TryEvaluate(yTilde, auxResiduals, tStartStar, nEffStar, visitedAicc, 1, 0, effMaxP, effMaxQ, ref bestP, ref bestQ, ref bestAicc);
                if (effMaxQ >= 1) TryEvaluate(yTilde, auxResiduals, tStartStar, nEffStar, visitedAicc, 0, 1, effMaxP, effMaxQ, ref bestP, ref bestQ, ref bestAicc);

                // Phase 2: Greedy 8-neighbor hill climbing
                ReadOnlySpan<int> dP = [-1, 1, 0, 0, -1, -1, 1, 1];
                ReadOnlySpan<int> dQ = [0, 0, -1, 1, -1, 1, -1, 1];

                for (int iter = 0; iter < MaxStepwiseSearchIterations; iter++)
                {
                    int centerP = bestP;
                    int centerQ = bestQ;
                    double baselineAicc = bestAicc;

                    for (int dir = 0; dir < 8; dir++)
                    {
                        TryEvaluate(yTilde, auxResiduals, tStartStar, nEffStar, visitedAicc, centerP + dP[dir], centerQ + dQ[dir], effMaxP, effMaxQ, ref bestP, ref bestQ, ref bestAicc);
                    }

                    if (bestAicc >= baselineAicc - AiccImproveTolerance)
                    {
                        break;
                    }
                }

                return double.IsInfinity(bestAicc)
                    ? (0, dStar, 0, double.PositiveInfinity)
                    : (bestP, dStar, bestQ, bestAicc);
            }
            finally
            {
                if (rentedAux != null)
                {
                    ArrayPool<double>.Shared.Return(rentedAux);
                }
            }
        }
        finally
        {
            if (rentedDiff != null)
            {
                ArrayPool<double>.Shared.Return(rentedDiff);
            }
        }
    }

    private static void ComputeSampleAutocovariance(ReadOnlySpan<double> centered, int maxLag, Span<double> gamma)
    {
        int n = centered.Length;
        for (int k = 0; k <= maxLag; k++)
        {
            double c = 0.0;
            for (int i = 0; i < n - k; i++)
            {
                c += centered[i] * centered[i + k];
            }
            gamma[k] = c / n;
        }
    }
}

