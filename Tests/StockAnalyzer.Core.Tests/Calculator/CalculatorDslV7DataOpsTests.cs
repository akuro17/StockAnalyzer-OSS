#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using Xunit;
using static StockAnalyzer.Core.Tests.Calculator.CalculatorDslTestHelpers;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v7 data structure operations: row-major matrix extraction/construction/arithmetic (<c>mat_get</c> .. <c>mat_scale</c>),
/// 1D array edit/gather/shift (<c>arr_set</c>, <c>arr_take</c>, <c>arr_shift</c>) and the two-variable combinators (<c>arr_scan</c>, <c>arr_zip_map</c>).
/// </summary>
public sealed class CalculatorDslV7DataOpsTests
{
    // ------------------------------------------------------------------ matrix extraction

    [Fact]
    public void MatGetRowColReadRowMajorElements()
    {
        const string a = "[1, 2, 3, 4, 5, 6]";
        Assert.Equal(6m, Scalar($"mat_get({a}, 2, 3, 1, 2)"));
        Assert.Equal(1m, Scalar($"mat_get({a}, 2, 3, 0, 0)"));
        Assert.Equal(5m, Scalar("mat_get([5], 1, 1, 0, 0)"));
        Assert.Equal(new[] { 4m, 5m, 6m }, Arr($"mat_row({a}, 2, 3, 1)"));
        Assert.Equal(new[] { 2m, 5m }, Arr($"mat_col({a}, 2, 3, 1)"));
        Assert.Equal(new[] { 7m }, Arr("mat_row([7], 1, 1, 0)"));
        Assert.Equal(new[] { 7m }, Arr("mat_col([7], 1, 1, 0)"));
    }

    [Fact]
    public void MatDiagExtractInvertsMatDiagAndHonoursTheSquareBound()
    {
        Assert.Equal(new[] { 1m, 4m }, Arr("mat_diag_extract([1, 2, 3, 4], 2)"));
        Assert.Equal(new[] { 9m }, Arr("mat_diag_extract([9], 1)"));
        Assert.Equal(new[] { 3m, 5m, 7m }, Arr("mat_diag_extract(mat_diag([3, 5, 7]), 3)"));
        Assert.Equal(Enumerable.Repeat(1m, 22).ToArray(), Arr("mat_diag_extract(mat_eye(22), 22)"));
        Fails("mat_diag_extract(mat_eye(22), 23)", CalculatorErrorCode.DomainError);
        Fails("mat_diag_extract([1, 2, 3, 4], 0)", CalculatorErrorCode.DomainError);
        Fails("mat_diag_extract([1, 2, 3, 4], 1.5)", CalculatorErrorCode.DomainError);
        Fails("mat_diag_extract([1, 2, 3], 2)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_diag_extract(5, 2)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void MatSubmatrixCopiesTheRequestedBlockAndRejectsEmptyOrOutOfRangeSpans()
    {
        const string a = "[1, 2, 3, 4, 5, 6, 7, 8, 9]";
        Assert.Equal(new[] { 5m, 6m, 8m, 9m }, Arr($"mat_submatrix({a}, 3, 3, 1, 1, 2, 2)"));
        Assert.Equal(new[] { 1m, 2m, 3m }, Arr($"mat_submatrix({a}, 3, 3, 0, 0, 1, 3)"));
        Assert.Equal(new[] { 9m }, Arr($"mat_submatrix({a}, 3, 3, 2, 2, 1, 1)"));
        Fails($"mat_submatrix({a}, 3, 3, 1, 1, 0, 2)", CalculatorErrorCode.DomainError);
        Fails($"mat_submatrix({a}, 3, 3, 1, 1, 2, 0)", CalculatorErrorCode.DomainError);
        Fails($"mat_submatrix({a}, 3, 3, 2, 0, 2, 1)", CalculatorErrorCode.DomainError);
        Fails($"mat_submatrix({a}, 3, 3, 3, 0, 1, 1)", CalculatorErrorCode.DomainError);
    }

    // ------------------------------------------------------------------ matrix construction

    [Fact]
    public void MatHstackInterleavesColumnsRowMajor()
    {
        Assert.Equal(new[] { 1m, 4m, 2m, 5m, 3m, 6m }, Arr("mat_hstack([1, 2, 3], [4, 5, 6])"));
        Assert.Equal(new[] { 1m, 3m, 5m, 2m, 4m, 6m }, Arr("mat_hstack([1, 2], [3, 4], [5, 6])"));
        Assert.Equal(new[] { 1m, 2m }, Arr("mat_hstack([1], [2])"));

        string sixteenColumns = string.Join(", ", Enumerable.Repeat("[1, 2]", 16));
        Assert.Equal(32, Arr($"mat_hstack({sixteenColumns})").Length);
        string seventeenColumns = string.Join(", ", Enumerable.Repeat("[1, 2]", 17));
        Fails($"mat_hstack({seventeenColumns})", CalculatorErrorCode.WrongArgumentCount);
        Fails("mat_hstack([1, 2])", CalculatorErrorCode.WrongArgumentCount);
    }

    [Fact]
    public void MatHstackValidationOrderIsSymmetricForEmptyColumns()
    {
        Fails("mat_hstack([1, 2], [])", CalculatorErrorCode.DomainError);
        Fails("mat_hstack([], [1, 2])", CalculatorErrorCode.DomainError);
        Fails("mat_hstack([], [])", CalculatorErrorCode.DomainError);
        Fails("mat_hstack([1, 2], 3)", CalculatorErrorCode.DomainError);
        Fails("mat_hstack([1, 2], [1])", CalculatorErrorCode.DimensionMismatch);
        // 256 x 2 = 512 elements is the bound; 256 x 3 exceeds it.
        Assert.Equal(512, Arr("mat_hstack(arr_zeros(256), arr_ones(256))").Length);
        Fails("mat_hstack(arr_zeros(256), arr_zeros(256), arr_zeros(256))", CalculatorErrorCode.DimensionMismatch);
    }

    [Fact]
    public void MatSetReplacesOneElementWithoutMutatingTheSource()
    {
        Assert.Equal(new[] { 1m, 9m, 3m, 4m }, Arr("mat_set([1, 2, 3, 4], 2, 2, 0, 1, 9)"));
        Assert.Equal(new[] { 1m, 2m, 3m, 4m }, Arr("mat_set([1, 2, 3, 4], 2, 2, 1, 1, 4)"));
        Fails("mat_set([1, 2, 3, 4], 2, 2, 2, 0, 9)", CalculatorErrorCode.DomainError);
        Fails("mat_set([1, 2, 3, 4], 2, 2, 0, 0, [9])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void MatTransposeRectHandlesNonSquareShapesWithoutChangingMatTranspose()
    {
        Assert.Equal(new[] { 1m, 4m, 2m, 5m, 3m, 6m }, Arr("mat_transpose_rect([1, 2, 3, 4, 5, 6], 2, 3)"));
        Assert.Equal(new[] { 1m, 3m, 2m, 4m }, Arr("mat_transpose_rect([1, 2, 3, 4], 2, 2)"));
        Assert.Equal(17, Arr("mat_transpose_rect(arr_ones(17), 17, 1)").Length);
        Assert.Equal(512, Arr("mat_transpose_rect(arr_zeros(512), 1, 512)").Length);
        Assert.Equal(new[] { 1m, 2m, 3m }, Arr("mat_transpose_rect(mat_transpose_rect([1, 2, 3], 3, 1), 1, 3)"));
        Fails("mat_transpose_rect([1, 2, 3, 4], 17, 1)", CalculatorErrorCode.DimensionMismatch);
        Assert.Equal(512, Arr("mat_transpose_rect(arr_zeros(512), 2, 256)").Length);
        Fails("mat_transpose_rect(arr_zeros(512), 2, 257)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_transpose_rect(arr_zeros(4), 513, 1)", CalculatorErrorCode.DomainError);

        // The legacy 1..16 double-path function is untouched.
        Fails("mat_transpose([1, 2, 3, 4], 17, 1)", CalculatorErrorCode.DomainError);
        Assert.Equal(new[] { 1m, 3m, 2m, 4m }, Arr("mat_transpose([1, 2, 3, 4], 2, 2)"));
    }

    // ------------------------------------------------------------------ matrix arithmetic

    [Fact]
    public void MatAddSubScaleComputeExactDecimalResults()
    {
        Assert.Equal(new[] { 11m, 22m, 33m, 44m }, Arr("mat_add([1, 2, 3, 4], [10, 20, 30, 40], 2, 2)"));
        Assert.Equal(new[] { -9m, -18m, -27m, -36m }, Arr("mat_sub([1, 2, 3, 4], [10, 20, 30, 40], 2, 2)"));
        Assert.Equal(new[] { 0.3m }, Arr("mat_add([0.1], [0.2], 1, 1)"));
        Assert.Equal(new[] { 0.5m, 1m, 1.5m, 2m }, Arr("mat_scale([1, 2, 3, 4], 2, 2, 0.5)"));
        Assert.Equal(new[] { 0m, 0m }, Arr("mat_scale([1, 2], 1, 2, 0)"));
        Fails("mat_add([1, 2, 3, 4], [1, 2, 3], 2, 2)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_sub([1, 2, 3], [1, 2, 3, 4], 2, 2)", CalculatorErrorCode.DimensionMismatch);
    }

    [Fact]
    public void MatArithmeticOverflowIsReportedNotThrown()
    {
        Fails("mat_add([7e28], [7e28], 1, 1)", CalculatorErrorCode.ArithmeticOverflow);
        Fails("mat_sub([-7e28], [7e28], 1, 1)", CalculatorErrorCode.ArithmeticOverflow);
        Fails("mat_scale([1e20], 1, 1, 1e20)", CalculatorErrorCode.ArithmeticOverflow);
    }

    // ------------------------------------------------------------------ matrix error precedence

    [Fact]
    public void MatrixValidationOrderIsKindThenRangeThenShapeThenCoordinates()
    {
        // Stage 1 (argument kinds) beats stage 3 (shape): rows = 5 would be a DimensionMismatch for a 4-element array.
        Fails("mat_scale([1, 2, 3, 4], 5, 2, [1])", CalculatorErrorCode.DomainError);
        Fails("mat_get([1, 2, 3, 4], 5, 2, [0], 0)", CalculatorErrorCode.DomainError);
        Fails("mat_row([1, 2, 3, 4], 5, 2, [0])", CalculatorErrorCode.DomainError);
        Fails("mat_col([1, 2, 3, 4], 5, 2, [0])", CalculatorErrorCode.DomainError);
        Fails("mat_submatrix([1, 2, 3, 4], 5, 2, 0, 0, 1, [1])", CalculatorErrorCode.DomainError);
        Fails("mat_set([1, 2, 3, 4], 5, 2, 0, 0, [1])", CalculatorErrorCode.DomainError);
        Fails("mat_add([1, 2, 3, 4], 5, 2, 2)", CalculatorErrorCode.DomainError);
        Fails("mat_get(7, 2, 2, 0, 0)", CalculatorErrorCode.DomainError);

        // Stage 2 (rows/cols range) beats stage 3 (shape).
        Fails("mat_get([1, 2, 3, 4], 0, 2, 0, 0)", CalculatorErrorCode.DomainError);
        Fails("mat_get([1, 2, 3, 4], 2, 1.5, 0, 0)", CalculatorErrorCode.DomainError);
        Fails("mat_get([1, 2, 3, 4], 513, 1, 0, 0)", CalculatorErrorCode.DomainError);

        // Stage 3 (shape) beats stage 4 (coordinates).
        Fails("mat_get([1, 2, 3], 2, 2, 5, 5)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_get([1, 2, 3, 4], 2, 3, 0, 0)", CalculatorErrorCode.DimensionMismatch);
        Fails("mat_get([], 1, 1, 0, 0)", CalculatorErrorCode.DimensionMismatch);

        // Stage 4 coordinates.
        Fails("mat_get([1, 2, 3, 4], 2, 2, 2, 0)", CalculatorErrorCode.DomainError);
        Fails("mat_get([1, 2, 3, 4], 2, 2, 0, -1)", CalculatorErrorCode.DomainError);
        Fails("mat_row([1, 2, 3, 4], 2, 2, 0.5)", CalculatorErrorCode.DomainError);
        Fails("mat_col([1, 2, 3, 4], 2, 2, 2)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void WrongArityIsReportedForEveryV7Function()
    {
        foreach (string call in new[]
        {
            "mat_get([1], 1, 1, 0)", "mat_row([1], 1, 1)", "mat_col([1], 1, 1)", "mat_diag_extract([1])", "mat_submatrix([1], 1, 1, 0, 0, 1)",
            "mat_set([1], 1, 1, 0, 0)", "mat_transpose_rect([1], 1)", "mat_add([1], [1], 1)", "mat_sub([1], [1], 1)", "mat_scale([1], 1, 1)",
            "arr_set([1], 0)", "arr_take([1])", "arr_shift([1], 1)",
        })
        {
            Fails(call, CalculatorErrorCode.WrongArgumentCount);
        }
    }

    // ------------------------------------------------------------------ 1D arrays

    [Fact]
    public void ArrSetReplacesOneElementNonDestructively()
    {
        Assert.Equal(new[] { 1m, 9m, 3m }, Arr("arr_set([1, 2, 3], 1, 9)"));
        Assert.Equal(new[] { 9m }, Arr("arr_set([1], 0, 9)"));
        Fails("arr_set([1, 2, 3], 3, 9)", CalculatorErrorCode.DomainError);
        Fails("arr_set([1, 2, 3], -1, 9)", CalculatorErrorCode.DomainError);
        Fails("arr_set([1, 2, 3], 1.5, 9)", CalculatorErrorCode.DomainError);
        Fails("arr_set([], 0, 9)", CalculatorErrorCode.DomainError);
        Fails("arr_set([1, 2, 3], [0], 9)", CalculatorErrorCode.DomainError);
        Fails("arr_set(5, 0, 9)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ArrTakeGathersWithDuplicatesAndValidatesEveryIndexFirst()
    {
        Assert.Equal(new[] { 30m, 10m, 30m }, Arr("arr_take([10, 20, 30], [2, 0, 2])"));
        Assert.Empty(Arr("arr_take([1, 2], [])"));
        Assert.Empty(Arr("arr_take([], [])"));
        Fails("arr_take([], [0])", CalculatorErrorCode.DomainError);
        Fails("arr_take([1, 2], [0, 2])", CalculatorErrorCode.DomainError);
        Fails("arr_take([1, 2], [0.5])", CalculatorErrorCode.DomainError);
        Fails("arr_take([1, 2], [-1])", CalculatorErrorCode.DomainError);
        Fails("arr_take(3, [0])", CalculatorErrorCode.DomainError);
        Fails("arr_take([1, 2], 0)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ArrShiftPreservesLengthAndFillsExplicitly()
    {
        Assert.Equal(new[] { 0m, 1m, 2m, 3m }, Arr("arr_shift([1, 2, 3, 4], 1, 0)"));
        Assert.Equal(new[] { 7m, 7m, 1m, 2m }, Arr("arr_shift([1, 2, 3, 4], 2, 7)"));
        Assert.Equal(new[] { 2m, 3m, 4m, 9m }, Arr("arr_shift([1, 2, 3, 4], -1, 9)"));
        Assert.Equal(new[] { 4m, 9m, 9m, 9m }, Arr("arr_shift([1, 2, 3, 4], -3, 9)"));
        Assert.Equal(new[] { 1m, 2m, 3m, 4m }, Arr("arr_shift([1, 2, 3, 4], 0, 9)"));
        Assert.Equal(new[] { 7m, 7m, 7m, 7m }, Arr("arr_shift([1, 2, 3, 4], 4, 7)"));
        Assert.Equal(new[] { 7m, 7m, 7m, 7m }, Arr("arr_shift([1, 2, 3, 4], 512, 7)"));
        Assert.Equal(new[] { 7m, 7m, 7m, 7m }, Arr("arr_shift([1, 2, 3, 4], -512, 7)"));
        Assert.Equal(new[] { 5m }, Arr("arr_shift([5], 0, 0)"));
        Assert.Equal(new[] { 0m }, Arr("arr_shift([5], 1, 0)"));
    }

    [Fact]
    public void ArrShiftValidatesTheShiftBeforeTheEmptyArrayShortcut()
    {
        Assert.Empty(Arr("arr_shift([], 3, 0)"));
        Assert.Empty(Arr("arr_shift([], 512, 0)"));
        Assert.Empty(Arr("arr_shift([], -512, 0)"));
        Fails("arr_shift([], 1000, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_shift([], 1.5, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_shift([1, 2], 513, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_shift([1, 2], [1], 0)", CalculatorErrorCode.DomainError);
        Fails("arr_shift([1, 2], 1, [0])", CalculatorErrorCode.DomainError);
        Fails("arr_shift(1, 1, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_shift([1, 2], 1)", CalculatorErrorCode.WrongArgumentCount);
    }

    // ------------------------------------------------------------------ combinators

    [Fact]
    public void ArrScanReturnsEveryIntermediateAccumulator()
    {
        Assert.Equal(new[] { 1m, 3m, 6m, 10m }, Arr("arr_scan(acc + x, acc, x, [1, 2, 3, 4], 0)"));
        Assert.Equal(new[] { 1m, 4m, 11m }, Arr("arr_scan(acc * 2 + x, acc, x, [1, 2, 3], 0)"));
        Assert.Equal(new[] { 2m, 5m, 8.5m }, Arr("arr_scan((acc + x) / 2, acc, x, [4, 8, 12], 0)"));
        Assert.Equal(new[] { 6m }, Arr("arr_scan(acc + x, acc, x, [1], 5)"));
        Assert.Equal(5m, Scalar("avg(arr_scan(acc + x, acc, x, [1, 2, 3, 4], 0))"));
        Assert.Equal(10m, Scalar("max(arr_scan(acc + x, acc, x, [1, 2, 3, 4], 0))"));
    }

    [Fact]
    public void ArrScanOnEmptyArrayStillValidatesInitAndRunsTheBodyZeroTimes()
    {
        Assert.Empty(Arr("arr_scan(1 / 0, acc, item, [], 5)"));
        Fails("arr_scan(acc, acc, item, [], 1 / 0)", CalculatorErrorCode.DivisionByZero);
        Fails("arr_scan(acc, acc, item, [], [1])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ArrScanAndZipMapReportValidationErrorsInLeftToRightOrderWithNodePositions()
    {
        const string nonArray = "arr_scan(a + b, a, b, 5, 0)";
        CalculatorError nonArrayError = Fails(nonArray, CalculatorErrorCode.DomainError);
        Assert.Equal(nonArray.IndexOf("5, 0", StringComparison.Ordinal), nonArrayError.Position);

        const string arrayInit = "arr_scan(a + b, a, b, [1], [2])";
        CalculatorError arrayInitError = Fails(arrayInit, CalculatorErrorCode.DomainError);
        Assert.Equal(arrayInit.IndexOf("[2]", StringComparison.Ordinal), arrayInitError.Position);

        const string duplicate = "arr_scan(a, a, a, 5, 0)";
        CalculatorError duplicateError = Fails(duplicate, CalculatorErrorCode.DomainError);
        Assert.Equal(duplicate.IndexOf("a, 5", StringComparison.Ordinal), duplicateError.Position);

        // The first dummy is checked before the array arguments are evaluated.
        Fails("arr_scan(a + b, 1, b, 1 / 0, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_zip_map(a + b, a, a, [1], [1])", CalculatorErrorCode.DomainError);
        Fails("arr_zip_map(a + b, a, b, 5, [1])", CalculatorErrorCode.DomainError);
        Fails("arr_zip_map(a + b, a, b, [1], 5)", CalculatorErrorCode.DomainError);
        Fails("arr_scan([1], a, b, [1], 0)", CalculatorErrorCode.DomainError);
        Fails("arr_zip_map([1], a, b, [1], [2])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void WrongArityOnTwoVariableCombinatorsIsNotMisreportedAsUndefinedName()
    {
        Fails("arr_scan(acc + x, acc, x, [1, 2])", CalculatorErrorCode.WrongArgumentCount);
        Fails("arr_zip_map(a + b, a, b, [1])", CalculatorErrorCode.WrongArgumentCount);
        Fails("arr_zip_map(a + b, a, b, [1], [2], [3])", CalculatorErrorCode.WrongArgumentCount);
    }

    [Fact]
    public void ArrZipMapCombinesEqualLengthArraysElementWise()
    {
        Assert.Equal(new[] { 4m, 10m, 18m }, Arr("arr_zip_map(a * b, a, b, [1, 2, 3], [4, 5, 6])"));
        Assert.Equal(new[] { 5m }, Arr("arr_zip_map(a + b, a, b, [2], [3])"));
        Assert.Empty(Arr("arr_zip_map(a + b, a, b, [], [])"));
        CalculatorError mismatch = Fails("arr_zip_map(a + b, a, b, [1, 2], [1])", CalculatorErrorCode.DimensionMismatch);
        Assert.Equal(0, mismatch.Position);
        Assert.Equal(11m, Scalar("sum(arr_zip_map(a * b, a, b, [1, 2], [3, 4]))"));
    }

    [Fact]
    public void CombinatorBodyErrorsPropagateVerbatim()
    {
        Fails("arr_scan(acc / (x - 2), acc, x, [1, 2, 3], 1)", CalculatorErrorCode.DivisionByZero);
        Fails("arr_zip_map(a / b, a, b, [1, 2], [1, 0])", CalculatorErrorCode.DivisionByZero);
        Fails("arr_scan(acc + y, acc, x, [1, 2], 0)", CalculatorErrorCode.UndefinedName);
    }

    [Fact]
    public void CombinatorsConsumeTheExecutionBudgetPerIteration()
    {
        var tight = new CalculatorLimits { MaxExecutionOps = 40 };
        string hundred = "arr_ones(100)";
        Fails($"arr_scan(acc + x, acc, x, {hundred}, 0)", CalculatorErrorCode.BudgetExceeded, tight);
        Fails($"arr_zip_map(a + b, a, b, {hundred}, {hundred})", CalculatorErrorCode.BudgetExceeded, tight);
    }

    [Fact]
    public void CombinatorBodiesRollBackTransientArenaAllocationsEveryStep()
    {
        // Each step allocates a 100-element transient array; 300 steps would need 30,000 elements without per-step rollback (arena = 4,096).
        decimal[] scan = Arr("arr_scan(acc + sum(arr_zeros(100)) + x, acc, x, arr_ones(300), 0)", Heavy);
        Assert.Equal(300, scan.Length);
        Assert.Equal(300m, scan[^1]);

        decimal[] zip = Arr("arr_zip_map(a + sum(arr_zeros(100)) + b, a, b, arr_ones(300), arr_ones(300))", Heavy);
        Assert.Equal(300, zip.Length);
        Assert.All(zip, v => Assert.Equal(2m, v));
    }

    [Fact]
    public void NestedCombinatorsCaptureOuterDummyVariablesAndStayStackSafe()
    {
        Assert.Equal(
            new[] { 12m, 20m },
            Arr("arr_zip_map(a * b + arr_get(arr_scan(acc + x + a, acc, x, [1, 2, 3], 0), 2), a, b, [1, 2], [3, 4])"));

        // s(d) = 3 + 2 * s(d - 1), s(0) = 0, nested through arr_get(arr_scan(...)).
        string expression = "0";
        for (int depth = 1; depth <= 3; depth++)
        {
            expression = $"arr_get(arr_scan(a{depth} + x{depth} + {expression}, a{depth}, x{depth}, [1, 2], 0), 1)";
        }

        Assert.Equal(21m, Scalar(expression, Heavy));

        // Full-length (512) inputs: only the result buffer is stack-allocated per call.
        decimal[] longScan = Arr("arr_scan(acc + x, acc, x, arr_ones(512), 0)", Heavy);
        Assert.Equal(512, longScan.Length);
        Assert.Equal(512m, longScan[^1]);
        Assert.Equal(512, Arr("arr_zip_map(a - b, a, b, arr_ones(512), arr_zeros(512))", Heavy).Length);
    }

    // ------------------------------------------------------------------ negative zero normalization

    [Fact]
    public void NegativeZeroInjectedThroughArrayVariablesIsNormalizedByEveryV7Output()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["nz"] = new[] { NegativeZero, NegativeZero },
            ["nz1"] = new[] { NegativeZero },
            ["idx"] = new[] { 0m, 1m },
        };
        var scalars = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["nzs"] = NegativeZero };

        var items = new ExpressionCalculator.CalculatorBatchItem[]
        {
            new("get", "mat_get(nz, 1, 2, 0, 1)"),
            new("row", "mat_row(nz, 1, 2, 0)"),
            new("col", "mat_col(nz, 2, 1, 0)"),
            new("diag", "mat_diag_extract(nz1, 1)"),
            new("sub", "mat_submatrix(nz, 1, 2, 0, 0, 1, 2)"),
            new("hstack", "mat_hstack(nz, nz)"),
            new("set", "mat_set(nz, 1, 2, 0, 0, nzs)"),
            new("transpose", "mat_transpose_rect(nz, 1, 2)"),
            new("add", "mat_add(nz, nz, 1, 2)"),
            new("sub2", "mat_sub(nz, nz, 1, 2)"),
            new("scale", "mat_scale(nz, 1, 2, -1)"),
            new("aset", "arr_set(nz, 0, nzs)"),
            new("take", "arr_take(nz, idx)"),
            new("shift0", "arr_shift(nz, 0, nzs)"),
            new("shiftLag", "arr_shift(nz, 1, nzs)"),
            new("shiftLead", "arr_shift(nz, -1, nzs)"),
            new("scan", "arr_scan(acc * x, acc, x, nz, nzs)"),
            new("zip", "arr_zip_map(a * -1, a, b, nz, nz)"),
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results =
            ExpressionCalculator.EvaluateBatch(scalars, arrays, items, CalculatorLimits.Default);

        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Name}: {r.Error?.Code} {r.Error?.Message}"));
        foreach (ExpressionCalculator.CalculatorBatchResult result in results)
        {
            if (result.IsArray)
            {
                AssertNoNegativeZero(result.ArrayValues!, result.Name);
            }
            else
            {
                AssertNoNegativeZero(new[] { result.ScalarValue }, result.Name);
            }
        }
    }

    // ------------------------------------------------------------------ array-lowering composition

    [Fact]
    public void ArrayReturningV7FunctionsComposeWithArrayLoweredVariadics()
    {
        Assert.Equal(7m, Scalar("sum(mat_col([1, 2, 3, 4, 5, 6], 2, 3, 1))"));
        Assert.Equal(15m, Scalar("sum(mat_row([1, 2, 3, 4, 5, 6], 2, 3, 1))"));
        Assert.Equal(13m, Scalar("sum(arr_shift([1, 2, 3], 1, 10))"));
        Assert.Equal(60m, Scalar("sum(arr_take([10, 20, 30], [0, 1, 2]))"));
        Assert.Equal(4m, Scalar("max(mat_diag_extract([1, 2, 3, 4], 2))"));
        Assert.Equal(10m, Scalar("sum(mat_add([1, 2], [3, 4], 1, 2))"));
        Assert.Equal(2m, Scalar("arr_len(mat_hstack([1], [2]))"));
    }

    // ------------------------------------------------------------------ indicator pipelines

    [Fact]
    public void TrueRangeAndWilderSmoothingPipelinesUseShiftZipMapAndScan()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["h"] = new[] { 10m, 15m, 13m },
            ["l"] = new[] { 8m, 14m, 11m },
            ["c"] = new[] { 9m, 11m, 12m },
        };

        var items = new ExpressionCalculator.CalculatorBatchItem[]
        {
            new("pc", "arr_shift(c, 1, arr_get(c, 0))"),
            new("r1", "arr_zip_sub(h, l)"),
            new("r2", "arr_zip_map(abs(a - b), a, b, h, pc)"),
            new("r3", "arr_zip_map(abs(a - b), a, b, l, pc)"),
            new("tr", "arr_zip_map(max(a, b), a, b, arr_zip_map(max(a, b), a, b, r1, r2), r3)"),
            new("atr", "arr_scan((acc + x) / 2, acc, x, tr, 2)"),
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results =
            ExpressionCalculator.EvaluateBatch(Empty, arrays, items, CalculatorLimits.Default);

        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Name}: {r.Error?.Code} {r.Error?.Message}"));
        Assert.Equal(new[] { 9m, 9m, 11m }, results[0].ArrayValues!.ToArray());
        Assert.Equal(new[] { 2m, 6m, 2m }, results[4].ArrayValues!.ToArray());
        Assert.Equal(new[] { 2m, 4m, 3m }, results[5].ArrayValues!.ToArray());
    }

    [Fact]
    public void HstackBuildsDesignMatrixBeyondSixteenObservationsForOls()
    {
        const int observations = 20;
        decimal[] x1 = Enumerable.Range(0, observations).Select(i => (decimal)i).ToArray();
        decimal[] x2 = Enumerable.Range(0, observations).Select(i => (decimal)((i * i) % 7)).ToArray();
        decimal[] y = Enumerable.Range(0, observations).Select(i => 3m + 2m * x1[i] - x2[i] + (i % 3) * 0.25m).ToArray();
        var interleaved = new decimal[observations * 2];
        for (int i = 0; i < observations; i++)
        {
            interleaved[i * 2] = x1[i];
            interleaved[i * 2 + 1] = x2[i];
        }

        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["x1"] = x1,
            ["x2"] = x2,
            ["y"] = y,
            ["X"] = interleaved,
        };

        var items = new ExpressionCalculator.CalculatorBatchItem[]
        {
            new("viaHstack", "ols_fit(mat_hstack(x1, x2), y)"),
            new("direct", "ols_fit(X, y)"),
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results =
            ExpressionCalculator.EvaluateBatch(Empty, arrays, items, CalculatorLimits.Default);

        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Name}: {r.Error?.Code} {r.Error?.Message}"));
        Assert.Equal(results[1].ArrayValues!.ToArray(), results[0].ArrayValues!.ToArray());
    }

    [Fact]
    public void EigenvectorColumnExtractionYieldsAUnitVector()
    {
        decimal norm = Scalar("vec_norm(mat_col(mat_eig_sym_vecs([2, 1, 1, 2], 2), 2, 2, 0))");
        Assert.True(Math.Abs((double)norm - 1.0) < 1e-9, $"column norm was {norm}.");
        Assert.Equal(2, Arr("mat_col(mat_eig_sym_vecs([2, 1, 1, 2], 2), 2, 2, 1)").Length);
    }
}
