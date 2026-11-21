using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal readonly record struct DslValue(decimal Scalar, DslArrayHandle Array, bool IsArray)
{
    public static DslValue FromScalar(decimal value) => new(value == 0m ? 0m : value, default, false);

    public static DslValue FromArray(DslArrayHandle handle) => new(0m, handle, true);
}

internal readonly record struct DslEvalOutcome(DslValue Value, CalculatorError? Error)
{
    public static DslEvalOutcome Ok(decimal value) => new(DslValue.FromScalar(value), null);

    public static DslEvalOutcome OkArray(DslArrayHandle handle) => new(DslValue.FromArray(handle), null);

    public static DslEvalOutcome Fail(CalculatorErrorCode code, int position, string message, string? token = null)
        => new(default, new CalculatorError(code, position, message, token));
}

/// <summary>
/// Evaluator dispatch for 1D array operations (<c>arr_len</c>, <c>arr_get</c>, <c>arr_slice</c>,
/// <c>arr_window</c>, <c>arr_zip_add</c>, <c>arr_zip_mul</c>), bit-flag inspection (<c>has_flag</c>),
/// and array-argument lowering for existing variadic and statistical functions.
/// </summary>
internal static partial class CalculatorArrayFunctions
{
    public static bool IsArrayOperation(string name) => name is
        "arr_len" or
        "arr_get" or
        "arr_slice" or
        "arr_window" or
        "arr_zip_add" or
        "arr_zip_mul" or
        "has_flag";

    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "arr_len":
                outcome = EvaluateLength(position, args[0], in arena);
                return true;

            case "arr_get":
                outcome = EvaluateGet(position, args[0], args[1], in arena);
                return true;

            case "arr_slice":
                outcome = EvaluateSlice(position, args[0], args[1], args[2], ref arena);
                return true;

            case "arr_window":
                outcome = EvaluateWindow(position, args[0], args[1], ref arena);
                return true;

            case "arr_zip_add":
                outcome = EvaluateZip(position, name, args[0], args[1], isMultiply: false, ref arena);
                return true;

            case "arr_zip_mul":
                outcome = EvaluateZip(position, name, args[0], args[1], isMultiply: true, ref arena);
                return true;

            case "has_flag":
                outcome = EvaluateHasFlag(position, args[0], args[1]);
                return true;

            case "solve_cubic":
            case "solve_quartic":
                outcome = EvaluatePolynomialRoots(position, name, args, ref arena);
                return true;

            case "arr_zip_sub":
                outcome = EvaluateCheckedZip(position, name, args[0], args[1], divide: false, ref arena);
                return true;

            case "arr_zip_div":
                outcome = EvaluateCheckedZip(position, name, args[0], args[1], divide: true, ref arena);
                return true;

            case "arr_scale":
                outcome = EvaluateScale(position, name, args[0], args[1], ref arena);
                return true;

            case "arr_cumsum":
            case "arr_cumprod":
                outcome = EvaluateCumulative(position, name, args[0], product: name == "arr_cumprod", ref arena);
                return true;

            case "arr_sort":
            case "arr_rank":
                outcome = EvaluateSortOrRank(position, name, args, ref arena);
                return true;

            case "arr_argmax":
            case "arr_argmin":
                outcome = EvaluateArgExtreme(position, name, args[0], findMax: name == "arr_argmax", in arena);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    public static bool AcceptsSingleArrayArgument(string name) => name is
        "min" or
        "max" or
        "sum" or
        "avg" or
        "var_p" or
        "var_s" or
        "stdev_p" or
        "stdev_s" or
        "median" or
        "geomean" or
        "harmmean" or
        "skew" or
        "kurt" or
        "iqr" or
        "mad" or
        "softplus_lse";

    public static bool AcceptsScalarPlusArrayArgument(string name) => name is "quantile" or "zscore";

    public static bool ReturnsArray(string name) => name is
        "arr_slice" or
        "arr_window" or
        "arr_zip_add" or
        "arr_zip_mul" or
        "mat_nearest_corr" or
        "mat_cov_to_corr" or
        "ols_fit" or
        "ridge_fit" or
        "pca_project" or
        "acf_series" or
        "pacf_series" or
        "p_adjust" or
        "boot_ci" or
        "diff_lag_series" or
        "fd_pde_1d" or
        "vec_normalize" or
        "cross3d" or
        "mat_transpose" or
        "mat_mul" or
        "mat_solve" or
        "mat_inv" or
        "mat_lstsq" or
        "mat_chol" or
        "mat_qr_q" or
        "mat_qr_r" or
        "mat_eig_sym_vals" or
        "mat_eig_sym_vecs" or
        "mat_svd_s" or
        "mat_svd_u" or
        "mat_svd_vt" or
        "mat_rot2d" or
        "ode_rk4" or
        "ode_rk45" or
        "solve_cubic" or
        "solve_quartic" or
        "opt_nelder_mead" or
        "opt_bfgs" or
        "arr_zip_sub" or
        "arr_zip_div" or
        "arr_scale" or
        "arr_cumsum" or
        "arr_cumprod" or
        "arr_sort" or
        "arr_rank" or
        "ttest_1samp" or
        "ttest_2samp" or
        "jb_test" or
        "ljung_box" or
        "ks_2samp" or
        "dft_mag";

    public static DslEvalOutcome EvaluateArrayLoweredVariadic(
        string name,
        int position,
        CalculatorFunctionInfo info,
        scoped ReadOnlySpan<DslValue> args,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (AcceptsScalarPlusArrayArgument(name))
        {
            if (args.Length != 2 || args[0].IsArray || !args[1].IsArray)
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.WrongArgumentCount,
                    position,
                    $"Function '{name}' expects {info.MinArguments}..{info.MaxArguments} scalar arguments or (scalar, array).",
                    name);
            }

            ReadOnlySpan<decimal> sample = arena.GetSlice(args[1].Array);
            if (sample.Length < 2)
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    position,
                    $"Function '{name}' requires an array of at least 2 elements.",
                    name);
            }

            MathResult<decimal> statRes = name == "quantile"
                ? DslStatisticsMath.Quantile(args[0].Scalar, sample)
                : DslStatisticsMath.ZScore(args[0].Scalar, sample);
            return ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(statRes, position, name));
        }

        if (args.Length != 1 || !args[0].IsArray)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.WrongArgumentCount,
                position,
                $"Function '{name}' expects {info.MinArguments}..{info.MaxArguments} scalar arguments or a single 1D array.",
                name);
        }

        ReadOnlySpan<decimal> values = arena.GetSlice(args[0].Array);
        int requiredLength = name is "min" or "max" or "sum" or "avg" ? 1 : info.MinArguments;
        if (values.Length < requiredLength)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                position,
                $"Function '{name}' requires an array of at least {requiredLength} element(s).",
                name);
        }

        CalculatorOutcome scalarOutcome = CalculatorEvaluator.EvaluateScalarSliceCall(name, position, values, limits, ref budget);
        return ToEvalOutcome(scalarOutcome);
    }

    internal static DslEvalOutcome ToEvalOutcome(CalculatorOutcome outcome)
        => outcome.Error is { } err
            ? new DslEvalOutcome(default, err)
            : DslEvalOutcome.Ok(outcome.Value!.Value);

    private static DslEvalOutcome EvaluateLength(int position, DslValue arrayArg, in DslArrayArena arena)
    {
        if (!arrayArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "arr_len requires a 1D array argument.", "arr_len");
        }

        return DslEvalOutcome.Ok(arena.GetSlice(arrayArg.Array).Length);
    }

    private static DslEvalOutcome EvaluateGet(int position, DslValue arrayArg, DslValue indexArg, in DslArrayArena arena)
    {
        if (!arrayArg.IsArray || indexArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "arr_get requires (array, index).", "arr_get");
        }

        ReadOnlySpan<decimal> slice = arena.GetSlice(arrayArg.Array);
        if (slice.Length == 0 || !DslMathValidation.TryReadInt32InRange(indexArg.Scalar, 0, slice.Length - 1, out int idx))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "Array index out of bounds in 'arr_get'.", "arr_get");
        }

        return DslEvalOutcome.Ok(slice[idx]);
    }

    private static DslEvalOutcome EvaluateSlice(
        int position,
        DslValue arrayArg,
        DslValue startArg,
        DslValue lengthArg,
        ref DslArrayArena arena)
    {
        if (!arrayArg.IsArray || startArg.IsArray || lengthArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "arr_slice requires (array, start, len).", "arr_slice");
        }

        int sourceLen = arrayArg.Array.Length;
        if (!DslMathValidation.TryReadInt32InRange(startArg.Scalar, 0, sourceLen, out int start) ||
            !DslMathValidation.TryReadInt32InRange(lengthArg.Scalar, 0, sourceLen - start, out int len))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "Slice range out of bounds in 'arr_slice'.", "arr_slice");
        }

        if (!arena.TryAllocate(len, out DslArrayHandle outHandle, out Span<decimal> outSlice))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, "Array arena budget exceeded in 'arr_slice'.", "arr_slice");
        }

        arena.GetSlice(arrayArg.Array).Slice(start, len).CopyTo(outSlice);
        return DslEvalOutcome.OkArray(outHandle);
    }

    private static DslEvalOutcome EvaluateWindow(
        int position,
        DslValue seriesArg,
        DslValue periodArg,
        ref DslArrayArena arena)
    {
        if (!seriesArg.IsArray || periodArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "arr_window requires (series, period).", "arr_window");
        }

        int sourceLen = seriesArg.Array.Length;
        if (sourceLen == 0 || !DslMathValidation.TryReadInt32InRange(periodArg.Scalar, 1, sourceLen, out int period))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "Period out of bounds in 'arr_window'.", "arr_window");
        }

        if (!arena.TryAllocate(period, out DslArrayHandle outHandle, out Span<decimal> outSlice))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, "Array arena budget exceeded in 'arr_window'.", "arr_window");
        }

        arena.GetSlice(seriesArg.Array).Slice(sourceLen - period, period).CopyTo(outSlice);
        return DslEvalOutcome.OkArray(outHandle);
    }

    private static DslEvalOutcome EvaluateZip(
        int position,
        string name,
        DslValue leftArg,
        DslValue rightArg,
        bool isMultiply,
        ref DslArrayArena arena)
    {
        if (!leftArg.IsArray || !rightArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires two 1D arrays.", name);
        }

        int lenA = leftArg.Array.Length;
        int lenB = rightArg.Array.Length;
        if (lenA == 0 || lenB == 0)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' does not accept empty arrays.", name);
        }

        if (lenA != lenB)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DimensionMismatch, position, $"Array length mismatch in '{name}' ({lenA} vs {lenB}).", name);
        }

        if (!arena.TryAllocate(lenA, out DslArrayHandle outHandle, out Span<decimal> outSlice))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> a = arena.GetSlice(leftArg.Array);
        ReadOnlySpan<decimal> b = arena.GetSlice(rightArg.Array);
        try
        {
            for (int i = 0; i < lenA; i++)
            {
                decimal val = isMultiply ? checked(a[i] * b[i]) : checked(a[i] + b[i]);
                outSlice[i] = val == 0m ? 0m : val;
            }
        }
        catch (OverflowException)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.");
        }

        return DslEvalOutcome.OkArray(outHandle);
    }

    private static DslEvalOutcome EvaluateHasFlag(int position, DslValue flagsArg, DslValue bitIndexArg)
    {
        if (flagsArg.IsArray || bitIndexArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "has_flag requires scalar integer arguments.", "has_flag");
        }

        if (!DslMathValidation.TryReadInt32InRange(flagsArg.Scalar, 0, ushort.MaxValue, out int flags) ||
            !DslMathValidation.TryReadInt32InRange(bitIndexArg.Scalar, 0, 15, out int bitIndex))
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                position,
                "has_flag requires integer flags in [0, 65535] and bitIndex in [0, 15].",
                "has_flag");
        }

        return DslEvalOutcome.Ok((flags & (1 << bitIndex)) != 0 ? 1m : 0m);
    }
}
