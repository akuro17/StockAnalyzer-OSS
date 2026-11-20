using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation robust location and slope estimators reusing
/// <see cref="OrderStatisticsMath"/> and <see cref="RobustRegressionMath"/> contracts.
/// </summary>
public static class DslRobustStatisticsMath
{
    public const double DefaultMadScale = OrderStatisticsMath.MadToSigma; // 1.482602218505602
    public const double DefaultHuberK = 1.345;
    public const double DefaultBiweightC = 4.685;
    public const int MaxIrlsIterations = 64;
    public const double IrlsTolerance = 1e-9;
    public const double MaxTrimAlpha = 0.49;
    public const decimal DuplicateSlopeEpsilon = 1e-12m;

    public static MathResult<double> MadScaled(scoped ReadOnlySpan<double> x, double scale = DefaultMadScale)
    {
        int n = x.Length;
        if (n < 1 || n > DslArrayArena.MaxArrayLength || !double.IsFinite(scale) || scale <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i]))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }
        }

        Span<double> work = stackalloc double[n];
        Span<double> scratch = stackalloc double[n];
        x.CopyTo(work);
        double? mad = OrderStatisticsMath.MedianAbsoluteDeviation(work, scratch);
        if (mad is not { } validMad || !double.IsFinite(validMad))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double res = validMad * scale;
        return double.IsFinite(res)
            ? MathResult<double>.Ok(res)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    public static MathResult<double> TrimmedMean(scoped ReadOnlySpan<double> x, double alpha)
        => ComputeTrimmedOrWinsorized(x, alpha, winsorize: false);

    public static MathResult<double> WinsorMean(scoped ReadOnlySpan<double> x, double alpha)
        => ComputeTrimmedOrWinsorized(x, alpha, winsorize: true);

    public static MathResult<double> HuberLocation(
        scoped ReadOnlySpan<double> x,
        ref DslExecutionBudget budget,
        double k = DefaultHuberK)
    {
        if (!double.IsFinite(k) || k <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        return ComputeIrlsLocation(x, k, isBiweight: false, ref budget);
    }

    public static MathResult<double> BiweightLocation(
        scoped ReadOnlySpan<double> x,
        ref DslExecutionBudget budget,
        double c = DefaultBiweightC)
    {
        if (!double.IsFinite(c) || c <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        return ComputeIrlsLocation(x, c, isBiweight: true, ref budget);
    }

    public static MathResult<decimal> TheilSenSlope(scoped ReadOnlySpan<decimal> y)
    {
        int n = y.Length;
        if (n < RobustRegressionMath.MinWindowLength || n > RobustRegressionMath.MaxWindowLength)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int scratchLen = RobustRegressionMath.ScratchLength(n, siegel: false);
        decimal[]? rented = scratchLen > MathBufferLimits.DecimalStackAllocThreshold
            ? ArrayPool<decimal>.Shared.Rent(scratchLen)
            : null;
        Span<decimal> scratch = rented is null
            ? stackalloc decimal[scratchLen]
            : rented.AsSpan(0, scratchLen);
        try
        {
            return RobustRegressionMath.TryFit(y, siegel: false, scratch, out RobustLine line)
                ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(line.Slope))
                : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<decimal>.Shared.Return(rented);
            }
        }
    }

    public static MathResult<decimal> TheilSenSlope(scoped ReadOnlySpan<decimal> x, scoped ReadOnlySpan<decimal> y)
    {
        if (x.Length != y.Length)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = x.Length;
        if (n < RobustRegressionMath.MinWindowLength || n > RobustRegressionMath.MaxWindowLength)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int maxPairs = RobustRegressionMath.PairCount(n);
        decimal[]? rented = maxPairs > MathBufferLimits.DecimalStackAllocThreshold
            ? ArrayPool<decimal>.Shared.Rent(maxPairs)
            : null;
        Span<decimal> slopes = rented is null
            ? stackalloc decimal[maxPairs]
            : rented.AsSpan(0, maxPairs);
        try
        {
            int count = 0;
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    decimal dx = checked(x[j] - x[i]);
                    if (Math.Abs(dx) > DuplicateSlopeEpsilon)
                    {
                        slopes[count++] = checked((y[j] - y[i]) / dx);
                    }
                }
            }

            if (count == 0)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
            }

            return OrderStatisticsMath.Median(slopes.Slice(0, count)) is { } med
                ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(med))
                : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<decimal>.Shared.Return(rented);
            }
        }
    }

    public static MathResult<decimal> SiegelSlope(scoped ReadOnlySpan<decimal> y)
    {
        int n = y.Length;
        if (n < RobustRegressionMath.MinWindowLength || n > RobustRegressionMath.MaxWindowLength)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int scratchLen = RobustRegressionMath.ScratchLength(n, siegel: true);
        decimal[]? rented = scratchLen > MathBufferLimits.DecimalStackAllocThreshold
            ? ArrayPool<decimal>.Shared.Rent(scratchLen)
            : null;
        Span<decimal> scratch = rented is null
            ? stackalloc decimal[scratchLen]
            : rented.AsSpan(0, scratchLen);
        try
        {
            return RobustRegressionMath.TryFit(y, siegel: true, scratch, out RobustLine line)
                ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(line.Slope))
                : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<decimal>.Shared.Return(rented);
            }
        }
    }

    public static MathResult<decimal> SiegelSlope(scoped ReadOnlySpan<decimal> x, scoped ReadOnlySpan<decimal> y)
    {
        if (x.Length != y.Length)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = x.Length;
        if (n < RobustRegressionMath.MinWindowLength || n > RobustRegressionMath.MaxWindowLength)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> perAnchor = stackalloc decimal[n];
        Span<decimal> row = stackalloc decimal[n - 1];
        try
        {
            int anchorCount = 0;
            for (int i = 0; i < n; i++)
            {
                int rowCount = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    decimal dx = checked(x[j] - x[i]);
                    if (Math.Abs(dx) > DuplicateSlopeEpsilon)
                    {
                        row[rowCount++] = checked((y[j] - y[i]) / dx);
                    }
                }

                if (rowCount > 0)
                {
                    perAnchor[anchorCount++] = OrderStatisticsMath.Median(row.Slice(0, rowCount))
                        ?? throw new OverflowException();
                }
            }

            if (anchorCount == 0)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
            }

            return OrderStatisticsMath.Median(perAnchor.Slice(0, anchorCount)) is { } med
                ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(med))
                : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    private static MathResult<double> ComputeTrimmedOrWinsorized(scoped ReadOnlySpan<double> x, double alpha, bool winsorize)
    {
        int n = x.Length;
        if (n < 1 || n > DslArrayArena.MaxArrayLength || !double.IsFinite(alpha) || alpha < 0.0 || alpha > MaxTrimAlpha)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        int trim = (int)Math.Floor(alpha * n);
        int remaining = n - (2 * trim);
        if (remaining < 1)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i]))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }
        }

        Span<double> work = stackalloc double[n];
        x.CopyTo(work);
        work.Sort();

        double sum = 0.0;
        if (winsorize)
        {
            double lo = work[trim];
            double hi = work[n - 1 - trim];
            for (int i = 0; i < trim; i++)
            {
                sum += lo + hi;
            }

            for (int i = trim; i < n - trim; i++)
            {
                sum += work[i];
            }

            double res = sum / n;
            return double.IsFinite(res)
                ? MathResult<double>.Ok(res)
                : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        for (int i = trim; i < n - trim; i++)
        {
            sum += work[i];
        }

        double trimmed = sum / remaining;
        return double.IsFinite(trimmed)
            ? MathResult<double>.Ok(trimmed)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    private static MathResult<double> ComputeIrlsLocation(
        scoped ReadOnlySpan<double> x,
        double tuning,
        bool isBiweight,
        ref DslExecutionBudget budget)
    {
        int n = x.Length;
        if (n < 1 || n > DslArrayArena.MaxArrayLength)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i]))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }
        }

        Span<double> work = stackalloc double[n];
        Span<double> scratch = stackalloc double[n];
        x.CopyTo(work);
        double? medNullable = OrderStatisticsMath.Median(work);
        if (medNullable is not { } med || !double.IsFinite(med))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        for (int i = 0; i < n; i++)
        {
            scratch[i] = Math.Abs(x[i] - med);
        }

        double? madNullable = OrderStatisticsMath.Median(scratch);
        if (madNullable is not { } rawMad || !double.IsFinite(rawMad))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double sigma = rawMad * DefaultMadScale;
        if (!(sigma > 0.0) || (Math.Abs(med) > 0.0 && sigma <= 1e-12 * Math.Abs(med)))
        {
            return MathResult<double>.Ok(med);
        }

        double mu = med;
        for (int iter = 0; iter < MaxIrlsIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double sumW = 0.0;
            double sumWx = 0.0;
            for (int i = 0; i < n; i++)
            {
                double xi = x[i];
                double r = (xi - mu) / sigma;
                double w;
                if (isBiweight)
                {
                    double u = r / tuning;
                    double au = Math.Abs(u);
                    if (au >= 1.0)
                    {
                        w = 0.0;
                    }
                    else
                    {
                        double oneMinus = 1.0 - (u * u);
                        w = oneMinus * oneMinus;
                    }
                }
                else
                {
                    double ar = Math.Abs(r);
                    w = ar <= tuning ? 1.0 : tuning / ar;
                }

                sumW += w;
                sumWx += w * xi;
            }

            if (sumW <= 1e-12)
            {
                return MathResult<double>.Ok(mu);
            }

            double muNext = sumWx / sumW;
            if (!double.IsFinite(muNext))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            if (Math.Abs(muNext - mu) <= IrlsTolerance * Math.Max(sigma, Math.Abs(mu)))
            {
                return MathResult<double>.Ok(muNext);
            }

            mu = muNext;
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }
}
