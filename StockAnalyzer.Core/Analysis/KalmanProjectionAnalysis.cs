using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Analysis;

/// <summary>One projected future bar of the Kalman projection (price-native, relative to the last filtered bar).</summary>
/// <param name="StepOffset">Bars ahead of the last filtered bar (1-based).</param>
/// <param name="ProjectedPrice">Extrapolated price p + StepOffset * v.</param>
/// <param name="UpperBand">ProjectedPrice + confidence margin.</param>
/// <param name="LowerBand">ProjectedPrice - confidence margin.</param>
public readonly record struct KalmanProjectedStep(int StepOffset, decimal ProjectedPrice, decimal UpperBand, decimal LowerBand);

/// <summary>
/// Result of the 2nd-order kinematic Kalman projection. Price, velocity (price per bar), gains and variances are
/// price-native decimals; <see cref="InnovationScale"/> is the effective band scale that multiplies the propagated
/// position standard deviation.
/// </summary>
public sealed class KalmanProjectionResult
{
    public static readonly KalmanProjectionResult Empty = new();

    public IReadOnlyList<KalmanProjectedStep> Steps { get; init; } = Array.Empty<KalmanProjectedStep>();

    /// <summary>Filtered price estimate at the last input bar.</summary>
    public decimal TerminalPosition { get; init; }

    /// <summary>Filtered velocity at the last input bar (price per bar).</summary>
    public decimal TerminalVelocity { get; init; }

    /// <summary>Kalman gain K0 of the last measurement update (0 when no update happened).</summary>
    public decimal TerminalGainPosition { get; init; }

    /// <summary>Kalman gain K1 of the last measurement update (0 when no update happened).</summary>
    public decimal TerminalGainVelocity { get; init; }

    /// <summary>Empirical innovation scale applied to the propagated position deviation when building the bands.</summary>
    public decimal InnovationScale { get; init; }

    /// <summary>Position variance P00 after the last measurement update.</summary>
    public decimal PositionVariance { get; init; }

    /// <summary>Velocity variance P11 after the last measurement update.</summary>
    public decimal VelocityVariance { get; init; }

    /// <summary>Number of input bars the filter ran over.</summary>
    public int SampleCount { get; init; }

    public bool IsEmpty => SampleCount == 0;
}

/// <summary>
/// 2nd-order kinematic (constant-velocity) Kalman filter, state x = [price, velocity]^T, F = [[1,1],[0,1]],
/// followed by a covariance-propagated extrapolation. Extracted unchanged from the Kalman projection drawing
/// so the math has a single home; the drawing only maps the steps onto timestamps.
/// </summary>
public static class KalmanProjectionAnalysis
{
    /// <summary>Lower bound applied to the process and measurement noise.</summary>
    public const decimal MinNoise = 0.00001m;

    public const int MinForecastHorizon = 1;
    public const int MaxForecastHorizon = 100;

    private const double MinInnovationScaleAbsolute = 0.01;
    private const double MinInnovationScaleRelativeToPrice = 0.005;

    /// <param name="closes">Chronological closes of the selected range (oldest first).</param>
    /// <param name="forecastHorizon">Bars to project; clamped to [MinForecastHorizon, MaxForecastHorizon].</param>
    /// <param name="processNoise">Q; floored at <see cref="MinNoise"/>.</param>
    /// <param name="measurementNoise">R; floored at <see cref="MinNoise"/>.</param>
    /// <param name="confidenceMultiplier">M in +-M*sigma; negative values are treated as 0.</param>
    /// <returns>The projection; <see cref="KalmanProjectionResult.Empty"/> for 0 closes or when a band or scale value cannot be narrowed to <c>decimal</c> (non-finite or out of range).</returns>
    public static KalmanProjectionResult CalculateProjection(
        ReadOnlySpan<decimal> closes,
        int forecastHorizon,
        decimal processNoise,
        decimal measurementNoise,
        decimal confidenceMultiplier)
    {
        int count = closes.Length;
        if (count == 0) return KalmanProjectionResult.Empty;

        decimal p = closes[0];
        decimal v = count > 1 ? closes[1] - closes[0] : 0m;

        decimal p00 = 1m;
        decimal p01 = 0m;
        decimal p10 = 0m;
        decimal p11 = 1m;

        decimal qVal = Math.Max(MinNoise, processNoise);
        decimal rVal = Math.Max(MinNoise, measurementNoise);

        decimal lastK0 = 0m;
        decimal lastK1 = 0m;

        // Innovation residuals estimate the empirical price scale (sigma_hat).
        double sumNormalizedResidualSq = 0.0;
        int validResidualCount = 0;

        for (int i = 1; i < count; i++)
        {
            // Predict: x_pred = [p + v, v]; P_pred = F P F^T + Q
            decimal pPred = p + v;
            decimal vPred = v;

            decimal predP00 = p00 + p01 + p10 + p11 + qVal;
            decimal predP01 = p01 + p11;
            decimal predP10 = p10 + p11;
            decimal predP11 = p11 + qVal;

            // Update
            decimal y = closes[i] - pPred;
            decimal s = predP00 + rVal;

            if (s != 0m)
            {
                decimal k0 = predP00 / s;
                decimal k1 = predP10 / s;

                p = pPred + k0 * y;
                v = vPred + k1 * y;

                p00 = (1m - k0) * predP00;
                p01 = (1m - k0) * predP01;
                p10 = predP10 - k1 * predP00;
                p11 = predP11 - k1 * predP01;

                lastK0 = k0;
                lastK1 = k1;

                sumNormalizedResidualSq += (double)(y * y / s);
                validResidualCount++;
            }
            else
            {
                p = pPred;
                v = vPred;
                p00 = predP00;
                p01 = predP01;
                p10 = predP10;
                p11 = predP11;
            }
        }

        decimal lastClose = closes[count - 1];
        double minScale = Math.Max(MinInnovationScaleAbsolute, (double)Math.Abs(lastClose) * MinInnovationScaleRelativeToPrice);
        double sigmaScale = validResidualCount > 0
            ? Math.Max(minScale, Math.Sqrt(sumNormalizedResidualSq / validResidualCount))
            : minScale;

        // Every narrowing from double goes through ToDecimalOrNull; a value it rejects makes the whole projection Empty (no silent 0, no exception).
        decimal? innovationScale = IndicatorCalculationHelper.ToDecimalOrNull(sigmaScale);
        if (innovationScale == null) return KalmanProjectionResult.Empty;

        int horizon = Math.Clamp(forecastHorizon, MinForecastHorizon, MaxForecastHorizon);
        double multiplier = (double)Math.Max(0m, confidenceMultiplier);

        var steps = new KalmanProjectedStep[horizon];
        decimal pCov00 = p00;
        decimal pCov01 = p01;
        decimal pCov10 = p10;
        decimal pCov11 = p11;

        for (int k = 1; k <= horizon; k++)
        {
            // Propagate the error covariance without measurement updates: P_k = F P_{k-1} F^T + Q
            decimal nextCov00 = pCov00 + pCov01 + pCov10 + pCov11 + qVal;
            decimal nextCov01 = pCov01 + pCov11;
            decimal nextCov10 = pCov10 + pCov11;
            decimal nextCov11 = pCov11 + qVal;

            pCov00 = nextCov00;
            pCov01 = nextCov01;
            pCov10 = nextCov10;
            pCov11 = nextCov11;

            decimal projectedPrice = p + (decimal)k * v;
            double stdDev = sigmaScale * Math.Sqrt((double)Math.Max(0m, pCov00));
            decimal? margin = IndicatorCalculationHelper.ToDecimalOrNull(stdDev * multiplier);
            if (margin == null) return KalmanProjectionResult.Empty;

            steps[k - 1] = new KalmanProjectedStep(k, projectedPrice, projectedPrice + margin.Value, projectedPrice - margin.Value);
        }

        return new KalmanProjectionResult
        {
            Steps = steps,
            TerminalPosition = p,
            TerminalVelocity = v,
            TerminalGainPosition = lastK0,
            TerminalGainVelocity = lastK1,
            InnovationScale = innovationScale.Value,
            PositionVariance = p00,
            VelocityVariance = p11,
            SampleCount = count
        };
    }
}
