using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="SpecialFunctionsMath"/> to reference values computed with mpmath at 60 significant digits (rounded once to double), to the
/// closed forms that exist (chi-square with 2 degrees of freedom is exp(-x/2)), and to the domain rules (NaN outside the domain, never an exception).
/// Specification: docs/indicators_formulas.md, record indicators_formulas_0594 (relative error bounds 1e-14 for Phi and its inverse, 1e-12 for the gamma tail).
/// <para>
/// Reference recipe (kind of calculation, not a script in the repository): python -c "import mpmath as mp; mp.mp.dps = 60; print(float(mp.erfc(mp.mpf('0.5'))))"; the normal quantile as
/// sqrt(2) * mp.erfinv(2 p - 1); the gamma tail as float(mp.gammainc(a, x, mp.inf, regularized=True)). Re-run on 2026-11-18 for ONE literal only: erfc(0.5) reproduces 0.4795001221869535 exactly; the other literals were not re-run.
/// Environment of 2026-11-18: Python with scipy 1.18.1, mpmath 1.3.0, numpy 2.5.2; statsmodels and arch are NOT installed.
/// </para>
/// </summary>
public class SpecialFunctionsMathTests
{
    private const double PhiRelativeTolerance = 1e-14;
    private const double GammaRelativeTolerance = 1e-12;

    private static void AssertRelative(double expected, double actual, double relativeTolerance)
    {
        Assert.True(double.IsFinite(actual), $"actual {actual} is not finite (expected {expected})");
        double scale = Math.Max(Math.Abs(expected), double.Epsilon);
        double error = Math.Abs(actual - expected) / scale;
        Assert.True(error <= relativeTolerance, $"expected {expected:R}, actual {actual:R}, relative error {error:E3} > {relativeTolerance:E1}");
    }

    [Theory]
    [InlineData(-3.0, 1.9999779095030015)]
    [InlineData(-1.0, 1.8427007929497148)]
    [InlineData(-0.5, 1.5204998778130465)]
    [InlineData(0.0, 1.0)]
    [InlineData(0.1, 0.8875370839817152)]
    [InlineData(0.46875, 0.507386526782062)]
    [InlineData(0.5, 0.4795001221869535)]
    [InlineData(1.0, 0.15729920705028513)]
    [InlineData(2.0, 0.004677734981047266)]
    [InlineData(3.999, 1.5544749490995007e-08)]
    [InlineData(4.0, 1.541725790028002e-08)]
    [InlineData(4.0001, 1.5404564743590115e-08)]
    [InlineData(5.0, 1.537459794428035e-12)]
    [InlineData(10.0, 2.088487583762545e-45)]
    [InlineData(20.0, 5.395865611607901e-176)]
    [InlineData(26.0, 5.663192408856143e-296)]
    public void Erfc_MatchesTheHighPrecisionReference(double x, double expected) =>
        AssertRelative(expected, SpecialFunctionsMath.Erfc(x), PhiRelativeTolerance);

    [Fact]
    public void Erfc_IsSymmetricAndSaturates()
    {
        Assert.Equal(0.0, SpecialFunctionsMath.Erfc(30.0));
        Assert.Equal(2.0, SpecialFunctionsMath.Erfc(-30.0));
        Assert.Equal(0.0, SpecialFunctionsMath.Erfc(double.PositiveInfinity));
        Assert.Equal(2.0, SpecialFunctionsMath.Erfc(double.NegativeInfinity));
        Assert.True(double.IsNaN(SpecialFunctionsMath.Erfc(double.NaN)));
        foreach (double x in new[] { 0.2, 0.7, 1.5, 3.0, 4.5 })
        {
            AssertRelative(2.0, SpecialFunctionsMath.Erfc(x) + SpecialFunctionsMath.Erfc(-x), 1e-15);
        }
    }

    [Theory]
    [InlineData(-37.0, 5.725571222524577e-300)]
    [InlineData(-10.0, 7.619853024160525e-24)]
    [InlineData(-8.0, 6.220960574271784e-16)]
    [InlineData(-5.0, 2.866515718791939e-07)]
    [InlineData(-3.0, 0.0013498980316300946)]
    [InlineData(-1.96, 0.024997895148220435)]
    [InlineData(-1.0, 0.15865525393145705)]
    [InlineData(-0.1, 0.460172162722971)]
    [InlineData(0.0, 0.5)]
    [InlineData(0.1, 0.539827837277029)]
    [InlineData(1.0, 0.8413447460685429)]
    [InlineData(1.96, 0.9750021048517795)]
    [InlineData(3.0, 0.9986501019683699)]
    [InlineData(8.0, 0.9999999999999993)]
    public void NormalCdf_MatchesTheHighPrecisionReference(double z, double expected) =>
        // The argument -z / sqrt 2 is rounded once (relative 1.1e-16) and Phi(z) ~ exp(-z^2 / 2), so the best possible relative error grows like 1e-16 * z^2
        // (about 1.4e-13 at z = -37); the bound of the plan (1e-14) holds up to |z| = 10, beyond it the conditioning bound is used.
        AssertRelative(expected, SpecialFunctionsMath.NormalCdf(z), Math.Max(PhiRelativeTolerance, 2e-16 * z * z));

    [Fact]
    public void NormalCdf_HandlesTheLimits()
    {
        Assert.Equal(1.0, SpecialFunctionsMath.NormalCdf(double.PositiveInfinity));
        Assert.Equal(0.0, SpecialFunctionsMath.NormalCdf(double.NegativeInfinity));
        Assert.True(double.IsNaN(SpecialFunctionsMath.NormalCdf(double.NaN)));
    }

    [Fact]
    public void NormalTwoSidedPValue_KeepsTheTailAndTheDefinition()
    {
        AssertRelative(0.05000000000000003, SpecialFunctionsMath.NormalTwoSidedPValue(1.959963984540054), 1e-13);
        AssertRelative(0.01241933065155227, SpecialFunctionsMath.NormalTwoSidedPValue(2.5), PhiRelativeTolerance);
        Assert.Equal(1.0, SpecialFunctionsMath.NormalTwoSidedPValue(0.0));
        Assert.Equal(SpecialFunctionsMath.NormalTwoSidedPValue(2.5), SpecialFunctionsMath.NormalTwoSidedPValue(-2.5));

        // 2 * (1 - Phi(10)) is exactly 0 in double arithmetic; erfc keeps the tail.
        AssertRelative(1.523970604832105e-23, SpecialFunctionsMath.NormalTwoSidedPValue(10.0), PhiRelativeTolerance);
        Assert.True(double.IsNaN(SpecialFunctionsMath.NormalTwoSidedPValue(double.NaN)));
    }

    [Theory]
    [InlineData(1e-10, -6.361340902404057)]
    [InlineData(1e-6, -4.753424308822899)]
    [InlineData(0.001, -3.0902323061678136)]
    [InlineData(0.01, -2.326347874040841)]
    [InlineData(0.02425, -1.972961051311885)]
    [InlineData(0.1, -1.2815515655446004)]
    [InlineData(0.25, -0.6744897501960817)]
    [InlineData(0.5, 0.0)]
    [InlineData(0.75, 0.6744897501960817)]
    [InlineData(0.9, 1.2815515655446004)]
    [InlineData(0.975, 1.9599639845400543)]
    [InlineData(0.99, 2.326347874040841)]
    // The double 0.999999 is 0.99999899999999997124..., not 0.999999: the reference is the quantile of that exact double (4.753424308817087).
    [InlineData(0.999999, 4.753424308817087)]
    public void NormalQuantile_MatchesTheHighPrecisionReference(double p, double expected)
    {
        double actual = SpecialFunctionsMath.NormalQuantile(p);
        if (expected == 0.0)
        {
            Assert.Equal(0.0, actual, 15);
        }
        else
        {
            AssertRelative(expected, actual, PhiRelativeTolerance);
        }
    }

    [Theory]
    [InlineData(1e-10)]
    [InlineData(1e-6)]
    [InlineData(0.01)]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(0.99)]
    [InlineData(0.999999)]
    public void NormalCdf_OfTheQuantile_ReturnsTheProbability(double p) =>
        AssertRelative(p, SpecialFunctionsMath.NormalCdf(SpecialFunctionsMath.NormalQuantile(p)), PhiRelativeTolerance);

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NormalQuantile_OutsideTheOpenUnitInterval_IsNaN(double p) =>
        Assert.True(double.IsNaN(SpecialFunctionsMath.NormalQuantile(p)));

    [Theory]
    [InlineData(1.0, 1.0, 0.36787944117144233)]
    [InlineData(1.0, 5.0, 0.006737946999085467)]
    [InlineData(0.5, 0.5, 0.3173105078629141)]
    [InlineData(0.5, 3.0, 0.01430587843542964)]
    [InlineData(2.0, 1.0, 0.7357588823428847)]
    [InlineData(2.0, 10.0, 0.0004993992273873333)]
    [InlineData(5.0, 0.001, 1.0)]
    [InlineData(5.0, 2.0, 0.9473469826562888)]
    [InlineData(5.0, 5.0, 0.4404932850652124)]
    [InlineData(5.0, 30.0, 3.6243009520614882e-09)]
    [InlineData(15.0, 10.0, 0.9165415270653372)]
    [InlineData(15.0, 15.5, 0.4154071101477917)]
    [InlineData(15.0, 16.0, 0.36752735976556494)]
    [InlineData(15.0, 40.0, 1.975623243491064e-06)]
    [InlineData(15.0, 100.0, 4.952733529003191e-27)]
    [InlineData(0.5, 40.0, 3.744097384202899e-19)]
    public void RegularizedGammaQ_MatchesTheHighPrecisionReference(double a, double x, double expected) =>
        AssertRelative(expected, SpecialFunctionsMath.RegularizedGammaQ(a, x), GammaRelativeTolerance);

    [Theory]
    [InlineData(2.0, 5.991464547107979, 0.05000000000000007)]
    [InlineData(10.0, 18.307038053275146, 0.05000000000000002)]
    [InlineData(1.0, 3.841458820694124, 0.05000000000000006)]
    [InlineData(30.0, 43.77297182574219, 0.04999999999999998)]
    [InlineData(4.0, 0.1, 0.9987908957257497)]
    public void ChiSquareUpperTail_MatchesTheCriticalValuesAndTheReference(double degreesOfFreedom, double x, double expected) =>
        AssertRelative(expected, SpecialFunctionsMath.ChiSquareUpperTail(degreesOfFreedom, x), GammaRelativeTolerance);

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    [InlineData(5.991464547107979)]
    [InlineData(40.0)]
    public void ChiSquareUpperTail_WithTwoDegreesOfFreedom_IsExpOfMinusHalfX(double x) =>
        AssertRelative(Math.Exp(-x / 2.0), SpecialFunctionsMath.ChiSquareUpperTail(2.0, x), GammaRelativeTolerance);

    [Fact]
    public void RegularizedGammaQ_OutsideTheDomain_IsNaN()
    {
        Assert.True(double.IsNaN(SpecialFunctionsMath.RegularizedGammaQ(0.0, 1.0)));
        Assert.True(double.IsNaN(SpecialFunctionsMath.RegularizedGammaQ(-1.0, 1.0)));
        Assert.True(double.IsNaN(SpecialFunctionsMath.RegularizedGammaQ(1.0, -0.1)));
        Assert.True(double.IsNaN(SpecialFunctionsMath.RegularizedGammaQ(double.NaN, 1.0)));
        Assert.True(double.IsNaN(SpecialFunctionsMath.RegularizedGammaQ(1.0, double.NaN)));
        Assert.True(double.IsNaN(SpecialFunctionsMath.ChiSquareUpperTail(0.0, 1.0)));
        Assert.Equal(1.0, SpecialFunctionsMath.RegularizedGammaQ(3.0, 0.0));
        Assert.Equal(0.0, SpecialFunctionsMath.RegularizedGammaQ(3.0, double.PositiveInfinity));
    }
}
