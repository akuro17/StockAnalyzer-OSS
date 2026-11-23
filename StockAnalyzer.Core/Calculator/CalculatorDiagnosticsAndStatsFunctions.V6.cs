using System;
using System.Buffers;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// DSL v6 dispatch for signal processing and spectral functions (<c>fft_*</c>, <c>ifft_*</c>,
/// <c>convolve</c>, <c>window_hann</c>, <c>window_hamming</c>, <c>ema_series</c>) wrapping
/// <see cref="FourierTransform"/> read-only with zero persistent heap allocation.
/// </summary>
internal static partial class CalculatorDiagnosticsAndStatsFunctions
{
    private const double FftZeroMagnitudeThreshold = 1e-15;

    private static bool TryEvaluateV6Value(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "fft_re":
            case "fft_im":
            case "fft_phase":
                outcome = EvalFftForward(name, position, args, ref budget, ref arena);
                return true;

            case "ifft_re":
            case "ifft_im":
                outcome = EvalFftInverse(name, position, args, ref budget, ref arena);
                return true;

            case "convolve":
                outcome = EvalConvolve(name, position, args, ref budget, ref arena);
                return true;

            case "window_hann":
            case "window_hamming":
                outcome = EvalWindowFunction(name, position, args, ref arena);
                return true;

            case "ema_series":
                outcome = EvalEmaSeries(name, position, args, ref arena);
                return true;

            case "polyval":
                outcome = EvalPolyVal(name, position, args, in arena);
                return true;

            case "poly_roots_re":
            case "poly_roots_im":
                outcome = EvalPolyRoots(name, position, args, ref budget, ref arena);
                return true;

            case "interp2d_bilinear":
                outcome = EvalInterp2DBilinear(name, position, args, in arena);
                return true;

            case "interp2d_bicubic":
                outcome = EvalInterp2DBicubic(name, position, args, in arena);
                return true;

            case "log_sum_exp_weighted":
                outcome = EvalLogSumExpWeighted(name, position, args, in arena);
                return true;

            case "kalman_1d_filter":
                outcome = EvalKalman1DFilter(name, position, args, ref budget, ref arena);
                return true;

            case "granger_test":
                outcome = EvalGrangerTest(name, position, args, ref budget, ref arena);
                return true;

            case "johansen_test":
                outcome = EvalJohansenTest(name, position, args, ref budget, ref arena);
                return true;

            case "arima_fit":
                outcome = EvalArimaFit(name, position, args, ref budget, ref arena);
                return true;

            case "arima_forecast":
                outcome = EvalArimaForecast(name, position, args, ref budget, ref arena);
                return true;

            case "mvnorm_logpdf":
                outcome = EvalMvNormLogPdf(name, position, args, ref budget, in arena);
                return true;

            case "rand_unif":
                outcome = EvalRandUnif(name, position, args, ref budget, ref arena);
                return true;

            case "rand_norm":
                outcome = EvalRandNorm(name, position, args, ref budget, ref arena);
                return true;

            case "rand_int":
                outcome = EvalRandInt(name, position, args, ref budget, ref arena);
                return true;

            case "arr_shuffle":
                outcome = EvalArrShuffle(name, position, args, ref budget, ref arena);
                return true;

            case "anova_1way":
                outcome = EvalAnova1Way(name, position, args, ref arena);
                return true;

            case "chi2_gof":
                outcome = EvalChi2Gof(name, position, args, ref arena);
                return true;

            case "mann_whitney_u":
                outcome = EvalMannWhitneyU(name, position, args, ref arena);
                return true;

            case "ols_stats":
                outcome = EvalOlsStats(name, position, args, ref budget, ref arena);
                return true;

            case "adf_test":
                outcome = EvalAdfTest(name, position, args, ref budget, ref arena);
                return true;

            case "kpss_stat":
                outcome = EvalKpssStat(name, position, args, ref budget, in arena);
                return true;

            case "hurst_rs":
                outcome = EvalHurst(name, position, args, isDfa: false, ref budget, in arena);
                return true;

            case "hurst_dfa":
                outcome = EvalHurst(name, position, args, isDfa: true, ref budget, in arena);
                return true;

            case "garch11_fit":
                outcome = EvalGarch11Fit(name, position, args, ref budget, ref arena);
                return true;

            case "date_serial":
                outcome = EvalDateSerial(name, position, args);
                return true;

            case "days_between":
                outcome = EvalDaysBetween(name, position, args);
                return true;

            case "yearfrac":
                outcome = EvalYearFrac(name, position, args);
                return true;

            case "xnpv":
                outcome = EvalXnpv(name, position, args, in arena);
                return true;

            case "xirr":
                outcome = EvalXirr(name, position, args, ref budget, in arena);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static DslEvalOutcome EvalFftForward(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailExpected(position, name, "(x) a 1D array");
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        if (n < 1 || n > DslArrayArena.MaxArrayLength)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int cost = Math.Max(1, n / 16);
        if (!budget.TryConsume(cost))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        Span<double> signal = stackalloc double[n];
        Span<double> outRe = stackalloc double[n];
        Span<double> outIm = stackalloc double[n];
        ToDouble(src, signal);
        FourierTransform.Forward(signal, outRe, outIm);

        if (name == "fft_re")
        {
            return CalculatorMatrixFunctions.NarrowArrayResult(outRe, position, name, ref arena);
        }

        if (name == "fft_im")
        {
            return CalculatorMatrixFunctions.NarrowArrayResult(outIm, position, name, ref arena);
        }

        Span<double> phase = stackalloc double[n];
        for (int k = 0; k < n; k++)
        {
            double mag = DslElementaryMath.HypotDouble(outRe[k], outIm[k]);
            phase[k] = mag < FftZeroMagnitudeThreshold ? 0.0 : Math.Atan2(outIm[k], outRe[k]);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(phase, position, name, ref arena);
    }

    private static DslEvalOutcome EvalFftInverse(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(re, im) 1D arrays");
        }

        ReadOnlySpan<decimal> reSrc = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> imSrc = arena.GetSlice(args[1].Array);
        int n = reSrc.Length;
        if (n < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (imSrc.Length != n || n > DslArrayArena.MaxArrayLength)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int cost = Math.Max(1, n / 16);
        if (!budget.TryConsume(cost))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        Span<double> inRe = stackalloc double[n];
        Span<double> inIm = stackalloc double[n];
        Span<double> outRe = stackalloc double[n];
        Span<double> outIm = stackalloc double[n];
        ToDouble(reSrc, inRe);
        ToDouble(imSrc, inIm);
        FourierTransform.Inverse(inRe, inIm, outRe, outIm);

        return CalculatorMatrixFunctions.NarrowArrayResult(
            name == "ifft_re" ? outRe : outIm,
            position,
            name,
            ref arena);
    }

    private static DslEvalOutcome EvalConvolve(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || (args.Length >= 3 && args[2].IsArray))
        {
            return FailExpected(position, name, "(a, b[, mode])");
        }

        int mode = 0;
        if (args.Length >= 3 && !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, 2, out mode))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> a = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> b = arena.GetSlice(args[1].Array);
        int m = a.Length;
        int n = b.Length;
        if (m < 1 || n < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int outLen;
        int startOffset;
        switch (mode)
        {
            case 0:
                outLen = m + n - 1;
                startOffset = 0;
                break;
            case 1:
                outLen = m;
                startOffset = (n - 1) / 2;
                break;
            default:
                int minLen = Math.Min(m, n);
                int maxLen = Math.Max(m, n);
                outLen = maxLen - minLen + 1;
                startOffset = minLen - 1;
                break;
        }

        if (outLen > DslArrayArena.MaxArrayLength)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int cost = Math.Max(1, (m * n) / DslExecutionBudget.CheapOperationsPerUnit);
        if (!budget.TryConsume(cost))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(outLen, out DslArrayHandle handle, out Span<decimal> result))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        a = arena.GetSlice(args[0].Array);
        b = arena.GetSlice(args[1].Array);
        try
        {
            for (int outIdx = 0; outIdx < outLen; outIdx++)
            {
                int k = startOffset + outIdx;
                int jStart = Math.Max(0, k - (n - 1));
                int jEnd = Math.Min(m - 1, k);
                decimal acc = 0m;
                for (int j = jStart; j <= jEnd; j++)
                {
                    acc = checked(acc + checked(a[j] * b[k - j]));
                }

                result[outIdx] = acc == 0m ? 0m : acc;
            }
        }
        catch (OverflowException)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.ArithmeticOverflow, position, name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalWindowFunction(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (args[0].IsArray || !DslMathValidation.TryReadInt32InRange(args[0].Scalar, 1, DslArrayArena.MaxArrayLength, out int n))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (n == 1)
        {
            if (!arena.TryAllocate(1, out DslArrayHandle oneHandle, out Span<decimal> oneSlice))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
            }

            oneSlice[0] = 1m;
            return DslEvalOutcome.OkArray(oneHandle);
        }

        bool isHann = name == "window_hann";
        double a0 = isHann ? 0.5 : 0.54;
        double a1 = isHann ? 0.5 : 0.46;
        Span<double> weights = stackalloc double[n];
        double denom = n - 1;
        for (int k = 0; k < n; k++)
        {
            weights[k] = a0 - a1 * Math.Cos((MathConstants.TwoPi * k) / denom);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(weights, position, name, ref arena);
    }

    private static DslEvalOutcome EvalEmaSeries(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray)
        {
            return FailExpected(position, name, "(x, alpha)");
        }

        decimal alpha = args[1].Scalar;
        int n = args[0].Array.Length;
        if (n < 1 || alpha <= 0m || alpha > 1m)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> result))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        decimal oneMinusAlpha = 1m - alpha;
        try
        {
            decimal prev = src[0];
            result[0] = prev == 0m ? 0m : prev;
            for (int t = 1; t < n; t++)
            {
                prev = checked(alpha * src[t] + oneMinusAlpha * prev);
                result[t] = prev == 0m ? 0m : prev;
            }
        }
        catch (OverflowException)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.ArithmeticOverflow, position, name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    // ---- DSL v6 Domain 5: Polynomials & 2D Interpolation ----

    private static DslEvalOutcome EvalPolyVal(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray)
        {
            return FailExpected(position, name, "(coeffs, x)");
        }

        ReadOnlySpan<decimal> coeffs = arena.GetSlice(args[0].Array);
        if (coeffs.Length == 0)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        decimal x = args[1].Scalar;
        try
        {
            decimal acc = coeffs[0];
            for (int i = 1; i < coeffs.Length; i++)
            {
                acc = checked(acc * x + coeffs[i]);
            }

            return DslEvalOutcome.Ok(DslElementaryMath.NormalizeZero(acc));
        }
        catch (OverflowException)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.ArithmeticOverflow, position, name);
        }
    }

    private static DslEvalOutcome EvalPolyRoots(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailExpected(position, name, "(coeffs) a 1D array");
        }

        ReadOnlySpan<decimal> coeffs = arena.GetSlice(args[0].Array);
        int len = coeffs.Length;
        int deg = len - 1;
        if (deg < 1 || deg > DslLinearAlgebraMath.MaxMatrixDim)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (coeffs[0] == 0m || DslElementaryMath.ExceedsRootScaleLimit(coeffs))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        double c0 = (double)coeffs[0];
        if (!double.IsFinite(c0))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> companion = stackalloc double[deg * deg];
        companion.Clear();
        for (int j = 0; j < deg; j++)
        {
            double val = -(double)coeffs[j + 1] / c0;
            if (!double.IsFinite(val))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.ArithmeticOverflow, position, name);
            }

            companion[j] = val;
        }

        for (int i = 1; i < deg; i++)
        {
            companion[i * deg + (i - 1)] = 1.0;
        }

        Span<double> re = stackalloc double[deg];
        Span<double> im = stackalloc double[deg];
        MathErrorCode err = DslLinearAlgebraMath.MatEigenGeneral(companion, deg, re, im, ref budget);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        DslLinearAlgebraMath.SortComplexPairsCanonical(re, im, deg);
        return CalculatorMatrixFunctions.NarrowArrayResult(name == "poly_roots_re" ? re : im, position, name, ref arena);
    }

    private static DslEvalOutcome EvalInterp2DBilinear(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray || args[3].IsArray || args[4].IsArray)
        {
            return FailExpected(position, name, "(xs, ys, Z, x, y)");
        }

        ReadOnlySpan<decimal> xsDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> ysDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> zDec = arena.GetSlice(args[2].Array);
        Span<double> xs = stackalloc double[xsDec.Length];
        Span<double> ys = stackalloc double[ysDec.Length];
        Span<double> z = stackalloc double[zDec.Length];
        ToDouble(xsDec, xs);
        ToDouble(ysDec, ys);
        ToDouble(zDec, z);

        return CalculatorMatrixFunctions.NarrowScalarResult(
            DslInterpolationMath.Interp2DBilinear(xs, ys, z, (double)args[3].Scalar, (double)args[4].Scalar),
            position,
            name);
    }

    // ---- DSL v6 Domain 6a: Deterministic PRNG ----

    private static DslEvalOutcome EvalRandUnif(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (args.Length is not (2 or 4))
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.WrongArgumentCount,
                position,
                $"Function '{name}' expects 2 or 4 arguments, got {args.Length}.",
                name);
        }

        if (args[0].IsArray || args[1].IsArray || (args.Length == 4 && (args[2].IsArray || args[3].IsArray)))
        {
            return FailExpected(position, name, "(n, seed[, lo, hi]) scalars");
        }

        if (!DslMathValidation.TryReadInt32InRange(args[0].Scalar, 0, DslArrayArena.MaxArrayLength, out int n) ||
            !DslMathValidation.TryReadUInt64Seed(args[1].Scalar, out ulong seed))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        double lo = args.Length == 4 ? (double)args[2].Scalar : 0.0;
        double hi = args.Length == 4 ? (double)args[3].Scalar : 1.0;
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo >= hi)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (n == 0)
        {
            return AllocateEmptyArray(position, name, ref arena);
        }

        var rng = new DeterministicRng(seed);
        double span = hi - lo;
        Span<double> samples = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = lo + (span * rng.NextUnitDouble());
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(samples, position, name, ref arena);
    }

    private static DslEvalOutcome EvalRandNorm(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (args.Length is not (2 or 4))
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.WrongArgumentCount,
                position,
                $"Function '{name}' expects 2 or 4 arguments, got {args.Length}.",
                name);
        }

        if (args[0].IsArray || args[1].IsArray || (args.Length == 4 && (args[2].IsArray || args[3].IsArray)))
        {
            return FailExpected(position, name, "(n, seed[, mu, sigma]) scalars");
        }

        if (!DslMathValidation.TryReadInt32InRange(args[0].Scalar, 0, DslArrayArena.MaxArrayLength, out int n) ||
            !DslMathValidation.TryReadUInt64Seed(args[1].Scalar, out ulong seed))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        double mu = args.Length == 4 ? (double)args[2].Scalar : 0.0;
        double sigma = args.Length == 4 ? (double)args[3].Scalar : 1.0;
        if (!double.IsFinite(mu) || !double.IsFinite(sigma) || sigma <= 0.0)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (n == 0)
        {
            return AllocateEmptyArray(position, name, ref arena);
        }

        var rng = new DeterministicRng(seed);
        const double minU1 = 1.0 / (1UL << 53);
        Span<double> samples = stackalloc double[n];
        for (int i = 0; i < n; i += 2)
        {
            double u1 = Math.Max(rng.NextUnitDouble(), minU1);
            double u2 = rng.NextUnitDouble();
            double radius = Math.Sqrt(-2.0 * Math.Log(u1));
            double angle = MathConstants.TwoPi * u2;
            samples[i] = mu + (sigma * radius * Math.Cos(angle));
            if (i + 1 < n)
            {
                samples[i + 1] = mu + (sigma * radius * Math.Sin(angle));
            }
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(samples, position, name, ref arena);
    }

    private static DslEvalOutcome EvalRandInt(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return FailExpected(position, name, "(n, lo, hi, seed) scalars");
        }

        if (!DslMathValidation.TryReadInt32InRange(args[0].Scalar, 0, DslArrayArena.MaxArrayLength, out int n) ||
            !DslMathValidation.TryReadUInt64Seed(args[3].Scalar, out ulong seed))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        decimal lo = args[1].Scalar;
        decimal hi = args[2].Scalar;
        if (lo != decimal.Truncate(lo) || hi != decimal.Truncate(hi) || lo > hi)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        decimal diff;
        try
        {
            diff = checked(hi - lo);
        }
        catch (OverflowException)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (diff > int.MaxValue - 1m)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> result))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        int maxExclusive = (int)diff + 1;
        var rng = new DeterministicRng(seed);
        try
        {
            for (int i = 0; i < n; i++)
            {
                decimal value = checked(lo + rng.NextInt(maxExclusive));
                result[i] = value == 0m ? 0m : value;
            }
        }
        catch (OverflowException)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.ArithmeticOverflow, position, name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalArrShuffle(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray)
        {
            return FailExpected(position, name, "(array, seed)");
        }

        if (!DslMathValidation.TryReadUInt64Seed(args[1].Scalar, out ulong seed))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> dst))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        for (int i = 0; i < n; i++)
        {
            dst[i] = src[i] == 0m ? 0m : src[i];
        }

        var rng = new DeterministicRng(seed);
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.NextInt(i + 1);
            (dst[i], dst[j]) = (dst[j], dst[i]);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    // ---- DSL v6 Domain 6b: Statistical Tests & Regression Diagnostics ----

    private static DslEvalOutcome EvalAnova1Way(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(values, groupIds) 1D arrays");
        }

        ReadOnlySpan<decimal> valuesDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> groupDec = arena.GetSlice(args[1].Array);
        if (valuesDec.Length != groupDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int n = valuesDec.Length;
        if (n < 3)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> values = stackalloc double[n];
        Span<int> groupIds = stackalloc int[n];
        for (int i = 0; i < n; i++)
        {
            if (!DslMathValidation.TryReadInt32InRange(
                    groupDec[i],
                    DslBivariateAndTestMath.AnovaMinGroupId,
                    DslBivariateAndTestMath.AnovaMaxGroupId,
                    out groupIds[i]))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            }

            values[i] = (double)valuesDec[i];
        }

        Span<double> output = stackalloc double[4];
        MathErrorCode err = DslBivariateAndTestMath.Anova1Way(values, groupIds, output);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
    }

    private static DslEvalOutcome EvalChi2Gof(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(observed, expected) 1D arrays");
        }

        ReadOnlySpan<decimal> obsDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> expDec = arena.GetSlice(args[1].Array);
        if (obsDec.Length != expDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int k = obsDec.Length;
        if (k < 2)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> observed = stackalloc double[k];
        Span<double> expected = stackalloc double[k];
        ToDouble(obsDec, observed);
        ToDouble(expDec, expected);
        Span<double> output = stackalloc double[3];
        MathErrorCode err = DslBivariateAndTestMath.Chi2Gof(observed, expected, output);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
    }

    private static DslEvalOutcome EvalMannWhitneyU(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(x, y) 1D arrays");
        }

        ReadOnlySpan<decimal> xDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> yDec = arena.GetSlice(args[1].Array);
        int n1 = xDec.Length;
        int n2 = yDec.Length;
        if (n1 < 2 || n2 < 2 || n1 + n2 > DslArrayArena.MaxArrayLength)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> x = stackalloc double[n1];
        Span<double> y = stackalloc double[n2];
        ToDouble(xDec, x);
        ToDouble(yDec, y);
        Span<double> output = stackalloc double[2];
        MathErrorCode err = DslBivariateAndTestMath.MannWhitneyU(x, y, output);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
    }

    private static DslEvalOutcome EvalOlsStats(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(X, y) 1D arrays");
        }

        ReadOnlySpan<decimal> xDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> yDec = arena.GetSlice(args[1].Array);
        int n = yDec.Length;
        if (n < 1 || xDec.Length == 0 || xDec.Length % n != 0)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int p = xDec.Length / n;
        if (p < 1 || p > DslRegressionMath.MaxFeatures)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> xBuf = stackalloc double[xDec.Length];
        Span<double> yBuf = stackalloc double[n];
        ToDouble(xDec, xBuf);
        ToDouble(yDec, yBuf);
        Span<double> output = stackalloc double[5];
        MathErrorCode err = DslBivariateAndTestMath.OlsStats(xBuf, n, p, yBuf, output, ref budget);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        return CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
    }

    // ---- DSL v6 Domain 6d: Econometric & Time-Series Wrappers ----

    private static DslEvalOutcome EvalAdfTest(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length == 2 && args[1].IsArray))
        {
            return FailExpected(position, name, "(series[, lagOrder])");
        }

        int lagOrder = 1;
        if (args.Length == 2 &&
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 0, StationarityTestMath.MaxLagOrder, out lagOrder))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        if (n < StationarityTestMath.MinWindowLength(lagOrder))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int cost = Math.Max(1, n * StationarityTestMath.ParameterCount(lagOrder) / DslExecutionBudget.CheapOperationsPerUnit);
        if (!budget.TryConsume(cost))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        Span<double> win = stackalloc double[n];
        ToDouble(src, win);
        int scratchLen = StationarityTestMath.ScratchLength(n, lagOrder);
        double[]? rented = null;
        Span<double> scratch = scratchLen <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[scratchLen]
            : default;
        try
        {
            if (scratchLen > MathBufferLimits.StackAllocThreshold)
            {
                rented = ArrayPool<double>.Shared.Rent(scratchLen);
                scratch = rented.AsSpan(0, scratchLen);
            }

            if (!StationarityTestMath.TryAugmentedDickeyFuller(win, lagOrder, 1e-12, scratch, out AugmentedDickeyFullerResult res))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            }

            if (double.IsNaN(res.Tau))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DivisionByZero, position, name);
            }

            Span<double> output = stackalloc double[4];
            output[0] = res.Tau;
            output[1] = res.Rho;
            output[2] = double.IsNaN(res.HalfLife) ? -1.0 : res.HalfLife;
            output[3] = res.CriticalValue5Percent;
            return CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }

    private static DslEvalOutcome EvalKpssStat(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailExpected(position, name, "(series) 1D array");
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        if (n < 8)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> win = stackalloc double[n];
        ToDouble(src, win);
        if (!WindowMoments.TryCompute(win, FlatWindowRule.Series, out WindowMoments moments))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.NonFiniteResult, position, name);
        }

        if (moments.IsFlat)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DivisionByZero, position, name);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        double stat = ArimaMath.ComputeKpssStatistic(win);
        if (double.IsNaN(stat))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(MathResult<double>.Ok(stat), position, name);
    }

    private static DslEvalOutcome EvalHurst(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        bool isDfa,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailExpected(position, name, "(series) 1D array");
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        if (n < 16)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> win = stackalloc double[n];
        ToDouble(src, win);
        if (!WindowMoments.TryCompute(win, FlatWindowRule.Series, out WindowMoments moments))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.NonFiniteResult, position, name);
        }

        if (moments.IsFlat)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DivisionByZero, position, name);
        }

        if (!budget.TryConsume(Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        double exp = isDfa
            ? HurstMath.DetrendedFluctuationExponent(win)
            : HurstMath.Exponent(win);
        if (double.IsNaN(exp))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(MathResult<double>.Ok(exp), position, name);
    }

    private static DslEvalOutcome EvalGarch11Fit(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailExpected(position, name, "(returns) 1D array");
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        if (n < 20)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(Math.Max(1, n / 4)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        double[] rented = ArrayPool<double>.Shared.Rent(n);
        try
        {
            Span<double> buf = rented.AsSpan(0, n);
            ToDouble(src, buf);
            if (!WindowMoments.TryCompute(buf, FlatWindowRule.Series, out WindowMoments moments))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.NonFiniteResult, position, name);
            }

            if (moments.IsFlat)
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DivisionByZero, position, name);
            }

            if (!Garch11Math.TryFit(rented, 0, n, maxIterations: 200, out Garch11Fit fit))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.ConvergenceFailure, position, name);
            }

            Span<double> output = stackalloc double[5];
            output[0] = fit.Mean;
            output[1] = fit.Omega;
            output[2] = fit.Alpha;
            output[3] = fit.Beta;
            output[4] = fit.NextVariance;
            return CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    // ---- DSL v6 Domain 7: Financial Dates & Irregular Cash Flows ----

    private static DslEvalOutcome EvalDateSerial(string name, int position, scoped ReadOnlySpan<DslValue> args)
    {
        if (args[0].IsArray || args[1].IsArray || args[2].IsArray)
        {
            return FailExpected(position, name, "(yyyy, mm, dd) scalars");
        }

        MathResult<decimal> res = DslQuantAndInfoMath.DateSerial(args[0].Scalar, args[1].Scalar, args[2].Scalar);
        return res.IsOk
            ? DslEvalOutcome.Ok(res.Value == 0m ? 0m : res.Value)
            : CalculatorMatrixFunctions.FailMath(res.Error, position, name);
    }

    private static DslEvalOutcome EvalDaysBetween(string name, int position, scoped ReadOnlySpan<DslValue> args)
    {
        if (args[0].IsArray || args[1].IsArray)
        {
            return FailExpected(position, name, "(d1, d2) scalars");
        }

        MathResult<decimal> res = DslQuantAndInfoMath.DaysBetween(args[0].Scalar, args[1].Scalar);
        return res.IsOk
            ? DslEvalOutcome.Ok(res.Value == 0m ? 0m : res.Value)
            : CalculatorMatrixFunctions.FailMath(res.Error, position, name);
    }

    private static DslEvalOutcome EvalYearFrac(string name, int position, scoped ReadOnlySpan<DslValue> args)
    {
        if (args[0].IsArray || args[1].IsArray || args[2].IsArray)
        {
            return FailExpected(position, name, "(d1, d2, basis) scalars");
        }

        MathResult<decimal> res = DslQuantAndInfoMath.YearFrac(args[0].Scalar, args[1].Scalar, args[2].Scalar);
        return res.IsOk
            ? DslEvalOutcome.Ok(res.Value == 0m ? 0m : res.Value)
            : CalculatorMatrixFunctions.FailMath(res.Error, position, name);
    }

    private static DslEvalOutcome EvalXnpv(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (args[0].IsArray || !args[1].IsArray || !args[2].IsArray)
        {
            return FailExpected(position, name, "(rate, cfArray, datesArray)");
        }

        ReadOnlySpan<decimal> cfDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> dateDec = arena.GetSlice(args[2].Array);
        if (cfDec.Length != dateDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int n = cfDec.Length;
        if (n < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<int> serialDates = stackalloc int[n];
        for (int i = 0; i < n; i++)
        {
            if (!DslQuantAndInfoMath.TryReadSerialDate(dateDec[i], out serialDates[i]))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            }
        }

        Span<double> cashFlows = stackalloc double[n];
        ToDouble(cfDec, cashFlows);
        return CalculatorMatrixFunctions.NarrowScalarResult(
            DslQuantAndInfoMath.Xnpv((double)args[0].Scalar, cashFlows, serialDates),
            position,
            name);
    }

    private static DslEvalOutcome EvalXirr(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || (args.Length == 3 && args[2].IsArray))
        {
            return FailExpected(position, name, "(cfArray, datesArray[, guess])");
        }

        ReadOnlySpan<decimal> cfDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> dateDec = arena.GetSlice(args[1].Array);
        if (cfDec.Length != dateDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int n = cfDec.Length;
        if (n < 2)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<int> serialDates = stackalloc int[n];
        for (int i = 0; i < n; i++)
        {
            if (!DslQuantAndInfoMath.TryReadSerialDate(dateDec[i], out serialDates[i]))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            }
        }

        Span<double> cashFlows = stackalloc double[n];
        ToDouble(cfDec, cashFlows);
        double guess = args.Length == 3 ? (double)args[2].Scalar : DslQuantAndInfoMath.IrrDefaultGuess;
        return CalculatorMatrixFunctions.NarrowScalarResult(
            DslQuantAndInfoMath.Xirr(cashFlows, serialDates, guess, ref budget),
            position,
            name);
    }

    private static DslEvalOutcome AllocateEmptyArray(int position, string name, ref DslArrayArena arena)
    {
        if (!arena.TryAllocate(0, out DslArrayHandle handle, out _))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    // ---- DSL v8: 2D Interpolation, Weighted LSE, Time-Series, & MVN Log-PDF ----

    private static DslEvalOutcome EvalInterp2DBicubic(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray || args[3].IsArray || args[4].IsArray)
        {
            return FailExpected(position, name, "(xs, ys, Z, x, y)");
        }

        ReadOnlySpan<decimal> xsDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> ysDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> zDec = arena.GetSlice(args[2].Array);
        int nx = xsDec.Length;
        int ny = ysDec.Length;
        if (nx < 3 || ny < 3)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (zDec.Length != nx * ny)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int total = nx + ny + zDec.Length;
        double[]? rented = total <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(total);
        Span<double> work = rented is null ? stackalloc double[total] : rented.AsSpan(0, total);
        try
        {
            Span<double> xs = work.Slice(0, nx);
            Span<double> ys = work.Slice(nx, ny);
            Span<double> z = work.Slice(nx + ny, zDec.Length);
            ToDouble(xsDec, xs);
            ToDouble(ysDec, ys);
            ToDouble(zDec, z);
            return CalculatorMatrixFunctions.NarrowScalarResult(
                DslInterpolationMath.Interp2DBicubic(xs, ys, z, (double)args[3].Scalar, (double)args[4].Scalar),
                position,
                name);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalLogSumExpWeighted(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(values, weights) 1D arrays");
        }

        ReadOnlySpan<decimal> values = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> weights = arena.GetSlice(args[1].Array);
        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(
                DslStatisticsMath.LogSumExpWeighted(values, weights),
                position,
                name));
    }

    private static DslEvalOutcome EvalKalman1DFilter(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray ||
            (args.Length >= 4 && args[3].IsArray) ||
            (args.Length >= 5 && args[4].IsArray))
        {
            return FailExpected(position, name, "(series, q, r[, initX, initP])");
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        if (n == 0)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        double q = (double)args[1].Scalar;
        double r = (double)args[2].Scalar;
        bool initialIsPosterior = args.Length < 4;
        double initX = !initialIsPosterior ? (double)args[3].Scalar : (double)src[0];
        double initP = args.Length >= 5 ? (double)args[4].Scalar : r;

        Span<double> series = stackalloc double[n];
        Span<double> output = stackalloc double[n];
        ToDouble(src, series);

        MathErrorCode err = DslTimeSeriesMath.Kalman1DFilter(series, q, r, initX, initP, output, ref budget, initialIsPosterior);
        return err != MathErrorCode.None
            ? CalculatorMatrixFunctions.FailMath(err, position, name)
            : CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
    }

    private static DslEvalOutcome EvalGrangerTest(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || (args.Length >= 3 && args[2].IsArray))
        {
            return FailExpected(position, name, "(x, y[, lagOrder])");
        }

        ReadOnlySpan<decimal> xDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> yDec = arena.GetSlice(args[1].Array);
        if (xDec.Length != yDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int lagOrder = IndicatorDefaultConstants.GrangerCausalityDefaultLagOrder;
        if (args.Length >= 3 &&
            !DslMathValidation.TryReadInt32InRange(
                args[2].Scalar,
                IndicatorDefaultConstants.GrangerCausalityMinLagOrder,
                IndicatorDefaultConstants.GrangerCausalityMaxLagOrder,
                out lagOrder))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = xDec.Length;
        int needed = n * 2;
        double[]? rented = needed <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(needed);
        Span<double> work = rented is null ? stackalloc double[needed] : rented.AsSpan(0, needed);
        try
        {
            Span<double> x = work.Slice(0, n);
            Span<double> y = work.Slice(n, n);
            ToDouble(xDec, x);
            ToDouble(yDec, y);

            Span<double> output = stackalloc double[4];
            MathErrorCode err = DslTimeSeriesMath.GrangerTest(x, y, lagOrder, output, ref budget);
            return err != MathErrorCode.None
                ? CalculatorMatrixFunctions.FailMath(err, position, name)
                : CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalJohansenTest(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(x, y) 1D arrays");
        }

        ReadOnlySpan<decimal> xDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> yDec = arena.GetSlice(args[1].Array);
        if (xDec.Length != yDec.Length)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int n = xDec.Length;
        int needed = n * 2;
        double[]? rented = needed <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(needed);
        Span<double> work = rented is null ? stackalloc double[needed] : rented.AsSpan(0, needed);
        try
        {
            Span<double> x = work.Slice(0, n);
            Span<double> y = work.Slice(n, n);
            ToDouble(xDec, x);
            ToDouble(yDec, y);

            Span<double> output = stackalloc double[3];
            MathErrorCode err = DslTimeSeriesMath.JohansenTest(x, y, output, ref budget);
            return err != MathErrorCode.None
                ? CalculatorMatrixFunctions.FailMath(err, position, name)
                : CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalArimaFit(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return FailExpected(position, name, "(series, p, d, q)");
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 0, DslTimeSeriesMath.MaxArimaOrder, out int p) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, DslTimeSeriesMath.MaxArimaDiffOrder, out int d) ||
            !DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, DslTimeSeriesMath.MaxArimaOrder, out int q) ||
            p + q < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        double[]? rented = n <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(n);
        Span<double> series = rented is null ? stackalloc double[n] : rented.AsSpan(0, n);
        try
        {
            ToDouble(src, series);

            Span<double> output = stackalloc double[p + q + 2];
            MathErrorCode err = DslTimeSeriesMath.ArimaFit(series, p, d, q, output, ref budget);
            return err != MathErrorCode.None
                ? CalculatorMatrixFunctions.FailMath(err, position, name)
                : CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalArimaForecast(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray || args[4].IsArray)
        {
            return FailExpected(position, name, "(series, p, d, q, horizon)");
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 0, DslTimeSeriesMath.MaxArimaOrder, out int p) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, DslTimeSeriesMath.MaxArimaDiffOrder, out int d) ||
            !DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, DslTimeSeriesMath.MaxArimaOrder, out int q) ||
            p + q < 1 ||
            !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 1, DslTimeSeriesMath.MaxArimaForecastHorizon, out int horizon))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        int n = src.Length;
        double[]? rented = n <= MathBufferLimits.StackAllocThreshold ? null : ArrayPool<double>.Shared.Rent(n);
        Span<double> series = rented is null ? stackalloc double[n] : rented.AsSpan(0, n);
        try
        {
            ToDouble(src, series);

            Span<double> output = stackalloc double[horizon * 2];
            MathErrorCode err = DslTimeSeriesMath.ArimaForecast(series, p, d, q, horizon, output, ref budget);
            return err != MathErrorCode.None
                ? CalculatorMatrixFunctions.FailMath(err, position, name)
                : CalculatorMatrixFunctions.NarrowArrayResult(output, position, name, ref arena);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalMvNormLogPdf(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray)
        {
            return FailExpected(position, name, "(x, mu, cov) 1D arrays");
        }

        ReadOnlySpan<decimal> xDec = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> muDec = arena.GetSlice(args[1].Array);
        ReadOnlySpan<decimal> covDec = arena.GetSlice(args[2].Array);
        int k = xDec.Length;
        if (k == 0)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (k > DslLinearAlgebraMath.MaxMatrixDim || muDec.Length != k || covDec.Length != k * k)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        Span<double> x = stackalloc double[k];
        Span<double> mu = stackalloc double[k];
        Span<double> cov = stackalloc double[k * k];
        ToDouble(xDec, x);
        ToDouble(muDec, mu);
        ToDouble(covDec, cov);

        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(
                DslProbabilityDistributionsMath.MvNormLogPdf(x, mu, cov, ref budget),
                position,
                name));
    }
}
