using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// DSL v5 general linear algebra (<c>vec_*</c>, <c>cross3d</c>, <c>mat_transpose</c> .. <c>mat_svd_vt</c>, <c>mat_rot2d</c>).
/// Matrices are row-major 1D arrays with explicit integer dimensions (<c>1..16</c>); vectors are 1D arrays of <c>1..512</c> elements.
/// Every result is computed in <see cref="double"/> by <see cref="DslLinearAlgebraMath"/> and narrowed exactly once to <see cref="decimal"/>.
/// </summary>
internal static partial class CalculatorMatrixFunctions
{
    /// <summary>Entries with a magnitude at or below this value are ignored when picking the sign-defining entry of an eigen/singular vector.</summary>
    private const double SignCanonicalizationThreshold = 1e-12;

    /// <summary>Largest square matrix dimension (<c>n * n &lt;= DslArrayArena.MaxArrayLength</c>) representable in a 1D DSL array (<c>floor(sqrt(512)) = 22</c>).</summary>
    internal const int MaxSquareArrayMatrixDim = 22;

    private static bool TryEvaluateGeneralLinearAlgebra(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "vec_dot":
                outcome = EvaluateVecDot(name, position, args, ref budget, in arena);
                return true;

            case "vec_norm":
            case "vec_normalize":
                outcome = EvaluateVecNorm(name, position, args, ref arena);
                return true;

            case "cross3d":
                outcome = EvaluateCross3D(name, position, args, ref arena);
                return true;

            case "mat_transpose":
                outcome = EvaluateTranspose(name, position, args, ref arena);
                return true;

            case "mat_mul":
                outcome = EvaluateMultiply(name, position, args, ref budget, ref arena);
                return true;

            case "mat_det":
                outcome = EvaluateDeterminant(name, position, args, ref budget, in arena);
                return true;

            case "mat_solve":
            case "mat_inv":
            case "mat_chol":
                outcome = EvaluateSquareSystem(name, position, args, ref budget, ref arena);
                return true;

            case "mat_lstsq":
                outcome = EvaluateLeastSquares(name, position, args, ref budget, ref arena);
                return true;

            case "mat_qr_q":
            case "mat_qr_r":
                outcome = EvaluateQr(name, position, args, ref budget, ref arena);
                return true;

            case "mat_eig_sym_vals":
            case "mat_eig_sym_vecs":
                outcome = EvaluateSymmetricEigen(name, position, args, ref budget, ref arena);
                return true;

            case "mat_svd_s":
            case "mat_svd_u":
            case "mat_svd_vt":
                outcome = EvaluateSvd(name, position, args, ref budget, ref arena);
                return true;

            case "mat_rot2d":
                outcome = EvaluateRotation2D(name, position, args, ref arena);
                return true;

            case "mat_lu_l":
            case "mat_lu_u":
            case "mat_lu_p":
                outcome = EvaluateLuDecomposition(name, position, args, ref budget, ref arena);
                return true;

            case "mat_eig_re":
            case "mat_eig_im":
                outcome = EvaluateGeneralEigenvalues(name, position, args, ref budget, ref arena);
                return true;

            case "mat_pinv":
                outcome = EvaluatePseudoinverse(name, position, args, ref budget, ref arena);
                return true;

            case "mat_rank":
                outcome = EvaluateMatrixRank(name, position, args, ref budget, in arena);
                return true;

            case "mat_trace":
                outcome = EvaluateMatrixTrace(name, position, args, in arena);
                return true;

            case "mat_norm":
                outcome = EvaluateMatrixNorm(name, position, args, ref budget, in arena);
                return true;

            case "mat_expm":
                outcome = EvaluateMatrixExp(name, position, args, ref budget, ref arena);
                return true;

            case "mat_kron":
                outcome = EvaluateKroneckerProduct(name, position, args, ref arena);
                return true;

            case "opt_qp":
                outcome = EvaluateOptQp(name, position, args, ref budget, ref arena);
                return true;

            case "opt_lp":
                outcome = EvaluateOptLp(name, position, args, ref budget, ref arena);
                return true;

            case "opt_qp_eq":
                outcome = EvaluateOptQpEq(name, position, args, ref budget, ref arena);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static DslEvalOutcome FailArguments(int position, string name, string expected)
        => DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects {expected}.", name);

    /// <summary>Reads an integer matrix dimension in <c>[1, 16]</c>; a non-integer or out-of-range value is a domain error of the caller.</summary>
    private static bool TryReadDimension(DslValue arg, out int dimension)
    {
        dimension = 0;
        return !arg.IsArray &&
               DslMathValidation.TryReadInt32InRange(arg.Scalar, 1, DslLinearAlgebraMath.MaxMatrixDim, out dimension);
    }

    // ---- Vectors ----

    private static DslEvalOutcome EvaluateVecDot(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailArguments(position, name, "(a, b) 1D arrays");
        }

        ReadOnlySpan<decimal> a = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> b = arena.GetSlice(args[1].Array);
        if (a.Length == 0 || b.Length == 0)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        if (a.Length != b.Length)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> da = stackalloc double[a.Length];
        Span<double> db = stackalloc double[b.Length];
        CopyToDouble(a, da);
        CopyToDouble(b, db);
        return NarrowScalarResult(DslLinearAlgebraMath.VecDotGeneral(da, db, ref budget), position, name);
    }

    private static DslEvalOutcome EvaluateVecNorm(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailArguments(position, name, "(v) a 1D array");
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length == 0)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> v = stackalloc double[source.Length];
        CopyToDouble(source, v);
        if (name == "vec_norm")
        {
            return NarrowScalarResult(DslLinearAlgebraMath.VecNormGeneral(v), position, name);
        }

        Span<double> unit = stackalloc double[source.Length];
        MathErrorCode err = DslLinearAlgebraMath.VecNormalizeGeneral(v, unit);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(unit, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateCross3D(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailArguments(position, name, "(u, v) 1D arrays of length 3");
        }

        ReadOnlySpan<decimal> u = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> v = arena.GetSlice(args[1].Array);
        if (u.Length != 3 || v.Length != 3)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> du = stackalloc double[3];
        Span<double> dv = stackalloc double[3];
        Span<double> cross = stackalloc double[3];
        CopyToDouble(u, du);
        CopyToDouble(v, dv);
        MathErrorCode err = DslLinearAlgebraMath.Cross3D(du, dv, cross);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(cross, position, name, ref arena);
    }

    // ---- Matrix Products & Transforms ----

    private static DslEvalOutcome EvaluateTranspose(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int rows) || !TryReadDimension(args[2], out int cols))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != rows * cols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        Span<double> transposed = stackalloc double[source.Length];
        CopyToDouble(source, a);
        MathErrorCode err = DslLinearAlgebraMath.MatTranspose(a, rows, cols, transposed);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(transposed, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateMultiply(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[3].IsArray ||
            !TryReadDimension(args[1], out int rowsA) || !TryReadDimension(args[2], out int colsA) ||
            !TryReadDimension(args[4], out int rowsB) || !TryReadDimension(args[5], out int colsB))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> sourceA = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> sourceB = arena.GetSlice(args[3].Array);
        if (colsA != rowsB || sourceA.Length != rowsA * colsA || sourceB.Length != rowsB * colsB)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[sourceA.Length];
        Span<double> b = stackalloc double[sourceB.Length];
        Span<double> product = stackalloc double[rowsA * colsB];
        CopyToDouble(sourceA, a);
        CopyToDouble(sourceB, b);
        MathErrorCode err = DslLinearAlgebraMath.MatMul(a, rowsA, colsA, b, rowsB, colsB, product, ref budget);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(product, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateRotation2D(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (args[0].IsArray)
        {
            return FailArguments(position, name, "(angleRad) a scalar");
        }

        if (Math.Abs(args[0].Scalar) > DslElementaryMath.RadianTrigRangeLimit)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> rotation = stackalloc double[4];
        MathErrorCode err = DslLinearAlgebraMath.MatRot2D((double)args[0].Scalar, rotation);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(rotation, position, name, ref arena);
    }

    // ---- Square Systems ----

    private static DslEvalOutcome EvaluateDeterminant(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int n))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != n * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        CopyToDouble(source, a);
        return NarrowScalarResult(DslLinearAlgebraMath.MatDet(a, n, ref budget), position, name);
    }

    /// <summary><c>mat_solve(A, n, b)</c>, <c>mat_inv(A, n)</c> and <c>mat_chol(A, n)</c>. <c>mat_chol</c> reports a non-positive-definite matrix as a domain error.</summary>
    private static DslEvalOutcome EvaluateSquareSystem(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool isSolve = name == "mat_solve";
        if (!args[0].IsArray || !TryReadDimension(args[1], out int n) || (isSolve && !args[2].IsArray))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != n * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        CopyToDouble(source, a);

        if (isSolve)
        {
            ReadOnlySpan<decimal> rhs = arena.GetSlice(args[2].Array);
            if (rhs.Length != n)
            {
                return FailMath(MathErrorCode.DimensionMismatch, position, name);
            }

            Span<double> b = stackalloc double[n];
            Span<double> x = stackalloc double[n];
            CopyToDouble(rhs, b);
            MathErrorCode solveErr = DslLinearAlgebraMath.MatSolve(a, n, b, x, ref budget);
            return solveErr != MathErrorCode.None
                ? FailMath(solveErr, position, name)
                : NarrowArrayResult(x, position, name, ref arena);
        }

        Span<double> result = stackalloc double[source.Length];
        MathErrorCode err = name == "mat_inv"
            ? DslLinearAlgebraMath.MatInv(a, n, result, ref budget)
            : DslLinearAlgebraMath.MatChol(a, n, result, ref budget);
        if (name == "mat_chol" && err == MathErrorCode.SingularMatrix)
        {
            err = MathErrorCode.DomainError;
        }

        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(result, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateLeastSquares(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[3].IsArray ||
            !TryReadDimension(args[1], out int rows) || !TryReadDimension(args[2], out int cols) ||
            rows < cols)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> rhs = arena.GetSlice(args[3].Array);
        if (source.Length != rows * cols || rhs.Length != rows)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        Span<double> b = stackalloc double[rows];
        Span<double> x = stackalloc double[cols];
        CopyToDouble(source, a);
        CopyToDouble(rhs, b);
        MathErrorCode err = DslLinearAlgebraMath.MatLstSq(a, rows, cols, b, x, out _, ref budget);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(x, position, name, ref arena);
    }

    // ---- Decompositions ----

    private static DslEvalOutcome EvaluateQr(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int rows) || !TryReadDimension(args[2], out int cols) || rows < cols)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != rows * cols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        Span<double> q = stackalloc double[rows * cols];
        Span<double> r = stackalloc double[cols * cols];
        CopyToDouble(source, a);
        MathErrorCode err = DslLinearAlgebraMath.MatQr(a, rows, cols, q, r, ref budget, fullQ: false);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        return NarrowArrayResult(name == "mat_qr_q" ? q : r, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateSymmetricEigen(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int n))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != n * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        Span<double> values = stackalloc double[n];
        Span<double> vectors = stackalloc double[source.Length];
        CopyToDouble(source, a);
        MathErrorCode err = DslLinearAlgebraMath.MatEigenSym(a, n, values, vectors, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        if (name == "mat_eig_sym_vals")
        {
            return NarrowArrayResult(values, position, name, ref arena);
        }

        CanonicalizeColumnSigns(vectors, n, n, default, 0);
        return NarrowArrayResult(vectors, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateSvd(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int rows) || !TryReadDimension(args[2], out int cols))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != rows * cols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int k = Math.Min(rows, cols);
        Span<double> a = stackalloc double[source.Length];
        Span<double> u = stackalloc double[rows * k];
        Span<double> s = stackalloc double[k];
        Span<double> vt = stackalloc double[k * cols];
        CopyToDouble(source, a);
        MathErrorCode err = DslLinearAlgebraMath.MatSvd(a, rows, cols, u, s, vt, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        if (name == "mat_svd_s")
        {
            return NarrowArrayResult(s, position, name, ref arena);
        }

        // Column j of U and row j of V^T flip together, so A = U diag(S) V^T keeps holding exactly.
        CanonicalizeColumnSigns(u, rows, k, vt, cols);
        return NarrowArrayResult(name == "mat_svd_u" ? u : vt, position, name, ref arena);
    }

    /// <summary>
    /// Makes the column signs of a row-major <paramref name="rows"/> x <paramref name="columns"/> matrix deterministic: the first entry of every column
    /// (top to bottom) whose magnitude exceeds <see cref="SignCanonicalizationThreshold"/> becomes positive. When <paramref name="pairedRows"/> is given
    /// (row-major, <paramref name="pairedWidth"/> entries per row), row <c>j</c> is negated together with column <c>j</c>.
    /// </summary>
    private static void CanonicalizeColumnSigns(
        scoped Span<double> matrix,
        int rows,
        int columns,
        scoped Span<double> pairedRows,
        int pairedWidth)
    {
        for (int j = 0; j < columns; j++)
        {
            for (int r = 0; r < rows; r++)
            {
                double entry = matrix[r * columns + j];
                if (Math.Abs(entry) <= SignCanonicalizationThreshold)
                {
                    continue;
                }

                if (entry < 0.0)
                {
                    for (int i = 0; i < rows; i++)
                    {
                        matrix[i * columns + j] = -matrix[i * columns + j];
                    }

                    for (int c = 0; c < pairedWidth; c++)
                    {
                        pairedRows[j * pairedWidth + c] = -pairedRows[j * pairedWidth + c];
                    }
                }

                break;
            }
        }
    }

    // ---- DSL v6 Domain 4: Extended Linear Algebra & Matrix Functions ----

    private static DslEvalOutcome EvaluateLuDecomposition(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int n))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != n * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        Span<double> l = stackalloc double[source.Length];
        Span<double> u = stackalloc double[source.Length];
        Span<int> perm = stackalloc int[n];
        CopyToDouble(source, a);

        MathErrorCode err = DslLinearAlgebraMath.MatLu(a, n, l, u, perm, out _, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        if (name == "mat_lu_p")
        {
            if (!arena.TryAllocate(n, out DslArrayHandle pHandle, out Span<decimal> pOut))
            {
                return FailMath(MathErrorCode.BudgetExceeded, position, name);
            }

            for (int i = 0; i < n; i++)
            {
                pOut[i] = perm[i];
            }

            return DslEvalOutcome.OkArray(pHandle);
        }

        return NarrowArrayResult(name == "mat_lu_l" ? l : u, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateGeneralEigenvalues(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int n))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != n * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        Span<double> re = stackalloc double[n];
        Span<double> im = stackalloc double[n];
        CopyToDouble(source, a);

        MathErrorCode err = DslLinearAlgebraMath.MatEigenGeneral(a, n, re, im, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        DslLinearAlgebraMath.SortComplexPairsCanonical(re, im, n);
        return NarrowArrayResult(name == "mat_eig_re" ? re : im, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluatePseudoinverse(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int rows) || !TryReadDimension(args[2], out int cols))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        if (args.Length >= 4 && (args[3].IsArray || args[3].Scalar < 0m))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != rows * cols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int k = Math.Min(rows, cols);
        Span<double> a = stackalloc double[source.Length];
        Span<double> u = stackalloc double[rows * k];
        Span<double> s = stackalloc double[k];
        Span<double> vt = stackalloc double[k * cols];
        CopyToDouble(source, a);

        MathErrorCode err = DslLinearAlgebraMath.MatSvd(a, rows, cols, u, s, vt, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        double rcond = args.Length >= 4
            ? (double)args[3].Scalar
            : Math.Max(rows, cols) * MathConstants.MachineEpsilon;
        double cutoff = rcond * s[0];

        Span<double> pinv = stackalloc double[cols * rows];
        pinv.Clear();
        for (int j = 0; j < cols; j++)
        {
            for (int i = 0; i < rows; i++)
            {
                double sum = 0.0;
                for (int r = 0; r < k; r++)
                {
                    if (s[r] > cutoff)
                    {
                        sum += vt[r * cols + j] * (1.0 / s[r]) * u[i * k + r];
                    }
                }

                pinv[j * rows + i] = sum;
            }
        }

        return NarrowArrayResult(pinv, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateMatrixRank(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int rows) || !TryReadDimension(args[2], out int cols))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        if (args.Length >= 4 && (args[3].IsArray || args[3].Scalar < 0m))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != rows * cols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int k = Math.Min(rows, cols);
        Span<double> a = stackalloc double[source.Length];
        Span<double> u = stackalloc double[rows * k];
        Span<double> s = stackalloc double[k];
        Span<double> vt = stackalloc double[k * cols];
        CopyToDouble(source, a);

        MathErrorCode err = DslLinearAlgebraMath.MatSvd(a, rows, cols, u, s, vt, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        double cutoff = args.Length >= 4
            ? (double)args[3].Scalar
            : Math.Max(rows, cols) * MathConstants.MachineEpsilon * s[0];
        int rank = 0;
        for (int r = 0; r < k; r++)
        {
            if (s[r] > cutoff)
            {
                rank++;
            }
        }

        return DslEvalOutcome.Ok(rank);
    }

    private static DslEvalOutcome EvaluateMatrixTrace(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, MaxSquareArrayMatrixDim, out int n))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != n * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        try
        {
            decimal sum = 0m;
            for (int i = 0; i < n; i++)
            {
                sum = checked(sum + source[i * n + i]);
            }

            return DslEvalOutcome.Ok(sum);
        }
        catch (OverflowException)
        {
            return FailMath(MathErrorCode.ArithmeticOverflow, position, name);
        }
    }

    private static DslEvalOutcome EvaluateMatrixNorm(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int rows) || !TryReadDimension(args[2], out int cols))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        int kind = 0;
        if (args.Length >= 4 && (args[3].IsArray || !DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, 3, out kind)))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != rows * cols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        CopyToDouble(source, a);
        MathErrorCode finiteErr = DslLinearAlgebraMath.ValidateFinite(a);
        if (finiteErr != MathErrorCode.None)
        {
            return FailMath(finiteErr, position, name);
        }

        double norm;
        switch (kind)
        {
            case 0:
                norm = DslLinearAlgebraMath.ComputeFrobeniusNorm(a, source.Length);
                break;

            case 1:
                norm = DslLinearAlgebraMath.ComputeInfinityNorm(a, rows, cols);
                break;

            case 2:
                norm = 0.0;
                for (int j = 0; j < cols; j++)
                {
                    double colSum = 0.0;
                    for (int i = 0; i < rows; i++)
                    {
                        colSum += Math.Abs(a[i * cols + j]);
                    }

                    if (colSum > norm)
                    {
                        norm = colSum;
                    }
                }

                break;

            default:
            {
                int k = Math.Min(rows, cols);
                Span<double> u = stackalloc double[rows * k];
                Span<double> s = stackalloc double[k];
                Span<double> vt = stackalloc double[k * cols];
                MathErrorCode svdErr = DslLinearAlgebraMath.MatSvd(a, rows, cols, u, s, vt, ref budget);
                if (svdErr != MathErrorCode.None)
                {
                    return FailMath(svdErr, position, name);
                }

                norm = s[0];
                break;
            }
        }

        return NarrowScalarResult(
            double.IsFinite(norm) ? MathResult<double>.Ok(norm) : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow),
            position,
            name);
    }

    private static DslEvalOutcome EvaluateMatrixExp(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !TryReadDimension(args[1], out int n))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        if (source.Length != n * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> a = stackalloc double[source.Length];
        Span<double> res = stackalloc double[source.Length];
        CopyToDouble(source, a);

        MathErrorCode err = DslLinearAlgebraMath.MatExpm(a, n, res, ref budget);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(res, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateKroneckerProduct(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[3].IsArray ||
            args[1].IsArray || args[2].IsArray || args[4].IsArray || args[5].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslArrayArena.MaxArrayLength, out int rA) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 1, DslArrayArena.MaxArrayLength, out int cA) ||
            !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 1, DslArrayArena.MaxArrayLength, out int rB) ||
            !DslMathValidation.TryReadInt32InRange(args[5].Scalar, 1, DslArrayArena.MaxArrayLength, out int cB))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> a = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> b = arena.GetSlice(args[3].Array);
        if (a.Length != rA * cA || b.Length != rB * cB)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        long outRows = (long)rA * rB;
        long outCols = (long)cA * cB;
        if (outRows * outCols > DslArrayArena.MaxArrayLength)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int total = (int)(outRows * outCols);
        if (!arena.TryAllocate(total, out DslArrayHandle handle, out Span<decimal> result))
        {
            return FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        a = arena.GetSlice(args[0].Array);
        b = arena.GetSlice(args[3].Array);
        int outWidth = (int)outCols;
        try
        {
            for (int i = 0; i < rA; i++)
            {
                for (int j = 0; j < cA; j++)
                {
                    decimal aij = a[i * cA + j];
                    for (int p = 0; p < rB; p++)
                    {
                        int outRowOffset = (i * rB + p) * outWidth + (j * cB);
                        for (int q = 0; q < cB; q++)
                        {
                            decimal prod = checked(aij * b[p * cB + q]);
                            result[outRowOffset + q] = prod == 0m ? 0m : prod;
                        }
                    }
                }
            }
        }
        catch (OverflowException)
        {
            return FailMath(MathErrorCode.ArithmeticOverflow, position, name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateOptQp(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !TryReadDimension(args[2], out int n) ||
            !args[3].IsArray || !args[4].IsArray || (args.Length >= 6 && args[5].IsArray))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> hDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> cDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> lbDec = arena.GetSlice(args[3].Array);
        ReadOnlySpan<decimal> ubDec = arena.GetSlice(args[4].Array);
        if (hDec.Length != n * n || cDec.Length != n || lbDec.Length != n || ubDec.Length != n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        double tol = args.Length >= 6 ? (double)args[5].Scalar : DslTolerance.DefaultTol;
        Span<double> h = stackalloc double[n * n];
        Span<double> c = stackalloc double[n];
        Span<double> lb = stackalloc double[n];
        Span<double> ub = stackalloc double[n];
        Span<double> xOut = stackalloc double[n];
        CopyToDouble(hDec, h);
        CopyToDouble(cDec, c);
        CopyToDouble(lbDec, lb);
        CopyToDouble(ubDec, ub);

        MathErrorCode err = DslOptimizationMath.OptQp(h, c, n, lb, ub, tol, xOut, ref budget);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(xOut, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateOptLp(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray ||
            !args[3].IsArray || !args[4].IsArray || (args.Length >= 6 && args[5].IsArray))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> cDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> aDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> bDec = arena.GetSlice(args[2].Array);
        ReadOnlySpan<decimal> lbDec = arena.GetSlice(args[3].Array);
        ReadOnlySpan<decimal> ubDec = arena.GetSlice(args[4].Array);

        int n = cDec.Length;
        int m = bDec.Length;
        if (n == 0)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        if (n > DslLinearAlgebraMath.MaxMatrixDim ||
            m > DslLinearAlgebraMath.MaxMatrixDim ||
            lbDec.Length != n ||
            ubDec.Length != n ||
            aDec.Length != m * n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        double tol = args.Length >= 6 ? (double)args[5].Scalar : DslTolerance.DefaultTol;
        Span<double> c = stackalloc double[n];
        Span<double> a = m > 0 ? stackalloc double[m * n] : Span<double>.Empty;
        Span<double> b = m > 0 ? stackalloc double[m] : Span<double>.Empty;
        Span<double> lb = stackalloc double[n];
        Span<double> ub = stackalloc double[n];
        Span<double> xOut = stackalloc double[n];
        CopyToDouble(cDec, c);
        if (m > 0)
        {
            CopyToDouble(aDec, a);
            CopyToDouble(bDec, b);
        }
        CopyToDouble(lbDec, lb);
        CopyToDouble(ubDec, ub);

        MathErrorCode err = DslOptimizationMath.OptLp(c, a, b, lb, ub, n, m, tol, xOut, ref budget);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(xOut, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateOptQpEq(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray || !args[3].IsArray ||
            !args[4].IsArray || !args[5].IsArray || (args.Length >= 7 && args[6].IsArray))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> hDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> cDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> aEqDec = arena.GetSlice(args[2].Array);
        ReadOnlySpan<decimal> bEqDec = arena.GetSlice(args[3].Array);
        ReadOnlySpan<decimal> lbDec = arena.GetSlice(args[4].Array);
        ReadOnlySpan<decimal> ubDec = arena.GetSlice(args[5].Array);

        int n = cDec.Length;
        int mEq = bEqDec.Length;
        if (n == 0 || mEq == 0)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        if (n > DslLinearAlgebraMath.MaxMatrixDim ||
            mEq > n ||
            hDec.Length != n * n ||
            aEqDec.Length != mEq * n ||
            lbDec.Length != n ||
            ubDec.Length != n)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        double tol = args.Length >= 7 ? (double)args[6].Scalar : DslTolerance.DefaultTol;
        Span<double> h = stackalloc double[n * n];
        Span<double> c = stackalloc double[n];
        Span<double> aEq = stackalloc double[mEq * n];
        Span<double> bEq = stackalloc double[mEq];
        Span<double> lb = stackalloc double[n];
        Span<double> ub = stackalloc double[n];
        Span<double> xOut = stackalloc double[n];
        CopyToDouble(hDec, h);
        CopyToDouble(cDec, c);
        CopyToDouble(aEqDec, aEq);
        CopyToDouble(bEqDec, bEq);
        CopyToDouble(lbDec, lb);
        CopyToDouble(ubDec, ub);

        MathErrorCode err = DslOptimizationMath.OptQpEq(h, c, aEq, bEq, lb, ub, n, mEq, tol, xOut, ref budget);
        return err != MathErrorCode.None
            ? FailMath(err, position, name)
            : NarrowArrayResult(xOut, position, name, ref arena);
    }
}
