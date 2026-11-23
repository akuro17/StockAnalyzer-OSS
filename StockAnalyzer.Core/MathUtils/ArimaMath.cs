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
    internal const int MaxArOrder = 5;
    internal const int MaxDiffOrder = 2;
    private const int MinStabilizedBaseWindow = 20;
    private const int StabilizedMinSampleMargin = 10;
    private const int MaxLegacyProjectionHorizon = 100;
    private const double StabilizedReflectionLimit = 0.98;
    private const double LegacyMinResidualThreshold = 100.0;
    private const double LegacyMaxPsiSqClamp = 1e15;
    private const double LegacyMaxAbsPsiClamp = 1e7;
    private const double ZeroVarianceTolerance = 1e-9;
    private const double MatrixSingularityTolerance = 1e-12;
    private const double DimensionlessVarianceFloor = 1e-12;
    private const double DimensionlessResidualExplosionFactor = 20.0;
    private const int MaxPublicForecastHorizon = 1024;
    private const double KpssCriticalValue5Percent = 0.463;
    private const int KpssMinSampleLength = 8;
    private const double KpssZeroVarianceThreshold = 1e-20;
    private const double KpssLongRunVarianceMinRatio = 1e-16;
    private const double AiccRegVarianceFloor = 1e-16;
    private const double AiccArSumMax = 0.99;
    private const double AiccCoefAbsMax = 1.5;
    private const double AiccImproveTolerance = 1e-9;
    private const int MaxStepwiseSearchIterations = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsValidOrderSpec(int p, int d, int q)
        => p >= 0 && p <= MaxArOrder && d >= 0 && d <= MaxDiffOrder && q >= 0 && q <= MaxArOrder;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ComputeMinimumStabilizedWindow(int p, int d, int q)
        => Math.Max(MinStabilizedBaseWindow + d, (2 * (p + q)) + d + StabilizedMinSampleMargin);

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
    /// <summary>
    /// Computes h-step ARIMA(p, d, q) price forecasts and cumulative conditional forecast error variances.
    /// Routes standard well-sized ARIMA(p+q &gt;= 1) windows through <see cref="TryEstimateStabilizedArimaCore"/>
    /// (scale-invariant centering, Householder QR OLS, Schur-Cohn pole stabilization) while preserving
    /// compatibility handling for ARIMA(0,d,0), flat series, short windows (&lt; minWindow), and horizon caps (1..100).
    /// Zero allocations on hot paths via stackalloc or ArrayPool.
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
        int steps = Math.Clamp(futureSteps, 1, MaxLegacyProjectionHorizon);

        innovationVariance = 0.0;
        residualStdDev = 0.0;

        // Fallback condition: insufficient samples or invalid parameters
        if (w <= d || !IsValidOrderSpec(p, d, q) ||
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

        // 1. Differencing (shared single pass for both stabilized and legacy paths)
        int n = w - d;
        double[]? rentedDiff = null;
        Span<double> diff = (n <= MathBufferLimits.StackAllocThreshold)
            ? stackalloc double[n]
            : (rentedDiff = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

        try
        {
            Difference(window, diff, d);

            Span<double> phi = p > 0 ? stackalloc double[p] : Span<double>.Empty;
            Span<double> theta = q > 0 ? stackalloc double[q] : Span<double>.Empty;
            Span<double> tailYTilde = p > 0 ? stackalloc double[p] : Span<double>.Empty;
            Span<double> tailResiduals = q > 0 ? stackalloc double[q] : Span<double>.Empty;
            phi.Clear();
            theta.Clear();
            tailYTilde.Clear();
            tailResiduals.Clear();

            double mu;
            double sigmaSq;
            int minWindow = ComputeMinimumStabilizedWindow(p, d, q);

            if (p + q >= 1 && w >= minWindow)
            {
                MathErrorCode err = TryEstimateStabilizedArimaCore(
                    diff,
                    p,
                    q,
                    phi,
                    theta,
                    tailYTilde,
                    tailResiduals,
                    out mu,
                    out double maxAbs,
                    out double sigma2U,
                    out _);
                if (err != MathErrorCode.None)
                {
                    forecastedPrices.Slice(0, steps).Fill(window[^1]);
                    errorVariances.Slice(0, steps).Clear();
                    return false;
                }

                sigmaSq = maxAbs * maxAbs * sigma2U;
                if (!double.IsFinite(mu) || !double.IsFinite(sigmaSq))
                {
                    forecastedPrices.Slice(0, steps).Fill(window[^1]);
                    errorVariances.Slice(0, steps).Clear();
                    return false;
                }
            }
            else if (!TryEstimateLegacyMultiStepCore(
                diff,
                p,
                q,
                phi,
                theta,
                tailYTilde,
                tailResiduals,
                out mu,
                out sigmaSq))
            {
                forecastedPrices.Slice(0, steps).Fill(window[^1]);
                errorVariances.Slice(0, steps).Clear();
                return false;
            }

            double xPrev1 = window[^1];
            double xPrev2 = (w >= 2) ? window[^2] : window[^1];
            if (!ComputeMultiStepPricesAndVariances(
                xPrev1,
                xPrev2,
                p,
                d,
                q,
                steps,
                phi,
                theta,
                tailYTilde,
                tailResiduals,
                mu,
                sigmaSq,
                forecastedPrices,
                errorVariances))
            {
                forecastedPrices.Slice(0, steps).Fill(xPrev1);
                errorVariances.Slice(0, steps).Clear();
                return false;
            }

            innovationVariance = sigmaSq;
            residualStdDev = Math.Sqrt(Math.Max(0.0, sigmaSq));
            return true;
        }
        finally
        {
            if (rentedDiff != null) ArrayPool<double>.Shared.Return(rentedDiff, clearArray: false);
        }
    }

    /// <summary>
    /// Legacy estimation compatibility kernel for ARIMA(0, d, 0) and sub-<c>minWindow</c> rolling slices
    /// preserved behind the <see cref="EstimateArimaMultiStepForecast"/> wrapper boundary.
    /// </summary>
    private static bool TryEstimateLegacyMultiStepCore(
        ReadOnlySpan<double> diff,
        int p,
        int q,
        Span<double> phi,
        Span<double> theta,
        Span<double> tailYTilde,
        Span<double> tailResiduals,
        out double mu,
        out double sigmaSq)
    {
        int n = diff.Length;
        double sumY = 0.0;
        for (int i = 0; i < n; i++) sumY += diff[i];
        mu = sumY / n;

        double[]? rentedTilde = null;
        Span<double> yTilde = (n <= MathBufferLimits.StackAllocThreshold)
            ? stackalloc double[n]
            : (rentedTilde = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

        try
        {
            for (int i = 0; i < n; i++) yTilde[i] = diff[i] - mu;

            if (p > 0)
            {
                int copyCount = Math.Min(n, p);
                yTilde.Slice(n - copyCount, copyCount).CopyTo(tailYTilde.Slice(p - copyCount, copyCount));
            }

            // Case A: ARIMA(0, d, 0)
            if (p == 0 && q == 0)
            {
                double sumSq = 0.0;
                for (int i = 0; i < n; i++) sumSq += yTilde[i] * yTilde[i];
                sigmaSq = Math.Max(ZeroVarianceTolerance, sumSq / n);
                return true;
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

                return SolveLevinsonDurbin(gamma, phi.Slice(0, p), out sigmaSq);
            }

            // Case C: Short-window ARIMA(p, d, q) with q > 0 - Hannan-Rissanen 2-stage regression
            sigmaSq = 0.0;
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

                // Legacy element-wise stability and invertibility clamp (|coef| <= StabilizedReflectionLimit)
                for (int i = 0; i < p; i++)
                {
                    double coef = beta[i];
                    if (Math.Abs(coef) > StabilizedReflectionLimit) coef = Math.Sign(coef) * StabilizedReflectionLimit;
                    phi[i] = coef;
                }
                for (int j = 0; j < q; j++)
                {
                    double coef = beta[p + j];
                    if (Math.Abs(coef) > StabilizedReflectionLimit) coef = Math.Sign(coef) * StabilizedReflectionLimit;
                    theta[j] = coef;
                }

                double maxYTilde = 0.0;
                for (int t = 0; t < n; t++)
                {
                    if (Math.Abs(yTilde[t]) > maxYTilde) maxYTilde = Math.Abs(yTilde[t]);
                }
                double residThreshold = Math.Max(LegacyMinResidualThreshold, maxYTilde * DimensionlessResidualExplosionFactor);

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
                        return false;
                    }
                    residuals[t] = r;
                }

                int copyResCount = Math.Min(n, q);
                residuals.Slice(n - copyResCount, copyResCount).CopyTo(tailResiduals.Slice(q - copyResCount, copyResCount));

                double sumResidSq = 0.0;
                for (int t = t0; t < n; t++)
                {
                    double err = residuals[t];
                    sumResidSq += err * err;
                }
                sigmaSq = Math.Max(ZeroVarianceTolerance, sumResidSq / sampleCount);
                return true;
            }
            finally
            {
                if (rentedResid != null) ArrayPool<double>.Shared.Return(rentedResid, clearArray: false);
            }
        }
        finally
        {
            if (rentedTilde != null) ArrayPool<double>.Shared.Return(rentedTilde, clearArray: false);
        }
    }

    private static bool ComputeMultiStepPricesAndVariances(
        double last1,
        double last2,
        int p,
        int d,
        int q,
        int steps,
        ReadOnlySpan<double> phi,
        ReadOnlySpan<double> theta,
        ReadOnlySpan<double> tailYTilde,
        ReadOnlySpan<double> tailResiduals,
        double mu,
        double sigmaSq,
        Span<double> forecastedPrices,
        Span<double> errorVariances)
    {
        if (TryComputeArimaMeanAdjustedPointForecasts(
                last1,
                last2,
                p,
                d,
                q,
                steps,
                phi,
                theta,
                tailYTilde,
                tailResiduals,
                mu,
                forecastedPrices) != MathErrorCode.None)
        {
            return false;
        }

        Span<double> psi = stackalloc double[steps];
        if (TryComputeIntegratedArAndPsiWeights(p, d, q, steps, phi, theta, clampExtremePsi: true, psi) != MathErrorCode.None)
        {
            return false;
        }

        double sumPsiSq = 0.0;
        for (int h = 1; h <= steps; h++)
        {
            double pWeight = psi[h - 1];
            sumPsiSq += pWeight * pWeight;
            if (sumPsiSq > LegacyMaxPsiSqClamp)
            {
                sumPsiSq = LegacyMaxPsiSqClamp; // Hard limit divergence guard
                for (int rem = h; rem <= steps; rem++)
                {
                    errorVariances[rem - 1] = Math.Max(0.0, sigmaSq * sumPsiSq);
                }
                break;
            }
            errorVariances[h - 1] = Math.Max(0.0, sigmaSq * sumPsiSq);
        }

        return true;
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
        => IsPolynomialStable(theta, AiccArSumMax);

    /// <summary>
    /// Generalized Schur-Cohn reflection step-down stability check for monic reciprocal polynomial
    /// <c>1 + c_1 L + ... + c_k L^k</c> requiring all reflection magnitudes strictly below <paramref name="maxAbsReflection"/>.
    /// </summary>
    private static bool IsPolynomialStable(ReadOnlySpan<double> coeffs, double maxAbsReflection)
    {
        int order = coeffs.Length;
        if (order == 0)
        {
            return true;
        }

        if (order > 5)
        {
            return false;
        }

        Span<double> cur = stackalloc double[order];
        Span<double> next = stackalloc double[order];
        coeffs.CopyTo(cur);

        for (int m = order; m >= 1; m--)
        {
            double km = cur[m - 1];
            if (!double.IsFinite(km) || Math.Abs(km) >= maxAbsReflection)
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

    private static bool TryStabilizePolynomial(ReadOnlySpan<double> orig, Span<double> stabilized, double maxAbsReflection)
    {
        int order = orig.Length;
        if (order == 0)
        {
            return true;
        }

        if (order > 5 || stabilized.Length < order)
        {
            return false;
        }

        for (int i = 0; i < order; i++)
        {
            if (!double.IsFinite(orig[i]))
            {
                return false;
            }
        }

        if (IsPolynomialStable(orig, maxAbsReflection))
        {
            orig.CopyTo(stabilized.Slice(0, order));
            return true;
        }

        Span<double> trial = stackalloc double[order];
        double lo = 0.0;
        double hi = 1.0;
        for (int iter = 0; iter < 40; iter++)
        {
            double gamma = 0.5 * (lo + hi);
            double factor = gamma;
            for (int i = 0; i < order; i++)
            {
                trial[i] = orig[i] * factor;
                factor *= gamma;
            }

            if (IsPolynomialStable(trial, maxAbsReflection))
            {
                lo = gamma;
            }
            else
            {
                hi = gamma;
            }
        }

        if (lo <= 1e-6)
        {
            return false;
        }

        double finalFactor = lo;
        for (int i = 0; i < order; i++)
        {
            stabilized[i] = orig[i] * finalFactor;
            finalFactor *= lo;
        }

        return IsPolynomialStable(stabilized.Slice(0, order), maxAbsReflection);
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

    /// <summary>
    /// Fits ARIMA(p, d, q) parameters (<c>p, q in [0, 5]</c>, <c>d in [0, 2]</c>, <c>p + q &gt;= 1</c>) on a real-valued window
    /// using the shared <see cref="Difference"/>, <see cref="SolveLevinsonDurbin"/>, and Hannan-Rissanen two-stage OLS kernels
    /// (additive MA convention, Schur-Cohn reflection stabilization below <c>0.98</c>, and relative AIC normalized to sample length <c>n = w - d</c>)
    /// without modifying forecasting APIs.
    /// </summary>
    public static MathErrorCode TryFitArimaParameters(
        ReadOnlySpan<double> window,
        int p,
        int d,
        int q,
        Span<double> phiOut,
        Span<double> thetaOut,
        out double sigma2,
        out double aic)
    {
        sigma2 = 0.0;
        aic = 0.0;
        int w = window.Length;
        int minWindow = ComputeMinimumStabilizedWindow(p, d, q);
        if (!IsValidOrderSpec(p, d, q) || p + q < 1 ||
            phiOut.Length < p || thetaOut.Length < q || w < minWindow)
        {
            return MathErrorCode.DomainError;
        }

        MathErrorCode finiteErr = ValidateWindowFinite(window);
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        int n = w - d;
        double[]? rentedDiff = n <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(n);
        Span<double> diff = rentedDiff is null ? stackalloc double[n] : rentedDiff.AsSpan(0, n);
        try
        {
            Difference(window, diff, d);
            return TryFitArimaDifferenced(diff, p, d, q, phiOut, thetaOut, out sigma2, out aic);
        }
        finally
        {
            if (rentedDiff is not null)
            {
                ArrayPool<double>.Shared.Return(rentedDiff, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Internal differenced-series entry point shared with <see cref="DslTimeSeriesMath.ArimaFit"/>
    /// to eliminate duplicate finite validation and duplicate differencing stack allocations.
    /// </summary>
    internal static MathErrorCode TryFitArimaDifferenced(
        ReadOnlySpan<double> diff,
        int p,
        int d,
        int q,
        Span<double> phiOut,
        Span<double> thetaOut,
        out double sigma2,
        out double aic)
    {
        sigma2 = 0.0;
        aic = 0.0;
        int minWindow = ComputeMinimumStabilizedWindow(p, d, q);
        if (!IsValidOrderSpec(p, d, q) || p + q < 1 ||
            phiOut.Length < p || thetaOut.Length < q || diff.Length + d < minWindow)
        {
            return MathErrorCode.DomainError;
        }

        MathErrorCode err = TryEstimateStabilizedArimaCore(
            diff,
            p,
            q,
            phiOut,
            thetaOut,
            Span<double>.Empty,
            Span<double>.Empty,
            out _,
            out double maxAbs,
            out double sigma2U,
            out int n);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        sigma2 = maxAbs * maxAbs * sigma2U;
        if (double.IsNaN(sigma2))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (!double.IsFinite(sigma2))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        int kParams = p + q + 2;
        aic = (n * ((2.0 * Math.Log(maxAbs)) + Math.Log(sigma2U))) + (2.0 * kParams);
        if (double.IsNaN(aic))
        {
            return MathErrorCode.NonFiniteResult;
        }

        return double.IsFinite(aic) ? MathErrorCode.None : MathErrorCode.ArithmeticOverflow;
    }

    /// <summary>
    /// Estimates stabilized ARIMA(p, d, q) parameters via the shared <see cref="TryEstimateStabilizedArimaCore"/> kernel
    /// and produces multi-step original-scale forecasts and Box-Jenkins standard errors for <paramref name="horizon"/> steps.
    /// </summary>
    public static MathErrorCode TryForecastArima(
        ReadOnlySpan<double> window,
        int p,
        int d,
        int q,
        int horizon,
        Span<double> forecastOut,
        Span<double> standardErrorOut)
    {
        int w = window.Length;
        int minWindow = ComputeMinimumStabilizedWindow(p, d, q);
        if (!IsValidOrderSpec(p, d, q) || p + q < 1 ||
            horizon < 1 || horizon > MaxPublicForecastHorizon ||
            forecastOut.Length < horizon || standardErrorOut.Length < horizon || w < minWindow)
        {
            return MathErrorCode.DomainError;
        }

        MathErrorCode finiteErr = ValidateWindowFinite(window);
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        int n = w - d;
        double[]? rentedDiff = n <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(n);
        Span<double> diff = rentedDiff is null ? stackalloc double[n] : rentedDiff.AsSpan(0, n);
        try
        {
            Difference(window, diff, d);
            double last1 = window[w - 1];
            double last2 = w >= 2 ? window[w - 2] : window[w - 1];
            return TryForecastArimaDifferenced(diff, last1, last2, p, d, q, horizon, forecastOut, standardErrorOut);
        }
        finally
        {
            if (rentedDiff is not null)
            {
                ArrayPool<double>.Shared.Return(rentedDiff, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Internal differenced-series forecasting kernel shared with <see cref="DslTimeSeriesMath.ArimaForecast"/>.
    /// Computes forecasts in dimensional difference space and scales Box-Jenkins standard errors via
    /// <c>maxAbs * sqrt(sigma2U * sumPsiSq)</c> to prevent premature squaring overflow at extreme amplitudes.
    /// </summary>
    internal static MathErrorCode TryForecastArimaDifferenced(
        ReadOnlySpan<double> diff,
        double last1,
        double last2,
        int p,
        int d,
        int q,
        int horizon,
        Span<double> forecastOut,
        Span<double> standardErrorOut)
    {
        int minWindow = ComputeMinimumStabilizedWindow(p, d, q);
        if (!IsValidOrderSpec(p, d, q) || p + q < 1 ||
            horizon < 1 || horizon > MaxPublicForecastHorizon ||
            forecastOut.Length < horizon || standardErrorOut.Length < horizon ||
            diff.Length + d < minWindow)
        {
            return MathErrorCode.DomainError;
        }

        if (double.IsNaN(last1) || double.IsNaN(last2))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (double.IsInfinity(last1) || double.IsInfinity(last2))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        Span<double> phi = p > 0 ? stackalloc double[p] : Span<double>.Empty;
        Span<double> theta = q > 0 ? stackalloc double[q] : Span<double>.Empty;
        Span<double> tailYTilde = p > 0 ? stackalloc double[p] : Span<double>.Empty;
        Span<double> tailResiduals = q > 0 ? stackalloc double[q] : Span<double>.Empty;

        MathErrorCode err = TryEstimateStabilizedArimaCore(
            diff,
            p,
            q,
            phi,
            theta,
            tailYTilde,
            tailResiduals,
            out double mu,
            out double maxAbs,
            out double sigma2U,
            out _);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        if (double.IsNaN(mu) || double.IsNaN(maxAbs) || double.IsNaN(sigma2U))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (!double.IsFinite(mu) || !double.IsFinite(maxAbs) || !double.IsFinite(sigma2U))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        // Step 1 & 2: Recursive multi-step forecast and cumulative undifferencing back to original price scale.
        MathErrorCode pointErr = TryComputeArimaMeanAdjustedPointForecasts(
            last1,
            last2,
            p,
            d,
            q,
            horizon,
            phi,
            theta,
            tailYTilde,
            tailResiduals,
            mu,
            forecastOut);
        if (pointErr != MathErrorCode.None)
        {
            return pointErr;
        }

        // Step 3: Box-Jenkins psi-weights & scaled standard errors from Phi*(B) = (1 - B)^d * Phi(B).
        double[]? rentedPsi = horizon <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(horizon);
        Span<double> psi = rentedPsi is null ? stackalloc double[horizon] : rentedPsi.AsSpan(0, horizon);
        try
        {
            MathErrorCode psiErr = TryComputeIntegratedArAndPsiWeights(p, d, q, horizon, phi, theta, clampExtremePsi: false, psi);
            if (psiErr != MathErrorCode.None)
            {
                return psiErr;
            }

            double sumPsiSq = 0.0;
            for (int h = 1; h <= horizon; h++)
            {
                double pk = psi[h - 1];
                sumPsiSq += pk * pk;
                double seH = maxAbs * Math.Sqrt(Math.Max(0.0, sigma2U * sumPsiSq));
                if (double.IsNaN(seH))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                if (double.IsInfinity(sumPsiSq) || double.IsInfinity(seH))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                standardErrorOut[h - 1] = seH;
            }

            return MathErrorCode.None;
        }
        finally
        {
            if (rentedPsi is not null)
            {
                ArrayPool<double>.Shared.Return(rentedPsi, clearArray: false);
            }
        }
    }

    private static MathErrorCode TryComputeArimaMeanAdjustedPointForecasts(
        double last1,
        double last2,
        int p,
        int d,
        int q,
        int steps,
        ReadOnlySpan<double> phi,
        ReadOnlySpan<double> theta,
        ReadOnlySpan<double> tailYTilde,
        ReadOnlySpan<double> tailResiduals,
        double mu,
        Span<double> forecastOut)
    {
        double[]? rentedHat = steps <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(steps);
        Span<double> hatTildeY = rentedHat is null ? stackalloc double[steps] : rentedHat.AsSpan(0, steps);
        try
        {
            for (int h = 1; h <= steps; h++)
            {
                double acc = 0.0;
                for (int i = 1; i <= p; i++)
                {
                    double yRef = (h - i > 0)
                        ? hatTildeY[h - i - 1]
                        : tailYTilde[p - 1 + h - i];
                    acc += phi[i - 1] * yRef;
                }

                for (int j = h; j <= q; j++)
                {
                    double epsRef = tailResiduals[q - 1 + h - j];
                    acc += theta[j - 1] * epsRef;
                }

                if (double.IsNaN(acc))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                if (double.IsInfinity(acc))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                hatTildeY[h - 1] = acc;
            }

            double xPrev1 = last1;
            double xPrev2 = last2;
            for (int h = 1; h <= steps; h++)
            {
                double wHat = hatTildeY[h - 1] + mu;
                double xH = d switch
                {
                    0 => wHat,
                    1 => xPrev1 + wHat,
                    2 => (2.0 * xPrev1) - xPrev2 + wHat,
                    _ => wHat,
                };

                if (double.IsNaN(xH))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                if (double.IsInfinity(xH))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                if (d == 1)
                {
                    xPrev1 = xH;
                }
                else if (d == 2)
                {
                    xPrev2 = xPrev1;
                    xPrev1 = xH;
                }

                forecastOut[h - 1] = xH;
            }

            return MathErrorCode.None;
        }
        finally
        {
            if (rentedHat is not null)
            {
                ArrayPool<double>.Shared.Return(rentedHat, clearArray: false);
            }
        }
    }

    private static MathErrorCode TryComputeIntegratedArAndPsiWeights(
        int p,
        int d,
        int q,
        int steps,
        ReadOnlySpan<double> phi,
        ReadOnlySpan<double> theta,
        bool clampExtremePsi,
        Span<double> psiOut)
    {
        int starDegree = p + d;
        Span<double> poly = stackalloc double[starDegree + 1];
        Span<double> tempPoly = stackalloc double[starDegree + 1];
        poly.Clear();
        poly[0] = 1.0;
        for (int i = 1; i <= p; i++)
        {
            poly[i] = -phi[i - 1];
        }

        for (int iter = 0; iter < d; iter++)
        {
            tempPoly.Clear();
            for (int k = 0; k <= starDegree; k++)
            {
                double prev = k > 0 ? poly[k - 1] : 0.0;
                tempPoly[k] = poly[k] - prev;
            }

            tempPoly.CopyTo(poly);
        }

        Span<double> phiStar = stackalloc double[starDegree + 1];
        phiStar.Clear();
        for (int k = 1; k <= starDegree; k++)
        {
            phiStar[k] = -poly[k];
        }

        psiOut[0] = 1.0;
        for (int k = 1; k < steps; k++)
        {
            double thetaVal = k <= q ? theta[k - 1] : 0.0;
            double acc = 0.0;
            int maxI = Math.Min(k, starDegree);
            for (int i = 1; i <= maxI; i++)
            {
                acc += phiStar[i] * psiOut[k - i];
            }

            double psiVal = thetaVal + acc;
            if (double.IsNaN(psiVal))
            {
                return MathErrorCode.NonFiniteResult;
            }

            if (clampExtremePsi)
            {
                if (psiVal * psiVal > LegacyMaxPsiSqClamp)
                {
                    psiVal = Math.Sign(psiVal) * LegacyMaxAbsPsiClamp;
                }
            }
            else if (double.IsInfinity(psiVal))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            psiOut[k] = psiVal;
        }

        return MathErrorCode.None;
    }

    private static MathErrorCode ValidateWindowFinite(ReadOnlySpan<double> window)
    {
        for (int i = 0; i < window.Length; i++)
        {
            double val = window[i];
            if (double.IsNaN(val))
            {
                return MathErrorCode.NonFiniteResult;
            }

            if (double.IsInfinity(val))
            {
                return MathErrorCode.ArithmeticOverflow;
            }
        }

        return MathErrorCode.None;
    }

    private static MathErrorCode TryEstimateStabilizedArimaCore(
        ReadOnlySpan<double> diff,
        int p,
        int q,
        Span<double> phiOut,
        Span<double> thetaOut,
        Span<double> tailYTildeOut,
        Span<double> tailResidualsOut,
        out double mu,
        out double maxAbs,
        out double sigma2U,
        out int n)
    {
        mu = 0.0;
        maxAbs = 0.0;
        sigma2U = 0.0;
        n = diff.Length;
        if (n < 1)
        {
            return MathErrorCode.DomainError;
        }

        double[]? rentedWork = (n * 2) <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(n * 2);
        Span<double> yCentered = rentedWork is null ? stackalloc double[n] : rentedWork.AsSpan(0, n);
        Span<double> u = rentedWork is null ? stackalloc double[n] : rentedWork.AsSpan(n, n);

        try
        {
            double s0 = 0.0;
            for (int i = 0; i < n; i++)
            {
                double val = diff[i];
                if (double.IsNaN(val))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                if (double.IsInfinity(val))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                double absVal = Math.Abs(val);
                if (absVal > s0)
                {
                    s0 = absVal;
                }
            }

            if (!(s0 > 0.0))
            {
                return MathErrorCode.ConvergenceFailure;
            }

            double scaledSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                scaledSum += diff[i] / s0;
            }

            mu = s0 * (scaledSum / n);
            if (double.IsNaN(mu))
            {
                return MathErrorCode.NonFiniteResult;
            }

            if (double.IsInfinity(mu))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            for (int i = 0; i < n; i++)
            {
                double yt = diff[i] - mu;
                if (double.IsNaN(yt))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                if (double.IsInfinity(yt))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                yCentered[i] = yt;
                double ay = Math.Abs(yt);
                if (ay > maxAbs)
                {
                    maxAbs = ay;
                }
            }

            if (!(maxAbs > 0.0))
            {
                return MathErrorCode.ConvergenceFailure;
            }

            for (int i = 0; i < n; i++)
            {
                u[i] = yCentered[i] / maxAbs;
            }

            if (p > 0 && tailYTildeOut.Length >= p)
            {
                yCentered.Slice(n - p, p).CopyTo(tailYTildeOut);
            }

            if (q == 0)
            {
                Span<double> gammaU = stackalloc double[p + 1];
                ComputeSampleAutocovariance(u, p, gammaU);
                if (!SolveLevinsonDurbin(gammaU, phiOut.Slice(0, p), out double rawVarU) ||
                    !(rawVarU > 0.0) ||
                    !double.IsFinite(rawVarU))
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                sigma2U = Math.Max(DimensionlessVarianceFloor, rawVarU);
                return MathErrorCode.None;
            }

            int m = Math.Min(n / 4, Math.Max(p + q + 2, 8));
            int kTotal = p + q;
            int t0 = Math.Max(p, m + q);
            int nEff = n - t0;
            if (m < 1 || m >= n - 1 || n < 20 || nEff <= kTotal + 2)
            {
                return MathErrorCode.DomainError;
            }

            Span<double> gammaAux = stackalloc double[m + 1];
            ComputeSampleAutocovariance(u, m, gammaAux);
            Span<double> phiAux = stackalloc double[m];
            if (!SolveLevinsonDurbin(gammaAux, phiAux, out _))
            {
                return MathErrorCode.ConvergenceFailure;
            }

            double[]? rentedResid = n <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(n);
            Span<double> residU = rentedResid is null ? stackalloc double[n] : rentedResid.AsSpan(0, n);
            double[]? rentedZ = ArrayPool<double>.Shared.Rent(nEff * kTotal);
            double[]? rentedRhs = ArrayPool<double>.Shared.Rent(nEff);
            try
            {
                residU.Slice(0, m).Clear();
                for (int t = m; t < n; t++)
                {
                    double pred = 0.0;
                    for (int k = 1; k <= m; k++)
                    {
                        pred += phiAux[k - 1] * u[t - k];
                    }

                    double rAux = u[t] - pred;
                    if (double.IsNaN(rAux))
                    {
                        return MathErrorCode.NonFiniteResult;
                    }

                    if (double.IsInfinity(rAux))
                    {
                        return MathErrorCode.ArithmeticOverflow;
                    }

                    residU[t] = rAux;
                }

                Span<double> zMat = rentedZ.AsSpan(0, nEff * kTotal);
                Span<double> rhsVec = rentedRhs.AsSpan(0, nEff);
                for (int t = t0; t < n; t++)
                {
                    int rowOffset = (t - t0) * kTotal;
                    for (int i = 0; i < p; i++)
                    {
                        zMat[rowOffset + i] = u[t - 1 - i];
                    }

                    for (int j = 0; j < q; j++)
                    {
                        zMat[rowOffset + p + j] = residU[t - 1 - j];
                    }

                    rhsVec[t - t0] = u[t];
                }

                if (!SmallMatrixMath.TryHouseholderQr(zMat, nEff, kTotal, rhsVec, out _))
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                Span<double> beta = stackalloc double[kTotal];
                SmallMatrixMath.SolveUpperTriangular(zMat, kTotal, rhsVec.Slice(0, kTotal), beta);
                for (int i = 0; i < kTotal; i++)
                {
                    if (double.IsNaN(beta[i]))
                    {
                        return MathErrorCode.NonFiniteResult;
                    }

                    if (double.IsInfinity(beta[i]))
                    {
                        return MathErrorCode.ArithmeticOverflow;
                    }
                }

                if (p > 0)
                {
                    Span<double> arPoly = stackalloc double[p];
                    Span<double> arStable = stackalloc double[p];
                    for (int i = 0; i < p; i++)
                    {
                        arPoly[i] = -beta[i];
                    }

                    if (!TryStabilizePolynomial(arPoly, arStable, StabilizedReflectionLimit))
                    {
                        return MathErrorCode.ConvergenceFailure;
                    }

                    for (int i = 0; i < p; i++)
                    {
                        phiOut[i] = -arStable[i];
                    }
                }

                if (!TryStabilizePolynomial(beta.Slice(p, q), thetaOut.Slice(0, q), StabilizedReflectionLimit))
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                for (int t = 0; t < n; t++)
                {
                    double fitVal = 0.0;
                    for (int i = 1; i <= p; i++)
                    {
                        if (t - i >= 0) fitVal += phiOut[i - 1] * u[t - i];
                    }

                    for (int j = 1; j <= q; j++)
                    {
                        if (t - j >= 0) fitVal += thetaOut[j - 1] * residU[t - j];
                    }

                    double r = u[t] - fitVal;
                    if (double.IsNaN(r))
                    {
                        return MathErrorCode.NonFiniteResult;
                    }

                    if (double.IsInfinity(r))
                    {
                        return MathErrorCode.ArithmeticOverflow;
                    }

                    if (Math.Abs(r) > DimensionlessResidualExplosionFactor)
                    {
                        return MathErrorCode.ConvergenceFailure;
                    }

                    residU[t] = r;
                }

                if (tailResidualsOut.Length >= q)
                {
                    for (int j = 0; j < q; j++)
                    {
                        double scaledRes = maxAbs * residU[n - q + j];
                        if (double.IsNaN(scaledRes))
                        {
                            return MathErrorCode.NonFiniteResult;
                        }

                        if (double.IsInfinity(scaledRes))
                        {
                            return MathErrorCode.ArithmeticOverflow;
                        }

                        tailResidualsOut[j] = scaledRes;
                    }
                }

                double sumResidSqU = 0.0;
                for (int t = t0; t < n; t++)
                {
                    double err = residU[t];
                    sumResidSqU += err * err;
                }

                if (double.IsNaN(sumResidSqU))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                if (double.IsInfinity(sumResidSqU))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                sigma2U = Math.Max(DimensionlessVarianceFloor, sumResidSqU / nEff);
                return MathErrorCode.None;
            }
            finally
            {
                ArrayPool<double>.Shared.Return(rentedRhs, clearArray: false);
                ArrayPool<double>.Shared.Return(rentedZ, clearArray: false);
                if (rentedResid is not null)
                {
                    ArrayPool<double>.Shared.Return(rentedResid, clearArray: false);
                }
            }
        }
        finally
        {
            if (rentedWork is not null)
            {
                ArrayPool<double>.Shared.Return(rentedWork, clearArray: false);
            }
        }
    }
}

