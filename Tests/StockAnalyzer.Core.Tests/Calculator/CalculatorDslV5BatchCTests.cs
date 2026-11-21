#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 review-adoption batch C: D-2 (dimension-normalised entry check of <c>solve_cubic</c>/<c>solve_quartic</c>: reject iff
/// <c>|a| &lt;= 1e-12</c> or <c>|a_(n-k)/a| &gt;= (1e12)^k</c>) and T4b (scale-free repeated-root / biquadratic decisions of <c>solve_quartic</c>).
/// Probe data: <c>docs/sa_probe_dsl_v5_cubic_quartic_scale</c>; analysis: <c>docs/sa_analysis_dsl_v5_d2_entry_check</c>.
/// </summary>
public sealed class CalculatorDslV5BatchCTests
{
    private static decimal[] PolynomialFromRoots(decimal scale, params decimal[] roots)
    {
        decimal[] poly = [scale];
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

    private static (MathErrorCode Error, double[] Roots) Quartic(params decimal[] k)
    {
        Span<decimal> buffer = stackalloc decimal[4];
        MathErrorCode error = DslElementaryMath.SolveQuarticDsl(k[0], k[1], k[2], k[3], k[4], buffer, out int count);
        return (error, buffer.Slice(0, count).ToArray().Select(r => (double)r).ToArray());
    }

    private static (MathErrorCode Error, double[] Roots) Cubic(params decimal[] k)
    {
        Span<decimal> buffer = stackalloc decimal[3];
        MathErrorCode error = DslElementaryMath.SolveCubicDsl(k[0], k[1], k[2], k[3], buffer, out int count);
        return (error, buffer.Slice(0, count).ToArray().Select(r => (double)r).ToArray());
    }

    /// <summary>Asserts the roots against the expected ones (ascending) with error <c>|r - r*| / max(|r*|, floor)</c> at most <paramref name="relativeTolerance"/>.</summary>
    private static void AssertRoots(string label, double[] actual, double[] expected, double relativeTolerance, double floor)
    {
        double[] sorted = expected.OrderBy(r => r).ToArray();
        Assert.True(actual.Length == sorted.Length, $"{label}: expected {sorted.Length} roots, got [{string.Join(", ", actual)}]");
        for (int i = 0; i < sorted.Length; i++)
        {
            double error = Math.Abs(actual[i] - sorted[i]) / Math.Max(Math.Abs(sorted[i]), floor);
            Assert.True(error <= relativeTolerance, $"{label}: root {i} = {actual[i]} (expected {sorted[i]}, relative error {error:E2}); all = [{string.Join(", ", actual)}]");
        }
    }

    // ---------------------------------------------------------------- D-2: sound large-scale polynomials are solved, not rejected

    [Fact]
    public void QuarticWithLargeRootsPassesTheEntryCheckAndKeepsItsRoots()
    {
        // The old check "|a| <= 1e-12 * max|coefficient|" rejected monic quartics whose constant term (about root^4) exceeded 1e12.
        (MathErrorCode error, double[] roots) = Quartic(PolynomialFromRoots(1m, 1000m, 1000m, 1000m, 1000m));
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("(x-1000)^4", roots, [1000, 1000, 1000, 1000], 1e-6, 1.0);

        (error, roots) = Quartic(PolynomialFromRoots(1m, 50000m, 50000m, 50000m, 50000m));
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("(x-50000)^4", roots, [50000, 50000, 50000, 50000], 1e-6, 1.0);

        (error, roots) = Quartic(1m, 0m, 0m, 0m, -1_000_000_000_000m);
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("x^4-1e12", roots, [-1000, 1000], 1e-6, 1.0);

        (error, roots) = Quartic(PolynomialFromRoots(1m, 1000m, 1001m, 1002m, 1003m));
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("(x-1000)(x-1001)(x-1002)(x-1003)", roots, [1000, 1001, 1002, 1003], 1e-6, 1.0);
    }

    [Fact]
    public void CubicWithLargeRootsPassesTheEntryCheckAndKeepsItsRoots()
    {
        (MathErrorCode error, double[] roots) = Cubic(PolynomialFromRoots(1m, 10000m, 10000m, 10000m));
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("(x-10000)^3", roots, [10000, 10000, 10000], 1e-6, 1.0);

        (error, roots) = Cubic(1m, 0m, 0m, -1_000_000_000_000m);
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("x^3-1e12", roots, [10000], 1e-9, 1.0);

        (error, roots) = Cubic(PolynomialFromRoots(1m, 10000m, 10001m, 10002m));
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("(x-10000)(x-10001)(x-10002)", roots, [10000, 10001, 10002], 1e-6, 1.0);
    }

    [Fact]
    public void CubicWithLeadingCoefficientTinyAgainstTheConstantTermIsSolvedExactlyBelowTheThirdPowerBoundary()
    {
        // |d / a| = 1e36 - 1e11 < (1e12)^3: accepted. The leading coefficient must survive the conversion to double
        // (a uniform power-of-ten rescaling of the decimals would turn 1e-11 into 0 here).
        (MathErrorCode error, double[] roots) = Cubic(0.00000000001m, 0m, 0m, 10_000_000_000_000_000_000_000_000m - 1m);
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("1e-11 x^3 + (1e25-1)", roots, [-Math.Cbrt(1e36 - 1e11)], 1e-9, 1.0);
    }

    [Fact]
    public void EntryCheckRejectsDegenerateScalesAndTheExactBoundary()
    {
        // |b/a| = 1e12 exactly (the boundary is inclusive and must be evaluated in exact decimal arithmetic).
        Assert.Equal(MathErrorCode.DomainError, Quartic(0.000000001m, 1000m, 0m, 0m, -1000m).Error);

        // |c/a| = 1e25 >= 1e24.
        Assert.Equal(MathErrorCode.DomainError, Quartic(0.00000000001m, 0m, 100_000_000_000_000m, 0m, -100_000_000_000_000m).Error);

        // |d/a| = 1e36 exactly for the cubic.
        Assert.Equal(MathErrorCode.DomainError, Cubic(0.00000000001m, 0m, 0m, 10_000_000_000_000_000_000_000_000m).Error);

        // Just below the first-power boundary: accepted by the check (a double root at 0 plus one root near -1e12).
        Assert.NotEqual(MathErrorCode.DomainError, Cubic(0.000000001m, 999.999999999m, 0m, 0m).Error);

        // The absolute floor is unchanged.
        Assert.Equal(MathErrorCode.DomainError, Cubic(0m, 1m, 2m, 3m).Error);
        Assert.Equal(MathErrorCode.DomainError, Cubic(0.000000000001m, 1m, 2m, 3m).Error);
        Assert.Equal(MathErrorCode.DomainError, Quartic(-0.000000000001m, 1m, 2m, 3m, 4m).Error);
    }

    [Fact]
    public void ExceedsRootScaleLimitIsExactAtEveryPowerBoundary()
    {
        // k = 3: |d / a| = 1e36 exactly rejects, one unit less passes (an iterative division would round 9.99..e-12 up to 1e-11 here).
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([1e-11m, 0m, 0m, 1e25m]));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([1e-11m, 0m, 0m, 1e25m - 1m]));

        // k = 1 with a 17-digit mantissa: the threshold 1.2345678901234567 is reached exactly.
        decimal a = 1.2345678901234567e-12m;
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([a, 1.2345678901234567m]));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([a, 1.2345678901234566m]));
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([a, -1.2345678901234567m]));

        // k = 2: 1e24.
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([1e-3m, 0m, 1e21m]));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([1e-3m, 0m, 1e21m - 1m]));
    }

    [Fact]
    public void ExceedsRootScaleLimitNeverOverflows()
    {
        // The running threshold would exceed decimal.MaxValue: nothing can reach it.
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([1e17m, decimal.MaxValue, decimal.MaxValue]));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue]));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([decimal.MaxValue, 1m]));

        // A threshold exactly at decimal.MaxValue / maxRootScale is still multiplied safely.
        decimal edge = decimal.MaxValue / 1e12m;
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([edge, decimal.MaxValue]));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([edge, decimal.MaxValue - 1m]));
    }

    [Fact]
    public void ExceedsRootScaleLimitHonoursTheInjectedScaleForEveryDegreeIncludingTheFourthPower()
    {
        // The fourth-power branch (1e48 with the production scale) is unreachable with decimal coefficients; a small scale exercises it.
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([1m, 0m, 0m, 0m, 100_000_000m], 100m));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([1m, 0m, 0m, 0m, 99_999_999m], 100m));
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([1m, 0m, 0m, 1_000_000m], 100m));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([1m, 0m, 0m, 999_999m], 100m));
        Assert.True(DslElementaryMath.ExceedsRootScaleLimit([1m, 100m], 100m));
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([1m, 99m, 9_999m, 999_999m, 99_999_999m], 100m));

        // Without an injected scale a quartic needs |e / a| >= 1e48, which no decimal reaches.
        Assert.False(DslElementaryMath.ExceedsRootScaleLimit([1e-11m, 0m, 0m, 0m, decimal.MaxValue]));
    }

    // ---------------------------------------------------------------- T4b: scale-free quartic decisions (translation 0, spacing 1e-3..1e-5)

    public static IEnumerable<object[]> SmallSpacings() => new[] { 1e-3, 1e-4, 1e-5 }.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(SmallSpacings))]
    public void QuarticWithTinyRootSpacingAtZeroIsNotMistakenForABiquadratic(double spacing)
    {
        decimal d = (decimal)spacing;

        // x (x+d) (x-d) (x-3d): |q| ~ d^3 is far below the old absolute 1e-14 for d <= 1e-5, which forced the biquadratic branch.
        (MathErrorCode error, double[] roots) = Quartic(PolynomialFromRoots(1m, -d, 0m, d, 3m * d));
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots($"asymmetric d={spacing}", roots, [-spacing, 0, spacing, 3 * spacing], 1e-6, spacing);

        // x^2 (x-d) (x-2d): a double root at 0 plus two simple roots.
        (error, roots) = Quartic(PolynomialFromRoots(1m, 0m, 0m, d, 2m * d));
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots($"double+2 simple d={spacing}", roots, [0, 0, spacing, 2 * spacing], 1e-5, spacing);
    }

    [Fact]
    public void QuarticRepeatedResolventRootDecisionDoesNotDependOnTheAbsoluteSize()
    {
        // (x^2 - d^2)(x^2 - 4 d^2) with d = 1e-8, scaled by 1e18: u1 = d^2 and u2 = 4 d^2 differ by 3e-16 < the old absolute 1e-15,
        // so they used to be merged into one double u (wrong roots). The roots +-1e-8 and +-2e-8 are four distinct simple roots.
        // Coefficients: a = 1e18, c = -5 d^2 * 1e18 = -500, e = 4 d^4 * 1e18 = 4e-14 (the unscaled 4e-32 is not representable as a decimal).
        (MathErrorCode error, double[] roots) = Quartic(1_000_000_000_000_000_000m, 0m, -500m, 0m, 0.00000000000004m);
        Assert.Equal(MathErrorCode.None, error);
        AssertRoots("biquadratic d=1e-8", roots, [-2e-8, -1e-8, 1e-8, 2e-8], 1e-6, 1e-8);
    }
}
