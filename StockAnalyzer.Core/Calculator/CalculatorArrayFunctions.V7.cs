using System;
using System.Globalization;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// DSL v7 shared output-normalization helpers (single source of truth for <c>-0m</c> to <c>0m</c> across the v7 array and matrix functions).
/// </summary>
internal static partial class CalculatorArrayFunctions
{
    /// <summary>
    /// Maps negative zero to <c>0m</c>; every other value is returned unchanged.
    /// Forwards to <see cref="DslElementaryMath.NormalizeZero(decimal)"/>, which owns the single definition of this rule.
    /// </summary>
    internal static decimal NormalizeZero(decimal value) => DslElementaryMath.NormalizeZero(value);

    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/> element by element, normalizing <c>-0m</c> to <c>0m</c>.</summary>
    internal static void CopyNormalized(ReadOnlySpan<decimal> source, Span<decimal> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = NormalizeZero(source[i]);
        }
    }

    /// <summary><c>arr_set(a, idx, val)</c>: non-destructive copy of <c>a</c> with element <c>idx</c> replaced by <c>val</c>.</summary>
    private static DslEvalOutcome EvaluateArraySet(
        int position,
        DslValue arrayArg,
        DslValue indexArg,
        DslValue valueArg,
        ref DslArrayArena arena)
    {
        const string name = "arr_set";
        if (!arrayArg.IsArray || indexArg.IsArray || valueArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "arr_set requires (array, index, value).", name);
        }

        int length = arrayArg.Array.Length;
        if (length == 0 || !DslMathValidation.TryReadInt32InRange(indexArg.Scalar, 0, length - 1, out int index))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "Array index out of bounds in 'arr_set'.", name);
        }

        if (!arena.TryAllocate(length, out DslArrayHandle handle, out Span<decimal> output))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, "Array arena budget exceeded in 'arr_set'.", name);
        }

        CopyNormalized(arena.GetSlice(arrayArg.Array), output);
        output[index] = NormalizeZero(valueArg.Scalar);
        return DslEvalOutcome.OkArray(handle);
    }

    /// <summary><c>arr_take(a, indices)</c>: gathers <c>a[indices[k]]</c>; duplicate indices are allowed.</summary>
    private static DslEvalOutcome EvaluateArrayTake(
        int position,
        DslValue arrayArg,
        DslValue indicesArg,
        ref DslArrayArena arena)
    {
        const string name = "arr_take";
        if (!arrayArg.IsArray || !indicesArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "arr_take requires (array, indices).", name);
        }

        int outputLength = indicesArg.Array.Length;
        if (outputLength == 0)
        {
            return arena.TryAllocate(0, out DslArrayHandle emptyHandle, out _)
                ? DslEvalOutcome.OkArray(emptyHandle)
                : DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, "Array arena budget exceeded in 'arr_take'.", name);
        }

        int sourceLength = arrayArg.Array.Length;
        if (sourceLength == 0)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "Array index out of bounds in 'arr_take'.", name);
        }

        int[]? rented = outputLength <= MathBufferLimits.StackAllocThreshold
            ? null
            : System.Buffers.ArrayPool<int>.Shared.Rent(outputLength);
        Span<int> validated = rented is null
            ? stackalloc int[outputLength]
            : rented.AsSpan(0, outputLength);
        try
        {
            ReadOnlySpan<decimal> indices = arena.GetSlice(indicesArg.Array);
            for (int k = 0; k < outputLength; k++)
            {
                if (!DslMathValidation.TryReadInt32InRange(indices[k], 0, sourceLength - 1, out validated[k]))
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "Array index out of bounds in 'arr_take'.", name);
                }
            }

            if (!arena.TryAllocate(outputLength, out DslArrayHandle handle, out Span<decimal> output))
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, "Array arena budget exceeded in 'arr_take'.", name);
            }

            ReadOnlySpan<decimal> source = arena.GetSlice(arrayArg.Array);
            for (int k = 0; k < outputLength; k++)
            {
                output[k] = NormalizeZero(source[validated[k]]);
            }

            return DslEvalOutcome.OkArray(handle);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<int>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    /// <summary>
    /// <c>arr_shift(a, k, fill)</c>: length-preserving shift. <c>k &gt; 0</c> lags (leading <c>fill</c>), <c>k &lt; 0</c> leads (trailing <c>fill</c>).
    /// <c>k</c> is validated before the empty-array shortcut.
    /// </summary>
    private static DslEvalOutcome EvaluateArrayShift(
        int position,
        DslValue arrayArg,
        DslValue shiftArg,
        DslValue fillArg,
        ref DslArrayArena arena)
    {
        const string name = "arr_shift";
        if (!arrayArg.IsArray || shiftArg.IsArray || fillArg.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, "arr_shift requires (array, k, fill).", name);
        }

        if (!DslMathValidation.TryReadInt32InRange(shiftArg.Scalar, -DslArrayArena.MaxArrayLength, DslArrayArena.MaxArrayLength, out int shift))
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                position,
                string.Create(CultureInfo.InvariantCulture, $"Shift k must be an integer in [-{DslArrayArena.MaxArrayLength}, {DslArrayArena.MaxArrayLength}] in 'arr_shift'."),
                name);
        }

        int length = arrayArg.Array.Length;
        if (!arena.TryAllocate(length, out DslArrayHandle handle, out Span<decimal> output))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, "Array arena budget exceeded in 'arr_shift'.", name);
        }

        if (length == 0)
        {
            return DslEvalOutcome.OkArray(handle);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(arrayArg.Array);
        decimal fill = NormalizeZero(fillArg.Scalar);
        if (shift == 0)
        {
            CopyNormalized(source, output);
        }
        else if (shift > 0)
        {
            int lagged = Math.Min(shift, length);
            output.Slice(0, lagged).Fill(fill);
            if (lagged < length)
            {
                CopyNormalized(source.Slice(0, length - lagged), output.Slice(lagged));
            }
        }
        else
        {
            int lead = Math.Min(-shift, length);
            int kept = length - lead;
            if (kept > 0)
            {
                CopyNormalized(source.Slice(lead), output.Slice(0, kept));
            }

            output.Slice(kept).Fill(fill);
        }

        return DslEvalOutcome.OkArray(handle);
    }
}
