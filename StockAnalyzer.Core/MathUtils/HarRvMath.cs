using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>Daily variance estimator backing the multi-horizon HAR-RV model.</summary>
public enum HarRvBaseEstimator
{
    /// <summary>Rogers-Satchell range variance per bar (default).</summary>
    RogersSatchell = 0,

    /// <summary>Short-window Yang-Zhang overnight/open-to-close/range variance estimator.</summary>
    YangZhang = 1,

    /// <summary>Squared close-to-close log return r_t^2.</summary>
    CloseToClose = 2
}

/// <summary>
/// Heterogeneous Autoregressive Realized Volatility (Corsi 2009) calculation engine.
/// Fits rolling Ridge-stabilized OLS on lagged short/medium/long variance components to forecast next-bar realized volatility.
/// Unit: percentage volatility per bar by default (annualized when requested via <see cref="IndicatorCalculationHelper.AnnualizationFactor"/>).
/// </summary>
public static class HarRvMath
{
    private const int RegressorCount = 4;

    /// <summary>
    /// Computes causal rolling HAR-RV forecasts and the short/medium/long horizon volatility components into the caller-provided output lists.
    /// </summary>
    public static void Compute(
        IReadOnlyList<CoreCandleData> candles,
        HarRvBaseEstimator estimator,
        int shortPeriod,
        int mediumPeriod,
        int longPeriod,
        int estimationWindow,
        bool annualize,
        int periodsPerYear,
        IList<decimal?> predictedVolatility,
        IList<decimal?> rvShortOut,
        IList<decimal?> rvMediumOut,
        IList<decimal?> rvLongOut)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(predictedVolatility);
        ArgumentNullException.ThrowIfNull(rvShortOut);
        ArgumentNullException.ThrowIfNull(rvMediumOut);
        ArgumentNullException.ThrowIfNull(rvLongOut);

        if (shortPeriod < 1 || mediumPeriod <= shortPeriod || longPeriod <= mediumPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mediumPeriod),
                "HAR-RV horizons must satisfy 1 <= ShortPeriod < MediumPeriod < LongPeriod.");
        }

        if (estimationWindow < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(estimationWindow), "EstimationWindow must be positive.");
        }

        predictedVolatility.Clear();
        rvShortOut.Clear();
        rvMediumOut.Clear();
        rvLongOut.Clear();

        int count = candles.Count;
        if (count == 0)
        {
            return;
        }

        int numBuffers = estimator == HarRvBaseEstimator.YangZhang ? 7 : 4;
        double[] pooled = ArrayPool<double>.Shared.Rent(count * numBuffers);
        try
        {
            Span<double> dailyVariance = pooled.AsSpan(0, count);
            Span<double> rvShort = pooled.AsSpan(count, count);
            Span<double> rvMedium = pooled.AsSpan(2 * count, count);
            Span<double> rvLong = pooled.AsSpan(3 * count, count);

            if (estimator == HarRvBaseEstimator.YangZhang)
            {
                ComputeDailyVariance(
                    candles,
                    estimator,
                    dailyVariance,
                    pooled.AsSpan(4 * count, count),
                    pooled.AsSpan(5 * count, count),
                    pooled.AsSpan(6 * count, count));
            }
            else
            {
                ComputeDailyVariance(candles, estimator, dailyVariance, Span<double>.Empty, Span<double>.Empty, Span<double>.Empty);
            }

            ComputeRollingMean(dailyVariance, shortPeriod, rvShort);
            ComputeRollingMean(dailyVariance, mediumPeriod, rvMedium);
            ComputeRollingMean(dailyVariance, longPeriod, rvLong);

            double scale = IndicatorDefaultConstants.PercentScale
                * IndicatorCalculationHelper.AnnualizationFactor(annualize, periodsPerYear);

            for (int t = 0; t < count; t++)
            {
                rvShortOut.Add(ToVolatilityDecimal(rvShort[t], scale));
                rvMediumOut.Add(ToVolatilityDecimal(rvMedium[t], scale));
                rvLongOut.Add(ToVolatilityDecimal(rvLong[t], scale));
            }

            Span<double> normalMatrix = stackalloc double[RegressorCount * RegressorCount];
            Span<double> rhs = stackalloc double[RegressorCount];
            Span<double> beta = stackalloc double[RegressorCount];

            for (int t = 0; t < count; t++)
            {
                if (!double.IsFinite(rvShort[t]) || !double.IsFinite(rvMedium[t]) || !double.IsFinite(rvLong[t]))
                {
                    predictedVolatility.Add(null);
                    continue;
                }

                int startS = t - estimationWindow + 1;
                if (startS < 1 || !TryBuildNormalEquations(rvShort, rvMedium, rvLong, startS, t, normalMatrix, rhs))
                {
                    predictedVolatility.Add(null);
                    continue;
                }

                double meanV = rhs[0] / normalMatrix[0];
                double forecast;
                if (!double.IsFinite(meanV) || meanV <= 1e-24)
                {
                    forecast = 0.0;
                }
                else
                {
                    // Scale normal equations by window mean variance so Ridge penalty remains scale-invariant across assets/timeframes.
                    double invMean = 1.0 / meanV;
                    double invMeanSq = invMean * invMean;
                    for (int i = 1; i < RegressorCount; i++)
                    {
                        normalMatrix[i] *= invMean;
                        normalMatrix[i * RegressorCount] = normalMatrix[i];
                        rhs[i] *= invMeanSq;
                        for (int j = 1; j < RegressorCount; j++)
                        {
                            normalMatrix[i * RegressorCount + j] *= invMeanSq;
                        }
                    }

                    rhs[0] *= invMean;
                    normalMatrix[1 * RegressorCount + 1] += IndicatorDefaultConstants.HarRvRidgeLambda;
                    normalMatrix[2 * RegressorCount + 2] += IndicatorDefaultConstants.HarRvRidgeLambda;
                    normalMatrix[3 * RegressorCount + 3] += IndicatorDefaultConstants.HarRvRidgeLambda;

                    bool solved = SolveLinearSystem4x4(normalMatrix, rhs, beta);
                    forecast = solved
                        ? (beta[0] * meanV) + beta[1] * rvShort[t] + beta[2] * rvMedium[t] + beta[3] * rvLong[t]
                        : double.NaN;

                    if (!solved || !double.IsFinite(forecast) || forecast <= 0.0)
                    {
                        forecast = (rvShort[t] + rvMedium[t] + rvLong[t]) / 3.0;
                    }
                }

                predictedVolatility.Add(ToVolatilityDecimal(forecast, scale));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(pooled);
        }
    }

    private static decimal? ToVolatilityDecimal(double variance, double scale)
    {
        if (!double.IsFinite(variance) || variance < 0.0)
        {
            return null;
        }

        return IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt(variance) * scale);
    }

    private static void ComputeDailyVariance(
        IReadOnlyList<CoreCandleData> candles,
        HarRvBaseEstimator estimator,
        Span<double> dailyVariance,
        Span<double> oRet,
        Span<double> cRet,
        Span<double> rsStep)
    {
        int count = candles.Count;
        for (int i = 0; i < count; i++)
        {
            dailyVariance[i] = double.NaN;
        }

        switch (estimator)
        {
            case HarRvBaseEstimator.RogersSatchell:
                for (int t = 0; t < count; t++)
                {
                    CoreCandleData c = candles[t];
                    double rs = RangeVolatilityMath.ComputeRogersSatchellStep(c.Open, c.High, c.Low, c.Close);
                    if (double.IsFinite(rs))
                    {
                        dailyVariance[t] = Math.Max(0.0, rs);
                    }
                }
                break;

            case HarRvBaseEstimator.CloseToClose:
                for (int t = 1; t < count; t++)
                {
                    double prevClose = (double)candles[t - 1].Close;
                    double close = (double)candles[t].Close;
                    if (prevClose > 0.0 && close > 0.0 && double.IsFinite(prevClose) && double.IsFinite(close))
                    {
                        double ret = Math.Log(close / prevClose);
                        dailyVariance[t] = ret * ret;
                    }
                }
                break;

            case HarRvBaseEstimator.YangZhang:
                ComputeYangZhangDailyVariance(candles, dailyVariance, oRet, cRet, rsStep);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(estimator), estimator, "Unsupported HAR-RV base estimator.");
        }
    }

    private static void ComputeYangZhangDailyVariance(
        IReadOnlyList<CoreCandleData> candles,
        Span<double> dailyVariance,
        Span<double> oRet,
        Span<double> cRet,
        Span<double> rsStep)
    {
        int count = candles.Count;
        const int innerPeriod = IndicatorDefaultConstants.HarRvYangZhangInnerPeriod;
        double k = IndicatorDefaultConstants.HarRvYangZhangAlphaFactor
            / (IndicatorDefaultConstants.HarRvYangZhangBetaFactor + (innerPeriod + 1.0) / (innerPeriod - 1.0));

        oRet[0] = double.NaN;
        cRet[0] = double.NaN;
        rsStep[0] = double.NaN;

        for (int t = 1; t < count; t++)
        {
            double prevClose = (double)candles[t - 1].Close;
            double open = (double)candles[t].Open;
            double close = (double)candles[t].Close;
            double rs = RangeVolatilityMath.ComputeRogersSatchellStep(
                candles[t].Open, candles[t].High, candles[t].Low, candles[t].Close);

            if (prevClose > 0.0 && open > 0.0 && close > 0.0 && double.IsFinite(rs))
            {
                oRet[t] = Math.Log(open / prevClose);
                cRet[t] = Math.Log(close / open);
                rsStep[t] = rs;
            }
            else
            {
                oRet[t] = double.NaN;
                cRet[t] = double.NaN;
                rsStep[t] = double.NaN;
            }

            if (t < innerPeriod)
            {
                continue;
            }

            int start = t - innerPeriod + 1;
            if (!TryComputeMean(oRet, start, innerPeriod, out double meanO) ||
                !TryComputeMean(cRet, start, innerPeriod, out double meanC) ||
                !TryComputeMean(rsStep, start, innerPeriod, out double meanRs))
            {
                continue;
            }

            double sumSqO = 0.0;
            double sumSqC = 0.0;
            for (int j = start; j <= t; j++)
            {
                double doj = oRet[j] - meanO;
                double dcj = cRet[j] - meanC;
                sumSqO += doj * doj;
                sumSqC += dcj * dcj;
            }

            double sigmaO2 = sumSqO / (innerPeriod - 1);
            double sigmaC2 = sumSqC / (innerPeriod - 1);
            double combined = sigmaO2 + k * sigmaC2 + (1.0 - k) * meanRs;
            if (double.IsFinite(combined))
            {
                dailyVariance[t] = Math.Max(0.0, combined);
            }
        }
    }

    private static bool TryComputeMean(ReadOnlySpan<double> values, int start, int length, out double mean)
    {
        double sum = 0.0;
        for (int i = start; i < start + length; i++)
        {
            double v = values[i];
            if (!double.IsFinite(v))
            {
                mean = double.NaN;
                return false;
            }

            sum += v;
        }

        mean = sum / length;
        return true;
    }

    private static void ComputeRollingMean(ReadOnlySpan<double> dailyVariance, int horizon, Span<double> result)
    {
        int count = dailyVariance.Length;
        for (int t = 0; t < count; t++)
        {
            if (t < horizon - 1)
            {
                result[t] = double.NaN;
                continue;
            }

            double sum = 0.0;
            bool valid = true;
            for (int k = t - horizon + 1; k <= t; k++)
            {
                double v = dailyVariance[k];
                if (!double.IsFinite(v))
                {
                    valid = false;
                    break;
                }

                sum += v;
            }

            result[t] = valid ? Math.Max(0.0, sum / horizon) : double.NaN;
        }
    }

    private static bool TryBuildNormalEquations(
        ReadOnlySpan<double> rvShort,
        ReadOnlySpan<double> rvMedium,
        ReadOnlySpan<double> rvLong,
        int startS,
        int endS,
        Span<double> a,
        Span<double> b)
    {
        a.Clear();
        b.Clear();

        for (int s = startS; s <= endS; s++)
        {
            double y = rvShort[s];
            double x1 = rvShort[s - 1];
            double x2 = rvMedium[s - 1];
            double x3 = rvLong[s - 1];
            if (!double.IsFinite(y) || !double.IsFinite(x1) || !double.IsFinite(x2) || !double.IsFinite(x3))
            {
                return false;
            }

            a[0] += 1.0;
            a[1] += x1;
            a[2] += x2;
            a[3] += x3;
            a[1 * RegressorCount + 1] += x1 * x1;
            a[1 * RegressorCount + 2] += x1 * x2;
            a[1 * RegressorCount + 3] += x1 * x3;
            a[2 * RegressorCount + 2] += x2 * x2;
            a[2 * RegressorCount + 3] += x2 * x3;
            a[3 * RegressorCount + 3] += x3 * x3;

            b[0] += y;
            b[1] += x1 * y;
            b[2] += x2 * y;
            b[3] += x3 * y;
        }

        a[1 * RegressorCount + 0] = a[0 * RegressorCount + 1];
        a[2 * RegressorCount + 0] = a[0 * RegressorCount + 2];
        a[2 * RegressorCount + 1] = a[1 * RegressorCount + 2];
        a[3 * RegressorCount + 0] = a[0 * RegressorCount + 3];
        a[3 * RegressorCount + 1] = a[1 * RegressorCount + 3];
        a[3 * RegressorCount + 2] = a[2 * RegressorCount + 3];
        return true;
    }

    private static bool SolveLinearSystem4x4(Span<double> matrix, Span<double> rhs, Span<double> solution)
    {
        for (int col = 0; col < RegressorCount; col++)
        {
            int pivotRow = col;
            double maxAbs = Math.Abs(matrix[col * RegressorCount + col]);
            for (int row = col + 1; row < RegressorCount; row++)
            {
                double candidate = Math.Abs(matrix[row * RegressorCount + col]);
                if (candidate > maxAbs)
                {
                    maxAbs = candidate;
                    pivotRow = row;
                }
            }

            if (!(maxAbs > IndicatorDefaultConstants.HarRvSingularPivotThreshold) || !double.IsFinite(maxAbs))
            {
                return false;
            }

            if (pivotRow != col)
            {
                for (int k = col; k < RegressorCount; k++)
                {
                    int colIdx = col * RegressorCount + k;
                    int pivIdx = pivotRow * RegressorCount + k;
                    (matrix[colIdx], matrix[pivIdx]) = (matrix[pivIdx], matrix[colIdx]);
                }

                (rhs[col], rhs[pivotRow]) = (rhs[pivotRow], rhs[col]);
            }

            double pivot = matrix[col * RegressorCount + col];
            for (int row = col + 1; row < RegressorCount; row++)
            {
                double factor = matrix[row * RegressorCount + col] / pivot;
                matrix[row * RegressorCount + col] = 0.0;
                for (int k = col + 1; k < RegressorCount; k++)
                {
                    matrix[row * RegressorCount + k] -= factor * matrix[col * RegressorCount + k];
                }

                rhs[row] -= factor * rhs[col];
            }
        }

        for (int row = RegressorCount - 1; row >= 0; row--)
        {
            double sum = rhs[row];
            for (int col = row + 1; col < RegressorCount; col++)
            {
                sum -= matrix[row * RegressorCount + col] * solution[col];
            }

            double diag = matrix[row * RegressorCount + row];
            if (!(Math.Abs(diag) > IndicatorDefaultConstants.HarRvSingularPivotThreshold))
            {
                return false;
            }

            double val = sum / diag;
            if (!double.IsFinite(val))
            {
                return false;
            }

            solution[row] = val;
        }

        return true;
    }
}
