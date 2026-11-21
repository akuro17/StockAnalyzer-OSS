#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 review-adoption T8 (improvement task, plan W-27): one tolerance contract for every DSL function that takes a <c>tol</c> argument: the accepted range is
/// <c>[1e-12, 1e-2]</c> (inclusive), anything outside is a <c>DomainError</c>, and the default of <c>ode_rk45</c> and the minimizers is <c>1e-8</c>.
/// These tests were written against the code BEFORE the constants were consolidated into <see cref="DslTolerance"/> and must stay green.
/// </summary>
public sealed class CalculatorDslToleranceContractTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static readonly CalculatorLimits Heavy = new()
    {
        MaxExecutionOps = 800_000,
        MaxExpressionChars = 2048,
        MaxTokens = 1024,
    };

    private const string Lowest = "0.000000000001";
    private const string Highest = "0.01";
    private const string BelowLowest = "0.0000000000001";
    private const string AboveHighest = "0.0100000001";

    public static IEnumerable<object[]> ToleranceTakingFunctions() =>
    [
        ["root_brent(x^2 - 2, x, 0, 2, {0})"],
        ["root_newton(x^2 - 2, x, 1, {0})"],
        ["root_secant(x^2 - 2, x, 1, 2, {0})"],
        ["quad_de(x, x, 0, 1, {0})"],
        ["quad_gk21(x, x, 0, 1, {0})"],
        ["quad_romberg(x, x, 0, 1, {0})"],
        ["ode_rk45([y], t, [y], 0, 1, [1], {0})"],
        ["opt_brent((x - 1)^2, x, -3, 3, {0})"],
        ["opt_golden((x - 1)^2, x, -3, 3, {0})"],
        ["opt_nelder_mead((x - 1)^2, [x], [0], {0})"],
        ["opt_bfgs((x - 1)^2, [x], [0], {0})"],
    ];

    private static string Instantiate(string template, string tolerance) => string.Format(CultureInfo.InvariantCulture, template, tolerance);

    private static CalculatorErrorCode? ErrorOf(string expression)
    {
        ExpressionCalculator.CalculatorBatchResult result = ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
            Heavy)[0];
        return result.IsOk ? null : result.Error!.Code;
    }

    [Theory]
    [MemberData(nameof(ToleranceTakingFunctions))]
    public void TheAcceptedRangeIsOneInclusiveIntervalForEveryFunction(string template)
    {
        // The boundaries are accepted (the result may still fail to converge for 1e-12, but never with a domain error).
        Assert.NotEqual(CalculatorErrorCode.DomainError, ErrorOf(Instantiate(template, Lowest)));
        Assert.NotEqual(CalculatorErrorCode.DomainError, ErrorOf(Instantiate(template, Highest)));

        // Just outside either end is a domain error.
        Assert.Equal(CalculatorErrorCode.DomainError, ErrorOf(Instantiate(template, BelowLowest)));
        Assert.Equal(CalculatorErrorCode.DomainError, ErrorOf(Instantiate(template, AboveHighest)));
        Assert.Equal(CalculatorErrorCode.DomainError, ErrorOf(Instantiate(template, "0")));
        Assert.Equal(CalculatorErrorCode.DomainError, ErrorOf(Instantiate(template, "-0.001")));
    }

    [Fact]
    public void TheContractValuesAreThoseOfTheSpecification()
    {
        Assert.Equal(1e-12, DslTolerance.MinTol);
        Assert.Equal(1e-2, DslTolerance.MaxTol);
        Assert.Equal(1e-8, DslTolerance.DefaultTol);
        Assert.Equal(Math.BitIncrement(1.0) - 1.0, MathConstants.MachineEpsilon);
    }
}
