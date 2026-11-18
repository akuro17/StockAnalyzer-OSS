using System;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>Pins <see cref="JumpVariationMath"/> to numpy on a fixed window with one jump, and to the boundary rules of plan section 3.10.</summary>
public class JumpVariationMathTests
{
    private static readonly double[] WindowWithJump = { 0.001, -0.002, 0.0015, 0.0, -0.0005, 0.05, -0.001, 0.002, 0.0005, -0.0015 };

    [Fact]
    public void WindowWithAJump_MatchesTheReference()
    {
        Assert.True(JumpVariationMath.TryCompute(WindowWithJump, out JumpVariation v));
        Assert.Equal(0.003823235865788627, v.BipowerVolatility, 14);
        Assert.Equal(0.015858751527153707, v.TotalVolatility, 14);
        Assert.Equal(0.015390999561904596, v.JumpVolatility, 14);
        Assert.Equal(0.9418801889246421, v.JumpRatio, 12);
        Assert.True(v.TotalVolatility >= v.BipowerVolatility);
    }

    [Fact]
    public void BipowerScale_IsPiOverTwo()
    {
        Assert.Equal(Math.PI / 2.0, JumpVariationMath.BipowerScale);
        // Hand value: r = {a, a, a} -> RV = a^2, BPV = (pi / 2) * (2 a^2 / 2) = (pi / 2) a^2, so J = max(0, a^2 (1 - pi / 2)) = 0 and the jump ratio is 0.
        Assert.True(JumpVariationMath.TryCompute(new[] { 0.01, 0.01, 0.01 }, out JumpVariation v));
        Assert.Equal(0.0, v.JumpVolatility);
        Assert.Equal(0.0, v.JumpRatio);
        Assert.Equal(0.01, v.TotalVolatility, 15);
        Assert.Equal(0.01 * Math.Sqrt(Math.PI / 2.0), v.BipowerVolatility, 15);
    }

    [Fact]
    public void WindowOfZeroReturns_GivesZeroVolatilitiesAndAnUndefinedJumpRatio()
    {
        Assert.True(JumpVariationMath.TryCompute(new double[8], out JumpVariation v));
        Assert.Equal(0.0, v.BipowerVolatility);
        Assert.Equal(0.0, v.TotalVolatility);
        Assert.Equal(0.0, v.JumpVolatility);
        Assert.True(double.IsNaN(v.JumpRatio));
    }

    [Fact]
    public void ShortOrNonFiniteWindows_AreRejected()
    {
        Assert.False(JumpVariationMath.TryCompute(new[] { 0.01, 0.02 }, out _));
        Assert.False(JumpVariationMath.TryCompute(ReadOnlySpan<double>.Empty, out _));
        Assert.False(JumpVariationMath.TryCompute(new[] { 0.01, double.NaN, 0.02 }, out _));
        Assert.False(JumpVariationMath.TryCompute(new[] { 0.01, double.PositiveInfinity, 0.02 }, out _));
        Assert.Equal(3, JumpVariationMath.MinCount);
    }

    [Fact]
    public void Kernel_AllocatesNothing()
    {
        JumpVariationMath.TryCompute(WindowWithJump, out _);
        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                JumpVariationMath.TryCompute(WindowWithJump, out _);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
