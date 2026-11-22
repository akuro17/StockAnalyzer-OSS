#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6LanguageTests
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyScalars = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static CalculatorOutcome Eval(string expr, IReadOnlyDictionary<string, decimal>? vars = null)
        => ExpressionCalculator.Evaluate(expr, vars ?? EmptyScalars, CalculatorLimits.Default);

    private static ExpressionCalculator.CalculatorBatchResult EvalBatchOne(
        string expr,
        IReadOnlyDictionary<string, decimal>? scalars = null,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>>? arrays = null)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            scalars ?? EmptyScalars,
            arrays,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            CalculatorLimits.Default);
        return results[0];
    }

    // ---- 1. Scientific Notation ----

    [Theory]
    [InlineData("1e5", "100000")]
    [InlineData("1E+3", "1000")]
    [InlineData("1.5e-5", "0.000015")]
    [InlineData("0e-100", "0")]
    [InlineData("-0e0", "0")]
    [InlineData("2.5e2 + 5e1", "300")]
    public void ScientificNotation_ValidLiterals_ParseExactly(string expr, string expected)
    {
        CalculatorOutcome outcome = Eval(expr);
        Assert.Null(outcome.Error);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), outcome.Value);
    }

    [Theory]
    [InlineData("1e-29", CalculatorErrorCode.NumberOutOfRange)]
    [InlineData("1e-500", CalculatorErrorCode.NumberOutOfRange)]
    [InlineData("1e100", CalculatorErrorCode.NumberOutOfRange)]
    [InlineData("1e+", CalculatorErrorCode.SyntaxError)]
    [InlineData("1eFoo", CalculatorErrorCode.SyntaxError)]
    public void ScientificNotation_OutOfRangeOrMalformed_FailsDeterministically(string expr, CalculatorErrorCode expected)
    {
        CalculatorOutcome outcome = Eval(expr);
        Assert.NotNull(outcome.Error);
        Assert.Equal(expected, outcome.Error!.Code);
    }

    // ---- 2. Comparison, Logical, Ternary & Subscript Operators ----

    [Theory]
    [InlineData("1 < 2", 1)]
    [InlineData("2 < 2", 0)]
    [InlineData("2 <= 2", 1)]
    [InlineData("3 > 2", 1)]
    [InlineData("2 >= 3", 0)]
    [InlineData("5 == 5.0", 1)]
    [InlineData("5 != 4", 1)]
    [InlineData("!0", 1)]
    [InlineData("!42", 0)]
    [InlineData("1 + 2 == 3 && 4 * 2 > 7", 1)]
    [InlineData("0 || 1 && 0", 0)]
    [InlineData("1 || 1 / 0", 1)]
    [InlineData("0 && 1 / 0", 0)]
    [InlineData("1 > 2 ? 10 : 20", 20)]
    [InlineData("2 > 1 ? (0 ? 1 : 2) : 3", 2)]
    public void ComparisonLogicalTernary_PrecedenceAndShortCircuit_AreExact(string expr, decimal expected)
    {
        CalculatorOutcome outcome = Eval(expr);
        Assert.Null(outcome.Error);
        Assert.Equal(expected, outcome.Value);
    }

    [Theory]
    [InlineData("1 = 2")]
    [InlineData("1 & 2")]
    [InlineData("1 | 2")]
    public void SingleEqualsAmpOrPipe_AreUnexpectedCharacter(string expr)
    {
        CalculatorOutcome outcome = Eval(expr);
        Assert.NotNull(outcome.Error);
        Assert.Equal(CalculatorErrorCode.UnexpectedCharacter, outcome.Error!.Code);
    }

    [Fact]
    public void Ternary_SupportsArrayBranchesAndArrayLowering()
    {
        ExpressionCalculator.CalculatorBatchResult r = EvalBatchOne("sum(1 > 0 ? [10, 20, 30] : [1, 2])");
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.Equal(60m, r.ScalarValue);
    }

    [Fact]
    public void Subscript_ReadsElementAndRespectsPowerPrecedence()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["a"] = new[] { 2m, 3m, 5m },
        };

        ExpressionCalculator.CalculatorBatchResult ok = EvalBatchOne("a[2]^2 + [10, 20][0]", null, arrays);
        Assert.True(ok.IsOk, ok.Error?.Message);
        Assert.Equal(35m, ok.ScalarValue);

        ExpressionCalculator.CalculatorBatchResult oob = EvalBatchOne("a[3]", null, arrays);
        Assert.False(oob.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, oob.Error!.Code);
    }

    [Fact]
    public void Operators_RejectArrayOperandsWithDomainError()
    {
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("![1, 2]").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("[1, 2] == [1, 2]").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("[1] && 1").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("[1] ? 1 : 2").Error!.Code);
    }

    // ---- 3. Lazy if_gt, if_ge, if_eq ----

    [Fact]
    public void LazyIfFunctions_SkipUntakenBranchErrorsAndSupportArrayBranches()
    {
        var vars = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["x"] = 0m };
        Assert.Equal(99m, Eval("if_gt(x, 0, 1 / x, 99)", vars).Value);
        Assert.Equal(42m, Eval("if_ge(x, 0, 42, 1 / x)", vars).Value);
        Assert.Equal(7m, Eval("if_eq(x, 0.001, 7, 1 / x, 0.01)", vars).Value);

        ExpressionCalculator.CalculatorBatchResult arrCond = EvalBatchOne("sum(if_gt(2, 1, [3, 4], [1 / 0]))");
        Assert.True(arrCond.IsOk, arrCond.Error?.Message);
        Assert.Equal(7m, arrCond.ScalarValue);
    }

    // ---- 4. Higher-Order Outer Array Capture & Combinators ----

    [Fact]
    public void HigherOrder_CanReadOuterArrayVariables_AcrossRepeatedEvaluations()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["w"] = new[] { 2m, 5m, 10m },
        };

        // f(x) = x - w[1] = x - 5; root on [0, 10] is 5
        ExpressionCalculator.CalculatorBatchResult root = EvalBatchOne("root_brent(x - w[1], x, 0, 10)", null, arrays);
        Assert.True(root.IsOk, root.Error?.Message);
        Assert.InRange(root.ScalarValue, 4.999999m, 5.000001m);

        // Nested transient array inside functor body + outer array reference
        ExpressionCalculator.CalculatorBatchResult quad = EvalBatchOne("quad_trapz(sum([x, w[0]]) + arr_get(w, 2), x, 0, 2, 4)", null, arrays);
        Assert.True(quad.IsOk, quad.Error?.Message);
        // integral of (x + 2 + 10) = (x + 12) on [0, 2] = [x^2/2 + 12x]_0^2 = 2 + 24 = 26
        Assert.InRange(quad.ScalarValue, 25.999m, 26.001m);
    }

    [Fact]
    public void SigmaAndProdOver_ComputeExactSumsAndProductsWithBoundaryGuards()
    {
        Assert.Equal(55m, Eval("sigma(i, i, 1, 10)").Value);
        Assert.Equal(0m, Eval("sigma(i, i, 5, 2)").Value);
        Assert.Equal(120m, Eval("prod_over(i, i, 1, 5)").Value);
        Assert.Equal(1m, Eval("prod_over(i, i, 5, 2)").Value);

        Assert.Equal(CalculatorErrorCode.DomainError, Eval("sigma(i, i, 1, 513)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("prod_over(i, i, 1.5, 3)").Error!.Code);
    }

    [Fact]
    public void ArrMapFilterReduce_ExecuteCorrectlyAndPreserveUnresolvedPrecedence()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["xs"] = new[] { 1m, 2m, 3m, 4m, 5m },
            ["empty"] = Array.Empty<decimal>(),
        };

        ExpressionCalculator.CalculatorBatchResult mapped = EvalBatchOne("arr_map(x * x + xs[0], x, xs)", null, arrays);
        Assert.True(mapped.IsOk, mapped.Error?.Message);
        Assert.Equal(new[] { 2m, 5m, 10m, 17m, 26m }, mapped.ArrayValues);

        ExpressionCalculator.CalculatorBatchResult filtered = EvalBatchOne("arr_filter(x % 2 == 1 && x > 1, x, xs)", null, arrays);
        Assert.True(filtered.IsOk, filtered.Error?.Message);
        Assert.Equal(new[] { 3m, 5m }, filtered.ArrayValues);

        ExpressionCalculator.CalculatorBatchResult reduced = EvalBatchOne("arr_reduce(acc + item * 2, acc, item, xs, 10)", null, arrays);
        Assert.True(reduced.IsOk, reduced.Error?.Message);
        Assert.Equal(40m, reduced.ScalarValue);

        ExpressionCalculator.CalculatorBatchResult emptyReduced = EvalBatchOne("arr_reduce(acc + item, acc, item, empty, 7)", null, arrays);
        Assert.True(emptyReduced.IsOk);
        Assert.Equal(7m, emptyReduced.ScalarValue);

        ExpressionCalculator.CalculatorBatchResult loweredMap = EvalBatchOne("sum(arr_map(x * 2, x, xs))", null, arrays);
        Assert.True(loweredMap.IsOk, loweredMap.Error?.Message);
        Assert.Equal(30m, loweredMap.ScalarValue);

        // Duplicate accumulator and item dummy variables must fail with DomainError
        ExpressionCalculator.CalculatorBatchResult dup = EvalBatchOne("arr_reduce(x + 1, x, x, xs, 0)", null, arrays);
        Assert.False(dup.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, dup.Error!.Code);

        // Failing arity check with unresolved identifier reports UndefinedName
        ExpressionCalculator.CalculatorBatchResult undef = EvalBatchOne("arr_reduce(acc + item + missing_var, acc, item, xs)", null, arrays);
        Assert.False(undef.IsOk);
        Assert.Equal(CalculatorErrorCode.UndefinedName, undef.Error!.Code);
        Assert.Equal("missing_var", undef.Error.Token);
    }

    [Fact]
    public void DslArrayArena_PreservesIntegrityAcrossRepeatedNestedHigherOrderBatches()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["xs"] = [1m, 2m, 3m],
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> batch = ExpressionCalculator.EvaluateBatch(
            EmptyScalars,
            arrays,
            new ExpressionCalculator.CalculatorBatchItem[]
            {
                new("a", "arr_map(sum(arr_map(y * x, y, [1, 2])), x, xs)"),
                new("b", "sigma(i + sum([xs[0], xs[2]]), i, 1, 3)"),
                new("c", "a[2] + b"),
            },
            CalculatorLimits.Default);

        Assert.All(batch, r => Assert.True(r.IsOk, r.Error?.Message));
        Assert.Equal([3m, 6m, 9m], batch[0].ArrayValues);
        Assert.Equal(18m, batch[1].ScalarValue); // (1+4) + (2+4) + (3+4)
        Assert.Equal(27m, batch[2].ScalarValue);
    }
}
