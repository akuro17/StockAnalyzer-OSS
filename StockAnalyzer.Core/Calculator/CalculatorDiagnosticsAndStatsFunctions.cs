using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for v4 robust statistics, ACF/PACF, multiple testing adjustment,
/// numerical error diagnostics, discrete differences, bootstrap resampling, and 1D PDE solver.
/// Reuses <see cref="CalculatorMatrixFunctions"/> narrowing helpers to avoid duplicating array marshaling.
/// </summary>
internal static partial class CalculatorDiagnosticsAndStatsFunctions
{
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
            case "mad_scaled":
                outcome = EvalMadScaled(position, name, args, in arena);
                return true;

            case "trimmed_mean":
                outcome = EvalTrimmedOrWinsor(position, name, args, winsorize: false, in arena);
                return true;

            case "winsor_mean":
                outcome = EvalTrimmedOrWinsor(position, name, args, winsorize: true, in arena);
                return true;

            case "huber_loc":
                outcome = EvalIrlsLocation(position, name, args, isBiweight: false, ref budget, in arena);
                return true;

            case "biweight_loc":
                outcome = EvalIrlsLocation(position, name, args, isBiweight: true, ref budget, in arena);
                return true;

            case "theil_sen":
                outcome = EvalRobustSlope(position, name, args, isSiegel: false, in arena);
                return true;

            case "siegel_slope":
                outcome = EvalRobustSlope(position, name, args, isSiegel: true, in arena);
                return true;

            case "acf":
                outcome = EvalAcfScalar(position, name, args, ref budget, in arena);
                return true;

            case "acf_series":
                outcome = EvalAcfSeries(position, name, args, ref budget, ref arena);
                return true;

            case "pacf":
                outcome = EvalPacfScalar(position, name, args, ref budget, in arena);
                return true;

            case "pacf_series":
                outcome = EvalPacfSeries(position, name, args, ref budget, ref arena);
                return true;

            case "p_adjust":
                outcome = EvalPAdjust(position, name, args, ref arena);
                return true;

            case "diag_residual_norm":
                outcome = EvalResidualNorm(position, name, args, in arena);
                return true;

            case "diag_rel_err":
                outcome = EvalRelErr(position, name, args);
                return true;

            case "diag_ulp_diff":
                outcome = EvalUlpDiff(position, name, args);
                return true;

            case "diag_kahan_sum":
                outcome = EvalKahan(position, name, args, returnError: false, in arena);
                return true;

            case "diag_kahan_err":
                outcome = EvalKahan(position, name, args, returnError: true, in arena);
                return true;

            case "diag_cancellation_bits":
                outcome = EvalCancellationBits(position, name, args);
                return true;

            case "boot_ci":
                outcome = EvalBootCi(position, name, args, ref budget, ref arena);
                return true;

            case "diff_lag":
                outcome = EvalDiffLag(position, name, args, in arena);
                return true;

            case "diff_lag_series":
                outcome = EvalDiffLagSeries(position, name, args, ref arena);
                return true;

            case "diff_pct":
                outcome = EvalDiffPct(position, name, args, in arena);
                return true;

            case "fd_pde_1d":
                outcome = EvalPde1D(position, name, args, ref budget, ref arena);
                return true;

            default:
                return TryEvaluateV5Value(name, position, args, ref budget, ref arena, out outcome);
        }
    }

    private static DslEvalOutcome EvalMadScaled(int position, string name, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length >= 2 && args[1].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        double scale = args.Length >= 2 ? (double)args[1].Scalar : DslRobustStatisticsMath.DefaultMadScale;
        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslRobustStatisticsMath.MadScaled(buf, scale), position, name);
    }

    private static DslEvalOutcome EvalTrimmedOrWinsor(int position, string name, scoped ReadOnlySpan<DslValue> args, bool winsorize, in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        double alpha = (double)args[1].Scalar;
        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);
        MathResult<double> raw = winsorize
            ? DslRobustStatisticsMath.WinsorMean(buf, alpha)
            : DslRobustStatisticsMath.TrimmedMean(buf, alpha);
        return CalculatorMatrixFunctions.NarrowScalarResult(raw, position, name);
    }

    private static DslEvalOutcome EvalIrlsLocation(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        bool isBiweight,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length >= 2 && args[1].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);

        MathResult<double> raw;
        if (isBiweight)
        {
            double c = args.Length >= 2 ? (double)args[1].Scalar : DslRobustStatisticsMath.DefaultBiweightC;
            raw = DslRobustStatisticsMath.BiweightLocation(buf, ref budget, c);
        }
        else
        {
            double k = args.Length >= 2 ? (double)args[1].Scalar : DslRobustStatisticsMath.DefaultHuberK;
            raw = DslRobustStatisticsMath.HuberLocation(buf, ref budget, k);
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(raw, position, name);
    }

    private static DslEvalOutcome EvalRobustSlope(int position, string name, scoped ReadOnlySpan<DslValue> args, bool isSiegel, in DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> first = arena.GetSlice(args[0].Array);
        MathResult<decimal> res;
        if (args.Length == 1)
        {
            res = isSiegel
                ? DslRobustStatisticsMath.SiegelSlope(first)
                : DslRobustStatisticsMath.TheilSenSlope(first);
        }
        else
        {
            if (!args[1].IsArray)
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            }

            ReadOnlySpan<decimal> second = arena.GetSlice(args[1].Array);
            res = isSiegel
                ? DslRobustStatisticsMath.SiegelSlope(first, second)
                : DslRobustStatisticsMath.TheilSenSlope(first, second);
        }

        return CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(res, position, name));
    }

    private static DslEvalOutcome EvalAcfScalar(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 0, DslTimeSeriesMath.MaxAcfLag, out int lag))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 3)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslTimeSeriesMath.Acf(buf, lag, ref budget), position, name);
    }

    private static DslEvalOutcome EvalAcfSeries(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 0, DslTimeSeriesMath.MaxAcfLag, out int maxLag))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 3 || maxLag > src.Length - 2)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);
        Span<double> outBuf = stackalloc double[maxLag + 1];
        MathErrorCode err = DslTimeSeriesMath.AcfSeries(buf, maxLag, outBuf, ref budget);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(outBuf, position, name, ref arena);
    }

    private static DslEvalOutcome EvalPacfScalar(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslTimeSeriesMath.MaxPacfLag, out int lag))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 3)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslTimeSeriesMath.Pacf(buf, lag, ref budget), position, name);
    }

    private static DslEvalOutcome EvalPacfSeries(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslTimeSeriesMath.MaxPacfLag, out int maxLag))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 3 || maxLag > src.Length - 2)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);
        Span<double> outBuf = stackalloc double[maxLag];
        MathErrorCode err = DslTimeSeriesMath.PacfSeries(buf, maxLag, outBuf, ref budget);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(outBuf, position, name, ref arena);
    }

    private static DslEvalOutcome EvalPAdjust(int position, string name, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 0, (int)MultipleTestingMethod.BenjaminiYekutieli, out int methodCode))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> pVals = arena.GetSlice(args[0].Array);
        if (pVals.Length < 1 || pVals.Length > DslMultipleTestingMath.MaxPValues)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<decimal> adjusted = stackalloc decimal[pVals.Length];
        MathErrorCode err = DslMultipleTestingMath.PAdjust(pVals, (MultipleTestingMethod)methodCode, adjusted);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        if (!arena.TryAllocate(pVals.Length, out DslArrayHandle handle, out Span<decimal> dst))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        adjusted.CopyTo(dst);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalResidualNorm(int position, string name, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> aSrc = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> xSrc = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> bSrc = arena.GetSlice(args[2].Array);

        int m = bSrc.Length;
        int n = xSrc.Length;
        if (m < 1 || m > DslLinearAlgebraMath.MaxMatrixDim ||
            n < 1 || n > DslLinearAlgebraMath.MaxMatrixDim ||
            aSrc.Length != m * n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> aBuf = stackalloc double[aSrc.Length];
        Span<double> xBuf = stackalloc double[n];
        Span<double> bBuf = stackalloc double[m];
        CalculatorMatrixFunctions.CopyToDouble(aSrc, aBuf);
        CalculatorMatrixFunctions.CopyToDouble(xSrc, xBuf);
        CalculatorMatrixFunctions.CopyToDouble(bSrc, bBuf);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslNumericalDiagnosticsMath.ResidualNorm(aBuf, m, n, xBuf, bBuf), position, name);
    }

    private static DslEvalOutcome EvalRelErr(int position, string name, scoped ReadOnlySpan<DslValue> args)
    {
        if (args[0].IsArray || args[1].IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(
            DslNumericalDiagnosticsMath.RelativeError(args[0].Scalar, args[1].Scalar),
            position,
            name);
    }

    private static DslEvalOutcome EvalUlpDiff(int position, string name, scoped ReadOnlySpan<DslValue> args)
    {
        if (args[0].IsArray || args[1].IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        MathResult<decimal> res = DslNumericalDiagnosticsMath.UlpDiff((double)args[0].Scalar, (double)args[1].Scalar);
        return CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(res, position, name));
    }

    private static DslEvalOutcome EvalKahan(int position, string name, scoped ReadOnlySpan<DslValue> args, bool returnError, in DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        if (src.Length < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> buf = stackalloc double[src.Length];
        CalculatorMatrixFunctions.CopyToDouble(src, buf);
        MathErrorCode err = DslNumericalDiagnosticsMath.KahanSum(buf, out double sum, out double recovered);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(MathResult<double>.Ok(returnError ? recovered : sum), position, name);
    }

    private static DslEvalOutcome EvalCancellationBits(int position, string name, scoped ReadOnlySpan<DslValue> args)
    {
        if (args[0].IsArray || args[1].IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(
            DslNumericalDiagnosticsMath.CancellationBits((double)args[0].Scalar, (double)args[1].Scalar),
            position,
            name);
    }

    private static DslEvalOutcome EvalBootCi(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray || args[4].IsArray || args[5].IsArray ||
            (args.Length >= 7 && args[6].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 0, (int)BootstrapStatKind.Sharpe, out int statCode) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, DslResamplingMath.MinResamples, DslResamplingMath.MaxResamples, out int nResamples) ||
            !DslMathValidation.TryReadInt32InRange(args[3].Scalar, 1, DslResamplingMath.MaxSeriesLength, out int blockLen) ||
            !DslMathValidation.TryReadUInt64Seed(args[5].Scalar, out ulong seed) ||
            args[2].Scalar * args[4].Scalar < (decimal)DslResamplingMath.MinTailResolutionProduct)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int blockModeCode = 0;
        if (args.Length >= 7 &&
            !DslMathValidation.TryReadInt32InRange(args[6].Scalar, 0, (int)BootstrapBlockMode.Stationary, out blockModeCode))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> series = arena.GetSlice(args[0].Array);
        double alpha = (double)args[4].Scalar;
        MathResult<BootstrapResult> bootRes = DslResamplingMath.BootConfidenceInterval(
            series,
            (BootstrapStatKind)statCode,
            nResamples,
            blockLen,
            alpha,
            seed,
            (BootstrapBlockMode)blockModeCode,
            ref budget);
        if (!bootRes.IsOk)
        {
            return CalculatorMatrixFunctions.FailMath(bootRes.Error, position, name);
        }

        if (!arena.TryAllocate(4, out DslArrayHandle handle, out Span<decimal> dst))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        dst[0] = bootRes.Value.Estimate;
        dst[1] = bootRes.Value.Lower;
        dst[2] = bootRes.Value.Upper;
        dst[3] = bootRes.Value.StandardError;
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalDiffLag(int position, string name, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length >= 2 && args[1].IsArray) || (args.Length >= 3 && args[2].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int k = 1;
        int order = 1;
        if (args.Length >= 2 && !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslFiniteDifferenceMath.MaxLag, out k))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (args.Length >= 3 && !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 1, DslFiniteDifferenceMath.MaxDiffOrder, out order))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> series = arena.GetSlice(args[0].Array);
        MathResult<decimal> res = DslFiniteDifferenceMath.DiffLag(series, k, order);
        return CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(res, position, name));
    }

    private static DslEvalOutcome EvalDiffLagSeries(int position, string name, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length >= 2 && args[1].IsArray) || (args.Length >= 3 && args[2].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int k = 1;
        int order = 1;
        if (args.Length >= 2 && !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslFiniteDifferenceMath.MaxLag, out k))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (args.Length >= 3 && !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 1, DslFiniteDifferenceMath.MaxDiffOrder, out order))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> series = arena.GetSlice(args[0].Array);
        if (series.Length <= k * order)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int outLen = series.Length - (k * order);
        Span<decimal> outBuf = stackalloc decimal[outLen];
        MathErrorCode err = DslFiniteDifferenceMath.DiffLagSeries(series, k, order, outBuf, out int writtenLen);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        if (!arena.TryAllocate(writtenLen, out DslArrayHandle handle, out Span<decimal> dst))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        outBuf.Slice(0, writtenLen).CopyTo(dst);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalDiffPct(int position, string name, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length >= 2 && args[1].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int k = 1;
        if (args.Length >= 2 && !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslFiniteDifferenceMath.MaxLag, out k))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> series = arena.GetSlice(args[0].Array);
        MathResult<decimal> res = DslFiniteDifferenceMath.DiffPct(series, k);
        return CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(res, position, name));
    }

    private static DslEvalOutcome EvalPde1D(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray || args[4].IsArray ||
            (args.Length >= 6 && args[5].IsArray) || (args.Length >= 7 && args[6].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!DslMathValidation.TryReadInt32InRange(args[4].Scalar, 1, DslFiniteDifferenceMath.MaxPdeSteps, out int nSteps))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int schemeCode = (int)PdeScheme.CrankNicolson;
        if (args.Length >= 6 && !DslMathValidation.TryReadInt32InRange(args[5].Scalar, 0, (int)PdeScheme.CrankNicolson, out schemeCode))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int bcCode = (int)PdeBoundaryCondition.DirichletFixed;
        if (args.Length >= 7 && !DslMathValidation.TryReadInt32InRange(args[6].Scalar, 0, (int)PdeBoundaryCondition.LinearExtrapolation, out bcCode))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> u0Src = arena.GetSlice(args[0].Array);
        int m = u0Src.Length;
        if (m < DslFiniteDifferenceMath.MinGridPoints || m > DslFiniteDifferenceMath.MaxGridPoints)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> u0Buf = stackalloc double[m];
        Span<double> uOutBuf = stackalloc double[m];
        CalculatorMatrixFunctions.CopyToDouble(u0Src, u0Buf);

        MathErrorCode err = DslFiniteDifferenceMath.SolvePde1D(
            u0Buf,
            (double)args[1].Scalar,
            (double)args[2].Scalar,
            (double)args[3].Scalar,
            nSteps,
            (PdeScheme)schemeCode,
            (PdeBoundaryCondition)bcCode,
            uOutBuf,
            ref budget);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(uOutBuf, position, name, ref arena);
    }
}
