#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// Backlog (legacy <c>SolveQuartic</c>, +-w pairing): for a quartic with q != 0 the two quadratic factors <c>(y^2 + w y + s)(y^2 - w y + t)</c> had the sign of w exchanged in their roots, so the
/// roots of one factor were returned with the wrong sign of w and the result was not a root of the polynomial (for example (x-1)^2 (x-2)(x-3) returned 0.825, 0.858, 1.75). The legacy contract
/// (the DISTINCT real roots, ascending) is unchanged. The production solver is <see cref="DslElementaryMath.SolveQuarticDsl"/>.
/// </summary>
public sealed class CalculatorDslLegacyQuarticPairingTests
{
    private const double RootTolerance = 1e-6;
    private const double ResidualTolerance = 1e-8;
    private const int RootBufferLength = 4;

    /// <summary>Monic polynomial coefficients (x^4 .. x^0) of the product of the factors (x - r).</summary>
    private static decimal[] Expand(params decimal[] factorRoots)
    {
        decimal[] poly = { 1m };
        foreach (decimal r in factorRoots)
        {
            decimal[] next = new decimal[poly.Length + 1];
            for (int i = 0; i < poly.Length; i++)
            {
                next[i] += poly[i];
                next[i + 1] -= poly[i] * r;
            }

            poly = next;
        }

        return poly;
    }

    private static decimal[] ExpandWithComplexPair(decimal re, decimal im, params decimal[] realRoots)
    {
        decimal[] poly = Expand(realRoots);
        decimal[] next = new decimal[poly.Length + 2];
        for (int i = 0; i < poly.Length; i++)
        {
            next[i] += poly[i];
            next[i + 1] -= 2m * re * poly[i];
            next[i + 2] += ((re * re) + (im * im)) * poly[i];
        }

        return next;
    }

    private static decimal[] Solve(decimal[] k)
    {
        decimal[] buffer = new decimal[RootBufferLength];
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuartic(k[0], k[1], k[2], k[3], k[4], buffer, out int count));
        return buffer.Take(count).ToArray();
    }

    private static void AssertRootsOf(decimal[] k, decimal[] expectedDistinct)
    {
        decimal[] roots = Solve(k);
        Assert.True(roots.Length == expectedDistinct.Length, $"expected {expectedDistinct.Length} distinct roots, got {roots.Length}: {string.Join(", ", roots)}");
        for (int i = 0; i < roots.Length; i++)
        {
            Assert.True(Math.Abs((double)(roots[i] - expectedDistinct[i])) <= RootTolerance, $"root {i}: expected {expectedDistinct[i]}, actual {roots[i]}");
            double x = (double)roots[i];
            double value = ((((((double)k[0] * x) + (double)k[1]) * x) + (double)k[2]) * x + (double)k[3]) * x + (double)k[4];
            double scale = (Math.Abs((double)k[0]) * Math.Pow(Math.Abs(x), 4)) + (Math.Abs((double)k[1]) * Math.Pow(Math.Abs(x), 3)) + (Math.Abs((double)k[2]) * x * x) + (Math.Abs((double)k[3]) * Math.Abs(x)) + Math.Abs((double)k[4]);
            Assert.True(Math.Abs(value) <= ResidualTolerance * Math.Max(1.0, scale), $"root {roots[i]} leaves the residual {value}");
        }
    }

    public static IEnumerable<object[]> Scales() => [[0.5m], [1m], [7m], [100m]];

    [Theory]
    [MemberData(nameof(Scales))]
    public void ADoubleRootWithTwoSimpleRootsReturnsTheThreeDistinctRoots(decimal s)
        => AssertRootsOf(Expand(s, s, 2m * s, 3m * s), new[] { s, 2m * s, 3m * s });

    [Theory]
    [MemberData(nameof(Scales))]
    public void AComplexPairWithTwoRealRootsReturnsTheTwoRealRoots(decimal s)
        => AssertRootsOf(ExpandWithComplexPair(0m, s, s, 2m * s), new[] { s, 2m * s });

    [Theory]
    [InlineData(-3, -0.5, 2, 5)]
    [InlineData(0.1, 0.2, 10, -7)]
    [InlineData(-8, -1, 4, 9)]
    public void FourDistinctRealRootsAreAllReturned(double r1, double r2, double r3, double r4)
    {
        decimal[] roots = new[] { (decimal)r1, (decimal)r2, (decimal)r3, (decimal)r4 }.OrderBy(v => v).ToArray();
        AssertRootsOf(Expand(roots), roots);
    }

    [Fact]
    public void ATripleRootIsReportedOncePerQuadraticFactor()
        // The legacy contract lists a repeated root once PER FACTOR, not once overall: (x-1)^3 (x-2) splits into (x-1)^2 and (x-1)(x-2), so the root 1 appears twice (unchanged behaviour).
        => AssertRootsOf(Expand(1m, 1m, 1m, 2m), new[] { 1m, 1m, 2m });
}
