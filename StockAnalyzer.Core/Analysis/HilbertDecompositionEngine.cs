using System;
using System.Buffers;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// High-performance, Zero-Allocation Hilbert Transform Decomposition Engine.
/// Implements John F. Ehlers' Canonical Homodyne Discriminator algorithm to decompose
/// price time series into in-phase/quadrature analytic components, instantaneous amplitude,
/// instantaneous phase, phase velocity/unwrapped phase, and dominant cycle period.
/// </summary>
public static class HilbertDecompositionEngine
{
    private const decimal Epsilon = 1e-10m;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// Decomposes the input price series into full Hilbert analytic signal metrics.
    /// Guarantees zero heap allocation per bar during calculation.
    /// </summary>
    /// <param name="prices">Chronological price series (e.g. Typical, Median, or Close).</param>
    /// <param name="parameters">Configuration parameters (clamping, smoothing, warmup).</param>
    /// <returns>Structured result containing all decomposed metrics and quality flags.</returns>
    public static HilbertDecompositionResult Decompose(
        ReadOnlySpan<decimal> prices,
        HilbertDecompositionParameters? parameters = null)
    {
        parameters ??= new HilbertDecompositionParameters();
        parameters.Validate();

        int n = prices.Length;
        if (n == 0)
        {
            return new HilbertDecompositionResult(Array.Empty<HilbertSampleResult>(), parameters.WarmupBars, parameters);
        }

        var results = new HilbertSampleResult[n];
        if (n == 1)
        {
            results[0] = new HilbertSampleResult(
                0m, 0m, 0m, 0m, 0m, 0m,
                0.0, 0.0, 0.0, 0.0,
                parameters.DefaultPeriod, parameters.DefaultPeriod,
                double.NaN, false, false, true);
            return new HilbertDecompositionResult(results, parameters.WarmupBars, parameters);
        }

        // Rent scratch buffers (8 arrays of length n: smooth, detrender, q1, i1, jI, jQ, i2, q2)
        const int bufferCount = 8;
        decimal[] pool = ArrayPool<decimal>.Shared.Rent(n * bufferCount);
        Array.Clear(pool, 0, n * bufferCount);

        try
        {
            Span<decimal> smooth = pool.AsSpan(0, n);
            Span<decimal> detrender = pool.AsSpan(n, n);
            Span<decimal> q1 = pool.AsSpan(2 * n, n);
            Span<decimal> i1 = pool.AsSpan(3 * n, n);
            Span<decimal> jI = pool.AsSpan(4 * n, n);
            Span<decimal> jQ = pool.AsSpan(5 * n, n);
            Span<decimal> i2 = pool.AsSpan(6 * n, n);
            Span<decimal> q2 = pool.AsSpan(7 * n, n);

            // Step 1: 4-bar WMA Price Smoother (Noise isolation)
            for (int t = 0; t < n; t++)
            {
                smooth[t] = t >= 3
                    ? (4.0m * prices[t] + 3.0m * prices[t - 1] + 2.0m * prices[t - 2] + prices[t - 3]) / 10.0m
                    : prices[t];
            }

            // Ehlers 7-point FIR filter coefficients
            const decimal a = 0.0962m;
            const decimal b = 0.5769m;

            decimal i2Prev = 0m;
            decimal q2Prev = 0m;
            decimal smoothRe = 0m;
            decimal smoothIm = 0m;
            decimal period = parameters.DefaultPeriod;
            decimal smoothPeriod = parameters.DefaultPeriod;
            decimal prevPeriod = parameters.DefaultPeriod;

            double prevPhaseRad = 0.0;
            double unwrappedOffset = 0.0;
            double prevUnwrappedDeg = 0.0;

            int stabilityWindow = parameters.StabilityWindow;
            Span<double> deltaHistory = stabilityWindow <= 64 ? stackalloc double[stabilityWindow] : new double[stabilityWindow];
            int deltaHistoryCount = 0;

            for (int t = 0; t < n; t++)
            {
                // Adaptive amplitude correction factor
                decimal ampCorr = 0.075m * prevPeriod + 0.54m;

                // Step 2: 7-tap FIR Detrender
                detrender[t] = t >= 6
                    ? (a * smooth[t] + b * smooth[t - 2] - b * smooth[t - 4] - a * smooth[t - 6]) * ampCorr
                    : 0m;

                // Step 3: Quadrature Q1 and In-Phase I1
                q1[t] = t >= 6
                    ? (a * detrender[t] + b * detrender[t - 2] - b * detrender[t - 4] - a * detrender[t - 6]) * ampCorr
                    : 0m;
                i1[t] = t >= 3 ? detrender[t - 3] : 0m;

                // Step 4: jI and jQ (90-degree phase advancement)
                jI[t] = t >= 6
                    ? (a * i1[t] + b * i1[t - 2] - b * i1[t - 4] - a * i1[t - 6]) * ampCorr
                    : 0m;
                jQ[t] = t >= 6
                    ? (a * q1[t] + b * q1[t - 2] - b * q1[t - 4] - a * q1[t - 6]) * ampCorr
                    : 0m;

                // Step 5: Analytic signal phasor addition & EMA smoothing (0.2 / 0.8)
                decimal rawI2 = i1[t] - jQ[t];
                decimal rawQ2 = q1[t] + jI[t];
                i2[t] = 0.2m * rawI2 + 0.8m * i2Prev;
                q2[t] = 0.2m * rawQ2 + 0.8m * q2Prev;

                // Step 6: Instantaneous Amplitude & Power
                decimal power = i2[t] * i2[t] + q2[t] * q2[t];
                decimal amplitude = (decimal)Math.Sqrt((double)power);

                bool isWarmup = t < parameters.WarmupBars;
                bool isAboveThreshold = amplitude >= parameters.MicroAmplitudeThreshold;
                bool isValid = isAboveThreshold && !isWarmup;

                decimal normI = isAboveThreshold ? i2[t] / amplitude : 0m;
                decimal normQ = isAboveThreshold ? q2[t] / amplitude : 0m;

                // Step 7: Phase calculation & Unwrapping
                double phaseRad;
                if (isAboveThreshold)
                {
                    phaseRad = Math.Atan2(-(double)q2[t], (double)i2[t]);
                }
                else
                {
                    phaseRad = prevPhaseRad;
                }
                double phaseDeg = phaseRad * RadToDeg;

                // Phase unwrap (shortest angular path accumulation)
                double dPhi = phaseRad - prevPhaseRad;
                if (dPhi > Math.PI) unwrappedOffset -= 2.0 * Math.PI;
                else if (dPhi < -Math.PI) unwrappedOffset += 2.0 * Math.PI;

                double unwrappedDeg = (phaseRad + unwrappedOffset) * RadToDeg;
                double phaseDeltaDeg = t == 0 ? 0.0 : unwrappedDeg - prevUnwrappedDeg;

                // Step 8: Homodyne Discriminator (complex correlation with previous bar)
                decimal re = i2[t] * i2Prev + q2[t] * q2Prev;
                decimal im = i2[t] * q2Prev - q2[t] * i2Prev;

                i2Prev = i2[t];
                q2Prev = q2[t];

                smoothRe = 0.2m * re + 0.8m * smoothRe;
                smoothIm = 0.2m * im + 0.8m * smoothIm;

                decimal rawPeriod;
                double magSq = (double)(smoothRe * smoothRe + smoothIm * smoothIm);
                if (magSq > (double)Epsilon)
                {
                    double deltaPhase = Math.Atan2((double)smoothIm, (double)smoothRe);
                    if (deltaPhase > 0.001)
                    {
                        double periodVal = (2.0 * Math.PI) / deltaPhase;
                        rawPeriod = double.IsFinite(periodVal) ? (decimal)periodVal : prevPeriod;
                    }
                    else
                    {
                        rawPeriod = prevPeriod;
                    }
                }
                else
                {
                    rawPeriod = prevPeriod;
                }

                // Step 9: Rate limiting & Clamping
                if (rawPeriod > 1.5m * prevPeriod) rawPeriod = 1.5m * prevPeriod;
                if (rawPeriod < 0.67m * prevPeriod) rawPeriod = 0.67m * prevPeriod;
                decimal safeDeltaLimit = Math.Max(parameters.DeltaLimit, Epsilon);
                rawPeriod = Math.Clamp(rawPeriod, prevPeriod - safeDeltaLimit, prevPeriod + safeDeltaLimit);
                rawPeriod = Math.Clamp(rawPeriod, (decimal)parameters.MinPeriod, (decimal)parameters.MaxPeriod);

                // Step 10: Two-stage Exponential Smoothing
                period = parameters.SmoothBeta * rawPeriod + (1.0m - parameters.SmoothBeta) * period;
                smoothPeriod = 0.33m * period + 0.67m * smoothPeriod;
                prevPeriod = period;

                decimal dominantCycle = Math.Clamp(smoothPeriod, (decimal)parameters.MinPeriod, (decimal)parameters.MaxPeriod);

                // Step 11: Cycle Stability (standard deviation of phase delta over trailing window)
                deltaHistory[t % stabilityWindow] = phaseDeltaDeg;
                if (deltaHistoryCount < stabilityWindow) deltaHistoryCount++;

                double stability = double.NaN;
                bool trendMode = false;
                if (deltaHistoryCount >= stabilityWindow)
                {
                    double sum = 0.0;
                    for (int k = 0; k < stabilityWindow; k++) sum += deltaHistory[k];
                    double mean = sum / stabilityWindow;

                    double sumSq = 0.0;
                    for (int k = 0; k < stabilityWindow; k++)
                    {
                        double diff = deltaHistory[k] - mean;
                        sumSq += diff * diff;
                    }
                    stability = Math.Sqrt(sumSq / stabilityWindow);
                    trendMode = stability > parameters.StabilityThresholdDegrees || dominantCycle >= parameters.MaxPeriod - 1;
                }

                results[t] = new HilbertSampleResult(
                    i2[t],
                    q2[t],
                    amplitude,
                    power,
                    normI,
                    normQ,
                    phaseRad,
                    phaseDeg,
                    unwrappedDeg,
                    phaseDeltaDeg,
                    rawPeriod,
                    dominantCycle,
                    stability,
                    trendMode,
                    isValid,
                    isWarmup);

                prevPhaseRad = phaseRad;
                prevUnwrappedDeg = unwrappedDeg;
            }

            return new HilbertDecompositionResult(results, parameters.WarmupBars, parameters);
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(pool, clearArray: false);
        }
    }
}
