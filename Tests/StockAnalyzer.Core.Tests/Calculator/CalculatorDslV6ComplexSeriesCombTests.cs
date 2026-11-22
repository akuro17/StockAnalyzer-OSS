#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6ComplexSeriesCombTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static decimal EvalScalar(string expr)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Value);
        return outcome.Value!.Value;
    }

    private static CalculatorErrorCode EvalError(string expr)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, CalculatorLimits.Default);
        Assert.NotNull(outcome.Error);
        return outcome.Error!.Code;
    }

    [Fact]
    public void ComplexAddAndSub_MatchKernelAndHandleOverflow()
    {
        Assert.Equal(5m, EvalScalar("c_add_re(2, 3, 3, -3)"));
        Assert.Equal(0m, EvalScalar("c_add_im(2, 3, 3, -3)"));
        Assert.Equal(-1m, EvalScalar("c_sub_re(2, 3, 3, 5)"));
        Assert.Equal(-2m, EvalScalar("c_sub_im(2, 3, 3, 5)"));

        MathResult<DslComplex> directAdd = DslElementaryMath.ComplexAdd(new DslComplex(1.25m, -4.5m), new DslComplex(2.75m, 1.5m));
        Assert.True(directAdd.IsOk);
        Assert.Equal(directAdd.Value.Re, EvalScalar("c_add_re(1.25, -4.5, 2.75, 1.5)"));
        Assert.Equal(directAdd.Value.Im, EvalScalar("c_add_im(1.25, -4.5, 2.75, 1.5)"));

        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, EvalError("c_add_re(70000000000000000000000000000, 0, 70000000000000000000000000000, 0)"));
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, EvalError("c_sub_im(0, -70000000000000000000000000000, 0, 70000000000000000000000000000)"));
    }

    [Fact]
    public void SeriesSummations_EvaluateExactDecimalAndValidateDomains()
    {
        Assert.Equal(0m, EvalScalar("sum_squares(0)"));
        Assert.Equal(385m, EvalScalar("sum_squares(10)"));
        Assert.Equal(333333833333500000m, EvalScalar("sum_squares(1000000)"));

        Assert.Equal(0m, EvalScalar("sum_cubes(0)"));
        Assert.Equal(3025m, EvalScalar("sum_cubes(10)"));
        Assert.Equal(250000500000250000000000m, EvalScalar("sum_cubes(1000000)"));

        Assert.Equal(0m, EvalScalar("sum_consec_prod(0)"));
        Assert.Equal(440m, EvalScalar("sum_consec_prod(10)"));
        Assert.Equal(333334333334000000m, EvalScalar("sum_consec_prod(1000000)"));

        Assert.Equal(2m, EvalScalar("sum_geom_inf(1, 0.5)"));
        Assert.Equal(2m, EvalScalar("sum_geom_inf(3, -0.5)"));

        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_squares(-1)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_squares(1.5)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_squares(1000001)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_cubes(1000001)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_consec_prod(1000001)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_geom_inf(1, 1)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_geom_inf(1, -1)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sum_geom_inf(1, 1.2)"));
    }

    [Fact]
    public void Combinatorics_EvaluateExactDecimalAndHonorStrictBounds()
    {
        Assert.Equal(1m, EvalScalar("comb_rep(100, 0)"));
        Assert.Equal(15m, EvalScalar("comb_rep(3, 4)"));
        Assert.Equal(220m, EvalScalar("comb_rep(10, 3)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("comb_rep(0, 2)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("comb_rep(101, 0)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("comb_rep(50, 52)"));

        Assert.Equal(1m, EvalScalar("perm_rep(0, 0)"));
        Assert.Equal(0m, EvalScalar("perm_rep(0, 5)"));
        Assert.Equal(81m, EvalScalar("perm_rep(3, 4)"));
        Assert.Equal(39614081257132168796771975168m, EvalScalar("perm_rep(2, 95)"));
        Assert.Equal(1m, EvalScalar("perm_rep(1, 96)"));
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, EvalError("perm_rep(2, 96)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("perm_rep(1, 97)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("perm_rep(-1, 2)"));
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, EvalError("perm_rep(10, 30)"));

        Assert.Equal(1m, EvalScalar("perm_circular(1)"));
        Assert.Equal(24m, EvalScalar("perm_circular(5)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("perm_circular(0)"));
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, EvalError("perm_circular(29)"));

        Assert.Equal(1m, EvalScalar("perm_necklace(1)"));
        Assert.Equal(1m, EvalScalar("perm_necklace(2)"));
        Assert.Equal(1m, EvalScalar("perm_necklace(3)"));
        Assert.Equal(12m, EvalScalar("perm_necklace(5)"));
        Assert.Equal(60m, EvalScalar("perm_necklace(6)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("perm_necklace(0)"));
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, EvalError("perm_necklace(29)"));
    }
}
