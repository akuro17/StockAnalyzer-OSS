using System;
using System.Buffers;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for DSL v9 native O(N) array/signal primitives and backward-compatible v10 array extensions
/// (<c>arr_interp_at</c>, <c>vp_value_area</c>).
/// </summary>
internal static partial class CalculatorArrayFunctions
{
    private const int MaxValueWhenOccurrence = 64;

    /// <summary>Dispatches DSL v9 array/signal primitives and v10 waveform/profile extensions by function name.</summary>
    public static bool TryEvaluateV9(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "arr_argwhere":
                outcome = EvalArgWhere(position, name, args[0], ref budget, ref arena);
                return true;

            case "arr_bars_since":
                outcome = EvalBarsSince(position, name, args, ref budget, ref arena);
                return true;

            case "arr_value_when":
                outcome = EvalValueWhen(position, name, args, ref budget, ref arena);
                return true;

            case "arr_ffill":
                outcome = EvalForwardFill(position, name, args, ref budget, ref arena);
                return true;

            case "cross_over":
            case "cross_under":
                outcome = EvalCrossOverOrUnder(position, name, args[0], args[1], isOver: name == "cross_over", ref budget, ref arena);
                return true;

            case "arr_rolling_max":
                outcome = EvalRollingExtreme(position, name, args[0], args[1], args[2], isMax: true, returnBarsSince: false, ref budget, ref arena);
                return true;

            case "arr_rolling_min":
                outcome = EvalRollingExtreme(position, name, args[0], args[1], args[2], isMax: false, returnBarsSince: false, ref budget, ref arena);
                return true;

            case "arr_rolling_bars_since_max":
                outcome = EvalRollingExtreme(position, name, args[0], args[1], args[2], isMax: true, returnBarsSince: true, ref budget, ref arena);
                return true;

            case "arr_rolling_bars_since_min":
                outcome = EvalRollingExtreme(position, name, args[0], args[1], args[2], isMax: false, returnBarsSince: true, ref budget, ref arena);
                return true;

            case "arr_rolling_corr":
                outcome = EvalRollingCorr(position, name, args[0], args[1], args[2], args[3], ref budget, ref arena);
                return true;

            case "align_asof":
                outcome = EvalAlignAsof(position, name, args, ref budget, ref arena);
                return true;

            case "arr_session_range":
                outcome = EvalSessionRange(position, name, args, ref budget, ref arena);
                return true;

            case "pivot_high_causal":
            case "pivot_low_causal":
                outcome = EvalPivotCausal(position, name, args, isHigh: name == "pivot_high_causal", ref budget, ref arena);
                return true;

            case "zigzag_causal":
                outcome = EvalZigzagCausal(position, name, args, ref budget, ref arena);
                return true;

            case "rdp_simplify":
                outcome = EvalRdpSimplify(position, name, args, ref budget, ref arena);
                return true;

            case "pip_extract":
                outcome = EvalPipExtract(position, name, args, ref budget, ref arena);
                return true;

            case "vp_bins":
                outcome = EvalVpBins(position, name, args[0], args[1], args[2], ref budget, ref arena);
                return true;

            case "vp_poc":
                outcome = EvalVpPoc(position, name, args[0], args[1], args[2], ref budget, in arena);
                return true;

            case "dtw_distance":
                outcome = EvalDtwDistance(position, name, args, ref budget, in arena);
                return true;

            case "cusum_filter":
                outcome = EvalCusumFilter(position, name, args[0], args[1], ref budget, ref arena);
                return true;

            case "arr_interp_at":
                outcome = EvalArrInterpAt(position, name, args, ref budget, ref arena);
                return true;

            case "vp_value_area":
                outcome = EvalVpValueArea(position, name, args, ref budget, ref arena);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static int BudgetCostForOps(long ops)
        => Math.Max(1, (int)Math.Min(int.MaxValue, ops / DslExecutionBudget.CheapOperationsPerUnit));

    private static DslEvalOutcome EvalArgWhere(
        int position,
        string name,
        DslValue condArg,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!condArg.IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = condArg.Array.Length;
        if (!budget.TryConsume(BudgetCostForOps(n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        int k = DslEventAndRollingMath.CountNonZero(arena.GetSlice(condArg.Array));
        if (!arena.TryAllocate(k, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (k > 0)
        {
            DslEventAndRollingMath.ArgWhere(arena.GetSlice(condArg.Array), output);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalBarsSince(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasFill = args.Length >= 2;
        if (!args[0].IsArray || (hasFill && args[1].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (!budget.TryConsume(BudgetCostForOps(n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        decimal fill = hasFill ? args[1].Scalar : -1m;
        DslEventAndRollingMath.BarsSince(arena.GetSlice(args[0].Array), fill, output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalValueWhen(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasFill = args.Length >= 4;
        if (!args[0].IsArray || !args[1].IsArray || args[2].IsArray || (hasFill && args[3].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (args[1].Array.Length != n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        if (!DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, MaxValueWhenOccurrence, out int occurrence))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(BudgetCostForOps(n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        decimal fill = hasFill ? args[3].Scalar : 0m;
        DslEventAndRollingMath.ValueWhen(arena.GetSlice(args[0].Array), arena.GetSlice(args[1].Array), occurrence, fill, output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalForwardFill(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasFill = args.Length >= 3;
        if (!args[0].IsArray || !args[1].IsArray || (hasFill && args[2].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (args[1].Array.Length != n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        if (!budget.TryConsume(BudgetCostForOps(n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        decimal fill = hasFill ? args[2].Scalar : 0m;
        DslEventAndRollingMath.ForwardFill(arena.GetSlice(args[0].Array), arena.GetSlice(args[1].Array), fill, output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalCrossOverOrUnder(
        int position,
        string name,
        DslValue leftArg,
        DslValue rightArg,
        bool isOver,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!leftArg.IsArray || !rightArg.IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = leftArg.Array.Length;
        if (rightArg.Array.Length != n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        if (!budget.TryConsume(BudgetCostForOps(n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        DslEventAndRollingMath.CrossOverOrUnder(arena.GetSlice(leftArg.Array), arena.GetSlice(rightArg.Array), isOver, output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalRollingExtreme(
        int position,
        string name,
        DslValue arrayArg,
        DslValue windowArg,
        DslValue fillArg,
        bool isMax,
        bool returnBarsSince,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!arrayArg.IsArray || windowArg.IsArray || fillArg.IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!DslMathValidation.TryReadInt32InRange(windowArg.Scalar, 1, DslArrayArena.MaxArrayLength, out int window))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = arrayArg.Array.Length;
        if (!budget.TryConsume(BudgetCostForOps(2L * n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        DslEventAndRollingMath.RollingExtreme(arena.GetSlice(arrayArg.Array), window, fillArg.Scalar, isMax, returnBarsSince, output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalRollingCorr(
        int position,
        string name,
        DslValue xArg,
        DslValue yArg,
        DslValue windowArg,
        DslValue fillArg,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!xArg.IsArray || !yArg.IsArray || windowArg.IsArray || fillArg.IsArray)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = xArg.Array.Length;
        if (yArg.Array.Length != n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        if (!DslMathValidation.TryReadInt32InRange(windowArg.Scalar, 2, DslArrayArena.MaxArrayLength, out int window))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        long ops = n >= window ? (long)(n - window + 1) * window : 1L;
        if (!budget.TryConsume(BudgetCostForOps(ops)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        MathErrorCode err = DslEventAndRollingMath.RollingCorr(
            arena.GetSlice(xArg.Array),
            arena.GetSlice(yArg.Array),
            window,
            fillArg.Scalar,
            output);
        return err == MathErrorCode.None
            ? DslEvalOutcome.OkArray(handle)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvalAlignAsof(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasFill = args.Length >= 4;
        bool hasAllowExact = args.Length >= 5;
        bool hasMaxTolerance = args.Length >= 6;
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray ||
            (hasFill && args[3].IsArray) ||
            (hasAllowExact && args[4].IsArray) ||
            (hasMaxTolerance && args[5].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        int m = args[1].Array.Length;
        if (args[2].Array.Length != m)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int allowExactInt = 1;
        if (hasAllowExact && !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 0, 1, out allowExactInt))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        decimal maxTolerance = -1m;
        if (hasMaxTolerance)
        {
            maxTolerance = args[5].Scalar;
            if (maxTolerance != -1m && maxTolerance < 0m)
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            }
        }

        ReadOnlySpan<decimal> targetTimes = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> srcTimes = arena.GetSlice(args[1].Array);
        if (!DslTimeAndProfileMath.IsMonotonicNonDecreasing(targetTimes) ||
            !DslTimeAndProfileMath.IsMonotonicNonDecreasing(srcTimes))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(BudgetCostForOps((long)n + m)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        decimal fill = hasFill ? args[3].Scalar : 0m;
        DslTimeAndProfileMath.AlignAsof(
            arena.GetSlice(args[0].Array),
            arena.GetSlice(args[1].Array),
            arena.GetSlice(args[2].Array),
            fill,
            allowExactInt == 1,
            maxTolerance,
            output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalSessionRange(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasFill = args.Length >= 6;
        bool hasSkipFirst = args.Length >= 7;
        if (!args[0].IsArray || !args[1].IsArray || !args[2].IsArray ||
            args[3].IsArray || args[4].IsArray ||
            (hasFill && args[5].IsArray) ||
            (hasSkipFirst && args[6].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (args[1].Array.Length != n || args[2].Array.Length != n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        int skipFirstInt = 0;
        if (!DslMathValidation.TryReadInt32InRange(args[3].Scalar, 1, DslArrayArena.MaxArrayLength, out int openingBars) ||
            !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 0, 1, out int kind) ||
            (hasSkipFirst && !DslMathValidation.TryReadInt32InRange(args[6].Scalar, 0, 1, out skipFirstInt)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> high = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> low = arena.GetSlice(args[1].Array);
        if (!DslTimeAndProfileMath.AreHighLowConsistent(high, low))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(BudgetCostForOps(n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        decimal fill = hasFill ? args[5].Scalar : 0m;
        DslTimeAndProfileMath.SessionRange(
            arena.GetSlice(args[0].Array),
            arena.GetSlice(args[1].Array),
            arena.GetSlice(args[2].Array),
            openingBars,
            kind,
            fill,
            skipFirstInt == 1,
            output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalPivotCausal(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        bool isHigh,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasFill = args.Length >= 4;
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || (hasFill && args[3].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslSwingAndWaveformMath.MaxCausalPivotHalfWindow, out int left) ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 1, DslSwingAndWaveformMath.MaxCausalPivotHalfWindow, out int right))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (!budget.TryConsume(BudgetCostForOps(2L * n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        decimal fill = hasFill ? args[3].Scalar : 0m;
        DslSwingAndWaveformMath.PivotCausal(arena.GetSlice(args[0].Array), left, right, fill, isHigh, output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvalZigzagCausal(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasOutputKind = args.Length >= 5;
        bool hasAtrArg = args.Length >= 6;
        bool hasFill = args.Length >= 7;

        if (!args[0].IsArray || !args[1].IsArray || args[2].IsArray || args[3].IsArray ||
            (hasOutputKind && args[4].IsArray) ||
            (hasAtrArg && !args[5].IsArray) ||
            (hasFill && args[6].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (args[1].Array.Length != n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
        }

        decimal threshold = args[2].Scalar;
        if (threshold <= 0m || !DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, 1, out int mode))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (mode == 0 && threshold >= 100m)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int outputKind = 0;
        if (hasOutputKind && !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 0, 2, out outputKind))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (mode == 1 && !hasAtrArg)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        ReadOnlySpan<decimal> atrSeries = ReadOnlySpan<decimal>.Empty;
        if (hasAtrArg)
        {
            int atrLen = args[5].Array.Length;
            if (mode == 1)
            {
                if (atrLen != n)
                {
                    return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
                }

                atrSeries = arena.GetSlice(args[5].Array);
            }
            else if (atrLen != 0 && atrLen != n)
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
            }
        }

        ReadOnlySpan<decimal> high = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> low = arena.GetSlice(args[1].Array);
        if (!DslSwingAndWaveformMath.ValidateZigzagInputs(high, low, mode, atrSeries))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(BudgetCostForOps(2L * n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        decimal fill = hasFill ? args[6].Scalar : (outputKind == 2 ? -1m : 0m);
        MathErrorCode err = DslSwingAndWaveformMath.ZigzagCausal(
            arena.GetSlice(args[0].Array),
            arena.GetSlice(args[1].Array),
            threshold,
            mode,
            outputKind,
            hasAtrArg && mode == 1 ? arena.GetSlice(args[5].Array) : ReadOnlySpan<decimal>.Empty,
            fill,
            output);
        return err == MathErrorCode.None
            ? DslEvalOutcome.OkArray(handle)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvalRdpSimplify(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasMetric = args.Length >= 3;
        if (!args[0].IsArray || args[1].IsArray || args[1].Scalar < 0m || (hasMetric && args[2].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int metric = 0;
        if (hasMetric && !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, 1, out metric))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (!budget.TryConsume(1))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (n == 0)
        {
            return arena.TryAllocate(0, out DslArrayHandle emptyHandle, out _)
                ? DslEvalOutcome.OkArray(emptyHandle)
                : CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        int[]? rentedIndices = n <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<int>.Shared.Rent(n);
        Span<int> indices = rentedIndices is null
            ? stackalloc int[n]
            : rentedIndices.AsSpan(0, n);

        try
        {
            MathErrorCode err = DslSwingAndWaveformMath.RdpSimplify(
                arena.GetSlice(args[0].Array),
                args[1].Scalar,
                metric,
                indices,
                out int keptCount,
                ref budget);
            if (err != MathErrorCode.None)
            {
                return CalculatorMatrixFunctions.FailMath(err, position, name);
            }

            if (!arena.TryAllocate(keptCount, out DslArrayHandle handle, out Span<decimal> output))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
            }

            for (int i = 0; i < keptCount; i++)
            {
                output[i] = indices[i];
            }

            return DslEvalOutcome.OkArray(handle);
        }
        finally
        {
            if (rentedIndices is not null)
            {
                ArrayPool<int>.Shared.Return(rentedIndices, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalPipExtract(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasMetric = args.Length >= 3;
        if (!args[0].IsArray || args[1].IsArray || (hasMetric && args[2].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        if (n < 2 || !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 2, n, out int k))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (hasMetric && !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, 0, out _))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        if (!budget.TryConsume(1))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(k, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        MathErrorCode err = DslSwingAndWaveformMath.PipExtract(arena.GetSlice(args[0].Array), k, output, ref budget);
        return err == MathErrorCode.None
            ? DslEvalOutcome.OkArray(handle)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private const int MaxVolumeProfileBins = 128;
    private const decimal DefaultValueAreaRatio = 0.70m;

    private static bool TryPrepareVolumeProfileInputs(
        int position,
        string name,
        DslValue pricesArg,
        DslValue volumesArg,
        DslValue nBinsArg,
        DslValue ratioArg,
        bool hasRatio,
        out int nBins,
        out decimal ratio,
        ref DslExecutionBudget budget,
        in DslArrayArena arena,
        out DslEvalOutcome error)
    {
        nBins = 0;
        ratio = DefaultValueAreaRatio;
        if (!pricesArg.IsArray || !volumesArg.IsArray || nBinsArg.IsArray || (hasRatio && ratioArg.IsArray))
        {
            error = CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            return false;
        }

        int n = pricesArg.Array.Length;
        if (volumesArg.Array.Length != n)
        {
            error = CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
            return false;
        }

        if (n < 1 || !DslMathValidation.TryReadInt32InRange(nBinsArg.Scalar, 1, MaxVolumeProfileBins, out nBins))
        {
            error = CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            return false;
        }

        if (hasRatio)
        {
            ratio = ratioArg.Scalar;
            if (ratio <= 0m || ratio > 1m)
            {
                error = CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
                return false;
            }
        }

        if (!DslTimeAndProfileMath.AreVolumesNonNegative(arena.GetSlice(volumesArg.Array)))
        {
            error = CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
            return false;
        }

        if (!budget.TryConsume(BudgetCostForOps((long)n + nBins)))
        {
            error = CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
            return false;
        }

        error = default;
        return true;
    }

    private static DslEvalOutcome EvalVpBins(
        int position,
        string name,
        DslValue pricesArg,
        DslValue volumesArg,
        DslValue nBinsArg,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!TryPrepareVolumeProfileInputs(position, name, pricesArg, volumesArg, nBinsArg, default, hasRatio: false, out int nBins, out _, ref budget, in arena, out DslEvalOutcome error))
        {
            return error;
        }

        if (!arena.TryAllocate(nBins, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        MathErrorCode err = DslTimeAndProfileMath.ComputeVolumeBins(
            arena.GetSlice(pricesArg.Array),
            arena.GetSlice(volumesArg.Array),
            output,
            out _,
            out _,
            out _);
        return err == MathErrorCode.None
            ? DslEvalOutcome.OkArray(handle)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvalVpPoc(
        int position,
        string name,
        DslValue pricesArg,
        DslValue volumesArg,
        DslValue nBinsArg,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!TryPrepareVolumeProfileInputs(position, name, pricesArg, volumesArg, nBinsArg, default, hasRatio: false, out int nBins, out _, ref budget, in arena, out DslEvalOutcome error))
        {
            return error;
        }

        MathErrorCode err = DslTimeAndProfileMath.VolumeProfilePoc(arena.GetSlice(pricesArg.Array), arena.GetSlice(volumesArg.Array), nBins, out decimal poc);
        return err == MathErrorCode.None
            ? DslEvalOutcome.Ok(poc)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvalDtwDistance(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        bool hasBand = args.Length >= 3;
        if (!args[0].IsArray || !args[1].IsArray || (hasBand && args[2].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = args[0].Array.Length;
        int m = args[1].Array.Length;
        if (n < 1 || m < 1)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int maxDim = Math.Max(n, m);
        int band = DtwMath.UnconstrainedRadius;
        if (hasBand && !DslMathValidation.TryReadInt32InRange(args[2].Scalar, DtwMath.UnconstrainedRadius, maxDim, out band))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int minDim = Math.Min(n, m);
        long cells = band < 0
            ? (long)n * m
            : (long)maxDim * Math.Min(minDim, (2L * band) + 1L);
        if (!budget.TryConsume(BudgetCostForOps(cells)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        int totalDoubles = n + m;
        double[]? rented = totalDoubles <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(totalDoubles);
        Span<double> work = rented is null
            ? stackalloc double[totalDoubles]
            : rented.AsSpan(0, totalDoubles);

        try
        {
            Span<double> p = work.Slice(0, n);
            Span<double> q = work.Slice(n, m);
            CalculatorMatrixFunctions.CopyToDouble(arena.GetSlice(args[0].Array), p);
            CalculatorMatrixFunctions.CopyToDouble(arena.GetSlice(args[1].Array), q);

            double dist = DtwMath.Calculate(p, q, band);
            return CalculatorMatrixFunctions.NarrowScalarResult(MathResult<double>.Ok(dist), position, name);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalCusumFilter(
        int position,
        string name,
        DslValue seriesArg,
        DslValue thresholdArg,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        if (!seriesArg.IsArray || thresholdArg.IsArray || thresholdArg.Scalar <= 0m)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int n = seriesArg.Array.Length;
        if (!budget.TryConsume(BudgetCostForOps(n)))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        MathErrorCode err = DslSwingAndWaveformMath.CusumFilter(arena.GetSlice(seriesArg.Array), thresholdArg.Scalar, output);
        return err == MathErrorCode.None
            ? DslEvalOutcome.OkArray(handle)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }

    private static DslEvalOutcome EvalArrInterpAt(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasFill = args.Length >= 3;
        if (!args[0].IsArray || !args[1].IsArray || (hasFill && args[2].IsArray))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int k = args[0].Array.Length;
        int n = args[1].Array.Length;
        if (k > n)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        int[]? rentedIndices = k <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<int>.Shared.Rent(k);
        Span<int> validatedIndices = k == 0
            ? Span<int>.Empty
            : (rentedIndices is null ? stackalloc int[k] : rentedIndices.AsSpan(0, k));

        try
        {
            ReadOnlySpan<decimal> rawIndices = arena.GetSlice(args[0].Array);
            int prevIdx = -1;
            for (int j = 0; j < k; j++)
            {
                if (!DslMathValidation.TryReadInt32InRange(rawIndices[j], 0, n - 1, out int idx) || idx <= prevIdx)
                {
                    return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
                }

                validatedIndices[j] = idx;
                prevIdx = idx;
            }

            if (!budget.TryConsume(BudgetCostForOps(n)))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
            }

            if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
            }

            decimal fill = hasFill ? args[2].Scalar : 0m;
            MathErrorCode err = DslSwingAndWaveformMath.InterpAtIndices(validatedIndices, arena.GetSlice(args[1].Array), fill, output);
            return err == MathErrorCode.None
                ? DslEvalOutcome.OkArray(handle)
                : CalculatorMatrixFunctions.FailMath(err, position, name);
        }
        finally
        {
            if (rentedIndices is not null)
            {
                ArrayPool<int>.Shared.Return(rentedIndices, clearArray: false);
            }
        }
    }

    private static DslEvalOutcome EvalVpValueArea(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena)
    {
        bool hasRatio = args.Length >= 4;
        DslValue ratioArg = hasRatio ? args[3] : default;
        if (!TryPrepareVolumeProfileInputs(position, name, args[0], args[1], args[2], ratioArg, hasRatio, out int nBins, out decimal ratio, ref budget, in arena, out DslEvalOutcome error))
        {
            return error;
        }

        if (!arena.TryAllocate(3, out DslArrayHandle handle, out Span<decimal> output))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        MathErrorCode err = DslTimeAndProfileMath.VolumeProfileValueArea(arena.GetSlice(args[0].Array), arena.GetSlice(args[1].Array), nBins, ratio, output);
        return err == MathErrorCode.None
            ? DslEvalOutcome.OkArray(handle)
            : CalculatorMatrixFunctions.FailMath(err, position, name);
    }
}
