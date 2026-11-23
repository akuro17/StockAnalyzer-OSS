using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation biased sample ACF and un-clamped Levinson-Durbin PACF kernels.
/// Uses <see cref="WindowMoments"/> for canonical flat-series detection without altering <see cref="ArimaMath"/>.
/// </summary>
public static partial class DslTimeSeriesMath
{
    public const int MaxAcfLag = 128;
    public const int MaxPacfLag = 64;
    public const double SingularityEpsilon = 1e-12;

    public static MathResult<double> Acf(
        scoped ReadOnlySpan<double> series,
        int lag,
        ref DslExecutionBudget budget)
    {
        if (!TryValidateSeries(series, MaxAcfLag, out int maxAllowed, out WindowMoments moments, out MathErrorCode err))
        {
            return MathResult<double>.Fail(err);
        }

        if (lag < 0 || lag > maxAllowed)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (!budget.TryConsume(1))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        if (lag == 0)
        {
            return MathResult<double>.Ok(1.0);
        }

        double val = ComputeBiasedAcfAt(series, lag, moments.Mean, moments.M2);
        return double.IsFinite(val)
            ? MathResult<double>.Ok(val)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    public static MathErrorCode AcfSeries(
        scoped ReadOnlySpan<double> series,
        int maxLag,
        scoped Span<double> rOut,
        ref DslExecutionBudget budget)
    {
        if (!TryValidateSeries(series, MaxAcfLag, out int maxAllowed, out WindowMoments moments, out MathErrorCode err))
        {
            return err;
        }

        if (maxLag < 0 || maxLag > maxAllowed)
        {
            return MathErrorCode.DomainError;
        }

        if (rOut.Length < maxLag + 1)
        {
            return MathErrorCode.DimensionMismatch;
        }

        rOut[0] = 1.0;
        for (int k = 1; k <= maxLag; k++)
        {
            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double val = ComputeBiasedAcfAt(series, k, moments.Mean, moments.M2);
            if (!double.IsFinite(val))
            {
                return MathErrorCode.NonFiniteResult;
            }

            rOut[k] = val;
        }

        return MathErrorCode.None;
    }

    public static MathResult<double> Pacf(
        scoped ReadOnlySpan<double> series,
        int lag,
        ref DslExecutionBudget budget)
    {
        if (!TryValidateSeries(series, MaxPacfLag, out int maxAllowed, out _, out MathErrorCode err))
        {
            return MathResult<double>.Fail(err);
        }

        if (lag < 1 || lag > maxAllowed)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        Span<double> phiOut = stackalloc double[lag];
        MathErrorCode pacfErr = PacfSeries(series, lag, phiOut, ref budget);
        return pacfErr == MathErrorCode.None
            ? MathResult<double>.Ok(phiOut[lag - 1])
            : MathResult<double>.Fail(pacfErr);
    }

    /// <summary>
    /// Computes partial autocorrelations for lags <c>1..maxLag</c> into <paramref name="phiOut"/>
    /// (zero-based index <c>k - 1</c> holds lag <c>k</c>). Uses un-clamped Levinson-Durbin
    /// recursion and rejects singular Toeplitz/near-unit-root steps with <see cref="MathErrorCode.SingularMatrix"/>.
    /// </summary>
    public static MathErrorCode PacfSeries(
        scoped ReadOnlySpan<double> series,
        int maxLag,
        scoped Span<double> phiOut,
        ref DslExecutionBudget budget)
    {
        if (!TryValidateSeries(series, MaxPacfLag, out int maxAllowed, out WindowMoments moments, out MathErrorCode err))
        {
            return err;
        }

        if (maxLag < 1 || maxLag > maxAllowed)
        {
            return MathErrorCode.DomainError;
        }

        if (phiOut.Length < maxLag)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<double> acf = stackalloc double[maxLag + 1];
        acf[0] = 1.0;
        for (int k = 1; k <= maxLag; k++)
        {
            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double val = ComputeBiasedAcfAt(series, k, moments.Mean, moments.M2);
            if (!double.IsFinite(val))
            {
                return MathErrorCode.NonFiniteResult;
            }

            acf[k] = val;
        }

        Span<double> phiPrev = stackalloc double[MaxPacfLag + 1];
        Span<double> phiCurr = stackalloc double[MaxPacfLag + 1];
        double predictionVariance = 1.0;

        for (int k = 1; k <= maxLag; k++)
        {
            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            if (!(predictionVariance > SingularityEpsilon))
            {
                return MathErrorCode.SingularMatrix;
            }

            double num = acf[k];
            for (int j = 1; j < k; j++)
            {
                num -= phiPrev[j] * acf[k - j];
            }

            double kk = num / predictionVariance;
            if (!double.IsFinite(kk))
            {
                return MathErrorCode.SingularMatrix;
            }

            if (Math.Abs(kk) >= 1.0 - SingularityEpsilon)
            {
                return MathErrorCode.SingularMatrix;
            }

            phiCurr[k] = kk;
            for (int j = 1; j < k; j++)
            {
                phiCurr[j] = phiPrev[j] - (kk * phiPrev[k - j]);
            }

            phiCurr.Slice(1, k).CopyTo(phiPrev.Slice(1, k));
            phiOut[k - 1] = kk;
            predictionVariance *= 1.0 - (kk * kk);
        }

        return MathErrorCode.None;
    }

    private static bool TryValidateSeries(
        scoped ReadOnlySpan<double> series,
        int absoluteMaxLag,
        out int maxAllowedLag,
        out WindowMoments moments,
        out MathErrorCode error)
    {
        maxAllowedLag = 0;
        moments = default;

        int n = series.Length;
        if (n < 3 || n > DslArrayArena.MaxArrayLength)
        {
            error = MathErrorCode.DomainError;
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(series[i]))
            {
                error = MathErrorCode.NonFiniteResult;
                return false;
            }
        }

        if (!WindowMoments.TryCompute(series, FlatWindowRule.Series, out moments) || moments.IsFlat)
        {
            error = MathErrorCode.DivisionByZero;
            return false;
        }

        if (!(moments.M2 > 0.0))
        {
            error = MathErrorCode.DivisionByZero;
            return false;
        }

        maxAllowedLag = Math.Min(n - 2, absoluteMaxLag);
        error = MathErrorCode.None;
        return true;
    }

    private static double ComputeBiasedAcfAt(scoped ReadOnlySpan<double> series, int lag, double mean, double gamma0)
    {
        double cov = 0.0;
        int limit = series.Length - lag;
        for (int t = 0; t < limit; t++)
        {
            cov += (series[t] - mean) * (series[t + lag] - mean);
        }

        return cov / gamma0;
    }
}
