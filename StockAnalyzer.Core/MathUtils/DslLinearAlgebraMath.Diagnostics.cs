using System;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslLinearAlgebraMath
{
    public const int MaxNearestCorrIterations = 256;
    public const double DefaultMatrixTolerance = 1e-9;
    public const double MinMatrixTolerance = 1e-12;
    public const double MaxMatrixTolerance = 1e-3;
    public const double RCondThreshold = 1e-12;

    // ---- Matrix Condition Number & Reciprocal Condition Number ----

    /// <summary>
    /// Computes the reciprocal condition number <c>rcond(A)</c> in <c>[0, 1]</c> for <c>1 &lt;= rows, cols &lt;= 16</c>.
    /// <paramref name="normKind"/>: <c>1</c> = 1-norm (square only), <c>2</c> = 2-norm via SVD (default, rectangular allowed),
    /// <c>3</c> = infinity-norm (square only). Returns <c>0.0</c> when singular.
    /// </summary>
    public static MathResult<double> MatRCond(
        scoped ReadOnlySpan<double> a,
        int rows,
        int cols,
        ref DslExecutionBudget budget,
        int normKind = 2)
    {
        if (!IsValidDims(rows, cols) || a.Length < rows * cols)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (normKind is not (1 or 2 or 3))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (normKind != 2 && rows != cols)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        int count = rows * cols;
        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, count));
        if (finiteErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(finiteErr);
        }

        if (normKind == 2)
        {
            int k = Math.Min(rows, cols);
            Span<double> u = stackalloc double[rows * k];
            Span<double> s = stackalloc double[k];
            Span<double> vt = stackalloc double[k * cols];
            MathErrorCode svdErr = MatSvd(a, rows, cols, u, s, vt, ref budget);
            if (svdErr != MathErrorCode.None)
            {
                return MathResult<double>.Fail(svdErr);
            }

            double sMax = s[0];
            double sMin = s[k - 1];
            if (sMax == 0.0 || !(sMin > 0.0))
            {
                return MathResult<double>.Ok(0.0);
            }

            double rcond = sMin / sMax;
            return double.IsFinite(rcond)
                ? MathResult<double>.Ok(Math.Clamp(rcond, 0.0, 1.0))
                : MathResult<double>.Ok(0.0);
        }

        double normA = normKind == 1
            ? ComputeOneNorm(a, rows, cols)
            : ComputeInfinityNorm(a, rows, cols);
        if (normA == 0.0)
        {
            return MathResult<double>.Ok(0.0);
        }

        Span<double> inv = stackalloc double[count];
        MathErrorCode invErr = MatInv(a, rows, inv, ref budget);
        if (invErr == MathErrorCode.SingularMatrix || invErr == MathErrorCode.ArithmeticOverflow)
        {
            return MathResult<double>.Ok(0.0);
        }

        if (invErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(invErr);
        }

        double normInv = normKind == 1
            ? ComputeOneNorm(inv, rows, cols)
            : ComputeInfinityNorm(inv, rows, cols);
        if (!double.IsFinite(normInv) || normInv <= 0.0)
        {
            return MathResult<double>.Ok(0.0);
        }

        double cond = normA * normInv;
        if (!double.IsFinite(cond))
        {
            return MathResult<double>.Ok(0.0);
        }

        double rcondP = 1.0 / cond;
        return MathResult<double>.Ok(Math.Clamp(rcondP, 0.0, 1.0));
    }

    /// <summary>
    /// Computes the condition number <c>cond(A) = 1 / rcond(A)</c>; returns <see cref="MathErrorCode.SingularMatrix"/>
    /// when singular or <c>rcond &lt;= 1e-12</c> (prohibits <c>Infinity</c>).
    /// </summary>
    public static MathResult<double> MatCond(
        scoped ReadOnlySpan<double> a,
        int rows,
        int cols,
        ref DslExecutionBudget budget,
        int normKind = 2)
    {
        MathResult<double> rcondRes = MatRCond(a, rows, cols, ref budget, normKind);
        if (!rcondRes.IsOk)
        {
            return rcondRes;
        }

        if (rcondRes.Value <= RCondThreshold)
        {
            return MathResult<double>.Fail(MathErrorCode.SingularMatrix);
        }

        double cond = 1.0 / rcondRes.Value;
        return double.IsFinite(cond)
            ? MathResult<double>.Ok(cond)
            : MathResult<double>.Fail(MathErrorCode.SingularMatrix);
    }

    // ---- Correlation Matrix Validation & Higham Nearest Correlation Projection ----

    /// <summary>
    /// Validates a square correlation matrix (<c>2 &lt;= N &lt;= 16</c>) across finiteness, range <c>[-1-tol, 1+tol]</c>,
    /// symmetry, unit diagonal, PSD (Jacobi eigenvalues), and conditioning.
    /// </summary>
    public static MathErrorCode MatValidateCorr(
        scoped ReadOnlySpan<double> r,
        int rows,
        int cols,
        double tol,
        out MatrixValidationFlags flags,
        ref DslExecutionBudget budget)
    {
        flags = MatrixValidationFlags.Valid;
        if (!double.IsFinite(tol) || tol < MinMatrixTolerance || tol > MaxMatrixTolerance)
        {
            return MathErrorCode.DomainError;
        }

        if (rows != cols)
        {
            flags |= MatrixValidationFlags.NotSquare;
        }

        if (rows < 2 || cols < 2 || rows > MaxMatrixDim || cols > MaxMatrixDim)
        {
            flags |= MatrixValidationFlags.DimensionOver;
            return MathErrorCode.None;
        }

        int count = rows * cols;
        if (r.Length < count)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (ValidateFinite(r.Slice(0, count)) != MathErrorCode.None)
        {
            flags |= MatrixValidationFlags.NonFinite;
            return MathErrorCode.None;
        }

        if (rows != cols)
        {
            return MathErrorCode.None;
        }

        int n = rows;
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(r[i * n + i] - 1.0) > tol)
            {
                flags |= MatrixValidationFlags.InvalidDiag;
            }

            for (int j = 0; j < n; j++)
            {
                double val = r[i * n + j];
                if (Math.Abs(val) > 1.0 + tol)
                {
                    flags |= MatrixValidationFlags.OutOfRange;
                }

                if (j > i && Math.Abs(val - r[j * n + i]) > tol)
                {
                    flags |= MatrixValidationFlags.Asymmetric;
                }
            }
        }

        Span<double> sym = stackalloc double[count];
        Symmetrize(r, n, sym);
        Span<double> evals = stackalloc double[n];
        Span<double> evecs = stackalloc double[count];
        MathErrorCode eigErr = MatEigenSym(sym, n, evals, evecs, ref budget);
        if (eigErr != MathErrorCode.None)
        {
            return eigErr;
        }

        double maxEval = evals[0];
        double minEval = evals[n - 1];
        if (minEval < -tol)
        {
            flags |= MatrixValidationFlags.NotPsd | MatrixValidationFlags.NotPd;
        }
        else if (minEval <= RCondThreshold * Math.Max(1.0, maxEval))
        {
            flags |= MatrixValidationFlags.NotPd | MatrixValidationFlags.IllConditioned;
        }

        return MathErrorCode.None;
    }

    public static MathResult<bool> MatIsCorr(
        scoped ReadOnlySpan<double> r,
        int rows,
        int cols,
        double tol,
        ref DslExecutionBudget budget,
        bool requirePd = false)
    {
        MathErrorCode err = MatValidateCorr(r, rows, cols, tol, out MatrixValidationFlags flags, ref budget);
        if (err != MathErrorCode.None)
        {
            return MathResult<bool>.Fail(err);
        }

        bool valid = requirePd
            ? flags == MatrixValidationFlags.Valid
            : (flags & ~(MatrixValidationFlags.NotPd | MatrixValidationFlags.IllConditioned)) == MatrixValidationFlags.Valid;
        return MathResult<bool>.Ok(valid);
    }

    /// <summary>
    /// Computes the nearest positive semidefinite correlation matrix to <paramref name="r"/>
    /// via Higham (2002) alternating projections with Dykstra correction (max 64 iterations).
    /// </summary>
    public static MathErrorCode MatNearestCorr(
        scoped ReadOnlySpan<double> r,
        int n,
        scoped Span<double> rPsd,
        ref DslExecutionBudget budget,
        double tol = DefaultMatrixTolerance)
    {
        if (n < 2 || n > MaxMatrixDim || r.Length < n * n || rPsd.Length < n * n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(tol) || tol < MinMatrixTolerance || tol > MaxMatrixTolerance)
        {
            return MathErrorCode.DomainError;
        }

        int nn = n * n;
        MathErrorCode finiteErr = ValidateFinite(r.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        Span<double> y = stackalloc double[nn];
        Span<double> ds = stackalloc double[nn];
        Span<double> rk = stackalloc double[nn];
        Span<double> x = stackalloc double[nn];
        Span<double> yNext = stackalloc double[nn];
        Span<double> diff = stackalloc double[nn];
        Span<double> evals = stackalloc double[n];
        Span<double> evecs = stackalloc double[nn];

        Symmetrize(r, n, y);
        ds.Clear();

        for (int iter = 0; iter < MaxNearestCorrIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            for (int i = 0; i < nn; i++)
            {
                rk[i] = y[i] - ds[i];
            }

            MathErrorCode eigErr = MatEigenSym(rk, n, evals, evecs, ref budget);
            if (eigErr != MathErrorCode.None)
            {
                return eigErr;
            }

            ReconstructPsd(evecs, evals, n, x);
            for (int i = 0; i < nn; i++)
            {
                ds[i] = x[i] - rk[i];
            }

            ProjectUnitDiagonalSym(x, n, yNext);

            double normY = Math.Max(1.0, ComputeInfinityNorm(yNext, n, n));
            for (int i = 0; i < nn; i++)
            {
                diff[i] = yNext[i] - y[i];
            }

            double deltaY = ComputeInfinityNorm(diff, n, n) / normY;
            for (int i = 0; i < nn; i++)
            {
                diff[i] = x[i] - yNext[i];
            }

            double gap = ComputeInfinityNorm(diff, n, n) / normY;
            yNext.CopyTo(y);

            if (Math.Max(deltaY, gap) <= tol)
            {
                NormalizeToCorrelation(x, n, rPsd);
                return MathErrorCode.None;
            }
        }

        return MathErrorCode.ConvergenceFailure;
    }

    // ---- Covariance Matrix Validation & Correlation Conversion ----

    /// <summary>
    /// Validates a square covariance matrix (<c>1 &lt;= N &lt;= 16</c>) for scale-adaptive symmetry,
    /// non-negative diagonal, Cauchy-Schwarz off-diagonal bounds, PSD eigenvalues, and Cholesky PD pivots.
    /// </summary>
    public static MathErrorCode MatValidateCov(
        scoped ReadOnlySpan<double> s,
        int rows,
        int cols,
        bool requirePd,
        double tol,
        out MatrixValidationFlags flags,
        ref DslExecutionBudget budget)
    {
        flags = MatrixValidationFlags.Valid;
        if (!double.IsFinite(tol) || tol < MinMatrixTolerance || tol > MaxMatrixTolerance)
        {
            return MathErrorCode.DomainError;
        }

        if (rows != cols)
        {
            flags |= MatrixValidationFlags.NotSquare;
        }

        if (rows < 1 || cols < 1 || rows > MaxMatrixDim || cols > MaxMatrixDim)
        {
            flags |= MatrixValidationFlags.DimensionOver;
            return MathErrorCode.None;
        }

        int count = rows * cols;
        if (s.Length < count)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (ValidateFinite(s.Slice(0, count)) != MathErrorCode.None)
        {
            flags |= MatrixValidationFlags.NonFinite;
            return MathErrorCode.None;
        }

        if (rows != cols)
        {
            return MathErrorCode.None;
        }

        int n = rows;
        double normInf = ComputeInfinityNorm(s, n, n);
        if (normInf == 0.0)
        {
            flags |= MatrixValidationFlags.NotPd | MatrixValidationFlags.IllConditioned;
            if (requirePd)
            {
                flags |= MatrixValidationFlags.InvalidDiag;
            }

            return MathErrorCode.None;
        }

        double scale = normInf;
        double symTol = tol * scale;

        for (int i = 0; i < n; i++)
        {
            double sii = s[i * n + i];
            if (sii < 0.0 || (requirePd && sii <= PivotTolerance * scale))
            {
                flags |= MatrixValidationFlags.InvalidDiag;
            }

            for (int j = i + 1; j < n; j++)
            {
                double sij = s[i * n + j];
                double sji = s[j * n + i];
                if (Math.Abs(sij - sji) > symTol)
                {
                    flags |= MatrixValidationFlags.Asymmetric;
                }

                double sjj = s[j * n + j];
                if (sii >= 0.0 && sjj >= 0.0)
                {
                    double symVal = 0.5 * (sij + sji);
                    double rhs = (1.0 + tol) * sii * sjj;
                    if (symVal * symVal > rhs + (tol * tol * scale * scale * 1e-12))
                    {
                        flags |= MatrixValidationFlags.OutOfRange;
                    }
                }
            }
        }

        Span<double> sym = stackalloc double[count];
        Symmetrize(s, n, sym);
        Span<double> evals = stackalloc double[n];
        Span<double> evecs = stackalloc double[count];
        MathErrorCode eigErr = MatEigenSym(sym, n, evals, evecs, ref budget);
        if (eigErr != MathErrorCode.None)
        {
            return eigErr;
        }

        double maxEval = evals[0];
        double minEval = evals[n - 1];
        if (minEval < -tol * scale)
        {
            flags |= MatrixValidationFlags.NotPsd | MatrixValidationFlags.NotPd;
        }

        if (!CheckCholeskyDiagonal(sym, n, PivotTolerance * Math.Sqrt(normInf)))
        {
            flags |= MatrixValidationFlags.NotPd;
        }

        if (maxEval <= 0.0 || minEval < RCondThreshold * maxEval)
        {
            flags |= MatrixValidationFlags.IllConditioned;
        }

        return MathErrorCode.None;
    }

    /// <summary>
    /// Converts a square covariance matrix (<c>1 &lt;= N &lt;= 16</c>) to a correlation matrix.
    /// Fails with <see cref="MathErrorCode.DivisionByZero"/> if any diagonal entry is <c>&lt;= max(1e-300, 1e-24 * ||S||_inf)</c>.
    /// </summary>
    public static MathErrorCode MatCovToCorr(
        scoped ReadOnlySpan<double> s,
        int n,
        scoped Span<double> rOut)
    {
        int nn = n * n;
        if (!IsValidDim(n) || s.Length < nn || rOut.Length < nn)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(s.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        double normInf = ComputeInfinityNorm(s, n, n);
        if (normInf == 0.0)
        {
            return MathErrorCode.DivisionByZero;
        }

        double symTol = DefaultMatrixTolerance * normInf;
        double diagMin = Math.Max(1e-300, (PivotTolerance * PivotTolerance) * normInf);
        for (int i = 0; i < n; i++)
        {
            if (s[i * n + i] <= diagMin)
            {
                return MathErrorCode.DivisionByZero;
            }

            for (int j = i + 1; j < n; j++)
            {
                if (Math.Abs(s[i * n + j] - s[j * n + i]) > symTol)
                {
                    return MathErrorCode.DomainError;
                }
            }
        }

        Span<double> invStd = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            invStd[i] = 1.0 / Math.Sqrt(s[i * n + i]);
        }

        for (int i = 0; i < n; i++)
        {
            rOut[i * n + i] = 1.0;
            for (int j = i + 1; j < n; j++)
            {
                double symCov = 0.5 * (s[i * n + j] + s[j * n + i]);
                double corr = Math.Clamp(symCov * invStd[i] * invStd[j], -1.0, 1.0);
                if (corr == 0.0)
                {
                    corr = 0.0;
                }

                rOut[i * n + j] = corr;
                rOut[j * n + i] = corr;
            }
        }

        return MathErrorCode.None;
    }

    // ---- Internal Diagnostics Helpers ----

    private static double ComputeOneNorm(ReadOnlySpan<double> matrix, int rows, int cols)
    {
        double maxColSum = 0.0;
        for (int j = 0; j < cols; j++)
        {
            double colSum = 0.0;
            for (int i = 0; i < rows; i++)
            {
                colSum += Math.Abs(matrix[i * cols + j]);
            }

            if (colSum > maxColSum)
            {
                maxColSum = colSum;
            }
        }

        return maxColSum;
    }

    private static void Symmetrize(ReadOnlySpan<double> a, int n, Span<double> sym)
    {
        for (int i = 0; i < n; i++)
        {
            sym[i * n + i] = a[i * n + i];
            for (int j = i + 1; j < n; j++)
            {
                double val = 0.5 * (a[i * n + j] + a[j * n + i]);
                sym[i * n + j] = val;
                sym[j * n + i] = val;
            }
        }
    }

    private static void ReconstructPsd(ReadOnlySpan<double> evecs, ReadOnlySpan<double> evals, int n, Span<double> psd)
    {
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                double sum = 0.0;
                for (int k = 0; k < n; k++)
                {
                    double lam = evals[k] > 0.0 ? evals[k] : 0.0;
                    sum += lam * evecs[i * n + k] * evecs[j * n + k];
                }

                psd[i * n + j] = sum;
                psd[j * n + i] = sum;
            }
        }
    }

    private static void ProjectUnitDiagonalSym(ReadOnlySpan<double> x, int n, Span<double> y)
    {
        for (int i = 0; i < n; i++)
        {
            y[i * n + i] = 1.0;
            for (int j = i + 1; j < n; j++)
            {
                double val = 0.5 * (x[i * n + j] + x[j * n + i]);
                y[i * n + j] = val;
                y[j * n + i] = val;
            }
        }
    }

    private static void NormalizeToCorrelation(ReadOnlySpan<double> psd, int n, Span<double> rOut)
    {
        Span<double> invSqrt = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            double diag = psd[i * n + i];
            invSqrt[i] = diag > 1e-15 ? 1.0 / Math.Sqrt(diag) : 1.0;
        }

        for (int i = 0; i < n; i++)
        {
            rOut[i * n + i] = 1.0;
            for (int j = i + 1; j < n; j++)
            {
                double symVal = 0.5 * (psd[i * n + j] + psd[j * n + i]);
                double corr = Math.Clamp(symVal * invSqrt[i] * invSqrt[j], -1.0, 1.0);
                if (corr == 0.0)
                {
                    corr = 0.0;
                }

                rOut[i * n + j] = corr;
                rOut[j * n + i] = corr;
            }
        }
    }

    private static bool CheckCholeskyDiagonal(ReadOnlySpan<double> sym, int n, double minDiag)
    {
        Span<double> l = stackalloc double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = sym[i * n + j];
                for (int k = 0; k < j; k++)
                {
                    sum -= l[i * n + k] * l[j * n + k];
                }

                if (i == j)
                {
                    if (!double.IsFinite(sum) || sum <= minDiag * minDiag)
                    {
                        return false;
                    }

                    l[i * n + i] = Math.Sqrt(sum);
                }
                else
                {
                    l[i * n + j] = sum / l[j * n + j];
                }
            }
        }

        return true;
    }
}
