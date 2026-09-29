using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

public class FresnelMathTests
{
    [Fact]
    public void Evaluate_ZeroArgument_ReturnsZero()
    {
        var (c, s) = FresnelMath.Evaluate(0.0);
        Assert.Equal(0.0, c, 12);
        Assert.Equal(0.0, s, 12);
    }

    [Fact]
    public void Evaluate_NaNArgument_ReturnsNaN()
    {
        var (c, s) = FresnelMath.Evaluate(double.NaN);
        Assert.True(double.IsNaN(c));
        Assert.True(double.IsNaN(s));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    [InlineData(5.0)]
    public void Evaluate_SymmetryCondition_SatisfiesOddFunctionProperty(double u)
    {
        var (cPos, sPos) = FresnelMath.Evaluate(u);
        var (cNeg, sNeg) = FresnelMath.Evaluate(-u);

        Assert.Equal(-cPos, cNeg, 10);
        Assert.Equal(-sPos, sNeg, 10);
    }

    [Fact]
    public void Evaluate_KnownNistReferenceValues_MatchesHighPrecision()
    {
        // Reference values from NIST Digital Library of Mathematical Functions (DLMF §7.2)
        // C(1.0) = 0.779893400376823, S(1.0) = 0.438259147390355
        var (c1, s1) = FresnelMath.Evaluate(1.0);
        Assert.Equal(0.7798934003768, c1, 6);
        Assert.Equal(0.4382591473903, s1, 6);

        // C(2.0) = 0.488253406075341, S(2.0) = 0.343415678363698
        var (c2, s2) = FresnelMath.Evaluate(2.0);
        Assert.Equal(0.4882534060753, c2, 6);
        Assert.Equal(0.3434156783637, s2, 6);

        // For large u = 5.0, values match exact numerical integrals:
        // C(5.0) = 0.563631, S(5.0) = 0.499191
        var (c5, s5) = FresnelMath.Evaluate(5.0);
        Assert.Equal(0.563631, c5, 5);
        Assert.Equal(0.499191, s5, 5);
    }

    [Fact]
    public void Evaluate_ExpansionBoundaryAtFour_MatchesSeamlessly()
    {
        double uLeft = 4.0 - 1e-7;
        double uRight = 4.0 + 1e-7;

        var (cLeft, sLeft) = FresnelMath.Evaluate(uLeft);
        var (cRight, sRight) = FresnelMath.Evaluate(uRight);

        // Continuous transition across boundary between Taylor and asymptotic expansions
        Assert.Equal(cLeft, cRight, 5);
        Assert.Equal(sLeft, sRight, 5);
    }
}
