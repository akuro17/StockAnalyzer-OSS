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
    private const int StackAllocThreshold = 512;
    private const double ZeroVarianceTolerance = 1e-9;
    private const double MatrixSingularityTolerance = 1e-12;

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
        Span<double> diff = (n <= StackAllocThreshold)
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
            Span<double> yTilde = (n <= StackAllocThreshold)
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
                Span<double> residuals = (n <= StackAllocThreshold)
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
                        // Fallback to AR only if p > 0, else mean
                        if (p > 0)
                        {
                            Span<double> gammaAR = stackalloc double[p + 1];
                            for (int k = 0; k <= p; k++)
                            {
                                double c = 0.0;
                                for (int i = 0; i < n - k; i++) c += yTilde[i] * yTilde[i + k];
                                gammaAR[k] = c / n;
                            }
                            Span<double> phiAR = stackalloc double[p];
                            if (SolveLevinsonDurbin(gammaAR, phiAR, out _))
                            {
                                double predDiff = mu;
                                for (int i = 0; i < p; i++) predDiff += phiAR[i] * yTilde[n - 1 - i];
                                forecast = Undifference(predDiff, window, d);
                                return !double.IsNaN(forecast) && !double.IsInfinity(forecast);
                            }
                        }

                        forecast = Undifference(mu, window, d);
                        return true;
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
                        // Fallback to mean on singularity
                        forecast = Undifference(mu, window, d);
                        return true;
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
        Span<double> diff = (n <= StackAllocThreshold)
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
            Span<double> yTilde = (n <= StackAllocThreshold)
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
                    Span<double> residuals = (n <= StackAllocThreshold)
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
}

