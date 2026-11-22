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

    // ---- DSL v6 Domain 3a: Pure Checked-Decimal Array/Matrix Generators & Utilities ----

    private static DslEvalOutcome EvaluateLinspace(int position, string name, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (args[0].IsArray || args[1].IsArray || args[2].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, 0, DslArrayArena.MaxArrayLength, out int n))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (start, stop, n) with integer n in [0, {DslArrayArena.MaxArrayLength}].", name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        if (n == 0)
        {
            return DslEvalOutcome.OkArray(handle);
        }

        decimal start = args[0].Scalar;
        decimal stop = args[1].Scalar;
        result[0] = start == 0m ? 0m : start;
        if (n == 1)
        {
            return DslEvalOutcome.OkArray(handle);
        }

        try
        {
            decimal diff = checked(stop - start);
            int denom = n - 1;
            bool usePreDividedStep = Math.Abs(diff) > 1e25m;
            decimal step = usePreDividedStep ? diff / denom : 0m;
            for (int k = 1; k < n - 1; k++)
            {
                decimal val = usePreDividedStep
                    ? checked(start + step * k)
                    : checked(start + (diff * k) / denom);
                result[k] = val == 0m ? 0m : val;
            }

            result[n - 1] = stop == 0m ? 0m : stop;
        }
        catch (OverflowException)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.", name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateArange(int position, string name, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (args[0].IsArray || args[1].IsArray || (args.Length >= 3 && args[2].IsArray))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects scalar arguments (start, stop[, step]).", name);
        }

        decimal start = args[0].Scalar;
        decimal stop = args[1].Scalar;
        decimal step = args.Length >= 3 ? args[2].Scalar : 1m;
        if (step == 0m)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' step must not be zero.", name);
        }

        if ((step > 0m && start >= stop) || (step < 0m && start <= stop))
        {
            if (!arena.TryAllocate(0, out DslArrayHandle emptyHandle, out _))
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
            }

            return DslEvalOutcome.OkArray(emptyHandle);
        }

        Span<decimal> buffer = stackalloc decimal[DslArrayArena.MaxArrayLength];
        int count = 0;
        try
        {
            decimal current = start;
            while (step > 0m ? current < stop : current > stop)
            {
                if (count >= DslArrayArena.MaxArrayLength)
                {
                    return DslEvalOutcome.Fail(
                        CalculatorErrorCode.DomainError,
                        position,
                        $"'{name}' output length exceeds {DslArrayArena.MaxArrayLength}.",
                        name);
                }

                buffer[count++] = current == 0m ? 0m : current;
                current = checked(start + count * step);
            }
        }
        catch (OverflowException)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.", name);
        }

        if (!arena.TryAllocate(count, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        buffer.Slice(0, count).CopyTo(result);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateArrayFill(int position, string name, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (args[0].IsArray || (args.Length >= 2 && args[1].IsArray) ||
            !DslMathValidation.TryReadInt32InRange(args[0].Scalar, 0, DslArrayArena.MaxArrayLength, out int n))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects integer length n in [0, {DslArrayArena.MaxArrayLength}].", name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        decimal fillVal = name switch
        {
            "arr_zeros" => 0m,
            "arr_ones" => 1m,
            _ => args[1].Scalar == 0m ? 0m : args[1].Scalar,
        };
        result.Fill(fillVal);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateMatEye(int position, string name, DslValue dimArg, ref DslArrayArena arena)
    {
        int maxSquareDim = CalculatorMatrixFunctions.MaxSquareArrayMatrixDim;
        if (dimArg.IsArray || !DslMathValidation.TryReadInt32InRange(dimArg.Scalar, 1, maxSquareDim, out int n))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires integer dimension n in [1, {maxSquareDim}].", name);
        }

        if (!arena.TryAllocate(n * n, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        result.Clear();
        for (int i = 0; i < n; i++)
        {
            result[i * n + i] = 1m;
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateMatDiag(int position, string name, DslValue vecArg, ref DslArrayArena arena)
    {
        int maxSquareDim = CalculatorMatrixFunctions.MaxSquareArrayMatrixDim;
        if (!vecArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires a 1D vector argument.", name);
        }

        int n = vecArg.Array.Length;
        if (n == 0)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' does not accept an empty vector.", name);
        }

        if (n > maxSquareDim)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DimensionMismatch, position, $"'{name}' vector length {n} exceeds {maxSquareDim}.", name);
        }

        if (!arena.TryAllocate(n * n, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(vecArg.Array);
        result.Clear();
        for (int i = 0; i < n; i++)
        {
            result[i * n + i] = src[i] == 0m ? 0m : src[i];
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateArrayConcat(int position, string name, DslValue leftArg, DslValue rightArg, ref DslArrayArena arena)
    {
        if (!leftArg.IsArray || !rightArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires two 1D arrays.", name);
        }

        int totalLen = leftArg.Array.Length + rightArg.Array.Length;
        if (totalLen > DslArrayArena.MaxArrayLength)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                position,
                $"Concatenated array length {totalLen} exceeds {DslArrayArena.MaxArrayLength}.",
                name);
        }

        if (!arena.TryAllocate(totalLen, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> a = arena.GetSlice(leftArg.Array);
        ReadOnlySpan<decimal> b = arena.GetSlice(rightArg.Array);
        a.CopyTo(result);
        b.CopyTo(result.Slice(a.Length));
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateArrayReverse(int position, string name, DslValue arrayArg, ref DslArrayArena arena)
    {
        if (!arrayArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires a 1D array.", name);
        }

        int len = arrayArg.Array.Length;
        if (!arena.TryAllocate(len, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        arena.GetSlice(arrayArg.Array).CopyTo(result);
        result.Reverse();
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateArrayUnique(int position, string name, DslValue arrayArg, ref DslArrayArena arena)
    {
        if (!arrayArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires a 1D array.", name);
        }

        int len = arrayArg.Array.Length;
        if (len == 0)
        {
            if (!arena.TryAllocate(0, out DslArrayHandle emptyHandle, out _))
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
            }

            return DslEvalOutcome.OkArray(emptyHandle);
        }

        Span<decimal> sorted = stackalloc decimal[len];
        arena.GetSlice(arrayArg.Array).CopyTo(sorted);
        sorted.Sort();

        int uniqueCount = 0;
        for (int i = 0; i < len; i++)
        {
            decimal v = sorted[i] == 0m ? 0m : sorted[i];
            if (uniqueCount == 0 || v != sorted[uniqueCount - 1])
            {
                sorted[uniqueCount++] = v;
            }
        }

        if (!arena.TryAllocate(uniqueCount, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        sorted.Slice(0, uniqueCount).CopyTo(result);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateArrayClip(int position, string name, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (array, lo, hi).", name);
        }

        decimal lo = args[1].Scalar;
        decimal hi = args[2].Scalar;
        if (lo > hi)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires lo <= hi.", name);
        }

        int len = args[0].Array.Length;
        if (!arena.TryAllocate(len, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(args[0].Array);
        for (int i = 0; i < len; i++)
        {
            decimal clamped = Math.Min(Math.Max(src[i], lo), hi);
            result[i] = clamped == 0m ? 0m : clamped;
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateArrayAddScalar(int position, string name, DslValue arrayArg, DslValue scalarArg, ref DslArrayArena arena)
    {
        if (!arrayArg.IsArray || scalarArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (array, scalar).", name);
        }

        int len = arrayArg.Array.Length;
        if (!arena.TryAllocate(len, out DslArrayHandle handle, out Span<decimal> result))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        ReadOnlySpan<decimal> src = arena.GetSlice(arrayArg.Array);
        decimal c = scalarArg.Scalar;
        try
        {
            for (int i = 0; i < len; i++)
            {
                decimal val = checked(src[i] + c);
                result[i] = val == 0m ? 0m : val;
            }
        }
        catch (OverflowException)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.", name);
        }

        return DslEvalOutcome.OkArray(handle);
    }
}
