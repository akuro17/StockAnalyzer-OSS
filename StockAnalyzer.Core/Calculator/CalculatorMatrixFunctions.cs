using System;
using System.Buffers;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for Phase 3 scalar matrix projections (<c>det2</c>, <c>det3</c>, <c>solve2_x</c>, <c>solve2_y</c>)
/// and v4 matrix diagnostics, correlation/covariance validation, weighted statistics, and regression/PCA projections.
/// </summary>
internal static partial class CalculatorMatrixFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            case "det2":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslLinearAlgebraMath.Det2(args[0], args[1], args[2], args[3]),
                    position,
                    name);
                return true;

            case "det3":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslLinearAlgebraMath.Det3(
                        args[0], args[1], args[2],
                        args[3], args[4], args[5],
                        args[6], args[7], args[8]),
                    position,
                    name);
                return true;

            case "solve2_x":
            case "solve2_y":
                MathErrorCode err = DslLinearAlgebraMath.SolveLinearSystem2x2(
                    args[0], args[1], args[2], args[3], args[4], args[5],
                    out decimal x,
                    out decimal y);
                outcome = err == MathErrorCode.None
                    ? CalculatorEvaluator.Ok(name == "solve2_x" ? x : y)
                    : CalculatorElementaryFunctions.FromMathError(err, position, name);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    public static bool TryEvaluateValue(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "mat_cond":
            case "mat_rcond":
                outcome = EvaluateConditionNumber(name, position, args, ref budget, in arena, reciprocal: name == "mat_rcond");
                return true;

            case "mat_validate_corr":
            case "mat_is_corr":
                outcome = EvaluateValidateCorr(name, position, args, ref budget, in arena, asBool: name == "mat_is_corr");
                return true;

            case "mat_nearest_corr":
                outcome = EvaluateNearestCorr(name, position, args, ref budget, ref arena);
                return true;

            case "mat_validate_cov":
                outcome = EvaluateValidateCov(name, position, args, ref budget, in arena);
                return true;

            case "mat_cov_to_corr":
                outcome = EvaluateCovToCorr(name, position, args, ref arena);
                return true;

            case "weighted_mean":
            case "weighted_var":
                outcome = EvaluateWeightedStat(name, position, args, in arena, isVariance: name == "weighted_var");
                return true;

            case "mahalanobis":
                outcome = EvaluateMahalanobis(name, position, args, ref budget, in arena);
                return true;

            case "ols_fit":
                outcome = EvaluateLinearFit(name, position, args, isRidge: false, ref budget, ref arena);
                return true;

            case "ridge_fit":
                outcome = EvaluateLinearFit(name, position, args, isRidge: true, ref budget, ref arena);
                return true;

            case "ols_predict":
                outcome = EvaluateOlsPredict(name, position, args, in arena);
                return true;

            case "poly_project":
                outcome = EvaluatePolyProject(name, position, args, ref budget, in arena);
                return true;

            case "pca_project":
                outcome = EvaluatePcaProject(name, position, args, ref budget, ref arena);
                return true;

            default:
                return TryEvaluateGeneralLinearAlgebra(name, position, args, ref budget, ref arena, out outcome);
        }
    }

    private static DslEvalOutcome EvaluateConditionNumber(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena,
        bool reciprocal)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || (args.Length == 4 && args[3].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (matrixArray, rows, cols[, normKind]).", name);
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslLinearAlgebraMath.MaxMatrixDim, out int rows) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 1, DslLinearAlgebraMath.MaxMatrixDim, out int cols))
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int normKind = 2;
        if (args.Length == 4)
        {
            if (!DslMathValidation.TryReadInt32InRange(args[3].Scalar, 1, 3, out normKind))
            {
                return FailMath(MathErrorCode.DomainError, position, name);
            }
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length != rows * cols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CopyToDouble(src, buf);

        MathResult<double> res = reciprocal
            ? DslLinearAlgebraMath.MatRCond(buf, rows, cols, ref budget, normKind)
            : DslLinearAlgebraMath.MatCond(buf, rows, cols, ref budget, normKind);
        return NarrowScalarResult(res, position, name);
    }

    private static DslEvalOutcome EvaluateValidateCorr(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena,
        bool asBool)
    {
        if (!args[0].IsArray ||
            (args.Length >= 2 && args[1].IsArray) ||
            (args.Length >= 3 && args[2].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (corrArray[, tol[, requirePd]]).", name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (!TryInferSquareDim(src.Length, 2, DslLinearAlgebraMath.MaxMatrixDim, out int n))
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        double tol = args.Length >= 2 ? (double)args[1].Scalar : DslLinearAlgebraMath.DefaultMatrixTolerance;
        bool requirePd = false;
        if (asBool && args.Length >= 3 && !TryReadBoolBit(args[2].Scalar, out requirePd))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CopyToDouble(src, buf);

        if (asBool)
        {
            MathResult<bool> boolRes = DslLinearAlgebraMath.MatIsCorr(buf, n, n, tol, ref budget, requirePd);
            return boolRes.IsOk
                ? DslEvalOutcome.Ok(boolRes.Value ? 1m : 0m)
                : FailMath(boolRes.Error, position, name);
        }

        MathErrorCode err = DslLinearAlgebraMath.MatValidateCorr(buf, n, n, tol, out MatrixValidationFlags flags, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        return DslEvalOutcome.Ok((ushort)flags);
    }

    private static DslEvalOutcome EvaluateNearestCorr(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length == 2 && args[1].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (corrArray[, tol]).", name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (!TryInferSquareDim(src.Length, 2, DslLinearAlgebraMath.MaxMatrixDim, out int n))
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        double tol = args.Length == 2 ? (double)args[1].Scalar : DslLinearAlgebraMath.DefaultMatrixTolerance;
        Span<double> buf = stackalloc double[src.Length];
        Span<double> outBuf = stackalloc double[src.Length];
        CopyToDouble(src, buf);

        MathErrorCode err = DslLinearAlgebraMath.MatNearestCorr(buf, n, outBuf, ref budget, tol);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        return NarrowArrayResult(outBuf, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateValidateCov(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length >= 2 && args[1].IsArray) || (args.Length == 3 && args[2].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (covArray[, requirePd, tol]).", name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (!TryInferSquareDim(src.Length, 1, DslLinearAlgebraMath.MaxMatrixDim, out int n))
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        bool requirePd = false;
        if (args.Length >= 2)
        {
            if (!TryReadBoolBit(args[1].Scalar, out requirePd))
            {
                return FailMath(MathErrorCode.DomainError, position, name);
            }
        }

        double tol = args.Length == 3 ? (double)args[2].Scalar : DslLinearAlgebraMath.DefaultMatrixTolerance;
        Span<double> buf = stackalloc double[src.Length];
        CopyToDouble(src, buf);

        MathErrorCode err = DslLinearAlgebraMath.MatValidateCov(buf, n, n, requirePd, tol, out MatrixValidationFlags flags, ref budget);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        return DslEvalOutcome.Ok((ushort)flags);
    }

    private static DslEvalOutcome EvaluateCovToCorr(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects a square covariance array.", name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (!TryInferSquareDim(src.Length, 1, DslLinearAlgebraMath.MaxMatrixDim, out int n))
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        Span<double> outBuf = stackalloc double[src.Length];
        CopyToDouble(src, buf);

        MathErrorCode err = DslLinearAlgebraMath.MatCovToCorr(buf, n, outBuf);
        if (err != MathErrorCode.None)
        {
            return FailMath(err, position, name);
        }

        return NarrowArrayResult(outBuf, position, name, ref arena);
    }

    private static DslEvalOutcome EvaluateWeightedStat(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena,
        bool isVariance)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (weightsArray, valuesArray).", name);
        }

        ReadOnlySpan<decimal> w = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> x = arena.GetSlice(args[1].Array);
        MathResult<decimal> res = isVariance
            ? DslRegressionMath.WeightedVariance(w, x)
            : DslRegressionMath.WeightedMean(w, x);
        return CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(res, position, name));
    }

    private static DslEvalOutcome EvaluateMahalanobis(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (xArray, muArray, covInvArray).", name);
        }

        ReadOnlySpan<decimal> xDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> muDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> covInvDec = arena.GetSlice(args[2].Array);
        int p = xDec.Length;
        if (!DslLinearAlgebraMath.IsValidDim(p) || muDec.Length != p || covInvDec.Length != p * p)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> x = stackalloc double[p];
        Span<double> mu = stackalloc double[p];
        Span<double> covInv = stackalloc double[p * p];
        CopyToDouble(xDec, x);
        CopyToDouble(muDec, mu);
        CopyToDouble(covInvDec, covInv);

        MathResult<double> res = DslRegressionMath.Mahalanobis(x, mu, covInv, p, ref budget);
        return NarrowScalarResult(res, position, name);
    }

    private static DslEvalOutcome EvaluateLinearFit(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        bool isRidge,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || (isRidge && args[2].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects array design matrix X and target vector y.", name);
        }

        ReadOnlySpan<decimal> xDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> yDec = arena.GetSlice(args[1].Array);
        int n = yDec.Length;
        if (n < 1 || xDec.Length == 0 || xDec.Length % n != 0)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int p = xDec.Length / n;
        if (p < 1 || p > DslRegressionMath.MaxFeatures)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        double[]? rentedX = null;
        double[]? rentedY = null;
        Span<double> xBuf = xDec.Length <= MathBufferLimits.StackAllocThreshold ? stackalloc double[xDec.Length] : default;
        Span<double> yBuf = n <= MathBufferLimits.StackAllocThreshold ? stackalloc double[n] : default;

        try
        {
            if (xDec.Length > MathBufferLimits.StackAllocThreshold)
            {
                rentedX = ArrayPool<double>.Shared.Rent(xDec.Length);
                xBuf = rentedX.AsSpan(0, xDec.Length);
            }

            if (n > MathBufferLimits.StackAllocThreshold)
            {
                rentedY = ArrayPool<double>.Shared.Rent(n);
                yBuf = rentedY.AsSpan(0, n);
            }

            CopyToDouble(xDec, xBuf);
            CopyToDouble(yDec, yBuf);
            Span<double> beta = stackalloc double[p + 1];

            MathErrorCode err = isRidge
                ? DslRegressionMath.RidgeFit(xBuf, n, p, yBuf, (double)args[2].Scalar, beta, ref budget)
                : DslRegressionMath.OlsFit(xBuf, n, p, yBuf, beta, ref budget);
            if (err != MathErrorCode.None)
            {
                return FailMath(err, position, name);
            }

            return NarrowArrayResult(beta, position, name, ref arena);
        }
        finally
        {
            if (rentedX is not null) ArrayPool<double>.Shared.Return(rentedX);
            if (rentedY is not null) ArrayPool<double>.Shared.Return(rentedY);
        }
    }

    private static DslEvalOutcome EvaluateOlsPredict(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (xNewArray, betaArray).", name);
        }

        ReadOnlySpan<decimal> xNew = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> beta = arena.GetSlice(args[1].Array);
        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(DslRegressionMath.OlsPredict(xNew, beta), position, name));
    }

    private static DslEvalOutcome EvaluatePolyProject(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (seriesArray, degree, horizon).", name);
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslRegressionMath.MaxPolyDegree, out int deg) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, DslRegressionMath.MaxPolyHorizon, out int horizon))
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < deg + 2)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CopyToDouble(src, buf);
        MathResult<double> res = DslRegressionMath.PolyProject(buf, deg, horizon, ref budget);
        return NarrowScalarResult(res, position, name);
    }

    private static DslEvalOutcome EvaluatePcaProject(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (matrixArray, nRows, nCols, k).", name);
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslRegressionMath.MaxObservations, out int nRows) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 1, DslRegressionMath.MaxFeatures, out int nCols) ||
            !DslMathValidation.TryReadInt32InRange(args[3].Scalar, 1, nCols, out int k))
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        if (nRows < 2)
        {
            return FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length != nRows * nCols)
        {
            return FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int outLen = nRows * k;
        if (outLen > DslArrayArena.MaxArrayLength)
        {
            return FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        double[]? rentedX = null;
        double[]? rentedZ = null;
        Span<double> xBuf = src.Length <= MathBufferLimits.StackAllocThreshold ? stackalloc double[src.Length] : default;
        Span<double> zBuf = outLen <= MathBufferLimits.StackAllocThreshold ? stackalloc double[outLen] : default;

        try
        {
            if (src.Length > MathBufferLimits.StackAllocThreshold)
            {
                rentedX = ArrayPool<double>.Shared.Rent(src.Length);
                xBuf = rentedX.AsSpan(0, src.Length);
            }

            if (outLen > MathBufferLimits.StackAllocThreshold)
            {
                rentedZ = ArrayPool<double>.Shared.Rent(outLen);
                zBuf = rentedZ.AsSpan(0, outLen);
            }

            CopyToDouble(src, xBuf);
            MathErrorCode err = DslRegressionMath.PcaProject(xBuf, nRows, nCols, k, zBuf, ref budget);
            if (err != MathErrorCode.None)
            {
                return FailMath(err, position, name);
            }

            return NarrowArrayResult(zBuf, position, name, ref arena);
        }
        finally
        {
            if (rentedX is not null) ArrayPool<double>.Shared.Return(rentedX);
            if (rentedZ is not null) ArrayPool<double>.Shared.Return(rentedZ);
        }
    }

    internal static void CopyToDouble(scoped ReadOnlySpan<decimal> src, scoped Span<double> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = (double)src[i];
        }
    }

    internal static DslEvalOutcome NarrowScalarResult(MathResult<double> raw, int position, string name)
    {
        if (!raw.IsOk)
        {
            return FailMath(raw.Error, position, name);
        }

        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(DslElementaryMath.NarrowDouble(raw.Value), position, name));
    }

    internal static DslEvalOutcome NarrowArrayResult(scoped ReadOnlySpan<double> raw, int position, string name, ref DslArrayArena arena)
    {
        if (!arena.TryAllocate(raw.Length, out DslArrayHandle handle, out Span<decimal> dst))
        {
            return FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        for (int i = 0; i < raw.Length; i++)
        {
            MathResult<decimal> narrowed = DslElementaryMath.NarrowDouble(raw[i]);
            if (!narrowed.IsOk)
            {
                return FailMath(narrowed.Error, position, name);
            }

            dst[i] = narrowed.Value;
        }

        return DslEvalOutcome.OkArray(handle);
    }

    internal static DslEvalOutcome FailMath(MathErrorCode error, int position, string name)
        => CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathError(error, position, name));

    private static bool TryInferSquareDim(int length, int minN, int maxN, out int n)
    {
        if (length < minN * minN || length > maxN * maxN)
        {
            n = 0;
            return false;
        }

        int root = (int)Math.Round(Math.Sqrt(length));
        if (root * root != length || root < minN || root > maxN)
        {
            n = 0;
            return false;
        }

        n = root;
        return true;
    }

    private static bool TryReadBoolBit(decimal value, out bool flag)
    {
        if (value == 0m)
        {
            flag = false;
            return true;
        }

        if (value == 1m)
        {
            flag = true;
            return true;
        }

        flag = false;
        return false;
    }
}
