using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation numerical stability and IEEE-754 floating-point error diagnostics.
/// </summary>
public static class DslNumericalDiagnosticsMath
{
    public const double ResidualDenomEpsilon = 1e-28;

    /// <summary>
    /// Computes normalized infinity-norm residual <c>||Ax - b||_inf / (||A||_inf * ||x||_inf + ||b||_inf)</c>,
    /// falling back to the unnormalized <c>||Ax - b||_inf</c> when the denominator is at most <c>1e-28</c>.
    /// </summary>
    public static MathResult<double> ResidualNorm(
        scoped ReadOnlySpan<double> a,
        int m,
        int n,
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> b)
    {
        if (m < 1 || m > DslLinearAlgebraMath.MaxMatrixDim ||
            n < 1 || n > DslLinearAlgebraMath.MaxMatrixDim ||
            a.Length != m * n || x.Length != n || b.Length != m)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!double.IsFinite(a[i])) return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double normX = 0.0;
        for (int j = 0; j < n; j++)
        {
            double xj = x[j];
            if (!double.IsFinite(xj)) return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            double ax = Math.Abs(xj);
            if (ax > normX) normX = ax;
        }

        double normB = 0.0;
        double normA = 0.0;
        double normRes = 0.0;

        for (int i = 0; i < m; i++)
        {
            double bi = b[i];
            if (!double.IsFinite(bi)) return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            double ab = Math.Abs(bi);
            if (ab > normB) normB = ab;

            double rowSumAbs = 0.0;
            double axRow = 0.0;
            int rowBase = i * n;
            for (int j = 0; j < n; j++)
            {
                double aij = a[rowBase + j];
                rowSumAbs += Math.Abs(aij);
                axRow += aij * x[j];
            }

            if (rowSumAbs > normA) normA = rowSumAbs;
            double r = Math.Abs(axRow - bi);
            if (r > normRes) normRes = r;
        }

        if (!double.IsFinite(normRes) || !double.IsFinite(normA))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double denom = (normA * normX) + normB;
        if (!double.IsFinite(denom))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double result = denom <= ResidualDenomEpsilon ? normRes : normRes / denom;
        return double.IsFinite(result)
            ? MathResult<double>.Ok(result)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    public static MathResult<double> RelativeError(decimal approx, decimal exact)
    {
        if (exact == 0m)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        double diff = Math.Abs((double)approx - (double)exact);
        double denom = Math.Abs((double)exact);
        if (denom == 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        double rel = diff / denom;
        return double.IsFinite(rel)
            ? MathResult<double>.Ok(rel)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    /// <summary>
    /// Exact IEEE-754 binary64 Units-in-Last-Place distance via signed-magnitude bit conversion.
    /// Rejects NaN and Infinity with <see cref="MathErrorCode.NonFiniteResult"/>.
    /// </summary>
    public static MathResult<decimal> UlpDiff(double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
        {
            return MathResult<decimal>.Fail(MathErrorCode.NonFiniteResult);
        }

        long ia = ToOrderedInt64(a);
        long ib = ToOrderedInt64(b);
        ulong diff = ia >= ib ? (ulong)(ia - ib) : (ulong)(ib - ia);
        return MathResult<decimal>.Ok(diff);
    }

    /// <summary>
    /// Compensated Neumaier summation of <paramref name="arr"/> returning the compensated sum
    /// and the recovered rounding error relative to naive left-to-right binary64 summation.
    /// </summary>
    public static MathErrorCode KahanSum(
        scoped ReadOnlySpan<double> arr,
        out double sum,
        out double recoveredError)
    {
        sum = 0.0;
        recoveredError = 0.0;
        if (arr.Length < 1 || arr.Length > DslArrayArena.MaxArrayLength)
        {
            return MathErrorCode.DomainError;
        }

        double naive = 0.0;
        double compSum = 0.0;
        double comp = 0.0;
        for (int i = 0; i < arr.Length; i++)
        {
            double x = arr[i];
            if (!double.IsFinite(x))
            {
                return MathErrorCode.NonFiniteResult;
            }

            naive += x;
            double t = compSum + x;
            if (Math.Abs(compSum) >= Math.Abs(x))
            {
                comp += (compSum - t) + x;
            }
            else
            {
                comp += (x - t) + compSum;
            }

            compSum = t;
        }

        double total = compSum + comp;
        if (!double.IsFinite(total) || !double.IsFinite(naive))
        {
            return MathErrorCode.NonFiniteResult;
        }

        sum = total;
        recoveredError = total - naive;
        return double.IsFinite(recoveredError) ? MathErrorCode.None : MathErrorCode.NonFiniteResult;
    }

    /// <summary>
    /// Estimates significand bits lost to catastrophic cancellation in <c>a + b</c>, clamped to <c>[0, 53]</c>.
    /// </summary>
    public static MathResult<double> CancellationBits(double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (a == 0.0 || b == 0.0 || Math.Sign(a) == Math.Sign(b))
        {
            return MathResult<double>.Ok(0.0);
        }

        double sum = a + b;
        if (sum == 0.0)
        {
            return MathResult<double>.Ok(53.0);
        }

        double ratio = Math.Max(Math.Abs(a), Math.Abs(b)) / Math.Abs(sum);
        double bits = Math.Log2(ratio);
        if (!double.IsFinite(bits))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        return MathResult<double>.Ok(Math.Clamp(bits, 0.0, 53.0));
    }

    private static long ToOrderedInt64(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return bits < 0 ? unchecked(long.MinValue - bits) : bits;
    }
}
