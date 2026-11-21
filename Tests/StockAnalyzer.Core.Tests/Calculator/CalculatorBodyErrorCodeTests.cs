#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// An error raised inside the body of a higher-order function keeps its own code, position (in the whole expression), message and token,
/// and an unresolved identifier of a failing argument-count check is reported as UNDEFINED_NAME (depth-first, left to right).
/// </summary>
public sealed class CalculatorBodyErrorCodeTests
{
    private static readonly IReadOnlyDictionary<string, decimal> NoScalars = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<decimal>> Heat = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
    {
        ["heat"] = new decimal[] { 0m, 1m, 2m },
    };

    private static ExpressionCalculator.CalculatorBatchResult Run(string expression, IReadOnlyDictionary<string, IReadOnlyList<decimal>>? arrays = null)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            NoScalars,
            arrays,
            new[] { new ExpressionCalculator.CalculatorBatchItem("e", expression) },
            CalculatorLimits.Default);
        return results[0];
    }

    private static void AssertError(string expression, CalculatorErrorCode code, int position, string? token, IReadOnlyDictionary<string, IReadOnlyList<decimal>>? arrays = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, arrays);
        Assert.False(result.IsOk, $"Expected an error for '{expression}'.");
        Assert.NotNull(result.Error);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(position, result.Error.Position);
        if (token is not null)
        {
            Assert.Equal(token, result.Error.Token);
        }
    }

    [Theory]
    [InlineData("quad_trapz(zzz * x, x, 0, 1, 8)", CalculatorErrorCode.UndefinedName, 11, "zzz")]
    [InlineData("mc_integrate_nd(zzz * x, [x], [0], [1], 64, 1)", CalculatorErrorCode.UndefinedName, 16, "zzz")]
    [InlineData("root_brent(qqq(x), x, 0, 3)", CalculatorErrorCode.UnknownFunction, 11, "qqq")]
    [InlineData("quad_simpson(abs(x, 1), x, 0, 1, 8)", CalculatorErrorCode.WrongArgumentCount, 13, "abs")]
    [InlineData("quad_trapz(quad_trapz(zzz * y, y, 0, 1, 4), x, 0, 1, 4)", CalculatorErrorCode.UndefinedName, 22, "zzz")]
    [InlineData("quad_trapz(sum(zzz), x, 0, 1, 8)", CalculatorErrorCode.UndefinedName, 15, "zzz")]
    public void BodyErrorKeepsItsOwnCodePositionAndToken(string expression, CalculatorErrorCode code, int position, string token)
    {
        AssertError(expression, code, position, token);
    }

    [Fact]
    public void BodyDivisionByZeroKeepsItsCodeAndPointsInsideTheBody()
    {
        ExpressionCalculator.CalculatorBatchResult result = Run("quad_trapz(1 / (x - x), x, 0, 1, 8)");

        Assert.False(result.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, result.Error!.Code);
        Assert.True(result.Error.Position >= "quad_trapz(".Length, $"Position {result.Error.Position} is not inside the body.");
    }

    [Fact]
    public void BodyBudgetExceededKeepsItsCode()
    {
        ExpressionCalculator.CalculatorBatchResult result = Run("mc_integrate(x^2 + sin(x) + cos(x), x, 0, 1, 2048, 1, 0)");

        Assert.False(result.IsOk);
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, result.Error!.Code);
    }

    [Theory]
    [InlineData("sum(zzz)", 4)]
    [InlineData("sum(zzz + 1)", 4)]
    [InlineData("sum(abs(zzz))", 8)]
    [InlineData("sum(1 + 2 * zzz)", 12)]
    public void UnresolvedIdentifierInAFailingArityCheckIsUndefinedNameAtTheFirstOccurrence(string expression, int position)
    {
        AssertError(expression, CalculatorErrorCode.UndefinedName, position, "zzz");
    }

    [Fact]
    public void FirstUnresolvedIdentifierWinsLeftToRight()
    {
        AssertError("sum(first_missing + second_missing)", CalculatorErrorCode.UndefinedName, 4, "first_missing");
    }

    [Fact]
    public void ArrayVariableInvisibleInsideABodyIsUndefinedName()
    {
        AssertError("mc_integrate_nd(sum(heat) * x, [x], [0], [1], 64, 1)", CalculatorErrorCode.UndefinedName, 20, "heat", Heat);
    }

    [Theory]
    [InlineData("min(1)", 0, "min")]
    [InlineData("sum(1)", 0, "sum")]
    [InlineData("sum(nan)", 0, "sum")]
    [InlineData("sum(e)", 0, "sum")]
    [InlineData("sum(quad_trapz(x^2, x, 0, 1, 10))", 0, "sum")]
    public void ResolvedArgumentsKeepTheWrongArgumentCountCode(string expression, int position, string token)
    {
        AssertError(expression, CalculatorErrorCode.WrongArgumentCount, position, token);
    }

    [Fact]
    public void ArrayArgumentStillLowersWithoutAnError()
    {
        ExpressionCalculator.CalculatorBatchResult result = Run("sum(heat)", Heat);

        Assert.True(result.IsOk);
        Assert.Equal(3m, result.ScalarValue);
    }

    [Fact]
    public void FailedBodyDoesNotLeakIntoTheNextExpressionOfTheSameBatch()
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            NoScalars,
            null,
            new[]
            {
                new ExpressionCalculator.CalculatorBatchItem("bad", "quad_trapz(zzz * x, x, 0, 1, 8)"),
                new ExpressionCalculator.CalculatorBatchItem("good", "quad_trapz(x, x, 0, 1, 8)"),
                new ExpressionCalculator.CalculatorBatchItem("bad_again", "mc_integrate_nd(qqq(x), [x], [0], [1], 64, 1)"),
            },
            CalculatorLimits.Default);

        Assert.Equal(CalculatorErrorCode.UndefinedName, results[0].Error!.Code);
        Assert.True(results[1].IsOk);
        Assert.Equal(0.5m, results[1].ScalarValue);
        Assert.Equal(CalculatorErrorCode.UnknownFunction, results[2].Error!.Code);
    }

    [Fact]
    public void KernelOwnFailuresStayWithTheirOwnCodes()
    {
        // A failure that does not come from the body (invalid sample count) is still reported by the higher-order function itself at the call position.
        AssertError("mc_integrate(x, x, 0, 1, 2049, 1, 0)", CalculatorErrorCode.DomainError, 0, "mc_integrate");
    }

    /// <summary>One valid call of every higher-order function whose body is the undefined name <c>zzz</c>; the body is the first argument.</summary>
    public static readonly string[] HigherOrderCallsWithUndefinedBody =
    {
        "diff_central(zzz * x, x, 1)",
        "fd_central(zzz * x, x, 1, 0.01)",
        "fd_forward(zzz * x, x, 1, 0.01)",
        "fd_backward(zzz * x, x, 1, 0.01)",
        "fd_second(zzz * x, x, 1, 0.01)",
        "fd_richardson(zzz * x, x, 1, 0.1, 2)",
        "mc_integrate(zzz * x, x, 0, 1, 64, 1)",
        "mc_integrate_se(zzz * x, x, 0, 1, 64, 1)",
        "mc_integrate_nd(zzz * x, [x], [0], [1], 64, 1)",
        "mc_integrate_nd_se(zzz * x, [x], [0], [1], 64, 1)",
        "root_brent(zzz * x, x, 0, 3)",
        "root_newton(zzz * x, x, 1)",
        "root_secant(zzz * x, x, 1, 2)",
        "quad_trapz(zzz * x, x, 0, 1, 8)",
        "quad_simpson(zzz * x, x, 0, 1, 8)",
        "quad_de(zzz * x, x, 0, 1)",
        "quad_gauss_legendre(zzz * x, x, 0, 1, 8)",
        "quad_gk21(zzz * x, x, 0, 1)",
        "quad_romberg(zzz * x, x, 0, 1)",
        "quad_gauss_chebyshev(zzz * x, x, 1, 4)",
        "quad_gauss_laguerre(zzz * x, x, 0, 4)",
        "quad_gauss_hermite(zzz * x, x, 4)",
        "quad_gauss_jacobi(zzz * x, x, 0, 0, 4)",
        "quad_gauss_lobatto(zzz * x, x, 0, 1, 5)",
        "ode_rk4([zzz * y], t, [y], 0, 1, [1], 8)",
        "ode_rk45([zzz * y], t, [y], 0, 1, [1])",
        "opt_brent(zzz * x, x, 0, 1)",
        "opt_golden(zzz * x, x, 0, 1)",
        "opt_nelder_mead(zzz * x, [x], [1])",
        "opt_bfgs(zzz * x, [x], [1])",
    };

    public static IEnumerable<object[]> HigherOrderCalls => HigherOrderCallsWithUndefinedBody.Select(call => new object[] { call });

    [Fact]
    public void TheSweepCoversEveryHigherOrderFunctionExactlyOnce()
    {
        string[] names = HigherOrderCallsWithUndefinedBody.Select(call => call[..call.IndexOf('(')]).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.True(CalculatorHigherOrderFunctions.IsHigherOrderFunction(name), name));

        // Every function the evaluator treats as higher-order is in the sweep (a new one fails here until it is added).
        IEnumerable<string> evaluatorNames = ExpressionCalculator.Functions.Select(f => f.Name).Where(CalculatorHigherOrderFunctions.IsHigherOrderFunction);
        Assert.Equal(evaluatorNames.OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(HigherOrderCalls))]
    public void EveryHigherOrderFunctionReportsAnUndefinedBodyNameAtItsOwnPosition(string expression)
    {
        AssertError(expression, CalculatorErrorCode.UndefinedName, expression.IndexOf("zzz", StringComparison.Ordinal), "zzz");
    }

    [Theory]
    [MemberData(nameof(HigherOrderCalls))]
    public void EveryHigherOrderFunctionKeepsTheMessageOfTheBodyError(string expression)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression);

        Assert.Contains("zzz", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultFailureSlotIsUnsetAndAZeroCodeIsNotReadAsStored()
    {
        DslFunctorFailure unset = default;

        Assert.False(unset.IsSet);
        Assert.False(DslFunctorFailure.Unset.IsSet);
        Assert.True(DslFunctorFailure.Of((int)CalculatorErrorCode.SyntaxError, 3, "m", "t").IsSet);
        Assert.False(DslExecutionBudget.CreateDefault().LastFunctorFailure.IsSet);
    }

    [Fact]
    public void FunctorFailureWithoutAStoredDetailIsADomainErrorOfTheCallAndNeverARestoredCode()
    {
        var budget = DslExecutionBudget.CreateDefault();
        FunctorFailureBridge.Clear(ref budget);

        CalculatorOutcome outcome = FunctorFailureBridge.ToOutcome(MathErrorCode.FunctorFailure, ref budget, 7, "quad_trapz");

        Assert.Equal(CalculatorErrorCode.DomainError, outcome.Error!.Code);
        Assert.Equal(7, outcome.Error.Position);
    }

    [Fact]
    public void AStoredBodyFailureIsReturnedUnchangedAndConsumedOnce()
    {
        var budget = DslExecutionBudget.CreateDefault();
        FunctorFailureBridge.Record(ref budget, new CalculatorError(CalculatorErrorCode.SyntaxError, 5, "stored", "tok"));

        CalculatorOutcome first = FunctorFailureBridge.ToOutcome(MathErrorCode.FunctorFailure, ref budget, 0, "f");
        CalculatorOutcome second = FunctorFailureBridge.ToOutcome(MathErrorCode.FunctorFailure, ref budget, 0, "f");

        Assert.Equal(CalculatorErrorCode.SyntaxError, first.Error!.Code);
        Assert.Equal(5, first.Error.Position);
        Assert.Equal("stored", first.Error.Message);
        Assert.Equal("tok", first.Error.Token);
        Assert.Equal(CalculatorErrorCode.DomainError, second.Error!.Code);
    }

    [Fact]
    public void ABodyFailureOfAnInnerCallBecomesTheBodyFailureOfTheOuterCall()
    {
        ExpressionCalculator.CalculatorBatchResult result = Run("quad_trapz(quad_trapz(qqq(y), y, 0, 1, 4) * x, x, 0, 1, 4)");

        Assert.Equal(CalculatorErrorCode.UnknownFunction, result.Error!.Code);
        Assert.Equal("quad_trapz(quad_trapz(".Length, result.Error.Position);
        Assert.Equal("qqq", result.Error.Token);
    }

    [Fact]
    public void ADummyVariableOfANestedCallIsNotReportedAsUndefinedInAnArityError()
    {
        // y is the dummy of the inner call; the outer sum has one argument and fails the count check: the only reportable code is WRONG_ARGUMENT_COUNT.
        AssertError("sum(quad_trapz(y * y, y, 0, 1, 8))", CalculatorErrorCode.WrongArgumentCount, 0, "sum");
        AssertError("sum(mc_integrate_nd(a * b, [a, b], [0, 0], [1, 1], 64, 1))", CalculatorErrorCode.WrongArgumentCount, 0, "sum");
    }

    [Fact]
    public void ADummyNameIsNotVisibleOutsideItsBody()
    {
        // x is bound only inside the body of the inner call; the one-argument sum uses it again, unbound, and fails the count check, so the scan must find it.
        AssertError("sum(quad_trapz(x, x, 0, 1, 8) + x)", CalculatorErrorCode.UndefinedName, "sum(quad_trapz(x, x, 0, 1, 8) + ".Length, "x");
    }
}
