#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// Backlog D-2b: <c>quad_root_min</c> / <c>quad_root_max</c> (through <see cref="DslElementaryMath.SolveQuadratic"/>) reduced a genuine quadratic to a linear equation when its roots reached
/// about 1e6 (the old rule compared <c>|a|</c> with <c>1e-12 * max coefficient</c>, but <c>c</c> carries the dimension of x^2), and returned a wrong root without any error.
/// The rule is now dimension-aware and scale-free: the quadratic is reduced to the linear equation only when <c>a = 0</c> or <c>|b / a| &gt; 1e12</c> (the second root lies beyond the
/// root scale limit of the polynomial solvers), evaluated without decimal underflow (<c>1e12 * |a| &lt; |b|</c> with an upper pre-cap). A change only shrinks the set that is reduced.
/// </summary>
public sealed class CalculatorDslQuadraticRootScaleTests
{
    private const int RootBufferLength = 2;
    private const double RelativeTolerance = 1e-9;

    private static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static decimal Evaluate(string expression)
    {
        ExpressionCalculator.CalculatorBatchResult result = ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
            new CalculatorLimits())[0];
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        return result.ScalarValue;
    }

    private static void AssertRelative(decimal expected, decimal actual, string label)
    {
        double scale = Math.Max(1.0, Math.Abs((double)expected));
        Assert.True(Math.Abs((double)(actual - expected)) <= RelativeTolerance * scale, $"{label}: expected {expected}, actual {actual}");
    }

    private static (MathErrorCode Error, decimal[] Roots) Solve(decimal a, decimal b, decimal c)
    {
        decimal[] buffer = new decimal[RootBufferLength];
        MathErrorCode error = DslElementaryMath.SolveQuadratic(a, b, c, buffer, out int count);
        return (error, buffer.Take(count).ToArray());
    }

    public static IEnumerable<object[]> MeasuredWrongCases() =>
    [
        // a, b, c, smaller root, larger root (the wrong answers measured before the fix: 1e6, 5.5e5, 666666.67, 1.2e9)
        ["1", "-4000000", "4000000000000", "2000000", "2000000"],
        ["1", "-2200000", "1210000000000", "1100000", "1100000"],
        ["1", "-3000000", "2000000000000", "1000000", "2000000"],
        ["1", "-5000000000", "6000000000000000000", "2000000000", "3000000000"],
        ["1", "-8000000000", "15000000000000000000", "3000000000", "5000000000"],
    ];

    [Theory]
    [MemberData(nameof(MeasuredWrongCases))]
    public void AGenuineQuadraticWithLargeRootsKeepsBothRoots(string a, string b, string c, string min, string max)
    {
        string args = string.Create(CultureInfo.InvariantCulture, $"{a}, {b}, {c}");
        AssertRelative(decimal.Parse(min, CultureInfo.InvariantCulture), Evaluate($"quad_root_min({args})"), "quad_root_min(" + args + ")");
        AssertRelative(decimal.Parse(max, CultureInfo.InvariantCulture), Evaluate($"quad_root_max({args})"), "quad_root_max(" + args + ")");
    }

    [Fact]
    public void ARootScaleBoundaryDecidesTheReductionExactly()
    {
        // |b / a| = 1e12 exactly: still a quadratic (strict inequality), roots 0 and 1e12.
        (MathErrorCode error, decimal[] roots) = Solve(1m, -1_000_000_000_000m, 0m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(new[] { 0m, 1_000_000_000_000m }, roots);

        // |b / a| = 1e12 + 1: the second root is beyond the scale limit, the quadratic is reduced to the linear equation b x + c = 0 (root 0 here).
        (error, roots) = Solve(1m, -1_000_000_000_001m, 0m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(new[] { 0m }, roots);

        // The same two decisions at the other end of the decimal range (1e-28 is the smallest positive decimal): the ratio rule does not depend on the magnitude.
        const decimal tiny = 0.0000000000000000000000000001m;
        (error, roots) = Solve(tiny, -0.0000000000000001m, 0m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(2, roots.Length);

        (error, roots) = Solve(tiny, -0.00000000000000011m, 0m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Single(roots);
    }

    [Fact]
    public void ALeadingCoefficientOfZeroOrAtTheUnderflowEdgeIsDecidedWithoutRounding()
    {
        // a = 0 with a tiny b: a linear equation (root 2), not a division by zero caused by 1e-12 * |b| underflowing to 0.
        (MathErrorCode error, decimal[] roots) = Solve(0m, 0.00000000000000000001m, -0.00000000000000000002m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(new[] { 2m }, roots);

        // a = 0 and b = 0: nothing to solve.
        Assert.Equal(MathErrorCode.DivisionByZero, Solve(0m, 0m, 1m).Error);
        Assert.Equal(MathErrorCode.DivisionByZero, Solve(0m, 0m, 0m).Error);
    }

    [Fact]
    public void ACoefficientRatioBeyondTheDecimalScaleGivesARootOrADefinedErrorNeverAWrongRoot()
    {
        // c / a = -1e30 does not fit one decimal, but the roots +-1e15 do: they must be found (the old code returned DIVISION_BY_ZERO here).
        (MathErrorCode error, decimal[] roots) = Solve(0.01m, 0m, -10_000_000_000_000_000_000_000_000_000m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(2, roots.Length);
        AssertRelative(-1_000_000_000_000_000m, roots[0], "lower root");
        AssertRelative(1_000_000_000_000_000m, roots[1], "upper root");

        (error, roots) = Solve(1m, 0m, -1_000_000_000_000_000_000_000_000_000m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(2, roots.Length);
        AssertRelative(-31_622_776_601_683.793m, roots[0], "lower root");
        AssertRelative(31_622_776_601_683.793m, roots[1], "upper root");

        // A reduced (linear) quadratic with a huge b does not overflow: |b / a| = 1e28 > 1e12, so the root is -c / b.
        (error, roots) = Solve(1m, 10_000_000_000_000_000_000_000_000_000m, 10_000_000_000_000_000_000_000_000_000m);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(new[] { -1m }, roots);
    }

    [Fact]
    public void TheDecisionIsInvariantUnderScalingAllCoefficients()
    {
        foreach (decimal scale in new[] { 0.00000000000000000001m, 1m, 1_000_000_000_000m })
        {
            (MathErrorCode error, decimal[] roots) = Solve(1m * scale, -4_000_000m * scale, 4_000_000_000_000m * scale);
            Assert.Equal(MathErrorCode.None, error);
            Assert.Single(roots);
            AssertRelative(2_000_000m, roots[0], $"scale {scale}");

            (error, roots) = Solve(1m * scale, -3m * scale, 2m * scale);
            Assert.Equal(MathErrorCode.None, error);
            Assert.Equal(new[] { 1m, 2m }, roots);
        }
    }

    [Fact]
    public void ANoRealRootStillIsADomainErrorOfTheFunctions()
    {
        ExpressionCalculator.CalculatorBatchResult result = ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", "quad_root_min(1, 0, 1)") },
            new CalculatorLimits())[0];
        Assert.False(result.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, result.Error!.Code);
    }
}
