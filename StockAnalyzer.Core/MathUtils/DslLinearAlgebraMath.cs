using System;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Stack-allocated (<see cref="MaxMatrixDim"/> &lt;= 16) zero-allocation linear algebra kernels
/// and exact <see cref="decimal"/> 2x2/3x3 matrix projections for StockAnalyzer DSL.
/// Row-major matrix layout (<c>row * cols + col</c>) matching <see cref="SmallMatrixMath"/>.
/// </summary>
public static partial class DslLinearAlgebraMath
{
    /// <summary>Hard upper bound on vector length and matrix row/column count (<c>16 x 16 = 2,048 bytes</c> in <c>double</c>).</summary>
    public const int MaxMatrixDim = 16;

    /// <summary>Threshold below which a vector norm is treated as zero for normalization and plane normals.</summary>
    public const double ZeroNormEpsilon = 1e-12;

    /// <summary>Relative pivot and Cholesky positive-definiteness singularity threshold.</summary>
    public const double PivotTolerance = 1e-12;

    /// <summary>Relative matrix symmetry tolerance used by <see cref="MatChol"/> and <see cref="MatEigenSym"/>.</summary>
    public const double SymmetryTolerance = 1e-10;

    private const decimal DecimalPivotTolerance = 1e-12m;

    // ---- Validation Helpers ----

    internal static MathErrorCode ValidateFinite(ReadOnlySpan<double> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                return MathErrorCode.NonFiniteResult;
            }
        }

        return MathErrorCode.None;
    }

    internal static bool IsValidDim(int dim) => dim >= 1 && dim <= MaxMatrixDim;

    internal static bool IsValidDims(int rows, int cols) => IsValidDim(rows) && IsValidDim(cols);

    internal static double ComputeInfinityNorm(ReadOnlySpan<double> matrix, int rows, int cols)
    {
        double maxRowSum = 0.0;
        for (int i = 0; i < rows; i++)
        {
            double rowSum = 0.0;
            int rowOffset = i * cols;
            for (int j = 0; j < cols; j++)
            {
                rowSum += Math.Abs(matrix[rowOffset + j]);
            }

            if (rowSum > maxRowSum)
            {
                maxRowSum = rowSum;
            }
        }

        return maxRowSum;
    }

    internal static double ComputeFrobeniusNorm(ReadOnlySpan<double> matrix, int count)
    {
        double maxAbs = 0.0;
        for (int i = 0; i < count; i++)
        {
            double a = Math.Abs(matrix[i]);
            if (a > maxAbs)
            {
                maxAbs = a;
            }
        }

        if (maxAbs == 0.0)
        {
            return 0.0;
        }

        double sumSq = 0.0;
        double inv = 1.0 / maxAbs;
        for (int i = 0; i < count; i++)
        {
            double scaled = matrix[i] * inv;
            sumSq += scaled * scaled;
        }

        return maxAbs * Math.Sqrt(sumSq);
    }

    // ---- Vector Operations (dim <= 16) ----

    public static MathResult<double> VecDot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (!IsValidDim(a.Length) || a.Length != b.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        MathErrorCode errA = ValidateFinite(a);
        if (errA != MathErrorCode.None)
        {
            return MathResult<double>.Fail(errA);
        }

        MathErrorCode errB = ValidateFinite(b);
        if (errB != MathErrorCode.None)
        {
            return MathResult<double>.Fail(errB);
        }

        double sum = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return double.IsFinite(sum)
            ? MathResult<double>.Ok(sum == 0.0 ? 0.0 : sum)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<double> VecNorm(ReadOnlySpan<double> v)
    {
        if (!IsValidDim(v.Length))
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        MathErrorCode finiteErr = ValidateFinite(v);
        if (finiteErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(finiteErr);
        }

        double norm = ComputeFrobeniusNorm(v, v.Length);
        return double.IsFinite(norm)
            ? MathResult<double>.Ok(norm)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathErrorCode VecNormalize(ReadOnlySpan<double> v, Span<double> normalized)
    {
        if (!IsValidDim(v.Length) || normalized.Length < v.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathResult<double> normRes = VecNorm(v);
        if (!normRes.IsOk)
        {
            return normRes.Error;
        }

        double norm = normRes.Value;
        if (norm < ZeroNormEpsilon)
        {
            return MathErrorCode.DivisionByZero;
        }

        double inv = 1.0 / norm;
        for (int i = 0; i < v.Length; i++)
        {
            double val = v[i] * inv;
            normalized[i] = val == 0.0 ? 0.0 : val;
        }

        return MathErrorCode.None;
    }

    // ---- General-Length Vector Operations (1 <= n <= DslArrayArena.MaxArrayLength) ----

    /// <summary>
    /// Dot product for DSL 1D arrays of length <c>1..512</c> (<see cref="DslArrayArena.MaxArrayLength"/>).
    /// <c>n == 0</c> -> <see cref="MathErrorCode.DomainError"/>, unequal lengths -> <see cref="MathErrorCode.DimensionMismatch"/>.
    /// Consumes <c>max(1, n / 16)</c> budget units. The dimension-limited <see cref="VecDot"/> (<c>n &lt;= 16</c>) is intentionally left unchanged.
    /// </summary>
    public static MathResult<double> VecDotGeneral(
        scoped ReadOnlySpan<double> a,
        scoped ReadOnlySpan<double> b,
        ref DslExecutionBudget budget)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a.Length != b.Length || a.Length > DslArrayArena.MaxArrayLength)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        MathErrorCode errA = ValidateFinite(a);
        if (errA != MathErrorCode.None)
        {
            return MathResult<double>.Fail(errA);
        }

        MathErrorCode errB = ValidateFinite(b);
        if (errB != MathErrorCode.None)
        {
            return MathResult<double>.Fail(errB);
        }

        if (!budget.TryConsume(Math.Max(1, a.Length / 16)))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        double sum = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return double.IsFinite(sum)
            ? MathResult<double>.Ok(sum == 0.0 ? 0.0 : sum)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    /// <summary>Euclidean norm for DSL 1D arrays of length <c>1..512</c> (scaled to avoid intermediate overflow).</summary>
    public static MathResult<double> VecNormGeneral(scoped ReadOnlySpan<double> v)
    {
        if (v.Length == 0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (v.Length > DslArrayArena.MaxArrayLength)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        MathErrorCode finiteErr = ValidateFinite(v);
        if (finiteErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(finiteErr);
        }

        double norm = ComputeFrobeniusNorm(v, v.Length);
        return double.IsFinite(norm)
            ? MathResult<double>.Ok(norm)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    /// <summary>
    /// Unit-length copy of a DSL 1D array of length <c>1..512</c>.
    /// A norm <c>&lt;= </c><see cref="ZeroNormEpsilon"/> -> <see cref="MathErrorCode.DivisionByZero"/>.
    /// </summary>
    public static MathErrorCode VecNormalizeGeneral(scoped ReadOnlySpan<double> v, scoped Span<double> normalized)
    {
        if (v.Length == 0)
        {
            return MathErrorCode.DomainError;
        }

        if (v.Length > DslArrayArena.MaxArrayLength || normalized.Length < v.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathResult<double> normRes = VecNormGeneral(v);
        if (!normRes.IsOk)
        {
            return normRes.Error;
        }

        double norm = normRes.Value;
        if (norm <= ZeroNormEpsilon)
        {
            return MathErrorCode.DivisionByZero;
        }

        double inv = 1.0 / norm;
        for (int i = 0; i < v.Length; i++)
        {
            double val = v[i] * inv;
            normalized[i] = val == 0.0 ? 0.0 : val;
        }

        return MathErrorCode.None;
    }

    public static MathErrorCode Cross3D(ReadOnlySpan<double> u, ReadOnlySpan<double> v, Span<double> result)
    {
        if (u.Length != 3 || v.Length != 3 || result.Length < 3)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode errU = ValidateFinite(u);
        if (errU != MathErrorCode.None)
        {
            return errU;
        }

        MathErrorCode errV = ValidateFinite(v);
        if (errV != MathErrorCode.None)
        {
            return errV;
        }

        double cx = u[1] * v[2] - u[2] * v[1];
        double cy = u[2] * v[0] - u[0] * v[2];
        double cz = u[0] * v[1] - u[1] * v[0];
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(cz))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        result[0] = cx == 0.0 ? 0.0 : cx;
        result[1] = cy == 0.0 ? 0.0 : cy;
        result[2] = cz == 0.0 ? 0.0 : cz;
        return MathErrorCode.None;
    }

    // ---- Matrix Transforms & Operations ----

    public static MathErrorCode MatTranspose(ReadOnlySpan<double> a, int rows, int cols, Span<double> transposed)
    {
        int count = rows * cols;
        if (!IsValidDims(rows, cols) || a.Length < count || transposed.Length < count)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, count));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        Span<double> work = stackalloc double[count];
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                work[j * rows + i] = a[i * cols + j];
            }
        }

        work.CopyTo(transposed);
        return MathErrorCode.None;
    }

    public static MathErrorCode MatMul(
        scoped ReadOnlySpan<double> a,
        int rowsA,
        int colsA,
        scoped ReadOnlySpan<double> b,
        int rowsB,
        int colsB,
        scoped Span<double> c,
        ref DslExecutionBudget budget)
    {
        if (!IsValidDims(rowsA, colsA) ||
            !IsValidDims(rowsB, colsB) ||
            colsA != rowsB ||
            a.Length < rowsA * colsA ||
            b.Length < rowsB * colsB ||
            c.Length < rowsA * colsB)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int countA = rowsA * colsA;
        int countB = rowsB * colsB;
        MathErrorCode errA = ValidateFinite(a.Slice(0, countA));
        if (errA != MathErrorCode.None)
        {
            return errA;
        }

        MathErrorCode errB = ValidateFinite(b.Slice(0, countB));
        if (errB != MathErrorCode.None)
        {
            return errB;
        }

        int opCost = rowsA * colsA * colsB;
        if (!budget.TryConsume(opCost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        int countC = rowsA * colsB;
        Span<double> work = stackalloc double[countC];
        for (int i = 0; i < rowsA; i++)
        {
            int rowOffsetA = i * colsA;
            int rowOffsetC = i * colsB;
            for (int j = 0; j < colsB; j++)
            {
                double sum = 0.0;
                for (int k = 0; k < colsA; k++)
                {
                    sum += a[rowOffsetA + k] * b[k * colsB + j];
                }

                if (!double.IsFinite(sum))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                work[rowOffsetC + j] = sum == 0.0 ? 0.0 : sum;
            }
        }

        work.CopyTo(c);
        return MathErrorCode.None;
    }

    public static MathErrorCode MatRot2D(double angleRad, Span<double> matrix2x2)
    {
        if (matrix2x2.Length < 4)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(angleRad))
        {
            return MathErrorCode.NonFiniteResult;
        }

        (double s, double c) = Math.SinCos(angleRad);
        matrix2x2[0] = c;
        matrix2x2[1] = -s;
        matrix2x2[2] = s;
        matrix2x2[3] = c;
        return MathErrorCode.None;
    }

    public static MathErrorCode MatRot3D(ReadOnlySpan<double> axis, double angleRad, Span<double> matrix3x3)
    {
        if (axis.Length != 3 || matrix3x3.Length < 9)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(angleRad))
        {
            return MathErrorCode.NonFiniteResult;
        }

        Span<double> k = stackalloc double[3];
        MathErrorCode normErr = VecNormalize(axis, k);
        if (normErr != MathErrorCode.None)
        {
            return normErr;
        }

        (double s, double c) = Math.SinCos(angleRad);
        double omc = 1.0 - c;
        double kx = k[0];
        double ky = k[1];
        double kz = k[2];

        // Rodrigues' rotation formula: R = cos(theta)*I + sin(theta)*[k]_x + (1 - cos(theta))*(k k^T).
        matrix3x3[0] = c + kx * kx * omc;
        matrix3x3[1] = kx * ky * omc - kz * s;
        matrix3x3[2] = kx * kz * omc + ky * s;
        matrix3x3[3] = ky * kx * omc + kz * s;
        matrix3x3[4] = c + ky * ky * omc;
        matrix3x3[5] = ky * kz * omc - kx * s;
        matrix3x3[6] = kz * kx * omc - ky * s;
        matrix3x3[7] = kz * ky * omc + kx * s;
        matrix3x3[8] = c + kz * kz * omc;
        return MathErrorCode.None;
    }

    public static MathErrorCode MatAffine(ReadOnlySpan<double> linear2x2, double tx, double ty, Span<double> matrix3x3)
    {
        if (linear2x2.Length < 4 || matrix3x3.Length < 9)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(linear2x2.Slice(0, 4));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        if (!double.IsFinite(tx) || !double.IsFinite(ty))
        {
            return MathErrorCode.NonFiniteResult;
        }

        matrix3x3[0] = linear2x2[0];
        matrix3x3[1] = linear2x2[1];
        matrix3x3[2] = tx;
        matrix3x3[3] = linear2x2[2];
        matrix3x3[4] = linear2x2[3];
        matrix3x3[5] = ty;
        matrix3x3[6] = 0.0;
        matrix3x3[7] = 0.0;
        matrix3x3[8] = 1.0;
        return MathErrorCode.None;
    }

    // ---- LU, Determinant, Inverse, Linear Solve & Least Squares ----

    public static MathErrorCode MatLu(
        scoped ReadOnlySpan<double> a,
        int n,
        scoped Span<double> l,
        scoped Span<double> u,
        scoped Span<int> perm,
        out int parity,
        ref DslExecutionBudget budget)
    {
        parity = 1;
        int nn = n * n;
        if (!IsValidDim(n) || a.Length < nn || l.Length < nn || u.Length < nn || perm.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        Span<double> lu = stackalloc double[nn];
        MathErrorCode err = FactorLu(a, n, lu, perm, out parity, ref budget);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        l.Slice(0, nn).Clear();
        u.Slice(0, nn).Clear();
        for (int i = 0; i < n; i++)
        {
            l[i * n + i] = 1.0;
            for (int j = 0; j < n; j++)
            {
                double val = lu[i * n + j];
                if (i > j)
                {
                    l[i * n + j] = val;
                }
                else
                {
                    u[i * n + j] = val;
                }
            }
        }

        return MathErrorCode.None;
    }

    public static MathResult<double> MatDet(scoped ReadOnlySpan<double> a, int n, ref DslExecutionBudget budget)
    {
        int nn = n * n;
        if (!IsValidDim(n) || a.Length < nn)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(finiteErr);
        }

        int opCost = Math.Max(1, (n * n * n) / 3);
        if (!budget.TryConsume(opCost))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        double normInf = ComputeInfinityNorm(a, n, n);
        if (normInf == 0.0)
        {
            return MathResult<double>.Ok(0.0);
        }

        Span<double> work = stackalloc double[nn];
        a.Slice(0, nn).CopyTo(work);

        // Preserve initial per-row max-abs scale so small uneliminated diagonal entries
        // (e.g. diag(1, 1e-13)) remain nonzero while rank-deficient eliminated rows still give 0.
        Span<double> rowNorms = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            double rowMax = 0.0;
            int rowBase = i * n;
            for (int j = 0; j < n; j++)
            {
                double abs = Math.Abs(work[rowBase + j]);
                if (abs > rowMax)
                {
                    rowMax = abs;
                }
            }

            rowNorms[i] = rowMax;
        }

        double det = 1.0;
        for (int k = 0; k < n; k++)
        {
            int pivotRow = k;
            double maxAbs = Math.Abs(work[k * n + k]);
            for (int i = k + 1; i < n; i++)
            {
                double cand = Math.Abs(work[i * n + k]);
                if (cand > maxAbs)
                {
                    maxAbs = cand;
                    pivotRow = i;
                }
            }

            if (maxAbs == 0.0 || maxAbs < PivotTolerance * rowNorms[pivotRow])
            {
                return MathResult<double>.Ok(0.0);
            }

            if (pivotRow != k)
            {
                SwapRows(work, n, k, pivotRow);
                (rowNorms[k], rowNorms[pivotRow]) = (rowNorms[pivotRow], rowNorms[k]);
                det = -det;
            }

            double pivot = work[k * n + k];
            det *= pivot;
            if (!double.IsFinite(det))
            {
                return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
            }

            for (int i = k + 1; i < n; i++)
            {
                double factor = work[i * n + k] / pivot;
                for (int j = k + 1; j < n; j++)
                {
                    work[i * n + j] -= factor * work[k * n + j];
                }
            }
        }

        return double.IsFinite(det)
            ? MathResult<double>.Ok(det == 0.0 ? 0.0 : det)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathErrorCode MatSolve(
        scoped ReadOnlySpan<double> a,
        int n,
        scoped ReadOnlySpan<double> b,
        scoped Span<double> x,
        ref DslExecutionBudget budget)
    {
        int nn = n * n;
        if (!IsValidDim(n) || a.Length < nn || b.Length < n || x.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode errA = ValidateFinite(a.Slice(0, nn));
        if (errA != MathErrorCode.None)
        {
            return errA;
        }

        MathErrorCode errB = ValidateFinite(b.Slice(0, n));
        if (errB != MathErrorCode.None)
        {
            return errB;
        }

        Span<double> lu = stackalloc double[nn];
        Span<int> perm = stackalloc int[n];
        MathErrorCode luErr = FactorLu(a, n, lu, perm, out _, ref budget);
        if (luErr != MathErrorCode.None)
        {
            return luErr;
        }

        Span<double> sol = stackalloc double[n];
        MathErrorCode solveErr = SolveLuFactored(lu, n, perm, b, sol);
        if (solveErr != MathErrorCode.None)
        {
            return solveErr;
        }

        sol.CopyTo(x);
        return MathErrorCode.None;
    }

    public static MathErrorCode MatInv(
        scoped ReadOnlySpan<double> a,
        int n,
        scoped Span<double> inverse,
        ref DslExecutionBudget budget)
    {
        int nn = n * n;
        if (!IsValidDim(n) || a.Length < nn || inverse.Length < nn)
        {
            return MathErrorCode.DimensionMismatch;
        }

        MathErrorCode finiteErr = ValidateFinite(a.Slice(0, nn));
        if (finiteErr != MathErrorCode.None)
        {
            return finiteErr;
        }

        Span<double> lu = stackalloc double[nn];
        Span<int> perm = stackalloc int[n];
        MathErrorCode luErr = FactorLu(a, n, lu, perm, out _, ref budget);
        if (luErr != MathErrorCode.None)
        {
            return luErr;
        }

        if (!budget.TryConsume(n * n))
        {
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> workInv = stackalloc double[nn];
        Span<double> rhs = stackalloc double[n];
        Span<double> col = stackalloc double[n];
        for (int j = 0; j < n; j++)
        {
            rhs.Clear();
            rhs[j] = 1.0;
            MathErrorCode solveErr = SolveLuFactored(lu, n, perm, rhs, col);
            if (solveErr != MathErrorCode.None)
            {
                return solveErr;
            }

            for (int i = 0; i < n; i++)
            {
                workInv[i * n + j] = col[i];
            }
        }

        workInv.CopyTo(inverse);
        return MathErrorCode.None;
    }

    /// <summary>
    /// Solves the overdetermined linear least-squares problem <c>min ||A x - b||_2</c> (<c>rows >= cols</c>)
    /// using stack-allocated scratch buffers and delegating directly to <see cref="SmallMatrixMath.TryHouseholderQr"/>.
    /// </summary>
    public static MathErrorCode MatLstSq(
        scoped ReadOnlySpan<double> a,
        int rows,
        int cols,
        scoped ReadOnlySpan<double> b,
        scoped Span<double> x,
        out double residualSumOfSquares,
        ref DslExecutionBudget budget)
    {
        residualSumOfSquares = double.NaN;
        if (!IsValidDims(rows, cols) || rows < cols || a.Length < rows * cols || b.Length < rows || x.Length < cols)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int count = rows * cols;
        MathErrorCode errA = ValidateFinite(a.Slice(0, count));
        if (errA != MathErrorCode.None)
        {
            return errA;
        }

        MathErrorCode errB = ValidateFinite(b.Slice(0, rows));
        if (errB != MathErrorCode.None)
        {
            return errB;
        }

        if (!budget.TryConsume(Math.Max(1, rows * cols * cols)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> qrWork = stackalloc double[count];
        Span<double> rhsWork = stackalloc double[rows];
        a.Slice(0, count).CopyTo(qrWork);
        b.Slice(0, rows).CopyTo(rhsWork);

        if (!SmallMatrixMath.TryHouseholderQr(qrWork, rows, cols, rhsWork, out residualSumOfSquares))
        {
            return MathErrorCode.SingularMatrix;
        }

        Span<double> sol = stackalloc double[cols];
        SmallMatrixMath.SolveUpperTriangular(qrWork, cols, rhsWork, sol);
        for (int j = 0; j < cols; j++)
        {
            if (!double.IsFinite(sol[j]))
            {
                return MathErrorCode.ArithmeticOverflow;
            }
        }

        if (!double.IsFinite(residualSumOfSquares))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        sol.CopyTo(x);
        return MathErrorCode.None;
    }

    // ---- Scalar Decimal Projections for ExpressionCalculator ----

    public static MathResult<decimal> Det2(decimal a11, decimal a12, decimal a21, decimal a22)
    {
        try
        {
            decimal det = checked(a11 * a22 - a12 * a21);
            return MathResult<decimal>.Ok(det == 0m ? 0m : det);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Det3(
        decimal a11, decimal a12, decimal a13,
        decimal a21, decimal a22, decimal a23,
        decimal a31, decimal a32, decimal a33)
    {
        try
        {
            decimal m1 = checked(a11 * checked(a22 * a33 - a23 * a32));
            decimal m2 = checked(a12 * checked(a21 * a33 - a23 * a31));
            decimal m3 = checked(a13 * checked(a21 * a32 - a22 * a31));
            decimal det = checked(m1 - m2 + m3);
            return MathResult<decimal>.Ok(det == 0m ? 0m : det);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    /// <summary>
    /// Solves the 2x2 linear system <c>a11*x + a12*y = b1</c>, <c>a21*x + a22*y = b2</c>
    /// using the canonical Matrix-first parameter order <c>(a11, a12, a21, a22, b1, b2)</c>
    /// consistent with <see cref="MatSolve"/> (<c>A</c> row-major followed by RHS <c>b</c>).
    /// </summary>
    public static MathErrorCode SolveLinearSystem2x2(
        decimal a11,
        decimal a12,
        decimal a21,
        decimal a22,
        decimal b1,
        decimal b2,
        out decimal x,
        out decimal y)
    {
        x = 0m;
        y = 0m;

        try
        {
            decimal maxA = Math.Max(
                Math.Max(Math.Abs(a11), Math.Abs(a12)),
                Math.Max(Math.Abs(a21), Math.Abs(a22)));
            if (maxA == 0m)
            {
                return MathErrorCode.SingularMatrix;
            }

            // Equilibrate both matrix A and RHS b by exact powers of 10 into [1, 10)
            // so decimal fixed scale (<= 10^-28) never loses significant digits on tiny/huge systems.
            int shiftA = ComputePower10Shift(maxA);
            decimal sa11 = ScaleByPow10(a11, shiftA);
            decimal sa12 = ScaleByPow10(a12, shiftA);
            decimal sa21 = ScaleByPow10(a21, shiftA);
            decimal sa22 = ScaleByPow10(a22, shiftA);

            decimal r1Norm = checked(Math.Abs(sa11) + Math.Abs(sa12));
            decimal r2Norm = checked(Math.Abs(sa21) + Math.Abs(sa22));
            decimal normInf = r1Norm >= r2Norm ? r1Norm : r2Norm;
            decimal pivotLimit = DecimalPivotTolerance * normInf;

            decimal u11 = sa11;
            decimal u12 = sa12;
            decimal c21 = sa21;
            decimal c22 = sa22;

            decimal maxB = Math.Max(Math.Abs(b1), Math.Abs(b2));
            int shiftB = maxB == 0m ? 0 : ComputePower10Shift(maxB);
            decimal rhs1 = ScaleByPow10(b1, shiftB);
            decimal rhs2 = ScaleByPow10(b2, shiftB);
            decimal origRhs1 = rhs1;
            decimal origRhs2 = rhs2;

            if (Math.Abs(c21) > Math.Abs(u11))
            {
                (u11, c21) = (c21, u11);
                (u12, c22) = (c22, u12);
                (rhs1, rhs2) = (rhs2, rhs1);
            }

            if (Math.Abs(u11) < pivotLimit || u11 == 0m)
            {
                return MathErrorCode.SingularMatrix;
            }

            decimal factor = checked(c21 / u11);
            decimal u22 = checked(c22 - factor * u12);
            if (Math.Abs(u22) < pivotLimit || u22 == 0m)
            {
                return MathErrorCode.SingularMatrix;
            }

            if (maxB == 0m)
            {
                x = 0m;
                y = 0m;
                return MathErrorCode.None;
            }

            decimal sx;
            decimal sy;
            bool solvedByCramer = false;

            // Try exact single-division Cramer's rule first on well-scaled equilibrated matrices.
            decimal det = checked(sa11 * sa22 - sa12 * sa21);
            if (det != 0m && Math.Abs(det) >= 1e-6m * normInf * normInf)
            {
                decimal numX = checked(origRhs1 * sa22 - sa12 * origRhs2);
                decimal numY = checked(sa11 * origRhs2 - origRhs1 * sa21);
                sx = checked(numX / det);
                sy = checked(numY / det);
                solvedByCramer = true;
            }
            else
            {
                sx = 0m;
                sy = 0m;
            }

            if (!solvedByCramer)
            {
                decimal elimRhs2 = checked(rhs2 - factor * rhs1);
                sy = checked(elimRhs2 / u22);
                sx = checked((rhs1 - u12 * sy) / u11);
            }

            int netShift = shiftA - shiftB;
            decimal solX = ScaleByPow10(sx, netShift);
            decimal solY = ScaleByPow10(sy, netShift);

            x = solX == 0m ? 0m : solX;
            y = solY == 0m ? 0m : solY;
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    private static int ComputePower10Shift(decimal positiveMax)
    {
        int shift = 0;
        decimal scaled = positiveMax;
        while (scaled < 1m && shift < 28)
        {
            scaled *= 10m;
            shift++;
        }

        while (scaled >= 10m && shift > -28)
        {
            scaled /= 10m;
            shift--;
        }

        return shift;
    }

    private static decimal ScaleByPow10(decimal value, int exp)
    {
        if (value == 0m || exp == 0)
        {
            return value;
        }

        decimal result = value;
        while (exp > 0)
        {
            int step = exp > 28 ? 28 : exp;
            result = checked(result * Pow10Decimal(step));
            exp -= step;
        }

        while (exp < 0)
        {
            int step = exp < -28 ? 28 : -exp;
            result /= Pow10Decimal(step);
            exp += step;
        }

        return result;
    }

    private static decimal Pow10Decimal(int exp)
    {
        decimal factor = 1m;
        for (int i = 0; i < exp; i++)
        {
            factor *= 10m;
        }

        return factor;
    }

    // ---- Internal LU Helpers ----

    private static MathErrorCode FactorLu(
        scoped ReadOnlySpan<double> a,
        int n,
        scoped Span<double> lu,
        scoped Span<int> perm,
        out int parity,
        ref DslExecutionBudget budget)
    {
        parity = 1;
        int nn = n * n;
        if (!budget.TryConsume(Math.Max(1, (n * n * n) / 3)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double normInf = ComputeInfinityNorm(a, n, n);
        if (normInf == 0.0)
        {
            return MathErrorCode.SingularMatrix;
        }

        a.Slice(0, nn).CopyTo(lu);
        for (int i = 0; i < n; i++)
        {
            perm[i] = i;
        }

        double pivotLimit = PivotTolerance * normInf;
        for (int k = 0; k < n; k++)
        {
            int pivotRow = k;
            double maxAbs = Math.Abs(lu[k * n + k]);
            for (int i = k + 1; i < n; i++)
            {
                double cand = Math.Abs(lu[i * n + k]);
                if (cand > maxAbs)
                {
                    maxAbs = cand;
                    pivotRow = i;
                }
            }

            if (maxAbs < pivotLimit || maxAbs == 0.0)
            {
                return MathErrorCode.SingularMatrix;
            }

            if (pivotRow != k)
            {
                SwapRows(lu, n, k, pivotRow);
                (perm[k], perm[pivotRow]) = (perm[pivotRow], perm[k]);
                parity = -parity;
            }

            double pivot = lu[k * n + k];
            for (int i = k + 1; i < n; i++)
            {
                double factor = lu[i * n + k] / pivot;
                lu[i * n + k] = factor;
                for (int j = k + 1; j < n; j++)
                {
                    lu[i * n + j] -= factor * lu[k * n + j];
                }
            }
        }

        return MathErrorCode.None;
    }

    private static MathErrorCode SolveLuFactored(
        ReadOnlySpan<double> lu,
        int n,
        ReadOnlySpan<int> perm,
        ReadOnlySpan<double> b,
        Span<double> x)
    {
        Span<double> y = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = b[perm[i]];
            int rowOffset = i * n;
            for (int j = 0; j < i; j++)
            {
                sum -= lu[rowOffset + j] * y[j];
            }

            y[i] = sum;
        }

        for (int i = n - 1; i >= 0; i--)
        {
            double sum = y[i];
            int rowOffset = i * n;
            for (int j = i + 1; j < n; j++)
            {
                sum -= lu[rowOffset + j] * x[j];
            }

            double val = sum / lu[rowOffset + i];
            if (!double.IsFinite(val))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            x[i] = val == 0.0 ? 0.0 : val;
        }

        return MathErrorCode.None;
    }

    private static void SwapRows(Span<double> matrix, int cols, int r1, int r2)
    {
        int off1 = r1 * cols;
        int off2 = r2 * cols;
        for (int j = 0; j < cols; j++)
        {
            (matrix[off1 + j], matrix[off2 + j]) = (matrix[off2 + j], matrix[off1 + j]);
        }
    }
}
