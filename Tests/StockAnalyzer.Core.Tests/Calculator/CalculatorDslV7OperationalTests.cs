#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;
using Xunit.Abstractions;
using static StockAnalyzer.Core.Tests.Calculator.CalculatorDslTestHelpers;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v7 operational checks: worst-case stack use of nested higher-order calls, concurrency and repeat determinism,
/// deterministic cancellation, algebraic properties and decimal rounding boundaries.
/// </summary>
public sealed class CalculatorDslV7OperationalTests
{
    /// <summary>Thread stack size used to prove the nested combinators fit a deliberately small stack (the common default is 1 MiB or more).</summary>
    private const int SmallStackBytes = 256 * 1024;

    private const string BigArrayName = "big";

    /// <summary>Expression size limits raised only so the deep stress texts can be parsed (depth and ops stay at their defaults).</summary>
    private const int StressMaxExpressionChars = 2048;

    private const int StressMaxTokens = 1024;

    /// <summary>Array element limit of the DSL; every size-dependent value in this file is derived from it.</summary>
    private static readonly int MaxArrayLength = DslArrayArena.MaxArrayLength;

    private readonly ITestOutputHelper _output;

    public CalculatorDslV7OperationalTests(ITestOutputHelper output) => _output = output;

    private static T RunOnSmallStack<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            SmallStackBytes);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    /// <summary>
    /// Builds <paramref name="levels"/> nested combinators, alternating <c>arr_scan</c> and <c>arr_zip_map</c>. Level d binds <c>a{d}, b{d}</c>,
    /// iterates the shared 512-element array variable and nests the next level inside its body through <c>arr_get(..., 0)</c>
    /// (a call plus a scalar wrapper, i.e. at least two depth units per level). The next level is reached at element 0, so the deepest
    /// frame is entered after very few operations while every live level holds its 512-element result buffer on the stack.
    /// </summary>
    private static string BuildNestedCombinators(int levels)
    {
        string inner = string.Empty;
        for (int level = levels; level >= 1; level--)
        {
            string a = $"a{level}";
            string b = $"b{level}";
            string body = inner.Length == 0 ? $"{a} + {b}" : $"{a} + {b} + {inner}";
            string call = level % 2 == 1
                ? $"arr_scan({body}, {a}, {b}, {BigArrayName}, 0)"
                : $"arr_zip_map({body}, {a}, {b}, {BigArrayName}, {BigArrayName})";
            inner = level == 1 ? call : $"arr_get({call}, 0)";
        }

        return inner;
    }

    private static bool IsParseStageError(CalculatorErrorCode code)
        => code is CalculatorErrorCode.TooDeep or CalculatorErrorCode.ExpressionTooLong or CalculatorErrorCode.TooManyTokens;

    [Fact]
    public void NestedHigherOrderCallsAtMaxDepthRunOnA256KiBThread()
    {
        // Default depth/ops; only the expression size limits are raised so deep texts can be parsed.
        var limits = new CalculatorLimits { MaxExpressionChars = StressMaxExpressionChars, MaxTokens = StressMaxTokens };
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            [BigArrayName] = Enumerable.Repeat(1m, MaxArrayLength).ToArray(),
        };

        int deepestEvaluated = 0;
        var report = new StringBuilder();
        for (int levels = 1; levels <= limits.MaxNestingDepth; levels++)
        {
            string expression = BuildNestedCombinators(levels);
            ExpressionCalculator.CalculatorBatchResult result = RunOnSmallStack(() =>
                ExpressionCalculator.EvaluateBatch(
                    Empty,
                    arrays,
                    new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
                    limits)[0]);

            string outcome = result.IsOk ? "Ok" : result.Error!.Code.ToString();
            report.AppendLine($"levels={levels}, chars={expression.Length}, outcome={outcome}");

            if (!result.IsOk)
            {
                if (IsParseStageError(result.Error!.Code))
                {
                    continue;
                }

                // The only evaluation-stage failure accepted for this worst case is a budget or arena exhaustion.
                Assert.True(
                    result.Error.Code == CalculatorErrorCode.BudgetExceeded,
                    $"levels={levels}: unexpected {result.Error.Code}: {result.Error.Message}");
            }

            deepestEvaluated = levels;
        }

        _output.WriteLine(report.ToString());
        _output.WriteLine($"Deepest level that passed the parser and was evaluated on a {SmallStackBytes / 1024} KiB stack: {deepestEvaluated}");

        // Depth accounting predicts about eight live levels at the default depth of 16. A lower value means this
        // generator no longer stresses the stack, so fix the generator rather than the bound.
        Assert.True(deepestEvaluated >= 5, $"Only {deepestEvaluated} levels were evaluated; the stress expression is too shallow.");
    }

    // ------------------------------------------------------------------ concurrency, repeat determinism, cancellation

    private const int ConcurrentThreads = 8;
    private const int IterationsPerThread = 200;
    private const int SequentialRepeats = 1_000;

    private static readonly Dictionary<string, IReadOnlyList<decimal>> PipelineArrays = new(StringComparer.Ordinal)
    {
        ["h"] = new[] { 10m, 15m, 13m },
        ["l"] = new[] { 8m, 14m, 11m },
        ["c"] = new[] { 9m, 11m, 12m },
    };

    private static readonly ExpressionCalculator.CalculatorBatchItem[] PipelineItems =
    [
        new("pc", "arr_shift(c, 1, arr_get(c, 0))"),
        new("r1", "arr_zip_sub(h, l)"),
        new("r2", "arr_zip_map(abs(a - b), a, b, h, pc)"),
        new("r3", "arr_zip_map(abs(a - b), a, b, l, pc)"),
        new("tr", "arr_zip_map(max(a, b), a, b, arr_zip_map(max(a, b), a, b, r1, r2), r3)"),
        new("atr", "arr_scan((acc + x) / 2, acc, x, tr, 2)"),
        new("design", "mat_hstack(h, l)"),
        new("transposed", "mat_transpose_rect(design, 3, 2)"),
        new("blend", "mat_add(design, mat_scale(design, 3, 2, 0.5), 3, 2)"),
        new("cell", "mat_get(blend, 3, 2, 2, 1)"),
    ];

    /// <summary>Bit-level fingerprint (all four <see cref="decimal.GetBits(decimal)"/> words) of every result of the fixed pipeline.</summary>
    private static int[] EvaluatePipelineFingerprint(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            Empty, PipelineArrays, PipelineItems, CalculatorLimits.Default, cancellationToken);
        var bits = new List<int>();
        foreach (ExpressionCalculator.CalculatorBatchResult result in results)
        {
            Assert.True(result.IsOk, $"{result.Name}: {result.Error?.Code} {result.Error?.Message}");
            if (result.IsArray)
            {
                bits.Add(result.ArrayValues!.Count);
                foreach (decimal value in result.ArrayValues)
                {
                    bits.AddRange(decimal.GetBits(value));
                }
            }
            else
            {
                bits.AddRange(decimal.GetBits(result.ScalarValue));
            }
        }

        return bits.ToArray();
    }

    [Fact]
    public void ConcurrentEvaluationsOfTheSamePipelineAreBitIdentical()
    {
        int[] baseline = EvaluatePipelineFingerprint();
        var mismatches = new ConcurrentBag<string>();

        Parallel.For(
            0,
            ConcurrentThreads,
            new ParallelOptions { MaxDegreeOfParallelism = ConcurrentThreads },
            thread =>
            {
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    if (!EvaluatePipelineFingerprint().AsSpan().SequenceEqual(baseline))
                    {
                        mismatches.Add($"thread {thread}, iteration {i}");
                    }
                }
            });

        Assert.Empty(mismatches);
    }

    [Fact]
    public void RepeatedEvaluationsAreBitIdentical()
    {
        int[] baseline = EvaluatePipelineFingerprint();
        for (int i = 0; i < SequentialRepeats; i++)
        {
            Assert.True(EvaluatePipelineFingerprint().AsSpan().SequenceEqual(baseline), $"repeat {i} differs from the baseline.");
        }
    }

    [Fact]
    public void PreCancelledTokenThrowsAndALaterEvaluationIsUnaffected()
    {
        int[] baseline = EvaluatePipelineFingerprint();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => EvaluatePipelineFingerprint(cancelled.Token));

        Assert.True(EvaluatePipelineFingerprint().AsSpan().SequenceEqual(baseline));
    }

    // ------------------------------------------------------------------ algebraic properties (deterministic enumeration)

    private static string Literal(IEnumerable<decimal> values)
        => "[" + string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

    private static decimal[] Sequence(int length) => Enumerable.Range(1, length).Select(i => (decimal)i).ToArray();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(1, 5)]
    [InlineData(5, 1)]
    [InlineData(4, 7)]
    public void TransposingTwiceRestoresTheMatrixForSquareAndRectangularShapes(int rows, int cols)
    {
        decimal[] a = Sequence(rows * cols);
        string literal = Literal(a);

        Assert.Equal(a, Arr($"mat_transpose_rect(mat_transpose_rect({literal}, {rows}, {cols}), {cols}, {rows})"));

        // One transpose maps (i, j) to (j, i); this catches a swapped rows/cols that two transposes would hide.
        decimal[] once = Arr($"mat_transpose_rect({literal}, {rows}, {cols})");
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                Assert.Equal(a[i * cols + j], once[j * rows + i]);
            }
        }
    }

    [Fact]
    public void MatrixAdditionSubtractionAndScalingObeyTheirLaws()
    {
        const string a = "[1, 2, 3, 4, 5, 6]";
        const string b = "[0.5, -2, 7, 0, 1.25, -3]";

        // Independent literal expectations (hand-computed element by element), so a consistently wrong implementation cannot pass.
        Assert.Equal(new[] { 1.5m, 0m, 10m, 4m, 6.25m, 3m }, Arr($"mat_add({a}, {b}, 2, 3)"));
        Assert.Equal(new[] { 0.5m, 4m, -4m, 4m, 3.75m, 9m }, Arr($"mat_sub({a}, {b}, 2, 3)"));
        Assert.Equal(new[] { 2m, 4m, 6m, 8m, 10m, 12m }, Arr($"mat_scale({a}, 2, 3, 2)"));

        Assert.Equal(Arr($"mat_add({a}, {b}, 2, 3)"), Arr($"mat_add({b}, {a}, 2, 3)"));

        decimal[] zeroByDifference = Arr($"mat_sub({a}, {a}, 2, 3)");
        Assert.All(zeroByDifference, v => Assert.Equal(0m, v));
        AssertNoNegativeZero(zeroByDifference, "A - A");

        Assert.Equal(Arr(a), Arr($"mat_scale({a}, 2, 3, 1)"));
        decimal[] zeroByScale = Arr($"mat_scale({a}, 2, 3, 0)");
        Assert.All(zeroByScale, v => Assert.Equal(0m, v));
        AssertNoNegativeZero(zeroByScale, "0 * A");
    }

    [Fact]
    public void ExtractionAndConstructionAreConsistentWithEachOther()
    {
        decimal[][] columns = [[1m, 2m, 3m], [4m, 5m, 6m], [7m, 8m, 9m]];
        string stacked = $"mat_hstack({Literal(columns[0])}, {Literal(columns[1])}, {Literal(columns[2])})";
        for (int j = 0; j < columns.Length; j++)
        {
            Assert.Equal(columns[j], Arr($"mat_col({stacked}, 3, 3, {j})"));
        }

        const string a = "[1, 2, 3, 4, 5, 6]";
        Assert.Equal(Arr(a), Arr($"mat_submatrix({a}, 2, 3, 0, 0, 2, 3)"));

        // Independent literal expectations: the 2 x 2 block starting at (0, 1) of [[1,2,3],[4,5,6]] is [[2,3],[5,6]].
        Assert.Equal(new[] { 2m, 3m, 5m, 6m }, Arr($"mat_submatrix({a}, 2, 3, 0, 1, 2, 2)"));
        Assert.Equal(new[] { 4m, 5m, 6m }, Arr($"mat_row({a}, 2, 3, 1)"));

        decimal[] updated = Arr($"mat_set({a}, 2, 3, 1, 2, 9.5)");
        Assert.Equal(new[] { 1m, 2m, 3m, 4m, 5m, 9.5m }, updated);
        Assert.Equal(9.5m, Scalar($"mat_get({Literal(updated)}, 2, 3, 1, 2)"));
    }

    [Fact]
    public void ArrScanSuccessiveDifferencesRecoverTheInput()
    {
        decimal[] input = [3m, -1m, 4m, 1m, -5m, 9m];
        decimal[] scan = Arr($"arr_scan(acc + x, acc, x, {Literal(input)}, 0)");

        Assert.Equal(input.Length, scan.Length);
        Assert.Equal(input[0], scan[0]);
        for (int i = 1; i < input.Length; i++)
        {
            Assert.Equal(input[i], scan[i] - scan[i - 1]);
        }
    }

    // ------------------------------------------------------------------ dummy-variable shadowing

    [Fact]
    public void DummyVariableShadowsScalarAndArrayVariablesOfTheSameName()
    {
        // Resolution order in EvaluateIdentifier: bound dummy variable, then scalar variable, then array variable.
        var scalars = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["a"] = 5m };
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["b"] = new[] { 100m, 200m } };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            scalars,
            arrays,
            new ExpressionCalculator.CalculatorBatchItem[]
            {
                // `a` is a scalar variable and `b` an array variable; the dummies of the same names must win.
                new("zip", "arr_zip_map(a + b, a, b, [1, 2], [10, 20])"),
                new("scan", "arr_scan(acc + b, acc, b, [1, 2], 0)"),
            },
            CalculatorLimits.Default);

        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Name}: {r.Error?.Code} {r.Error?.Message}"));
        Assert.Equal(new[] { 11m, 22m }, results[0].ArrayValues!.ToArray());
        Assert.Equal(new[] { 1m, 3m }, results[1].ArrayValues!.ToArray());
    }

    [Fact]
    public void InnerDummyVariableOfTheSameNameShadowsTheOuterOne()
    {
        // The inner scan uses its own acc/x ([11, 13] from init 10 over [1, 2]); the outer x = 100 must not leak into it.
        // Outer: s0 = 13 + 0 = 13, s1 = 13 + 13 = 26.
        decimal[] result = Arr("arr_scan(arr_get(arr_scan(acc + x, acc, x, [1, 2], 10), 1) + acc, acc, x, [100, 100], 0)");
        Assert.Equal(new[] { 13m, 26m }, result);
    }

    [Fact]
    public void SquareMatrixLimitIsTheLargestSquareThatFitsTheArrayLimit()
    {
        int maxDim = CalculatorMatrixFunctions.MaxSquareArrayMatrixDim;
        Assert.Equal((int)Math.Floor(Math.Sqrt(DslArrayArena.MaxArrayLength)), maxDim);
        Assert.True(maxDim * maxDim <= DslArrayArena.MaxArrayLength);
        Assert.True((maxDim + 1) * (maxDim + 1) > DslArrayArena.MaxArrayLength);
    }

    // ------------------------------------------------------------------ decimal rounding boundaries (theory first)

    // Expected values follow the C# language rule (ECMA-334, decimal type): a result that needs more than 28-29 significant
    // digits is rounded to the nearest representable value; an exact tie goes to the value whose last digit is even.
    // They were derived by hand and are NOT copied from a run; a disagreement means the derivation or the runtime must be examined.
    [Theory]
    // 1e-28 * 0.5 = 5e-29: tie between 0 and 1e-28 -> even last digit -> 0.
    [InlineData("mat_scale([1e-28], 1, 1, 0.5)", "0")]
    // 1e-28 * 1.5 = 1.5e-28: tie between 1e-28 and 2e-28 -> even last digit -> 2e-28.
    [InlineData("mat_scale([1e-28], 1, 1, 1.5)", "0.0000000000000000000000000002")]
    // 4e-29 is below the half-way point -> 0.
    [InlineData("mat_scale([1e-28], 1, 1, 0.4)", "0")]
    // 6e-29 is above the half-way point -> 1e-28.
    [InlineData("mat_scale([1e-28], 1, 1, 0.6)", "0.0000000000000000000000000001")]
    // Rounding is symmetric about zero.
    [InlineData("mat_scale([-1e-28], 1, 1, 1.5)", "-0.0000000000000000000000000002")]
    // 1e28 + 0.5: tie between ...000 and ...001 -> even -> 1e28.
    [InlineData("mat_add([1e28], [0.5], 1, 1)", "10000000000000000000000000000")]
    // 1e28 + 1.5: tie between ...001 and ...002 -> even -> ...002.
    [InlineData("mat_add([1e28], [1.5], 1, 1)", "10000000000000000000000000002")]
    // 1/3 is held as 28 threes; times 3 is exactly 28 nines (not 1).
    [InlineData("mat_scale([1 / 3], 1, 1, 3)", "0.9999999999999999999999999999")]
    public void DecimalRoundingFollowsNearestEvenAtTheDigitLimit(string expression, string expected)
    {
        decimal[] result = Arr(expression);
        Assert.Single(result);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), result[0]);
    }

    // ------------------------------------------------------------------ shape error messages carry the actual values

    [Fact]
    public void HorizontalStackSizeErrorStatesTheActualProductAndTheLimit()
    {
        // Half the array limit as the row count and three columns: n * k is 1.5 times the limit, so it must be rejected.
        int rows = MaxArrayLength / 2;
        const int columns = 3;
        string zeros = $"arr_zeros({rows})";
        CalculatorError error = Fails(
            $"mat_hstack({zeros}, {zeros}, {zeros})",
            CalculatorErrorCode.DimensionMismatch);

        Assert.Contains($"n={rows}", error.Message, StringComparison.Ordinal);
        Assert.Contains($"k={columns}", error.Message, StringComparison.Ordinal);
        Assert.Contains($"n*k={rows * columns}", error.Message, StringComparison.Ordinal);
        Assert.Contains(MaxArrayLength.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShapeErrorsStateRowsColsProductAndEveryArrayLength()
    {
        CalculatorError single = Fails("mat_get([1, 2, 3], 2, 2, 0, 0)", CalculatorErrorCode.DimensionMismatch);
        Assert.Contains("rows=2", single.Message, StringComparison.Ordinal);
        Assert.Contains("cols=2", single.Message, StringComparison.Ordinal);
        Assert.Contains("rows*cols=4", single.Message, StringComparison.Ordinal);
        Assert.Contains("array length=3", single.Message, StringComparison.Ordinal);

        // mat_add validates two arrays; both lengths are reported.
        CalculatorError pair = Fails("mat_add([1, 2, 3, 4], [1, 2, 3], 2, 2)", CalculatorErrorCode.DimensionMismatch);
        Assert.Contains("array length=4 and 3", pair.Message, StringComparison.Ordinal);

        // A product above the limit reports the product itself.
        int overLimitCols = (MaxArrayLength / 2) + 1;
        CalculatorError tooLarge = Fails(
            $"mat_transpose_rect(arr_zeros({MaxArrayLength}), 2, {overLimitCols})",
            CalculatorErrorCode.DimensionMismatch);
        Assert.Contains($"rows*cols={2 * overLimitCols}", tooLarge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NumbersInV7ErrorMessagesAreFormattedWithTheInvariantCultureAndAreAsciiOnly()
    {
        // Messages are built only on failing branches; run them under a culture that changes every number format setting.
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo exotic = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        exotic.NumberFormat.NegativeSign = "\u2212"; // MINUS SIGN
        exotic.NumberFormat.NumberGroupSeparator = "\u202F"; // NARROW NO-BREAK SPACE
        exotic.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = exotic;
            string zeros = $"arr_zeros({MaxArrayLength / 2})";
            int overSquareDim = CalculatorMatrixFunctions.MaxSquareArrayMatrixDim + 1;
            string[] expressions =
            [
                $"mat_hstack({zeros}, {zeros}, {zeros})",
                "mat_get([1, 2, 3], 2, 2, 0, 0)",
                "mat_add([1, 2, 3, 4], [1, 2, 3], 2, 2)",
                "mat_diag_extract([1, 2, 3], 2)",
                $"mat_diag_extract([1, 2, 3, 4], {overSquareDim})",
                $"arr_shift([1, 2], {MaxArrayLength + 1}, 0)",
                "arr_zip_map(a + b, a, b, [1, 2], [1])",
            ];

            foreach (string expression in expressions)
            {
                ExpressionCalculator.CalculatorBatchResult result = Run(expression);
                Assert.False(result.IsOk, expression);
                Assert.All(result.Error!.Message, ch => Assert.True(ch < 128, $"{expression}: non-ASCII character in \"{result.Error.Message}\""));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
