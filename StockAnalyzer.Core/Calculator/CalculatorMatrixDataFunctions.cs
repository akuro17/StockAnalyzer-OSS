using System;
using System.Globalization;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// DSL v7 row-major 2D matrix data operations on 1D arrays: extraction (<c>mat_get</c>, <c>mat_row</c>, <c>mat_col</c>,
/// <c>mat_diag_extract</c>, <c>mat_submatrix</c>), construction (<c>mat_hstack</c>, <c>mat_set</c>, <c>mat_transpose_rect</c>) and
/// shape-validated arithmetic (<c>mat_add</c>, <c>mat_sub</c>, <c>mat_scale</c>). Pure checked <see cref="decimal"/>; no <see cref="double"/> path.
/// Element (r, c) of a <c>rows x cols</c> matrix is stored at index <c>r * cols + c</c>.
/// </summary>
/// <remarks>
/// Validation order (all functions taking rows/cols): (1) argument kinds (arrays vs scalars) -> DomainError;
/// (2) rows, cols integers in [1, <see cref="DslArrayArena.MaxArrayLength"/>] -> DomainError;
/// (3) rows * cols within the array bound and every matrix length equal to rows * cols -> DimensionMismatch;
/// (4) coordinate / span ranges -> DomainError.
/// </remarks>
internal static class CalculatorMatrixDataFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "mat_get":
                outcome = EvaluateGet(position, args, in arena);
                return true;

            case "mat_row":
                outcome = EvaluateRow(position, args, ref arena);
                return true;

            case "mat_col":
                outcome = EvaluateColumn(position, args, ref arena);
                return true;

            case "mat_diag_extract":
                outcome = EvaluateDiagonalExtract(position, args, ref arena);
                return true;

            case "mat_submatrix":
                outcome = EvaluateSubmatrix(position, args, ref arena);
                return true;

            case "mat_hstack":
                outcome = EvaluateHorizontalStack(position, args, ref arena);
                return true;

            case "mat_set":
                outcome = EvaluateSet(position, args, ref arena);
                return true;

            case "mat_transpose_rect":
                outcome = EvaluateTransposeRect(position, args, ref arena);
                return true;

            case "mat_add":
                outcome = EvaluateAddOrSubtract(position, name, args, subtract: false, ref arena);
                return true;

            case "mat_sub":
                outcome = EvaluateAddOrSubtract(position, name, args, subtract: true, ref arena);
                return true;

            case "mat_scale":
                outcome = EvaluateScale(position, args, ref arena);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static DslEvalOutcome EvaluateGet(int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        const string name = "mat_get";
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray || args[4].IsArray)
        {
            return KindError(position, name, "(A, rows, cols, r, c) with A an array and the rest scalars");
        }

        if (!TryValidateShape(position, name, args[1], args[2], args[0].Array.Length, -1, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        if (!DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, rows - 1, out int ri) ||
            !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 0, cols - 1, out int ci))
        {
            return RangeError(position, name, "Row or column index out of range");
        }

        return DslEvalOutcome.Ok(CalculatorArrayFunctions.NormalizeZero(arena.GetSlice(args[0].Array)[ri * cols + ci]));
    }

    private static DslEvalOutcome EvaluateRow(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_row";
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return KindError(position, name, "(A, rows, cols, r) with A an array and the rest scalars");
        }

        if (!TryValidateShape(position, name, args[1], args[2], args[0].Array.Length, -1, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        if (!DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, rows - 1, out int ri))
        {
            return RangeError(position, name, "Row index out of range");
        }

        if (!arena.TryAllocate(cols, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        CalculatorArrayFunctions.CopyNormalized(arena.GetSlice(args[0].Array).Slice(ri * cols, cols), output);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateColumn(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_col";
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return KindError(position, name, "(A, rows, cols, c) with A an array and the rest scalars");
        }

        if (!TryValidateShape(position, name, args[1], args[2], args[0].Array.Length, -1, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        if (!DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, cols - 1, out int ci))
        {
            return RangeError(position, name, "Column index out of range");
        }

        if (!arena.TryAllocate(rows, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        for (int i = 0; i < rows; i++)
        {
            output[i] = CalculatorArrayFunctions.NormalizeZero(source[i * cols + ci]);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateDiagonalExtract(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_diag_extract";
        if (!args[0].IsArray || args[1].IsArray)
        {
            return KindError(position, name, "(A, n) with A an array and n a scalar");
        }

        if (!DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, CalculatorMatrixFunctions.MaxSquareArrayMatrixDim, out int n))
        {
            return RangeError(position, name, string.Create(CultureInfo.InvariantCulture, $"n must be an integer in [1, {CalculatorMatrixFunctions.MaxSquareArrayMatrixDim}]"));
        }

        if (args[0].Array.Length != n * n)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                position,
                string.Create(CultureInfo.InvariantCulture, $"'{name}' expects an array of length n*n ({n * n}), got {args[0].Array.Length}."),
                name);
        }

        if (!arena.TryAllocate(n, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        for (int i = 0; i < n; i++)
        {
            output[i] = CalculatorArrayFunctions.NormalizeZero(source[i * n + i]);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateSubmatrix(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_submatrix";
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray || args[4].IsArray || args[5].IsArray || args[6].IsArray)
        {
            return KindError(position, name, "(A, rows, cols, r0, c0, rLen, cLen) with A an array and the rest scalars");
        }

        if (!TryValidateShape(position, name, args[1], args[2], args[0].Array.Length, -1, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        if (!DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, rows - 1, out int r0) ||
            !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 0, cols - 1, out int c0) ||
            !DslMathValidation.TryReadInt32InRange(args[5].Scalar, 1, rows - r0, out int rLen) ||
            !DslMathValidation.TryReadInt32InRange(args[6].Scalar, 1, cols - c0, out int cLen))
        {
            return RangeError(position, name, "Submatrix origin or extent out of range (rLen and cLen must be at least 1)");
        }

        if (!arena.TryAllocate(rLen * cLen, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        for (int i = 0; i < rLen; i++)
        {
            CalculatorArrayFunctions.CopyNormalized(
                source.Slice((r0 + i) * cols + c0, cLen),
                output.Slice(i * cLen, cLen));
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateHorizontalStack(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_hstack";
        int k = args.Length;
        for (int j = 0; j < k; j++)
        {
            if (!args[j].IsArray)
            {
                return KindError(position, name, "column vectors (1D arrays) only");
            }
        }

        for (int j = 0; j < k; j++)
        {
            if (args[j].Array.Length == 0)
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' does not accept empty columns.", name);
            }
        }

        int n = args[0].Array.Length;
        for (int j = 1; j < k; j++)
        {
            if (args[j].Array.Length != n)
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.DimensionMismatch,
                    position,
                    string.Create(CultureInfo.InvariantCulture, $"Column length mismatch in '{name}' ({n} vs {args[j].Array.Length})."),
                    name);
            }
        }

        if ((long)n * k > DslArrayArena.MaxArrayLength)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                position,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' result would exceed {DslArrayArena.MaxArrayLength} elements (n={n}, k={k}, n*k={(long)n * k})."),
                name);
        }

        if (!arena.TryAllocate(n * k, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        for (int j = 0; j < k; j++)
        {
            ReadOnlySpan<decimal> column = arena.GetSlice(args[j].Array);
            for (int i = 0; i < n; i++)
            {
                output[i * k + j] = CalculatorArrayFunctions.NormalizeZero(column[i]);
            }
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateSet(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_set";
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray || args[4].IsArray || args[5].IsArray)
        {
            return KindError(position, name, "(A, rows, cols, r, c, val) with A an array and the rest scalars");
        }

        if (!TryValidateShape(position, name, args[1], args[2], args[0].Array.Length, -1, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        if (!DslMathValidation.TryReadInt32InRange(args[3].Scalar, 0, rows - 1, out int ri) ||
            !DslMathValidation.TryReadInt32InRange(args[4].Scalar, 0, cols - 1, out int ci))
        {
            return RangeError(position, name, "Row or column index out of range");
        }

        if (!arena.TryAllocate(rows * cols, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        CalculatorArrayFunctions.CopyNormalized(arena.GetSlice(args[0].Array), output);
        output[ri * cols + ci] = CalculatorArrayFunctions.NormalizeZero(args[5].Scalar);
        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateTransposeRect(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_transpose_rect";
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray)
        {
            return KindError(position, name, "(A, rows, cols) with A an array and the rest scalars");
        }

        if (!TryValidateShape(position, name, args[1], args[2], args[0].Array.Length, -1, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        if (!arena.TryAllocate(rows * cols, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                output[j * rows + i] = CalculatorArrayFunctions.NormalizeZero(source[i * cols + j]);
            }
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateAddOrSubtract(
        int position,
        string name,
        scoped ReadOnlySpan<DslValue> args,
        bool subtract,
        ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return KindError(position, name, "(A, B, rows, cols) with A and B arrays and rows and cols scalars");
        }

        if (!TryValidateShape(position, name, args[2], args[3], args[0].Array.Length, args[1].Array.Length, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        int length = rows * cols;
        if (!arena.TryAllocate(length, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        ReadOnlySpan<decimal> left = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> right = arena.GetSlice(args[1].Array);
        try
        {
            for (int i = 0; i < length; i++)
            {
                decimal value = subtract ? checked(left[i] - right[i]) : checked(left[i] + right[i]);
                output[i] = CalculatorArrayFunctions.NormalizeZero(value);
            }
        }
        catch (OverflowException)
        {
            return OverflowError(position, name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateScale(int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        const string name = "mat_scale";
        if (!args[0].IsArray || args[1].IsArray || args[2].IsArray || args[3].IsArray)
        {
            return KindError(position, name, "(A, rows, cols, c) with A an array and the rest scalars");
        }

        if (!TryValidateShape(position, name, args[1], args[2], args[0].Array.Length, -1, out int rows, out int cols, out DslEvalOutcome shapeError))
        {
            return shapeError;
        }

        int length = rows * cols;
        if (!arena.TryAllocate(length, out DslArrayHandle handle, out Span<decimal> output))
        {
            return BudgetError(position, name);
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        decimal factor = args[3].Scalar;
        try
        {
            for (int i = 0; i < length; i++)
            {
                output[i] = CalculatorArrayFunctions.NormalizeZero(checked(source[i] * factor));
            }
        }
        catch (OverflowException)
        {
            return OverflowError(position, name);
        }

        return DslEvalOutcome.OkArray(handle);
    }

    /// <summary>
    /// Stages 2 and 3 of the matrix validation contract: integer <c>rows</c>/<c>cols</c> in [1, 512] (DomainError), then
    /// <c>rows * cols</c> within the array bound and every supplied array length equal to <c>rows * cols</c> (DimensionMismatch).
    /// Pass <c>-1</c> for <paramref name="secondLength"/> when only one array is validated.
    /// </summary>
    private static bool TryValidateShape(
        int position,
        string name,
        DslValue rowsArg,
        DslValue colsArg,
        int firstLength,
        int secondLength,
        out int rows,
        out int cols,
        out DslEvalOutcome error)
    {
        if (!DslMathValidation.TryReadInt32InRange(rowsArg.Scalar, 1, DslArrayArena.MaxArrayLength, out rows) ||
            !DslMathValidation.TryReadInt32InRange(colsArg.Scalar, 1, DslArrayArena.MaxArrayLength, out cols))
        {
            rows = 0;
            cols = 0;
            error = DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                position,
                string.Create(CultureInfo.InvariantCulture, $"'{name}' requires integer rows and cols in [1, {DslArrayArena.MaxArrayLength}]."),
                name);
            return false;
        }

        long product = (long)rows * cols;
        if (product > DslArrayArena.MaxArrayLength || firstLength != product || (secondLength >= 0 && secondLength != product))
        {
            error = DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                position,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' requires rows*cols <= {DslArrayArena.MaxArrayLength} and array length equal to rows*cols (rows={rows}, cols={cols}, rows*cols={product}, array length={firstLength}{DescribeSecondLength(secondLength)})."),
                name);
            return false;
        }

        error = default;
        return true;
    }

    /// <summary>Failure path only: the second array's length for <c>mat_add</c>/<c>mat_sub</c>, empty when only one array is validated.</summary>
    private static string DescribeSecondLength(int secondLength)
        => secondLength >= 0 ? string.Create(CultureInfo.InvariantCulture, $" and {secondLength}") : string.Empty;

    private static DslEvalOutcome KindError(int position, string name, string expected)
        => DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' requires {expected}.", name);

    private static DslEvalOutcome RangeError(int position, string name, string detail)
        => DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"{detail} in '{name}'.", name);

    private static DslEvalOutcome BudgetError(int position, string name)
        => DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, position, $"Array arena budget exceeded in '{name}'.", name);

    private static DslEvalOutcome OverflowError(int position, string name)
        => DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}'.", name);
}
