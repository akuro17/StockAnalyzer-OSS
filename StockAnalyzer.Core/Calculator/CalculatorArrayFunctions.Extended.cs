using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// DSL v5 additions: polynomial root arrays (<c>solve_cubic</c>, <c>solve_quartic</c>) and the pure checked-<see cref="decimal"/>
/// 1D array operations (<c>arr_zip_sub</c>, <c>arr_zip_div</c>, <c>arr_scale</c>, <c>arr_cumsum</c>, <c>arr_cumprod</c>,
/// <c>arr_sort</c>, <c>arr_rank</c>, <c>arr_argmax</c>, <c>arr_argmin</c>). No <see cref="double"/> conversion takes place in the array operations.
/// </summary>
internal static partial class CalculatorArrayFunctions
{
    private const int SortAscending = 0;
    private const int SortDescending = 1;

    private static DslEvalOutcome EvaluatePolynomialRoots(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsArray)
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires scalar coefficients.", name);
            }
        }

        Span<decimal> roots = stackalloc decimal[4];
        int rootCount;
        MathErrorCode err = name == "solve_cubic"
            ? DslElementaryMath.SolveCubicDsl(args[0].Scalar, args[1].Scalar, args[2].Scalar, args[3].Scalar, roots, out rootCount)
            : DslElementaryMath.SolveQuarticDsl(args[0].Scalar, args[1].Scalar, args[2].Scalar, args[3].Scalar, args[4].Scalar, roots, out rootCount);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        if (!arena.TryAllocate(rootCount, out DslArrayHandle handle, out Span<decimal> result))
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
        }

        for (int i = 0; i < rootCount; i++)
        {
            result[i] = roots[i] == 0m ? 0m : roots[i];
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateCheckedZip(
        int position,
        string name,
        DslValue leftArg,
        DslValue rightArg,
        bool divide,
        ref DslArrayArena arena)
    {
        if (!leftArg.IsArray || !rightArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires two 1D arrays.", name);
        }

        int length = leftArg.Array.Length;
        if (length != rightArg.Array.Length)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                position,
                $"Array length mismatch in '{name}' ({length} vs {rightArg.Array.Length}).",
                name);
        }

        if (!arena.TryAllocate(length, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> a = arena.GetSlice(leftArg.Array);
        ReadOnlySpan<decimal> b = arena.GetSlice(rightArg.Array);
        try
        {
            for (int i = 0; i < length; i++)
            {
                if (divide && b[i] == 0m)
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.DivisionByZero, position, $"Division by zero in '{name}' at index {i}.", name);
                }

                decimal value = divide ? checked(a[i] / b[i]) : checked(a[i] - b[i]);
                result[i] = value == 0m ? 0m : value;
            }
        }
        catch (OverflowException)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.", name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateScale(int position, string name, DslValue arrayArg, DslValue factorArg, ref DslArrayArena arena)
    {
        if (!arrayArg.IsArray || factorArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires (array, scalar).", name);
        }

        if (!arena.TryAllocate(arrayArg.Array.Length, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(arrayArg.Array);
        try
        {
            for (int i = 0; i < source.Length; i++)
            {
                decimal value = checked(source[i] * factorArg.Scalar);
                result[i] = value == 0m ? 0m : value;
            }
        }
        catch (OverflowException)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.", name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateCumulative(int position, string name, DslValue arrayArg, bool product, ref DslArrayArena arena)
    {
        if (!arrayArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires a 1D array.", name);
        }

        if (!arena.TryAllocate(arrayArg.Array.Length, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(arrayArg.Array);
        try
        {
            decimal running = product ? 1m : 0m;
            for (int i = 0; i < source.Length; i++)
            {
                running = product ? checked(running * source[i]) : checked(running + source[i]);
                result[i] = running == 0m ? 0m : running;
            }
        }
        catch (OverflowException)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.", name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    /// <summary><c>arr_sort(a[, order])</c> and <c>arr_rank(a[, order])</c> with <c>order</c> 0 = ascending (default), 1 = descending.</summary>
    private static DslEvalOutcome EvaluateSortOrRank(int position, string name, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length == 2 && args[1].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires (array[, order]).", name);
        }

        int order = SortAscending;
        if (args.Length == 2 && !DslMathValidation.TryReadInt32InRange(args[1].Scalar, SortAscending, SortDescending, out order))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' order must be 0 (ascending) or 1 (descending).", name);
        }

        int length = args[0].Array.Length;
        if (!arena.TryAllocate(length, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        bool descending = order == SortDescending;
        if (name == "arr_sort")
        {
            source.CopyTo(result);
            result.Sort();
            if (descending)
            {
                result.Reverse();
            }

            return DslEvalOutcome.OkArray(handle);
        }

        // arr_rank: 1-based ranks; a group of equal values shares the mean of the ranks it spans ((first + last) / 2, exact in decimal).
        Span<int> indices = stackalloc int[length];
        for (int i = 0; i < length; i++)
        {
            indices[i] = i;
        }

        // O(n log n), allocation free: the result buffer holds the keys while sorting (it is overwritten with the ranks below). Descending order reverses the
        // ascending order; the order inside a group of equal values is irrelevant because the group shares one rank.
        source.CopyTo(result);
        result.Sort(indices);
        if (descending)
        {
            indices.Reverse();
        }

        int start = 0;
        while (start < length)
        {
            int end = start;
            while (end + 1 < length && source[indices[end + 1]] == source[indices[start]])
            {
                end++;
            }

            decimal rank = 1m + (start + end) / 2m;
            for (int k = start; k <= end; k++)
            {
                result[indices[k]] = rank;
            }

            start = end + 1;
        }

        return DslEvalOutcome.OkArray(handle);
    }

    /// <summary><c>arr_argmax</c> / <c>arr_argmin</c>: smallest 0-based index of the maximum / minimum; an empty array is a domain error.</summary>
    private static DslEvalOutcome EvaluateArgExtreme(int position, string name, DslValue arrayArg, bool findMax, in DslArrayArena arena)
    {
        if (!arrayArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires a 1D array.", name);
        }

        ReadOnlySpan<decimal> values = arena.GetSlice(arrayArg.Array);
        if (values.Length == 0)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' does not accept an empty array.", name);
        }

        int best = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (findMax ? values[i] > values[best] : values[i] < values[best])
            {
                best = i;
            }
        }

        return DslEvalOutcome.Ok(best);
    }
}
