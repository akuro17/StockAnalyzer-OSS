#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 Task 1b: owner decisions on <c>solve_quartic</c> multiplicity and factor pairing, <c>solve_cubic</c> branches,
/// the <c>|a| &lt;= 1e-12</c> boundary, BFGS recovery rules and the <c>ode_rk45</c> tolerance range.
/// The quartic regression grid is the measured Task 1b grid (<c>docs/sa_probe_dsl_v5_quartic_multiplicity_grid</c>).
/// </summary>
public sealed class CalculatorDslV5Task1bTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static readonly CalculatorLimits Heavy = new()
    {
        MaxExecutionOps = 800_000,
        MaxExpressionChars = 2048,
        MaxTokens = 1024,
    };

    private static ExpressionCalculator.CalculatorBatchResult Run(string expression, CalculatorLimits? limits = null)
        => ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
            limits ?? CalculatorLimits.Default)[0];

    private static decimal[] Arr(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}");
        Assert.True(result.IsArray);
        return result.ArrayValues!.ToArray();
    }

    private static void Fails(string expression, CalculatorErrorCode expected)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression);
        Assert.False(result.IsOk, $"{expression} unexpectedly succeeded.");
        Assert.Equal(expected, result.Error!.Code);
    }

    // ---------------------------------------------------------------- quartic: pairing, multiplicity, tolerance

    /// <summary>Real roots (with multiplicity), the number of (x^2 + 1) factors and the root tolerance appropriate to the largest multiplicity.</summary>
    private sealed record QuarticCase(string Name, decimal[] RealRoots, int ComplexPairs, double Tolerance)
    {
        public decimal[] Coefficients(decimal scale)
        {
            decimal[] poly = [1m];
            foreach (decimal root in RealRoots)
            {
                poly = Multiply(poly, [1m, -root]);
            }

            for (int i = 0; i < ComplexPairs; i++)
            {
                poly = Multiply(poly, [1m, 0m, 1m]);
            }

            return poly.Select(c => c * scale).ToArray();
        }

        private static decimal[] Multiply(decimal[] a, decimal[] b)
        {
            var result = new decimal[a.Length + b.Length - 1];
            for (int i = 0; i < a.Length; i++)
            {
                for (int j = 0; j < b.Length; j++)
                {
                    result[i + j] += a[i] * b[j];
                }
            }

            return result;
        }
    }

    private const double SimpleRootTolerance = 1e-7;
    private const double DoubleRootTolerance = 1e-5;
    private const double TripleRootTolerance = 1e-4;

    private static readonly QuarticCase[] QuarticCases =
    [
        new("(x-1)(x-2)(x-3)(x-4)", [1m, 2m, 3m, 4m], 0, SimpleRootTolerance),
        new("(x^2+1)(x-1)(x-2)", [1m, 2m], 1, SimpleRootTolerance),
        new("(x-1)^2 (x-2)(x-3)", [1m, 1m, 2m, 3m], 0, DoubleRootTolerance),
        new("(x-1)^2 (x-2)^2", [1m, 1m, 2m, 2m], 0, DoubleRootTolerance),
        new("(x-2)^2 (x^2+1)", [2m, 2m], 1, DoubleRootTolerance),
        new("(x-0.3)^2 (x-2.7)(x-1.1)", [0.3m, 0.3m, 2.7m, 1.1m], 0, DoubleRootTolerance),
        new("(x-0.3)^2 (x^2+1)", [0.3m, 0.3m], 1, DoubleRootTolerance),
        new("(x-0.3)^2 (x-2)(x-3)", [0.3m, 0.3m, 2m, 3m], 0, DoubleRootTolerance),
        new("(x-0.3)^2 (x-0.7)^2", [0.3m, 0.3m, 0.7m, 0.7m], 0, DoubleRootTolerance),
        new("x (x-1)^3", [0m, 1m, 1m, 1m], 0, TripleRootTolerance),
        new("(x-1)^3 (x-2)", [1m, 1m, 1m, 2m], 0, TripleRootTolerance),
        new("(x-0.3)^3 (x-2)", [0.3m, 0.3m, 0.3m, 2m], 0, TripleRootTolerance),
        new("x (x-0.7)^3", [0m, 0.7m, 0.7m, 0.7m], 0, TripleRootTolerance),
        new("(x+0.7)^3 (x-1.3)", [-0.7m, -0.7m, -0.7m, 1.3m], 0, TripleRootTolerance),
        new("(x-1.7)^3 (x+0.2)", [1.7m, 1.7m, 1.7m, -0.2m], 0, TripleRootTolerance),
    ];

    private static readonly decimal[] QuarticScales = [0.0001m, 0.0123m, 0.1m, 0.3m, 0.7m, 1m, 1.3m, 2.7m, 7m, 9.9m, 123.456m, 10000m];

    public static IEnumerable<object[]> QuarticGrid()
        => from c in QuarticCases from s in QuarticScales select new object[] { c.Name, s };

    [Theory]
    [MemberData(nameof(QuarticGrid))]
    public void QuarticRootsKeepMultiplicityAndValuesAcrossCoefficientScales(string name, decimal scale)
    {
        QuarticCase c = QuarticCases.Single(x => x.Name == name);
        decimal[] k = c.Coefficients(scale);
        Span<decimal> roots = stackalloc decimal[4];

        MathErrorCode err = DslElementaryMath.SolveQuarticDsl(k[0], k[1], k[2], k[3], k[4], roots, out int count);

        double[] expected = c.RealRoots.Select(r => (double)r).OrderBy(r => r).ToArray();
        Assert.Equal(MathErrorCode.None, err);
        Assert.Equal(expected.Length, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(
                Math.Abs((double)roots[i] - expected[i]) <= c.Tolerance,
                $"{name} x{scale}: root {i} = {roots[i]} (expected {expected[i]}, tolerance {c.Tolerance}).");
        }
    }

    [Fact]
    public void QuarticFerrariPathPairsFactorsWithTheirOwnRoots()
    {
        // Regression of the swapped +-w pairing: q != 0 quartics used to return roots that were not roots of the polynomial.
        Assert.Equal(new[] { 1m, 1m, 2m, 3m }, Round(Arr("solve_quartic(1, -7, 17, -17, 6)")));
        Assert.Equal(new[] { 1m, 2m }, Round(Arr("solve_quartic(1, -3, 3, -3, 2)")));
        Assert.Equal(new[] { 0m, 1m, 1m, 1m }, Round(Arr("solve_quartic(1, -3, 3, -1, 0)")));
    }

    [Fact]
    public void QuarticToleranceRoundsOnlyNegligiblyNegativeFactorDiscriminants()
    {
        // y^4 - 2 y^2 + (1 + eta): u^2 - 2 u + 1 + eta has D = -4 eta, relative -eta / (1 + eta) (tolerance 1e-8).
        Assert.Equal(new[] { -1m, -1m, 1m, 1m }, Round(Arr("solve_quartic(1, 0, -2, 0, 1.000000005)")));
        Assert.Empty(Arr("solve_quartic(1, 0, -2, 0, 1.00000002)"));
        Assert.Empty(Arr("solve_quartic(1, 0, -2, 0, 1.000001)"));

        // The tolerance is internal to the DSL variant: the legacy kernel keeps reporting distinct roots only.
        Span<decimal> roots = stackalloc decimal[4];
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuartic(1m, 0m, -2m, 0m, 1m, roots, out int legacyCount));
        Assert.Equal(2, legacyCount);
    }

    [Fact]
    public void QuarticAndCubicLeadingCoefficientBoundaryIsAnExplicitDomainError()
    {
        Fails("solve_quartic(0, 1, 2, 3, 4)", CalculatorErrorCode.DomainError);
        Fails("solve_quartic(0.000000000001, 1, 2, 3, 4)", CalculatorErrorCode.DomainError);
        Fails("solve_quartic(-0.000000000001, 1, 2, 3, 4)", CalculatorErrorCode.DomainError);
        Fails("solve_cubic(0, 1, 2, 3)", CalculatorErrorCode.DomainError);
        Fails("solve_cubic(0.000000000001, 1, 2, 3)", CalculatorErrorCode.DomainError);
        Fails("solve_cubic(-0.000000000001, 1, 2, 3)", CalculatorErrorCode.DomainError);

        // 1e-11 * (x-1)(x-2)(x-3)(x-4) and 1e-11 * (x-1)(x-2)(x-3): a leading coefficient just above the boundary is solved normally.
        decimal[] quartic = Arr("solve_quartic(0.00000000001, -0.0000000001, 0.00000000035, -0.0000000005, 0.00000000024)");
        Assert.Equal(4, quartic.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(Math.Abs((double)quartic[i] - (i + 1)) < 1e-6);
        }

        decimal[] cubic = Arr("solve_cubic(0.00000000001, -0.00000000006, 0.00000000011, -0.00000000006)");
        Assert.Equal(3, cubic.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Math.Abs((double)cubic[i] - (i + 1)) < 1e-6);
        }

        // The legacy kernels (not reachable from the DSL) still degrade to a lower degree.
        Span<decimal> roots = stackalloc decimal[4];
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuartic(0m, 1m, -6m, 11m, -6m, roots, out int legacyCount));
        Assert.Equal(3, legacyCount);
    }

    // ---------------------------------------------------------------- cubic: mutually exclusive repeated-root branches

    [Fact]
    public void CubicRepeatedRootBranchSplitsOnTheMagnitudeOfQ()
    {
        // |disc| <= 1e-15 and |p| < 1e-15: |q| < 1e-15 -> triple root [-shift] x 3; otherwise the single real root [-cbrt(q) - shift].
        Assert.Equal(new[] { 0m, 0m, 0m }, Arr("solve_cubic(1, 0, 0, 0)"));

        decimal[] single = Arr("solve_cubic(1, 0, 0, 0.00000001)");
        Assert.Single(single);
        Assert.True(Math.Abs((double)single[0] + Math.Cbrt(1e-8)) < 1e-12, single[0].ToString());

        // The same split with a non-zero shift.
        decimal[] shifted = Arr("solve_cubic(1, -3, 3, -1)");
        Assert.Equal(3, shifted.Length);
        Assert.All(shifted, r => Assert.True(Math.Abs((double)r - 1.0) < 1e-5));
    }

    // ---------------------------------------------------------------- cubic: D-1 (scale-independent repeated-root detection)

    private static decimal[] CubicCoefficients(params decimal[] roots)
    {
        decimal[] poly = [1m];
        foreach (decimal root in roots)
        {
            decimal[] next = new decimal[poly.Length + 1];
            for (int i = 0; i < poly.Length; i++)
            {
                next[i] += poly[i];
                next[i + 1] -= poly[i] * root;
            }

            poly = next;
        }

        return poly;
    }

    private static decimal[] SolveCubicFromRoots(params decimal[] roots)
    {
        decimal[] c = CubicCoefficients(roots);
        string expression = FormattableString.Invariant($"solve_cubic({c[0]:0.##############################}, {c[1]:0.##############################}, {c[2]:0.##############################}, {c[3]:0.##############################})");
        return Arr(expression);
    }

    /// <summary>D-1: three distinct roots closer together than ~5e-3 used to be returned as one simple and one double root (absolute discriminant tolerance).</summary>
    [Theory]
    [InlineData(0.0001, 0.0)]
    [InlineData(0.001, 0.0)]
    [InlineData(0.003, 0.0)]
    [InlineData(0.01, 0.0)]
    [InlineData(0.1, 0.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(100.0, 0.0)]
    [InlineData(0.01, 5.0)]
    [InlineData(0.1, -7.0)]
    public void CubicDistinctRootsAreIndependentOfTheirSpacing(double spacing, double centre)
    {
        decimal d = (decimal)spacing;
        decimal c = (decimal)centre;
        decimal[] expected = [c - d, c, c + d];
        decimal[] roots = SolveCubicFromRoots(expected);

        Assert.Equal(3, roots.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Math.Abs((double)(roots[i] - expected[i])) <= 1e-5 * spacing, $"d={spacing} c={centre}: [{string.Join(", ", roots)}]");
        }

        // The same spacing with unequal gaps.
        decimal[] unequal = [c, c + d, c + (3m * d)];
        decimal[] unequalRoots = SolveCubicFromRoots(unequal);
        Assert.Equal(3, unequalRoots.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Math.Abs((double)(unequalRoots[i] - unequal[i])) <= 1e-5 * spacing, $"d={spacing} c={centre}: [{string.Join(", ", unequalRoots)}]");
        }
    }

    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.001)]
    [InlineData(0.01)]
    [InlineData(1.0)]
    [InlineData(100.0)]
    public void CubicRepeatedRootsAreDetectedAtEveryScale(double scale)
    {
        decimal s = (decimal)scale;

        // (x - s)^2 (x - 2s): one simple and one double root.
        decimal[] doubleRoot = SolveCubicFromRoots(s, s, 2m * s);
        Assert.Equal(3, doubleRoot.Length);
        Assert.True(Math.Abs((double)doubleRoot[0] - scale) <= 1e-5 * scale, string.Join(", ", doubleRoot));
        Assert.True(Math.Abs((double)doubleRoot[1] - scale) <= 1e-5 * scale, string.Join(", ", doubleRoot));
        Assert.True(Math.Abs((double)doubleRoot[2] - (2 * scale)) <= 1e-5 * scale, string.Join(", ", doubleRoot));

        // (x - s)^3: a triple root.
        decimal[] tripleRoot = SolveCubicFromRoots(s, s, s);
        Assert.Equal(3, tripleRoot.Length);
        Assert.All(tripleRoot, r => Assert.True(Math.Abs((double)r - scale) <= 1e-4 * scale, string.Join(", ", tripleRoot)));
    }

    // ---------------------------------------------------------------- BFGS recovery rules

    private readonly struct StepFunction(int[] evaluations) : INdScalarFunction
    {
        public int Dimension => 1;

        // 0 at the start point and everywhere to its left, 1 to its right: the central-difference gradient is positive,
        // so the steepest-descent step goes left where f never drops below f(x0) -> the Armijo condition cannot hold.
        public bool TryEvaluate(scoped ReadOnlySpan<double> x, ref DslExecutionBudget budget, out double y)
        {
            evaluations[0]++;
            y = x[0] > 0.0 ? 1.0 : 0.0;
            return true;
        }
    }

    [Fact]
    public void BfgsLineSearchFailureWithIdentityHessianFailsImmediatelyAndConsumesBudget()
    {
        var evaluations = new int[1];
        var budget = DslExecutionBudget.CreateDefault();
        int before = budget.RemainingOps;
        double[] x0 = [0.0];
        double[] result = new double[1];

        MathErrorCode err = DslOptimizationMath.OptBfgs(new StepFunction(evaluations), x0, 1e-8, result, ref budget);

        // f(x0) + central difference (2) + 20 backtracking evaluations; no second iteration and no retry (H is already the identity).
        Assert.Equal(MathErrorCode.ConvergenceFailure, err);
        Assert.Equal(23, evaluations[0]);
        Assert.Equal(before - 23, budget.RemainingOps);
    }

    // ---------------------------------------------------------------- ode_rk45 tolerance range

    [Fact]
    public void OdeRk45AcceptsExactlyTheDocumentedToleranceRange()
    {
        // Validation precedes the t0 == t1 shortcut, so the boundary values are checked without a real integration.
        Assert.Equal(new[] { 5m }, Arr("ode_rk45([y], t, [y], 2, 2, [5], 0.000000000001)", Heavy));
        Assert.Equal(new[] { 5m }, Arr("ode_rk45([y], t, [y], 2, 2, [5], 0.01)", Heavy));
        Assert.Equal(new[] { 5m }, Arr("ode_rk45([y], t, [y], 2, 2, [5])", Heavy));
        Fails("ode_rk45([y], t, [y], 2, 2, [5], 0.0000000000001)", CalculatorErrorCode.DomainError);
        Fails("ode_rk45([y], t, [y], 2, 2, [5], 0.0100000001)", CalculatorErrorCode.DomainError);

        // The upper boundary also integrates for real.
        decimal[] solved = Arr("ode_rk45([y], t, [y], 0, 1, [1], 0.01)", Heavy);
        Assert.Single(solved);
        Assert.True(Math.Abs((double)solved[0] - Math.E) < 0.1);

        // The DSL tolerance contract (also pinned in CalculatorDslToleranceContractTests).
        Assert.Equal(1e-12, DslTolerance.MinTol);
        Assert.Equal(1e-2, DslTolerance.MaxTol);
        Assert.Equal(1e-8, DslTolerance.DefaultTol);
    }

    private static decimal[] Round(decimal[] values) => values.Select(v => Math.Round(v, 4)).ToArray();
}
