#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 review-adoption T4c (owner decisions Q-3, 2026-11-21): the depressed coefficients of <c>solve_cubic</c> / <c>solve_quartic</c> are formed in exact
/// decimal arithmetic (an exactly representable translation of the variable, then a power-of-ten scaling), so that roots clustered far from the origin are not
/// lost to the cancellation of the double reduction. Probe data: <c>docs/sa_probe_dsl_v5_cubic_quartic_scale</c>, <c>docs/sa_probe_t5_script_smoketest</c>.
/// Relative error of a root <c>r</c> against the true <c>r*</c> with minimum spacing <c>d</c>: <c>|r - r*| / max(|r*|, d)</c> (plan section 2.4).
/// </summary>
public sealed class CalculatorDslV5DecimalReductionTests
{
    private const double RelativeErrorLimit = 1e-6;

    private static decimal[] PolynomialFromRoots(params decimal[] roots)
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

    private static (MathErrorCode Error, double[] Roots) Solve(decimal[] k)
    {
        Span<decimal> buffer = stackalloc decimal[4];
        MathErrorCode error = k.Length == 4
            ? DslElementaryMath.SolveCubicDsl(k[0], k[1], k[2], k[3], buffer, out int count)
            : DslElementaryMath.SolveQuarticDsl(k[0], k[1], k[2], k[3], k[4], buffer, out count);
        return (error, buffer.Slice(0, count).ToArray().Select(r => (double)r).ToArray());
    }

    private static double WorstRelativeError(double[] actual, decimal[] trueRoots, double spacing)
    {
        double[] expected = trueRoots.Select(r => (double)r).OrderBy(r => r).ToArray();
        return Enumerable.Range(0, expected.Length).Max(i => Math.Abs(actual[i] - expected[i]) / Math.Max(Math.Abs(expected[i]), spacing));
    }

    /// <summary>
    /// The 28-digit decimal input itself cannot resolve a simple root: <c>1e-28 * sum |a_k| |r|^k / |P'(r)| &gt; 1e-7 * max(|r|, d)</c> (the ILL marker of the T4a probe).
    /// Such cells are excluded from the verdict; multiple roots are not classified.
    /// </summary>
    private static bool IsIllConditioned(decimal[] coefficients, decimal[] roots, double spacing)
    {
        foreach (decimal root in roots.Distinct())
        {
            if (roots.Count(r => r == root) != 1)
            {
                continue;
            }

            double r = (double)root;
            double magnitude = 0.0;
            for (int i = 0; i < coefficients.Length; i++)
            {
                magnitude += Math.Abs((double)coefficients[i]) * Math.Pow(Math.Abs(r), coefficients.Length - 1 - i);
            }

            double derivative = 1.0;
            foreach (decimal other in roots)
            {
                if (other != root)
                {
                    derivative *= Math.Abs(r - (double)other);
                }
            }

            if (1e-28 * magnitude / derivative > 1e-7 * Math.Max(Math.Abs(r), spacing))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- regression cases found by the T5 reference comparison

    [Fact]
    public void CubicWithADoubleRootAwayFromTheOriginKeepsAllThreeRoots()
    {
        // (x - 11)^2 (x - 12) returned a single root: the fixed 1e-15 tolerance of the scaled discriminant is below the cancellation noise of the double reduction.
        decimal[] roots = [11m, 11m, 12m];
        (MathErrorCode error, double[] actual) = Solve(PolynomialFromRoots(roots));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(3, actual.Length);
        Assert.True(WorstRelativeError(actual, roots, 1.0) <= 1e-9, string.Join(", ", actual));
    }

    [Fact]
    public void ClusteredRootsFarFromTheOriginAreFound()
    {
        decimal[] cubic = [1000.99m, 1001m, 1001.01m];
        (MathErrorCode error, double[] actual) = Solve(PolynomialFromRoots(cubic));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(3, actual.Length);
        Assert.True(WorstRelativeError(actual, cubic, 0.01) <= RelativeErrorLimit, string.Join(", ", actual));

        decimal[] quartic = [1000.99m, 1001m, 1001.01m, 1001.02m];
        (error, actual) = Solve(PolynomialFromRoots(quartic));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(4, actual.Length);
        Assert.True(WorstRelativeError(actual, quartic, 0.01) <= RelativeErrorLimit, string.Join(", ", actual));
    }

    // ---------------------------------------------------------------- the T4a band: translation 0..1000 x spacing 1e-3..1e-5

    private static readonly (string Name, Func<decimal, decimal, decimal[]> Roots)[] Shapes =
    [
        ("cubic equal gaps", (m, d) => [m - d, m, m + d]),
        ("cubic unequal", (m, d) => [m, m + d, m + (3m * d)]),
        ("cubic double+simple", (m, d) => [m, m, m + d]),
        ("quartic asym", (m, d) => [m - d, m, m + d, m + (3m * d)]),
        ("quartic sym", (m, d) => [m - (2m * d), m - d, m + d, m + (2m * d)]),
        ("quartic double+2 simple", (m, d) => [m, m, m + d, m + (2m * d)]),
        ("quartic two doubles", (m, d) => [m, m, m + d, m + d]),
    ];

    public static IEnumerable<object[]> BandCells()
    {
        for (int shape = 0; shape < Shapes.Length; shape++)
        {
            foreach (int translation in new[] { 0, 1, 10, 100, 1000 })
            {
                foreach (double spacing in new[] { 1e-3, 1e-4, 1e-5 })
                {
                    yield return new object[] { shape, translation, spacing };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(BandCells))]
    public void RootsInThePracticalBandAreCountedAndAccurate(int shape, int translation, double spacing)
    {
        decimal d = (decimal)spacing;
        decimal[] roots = Shapes[shape].Roots(translation, d);
        decimal[] coefficients = PolynomialFromRoots(roots);
        if (IsIllConditioned(coefficients, roots, spacing))
        {
            return;
        }

        (MathErrorCode error, double[] actual) = Solve(coefficients);

        Assert.Equal(MathErrorCode.None, error);
        Assert.True(actual.Length == roots.Length, $"{Shapes[shape].Name} m={translation} d={spacing}: expected {roots.Length} roots, got [{string.Join(", ", actual)}]");
        double worst = WorstRelativeError(actual, roots, spacing);
        Assert.True(worst <= RelativeErrorLimit, $"{Shapes[shape].Name} m={translation} d={spacing}: relative error {worst:E2}, roots [{string.Join(", ", actual)}]");
    }

    // ---------------------------------------------------------------- multiple roots stay detected (the cubic tolerance is only raised if the measurement demands it)

    [Theory]
    [InlineData(0.5, 0.0)]
    [InlineData(1.0, 11.0)]
    [InlineData(1.0, 100.0)]
    [InlineData(0.1, 250.0)]
    [InlineData(2.5, -37.0)]
    [InlineData(0.01, 1000.0)]
    public void ADoubleRootAtAnyTranslationIsStillADoubleRoot(double gap, double translation)
    {
        decimal g = (decimal)gap;
        decimal t = (decimal)translation;
        decimal[] roots = [t, t, t + g];
        (MathErrorCode error, double[] actual) = Solve(PolynomialFromRoots(roots));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(3, actual.Length);
        Assert.True(WorstRelativeError(actual, roots, gap) <= 1e-8, string.Join(", ", actual));

        decimal[] mirrored = [t - g, t, t];
        (error, actual) = Solve(PolynomialFromRoots(mirrored));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(3, actual.Length);
        Assert.True(WorstRelativeError(actual, mirrored, gap) <= 1e-8, string.Join(", ", actual));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(1000.0)]
    [InlineData(-45.5)]
    public void MultipleRootsOfExactDecimalsAreExactAtAnyTranslation(double centre)
    {
        decimal c = (decimal)centre;
        (MathErrorCode error, double[] actual) = Solve(PolynomialFromRoots(c, c, c));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(new[] { centre, centre, centre }, actual);

        (error, actual) = Solve(PolynomialFromRoots(c, c, c, c));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(new[] { centre, centre, centre, centre }, actual);
    }

    // ---------------------------------------------------------------- large roots, and the fallback to the double path where the monic ratios overflow a decimal

    [Fact]
    public void LargeRootsAreSolvedByTheDecimalReduction()
    {
        // (x - 3e8)^3 and (x - 1e6)(x - 2e6)(x - 3e6)(x - 4e6): the coefficients reach 2.7e25 / 2.4e25 and are scaled to unit size before they are shifted, so nothing overflows.
        decimal[] big = [300_000_000m, 300_000_000m, 300_000_000m];
        (MathErrorCode error, double[] actual) = Solve(PolynomialFromRoots(big));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(3, actual.Length);
        Assert.True(WorstRelativeError(actual, big, 1.0) <= 1e-6, string.Join(", ", actual));

        decimal[] quartic = [1_000_000m, 2_000_000m, 3_000_000m, 4_000_000m];
        (error, actual) = Solve(PolynomialFromRoots(quartic));
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(4, actual.Length);
        Assert.True(WorstRelativeError(actual, quartic, 1.0) <= 1e-6, string.Join(", ", actual));
    }

    [Fact]
    public void MonicRatiosBeyondTheDecimalRangeFallBackToTheDoublePath()
    {
        // 1e-11 x^3 + (1e25 - 1): d / a = 1e36 - 1e11 exceeds the decimal range (7.9e28), the decimal reduction reports an overflow and the double path answers.
        (MathErrorCode error, double[] actual) = Solve([0.00000000001m, 0m, 0m, 10_000_000_000_000_000_000_000_000m - 1m]);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Single(actual);
        Assert.True(Math.Abs(actual[0] + Math.Cbrt(1e36 - 1e11)) <= 1e-9 * 1e12, string.Join(", ", actual));

        // The quartic: 1e-11 x^4 - 1e21 = 0 (e / a = -1e32 exceeds the decimal range) has the real roots +-1e8.
        (error, actual) = Solve([0.00000000001m, 0m, 0m, 0m, -1_000_000_000_000_000_000_000m]);
        Assert.Equal(MathErrorCode.None, error);
        Assert.Equal(2, actual.Length);
        Assert.True(Math.Abs(actual[0] + 1e8) <= 1e-6 * 1e8 && Math.Abs(actual[1] - 1e8) <= 1e-6 * 1e8, string.Join(", ", actual));
    }

    [Fact]
    public void ReductionAgreesWithTheDoublePathWhereBothAreAccurate()
    {
        // Well-conditioned polynomials with ordinary scales: the answer must not depend on which path produced it.
        decimal[][] cases =
        [
            [1m, 2m, 3m],
            [-1.5m, 0.5m, 4.25m],
            [0.1m, 0.7m, 2.9m],
            [10m, 20m, 35m],
            [-300m, 5m, 6m],
        ];
        foreach (decimal[] roots in cases)
        {
            (MathErrorCode error, double[] actual) = Solve(PolynomialFromRoots(roots));
            Assert.Equal(MathErrorCode.None, error);
            Assert.Equal(3, actual.Length);
            Assert.True(WorstRelativeError(actual, roots, 0.1) <= 1e-12, string.Join(", ", actual));
        }

        foreach (decimal[] roots in new[] { new[] { 1m, 2m, 3m, 4m }, new[] { -2m, -1m, 1m, 2m }, new[] { 0.5m, 1.5m, 2.5m, 100m }, new[] { -7m, -3m, 6m, 11m } })
        {
            (MathErrorCode error, double[] actual) = Solve(PolynomialFromRoots(roots));
            Assert.Equal(MathErrorCode.None, error);
            Assert.Equal(4, actual.Length);

            // The Ferrari stage works in double: about 1e-12 for roots that differ by a factor of 200 (0.5 ... 100), so the limit is 1e-10.
            Assert.True(WorstRelativeError(actual, roots, 0.1) <= 1e-10, string.Join(", ", actual));
        }
    }
}
