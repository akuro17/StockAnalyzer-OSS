using System;
using System.Globalization;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal static partial class CalculatorArrayFunctions
{
    /// <summary>
    /// <c>arr_where(condArr, trueArr, falseArr)</c>: element-wise selection between two 1D arrays
    /// (<c>cond[i] != 0m ? trueArr[i] : falseArr[i]</c>), pure checked <see cref="decimal"/>.
    /// </summary>
    private static DslEvalOutcome EvaluateArrayWhere(
        int position,
        DslValue condArg,
        DslValue trueArg,
        DslValue falseArg,
        ref DslArrayArena arena)
    {
        const string name = "arr_where";
        if (!condArg.IsArray || !trueArg.IsArray || !falseArg.IsArray)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                position,
                $"'{name}' requires three 1D array arguments (condArr, trueArr, falseArr).",
                name);
        }

        int n = condArg.Array.Length;
        if (trueArg.Array.Length != n || falseArg.Array.Length != n)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                position,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Array length mismatch in '{name}' ({n}, {trueArg.Array.Length}, {falseArg.Array.Length})."),
                name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);
        }

        if (n == 0)
        {
            return DslEvalOutcome.OkArray(handle);
        }

        ReadOnlySpan<decimal> cond = arena.GetSlice(condArg.Array);
        ReadOnlySpan<decimal> tArr = arena.GetSlice(trueArg.Array);
        ReadOnlySpan<decimal> fArr = arena.GetSlice(falseArg.Array);
        for (int i = 0; i < n; i++)
        {
            output[i] = NormalizeZero(cond[i] != 0m ? tArr[i] : fArr[i]);
        }

        return DslEvalOutcome.OkArray(handle);
    }
}
