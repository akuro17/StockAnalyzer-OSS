using System;
using Xunit;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Guards the <see cref="MathConstants"/> SSoT: each member MUST stay bit-identical to the inline
/// literal expression it replaced across the solution, otherwise migrated call sites would silently
/// shift their results.
/// </summary>
public class MathConstantsTests
{
    [Fact]
    public void DegToRad_EqualsPiOver180_Exactly()
    {
        Assert.Equal(Math.PI / 180.0, MathConstants.DegToRad);
    }

    [Fact]
    public void RadToDeg_Equals180OverPi_Exactly()
    {
        Assert.Equal(180.0 / Math.PI, MathConstants.RadToDeg);
    }

    [Fact]
    public void RadToDegF_EqualsSinglePrecision180OverPi_Exactly()
    {
        Assert.Equal((float)(180.0 / Math.PI), MathConstants.RadToDegF);
    }

    [Fact]
    public void TwoPi_EqualsTwoTimesPi_Exactly()
    {
        Assert.Equal(2.0 * Math.PI, MathConstants.TwoPi);
    }

    [Fact]
    public void DegToRad_And_RadToDeg_RoundTripToUnity()
    {
        Assert.Equal(1.0, MathConstants.DegToRad * MathConstants.RadToDeg, 15);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(180.0)]
    [InlineData(-137.5)]
    [InlineData(360.0)]
    public void DegToRad_MatchesInlineMultiplyByFactor_ForRepresentativeAngles(double degrees)
    {
        // The pre-divided factor form -- the canonical single rounding every caller now shares.
        Assert.Equal(degrees * (Math.PI / 180.0), degrees * MathConstants.DegToRad);
    }
}
