using System;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="AsymmetricRiskMath"/> to values computed with Python's Decimal (40 digits) and numpy on fixed windows, and to the null rules of plan section 3.8
/// (flat window, empty window, regime too small, zero benchmark days belonging to neither regime).
/// </summary>
public class AsymmetricRiskMathTests
{
    private static readonly decimal[] Window = { 0.01m, -0.02m, 0.015m, -0.005m, 0.03m, -0.01m, 0.0m, 0.02m };

    private static readonly decimal[] Asset = { 0.012m, -0.03m, 0.02m, 0.004m, -0.011m, 0.025m, -0.018m, 0.007m, -0.004m, 0.031m, -0.022m, 0.009m };

    // Index 3 is a benchmark return of exactly 0: it belongs to neither regime.
    private static readonly decimal[] Benchmark = { 0.01m, -0.02m, 0.015m, 0.0m, -0.008m, 0.02m, -0.012m, 0.005m, -0.003m, 0.018m, -0.016m, 0.006m };

    [Fact]
    public void DownsideStatistics_MatchTheHighPrecisionReference_ForTargetZero()
    {
        Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(Window, 0m, out DownsideStatistics s));
        Assert.Equal(0.008100925873009825m, s.DownsideDeviation, 14);
        Assert.Equal(0.014252192813739225m, s.UpsideDeviation, 14);
        Assert.Equal(0.6172133998483676m, s.RollingSortino!.Value, 12);
        Assert.Equal(0.5116279069767442m, s.SemivarianceAsymmetry!.Value, 14);
    }

    [Fact]
    public void DownsideStatistics_AtATargetEqualToTheMean_HaveEqualSidesAndZeroQuotients()
    {
        // The mean of the window is 0.005; against that target sigma- = sigma+ (0.01103970108290981), so the Sortino ratio and the asymmetry are exactly 0.
        Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(Window, 0.005m, out DownsideStatistics s));
        Assert.Equal(0.01103970108290981m, s.DownsideDeviation, 14);
        Assert.Equal(s.DownsideDeviation, s.UpsideDeviation);
        Assert.Equal(0m, s.RollingSortino);
        Assert.Equal(0m, s.SemivarianceAsymmetry);
    }

    [Fact]
    public void DownsideStatistics_AllReturnsAboveTheTarget_HaveNoDownsideAndNoSortino()
    {
        decimal[] up = { 0.01m, 0.02m, 0.03m, 0.015m, 0.012m };
        Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(up, 0m, out DownsideStatistics s));
        Assert.Equal(0m, s.DownsideDeviation);
        Assert.True(s.UpsideDeviation > 0m);
        Assert.Null(s.RollingSortino);
        Assert.Equal(1m, s.SemivarianceAsymmetry);
    }

    [Fact]
    public void DownsideStatistics_AFlatWindowOfZeroReturns_IsDefinedAsZeroDeviationsWithNullQuotients()
    {
        decimal[] flat = new decimal[10];
        Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(flat, 0m, out DownsideStatistics s));
        Assert.Equal(0m, s.DownsideDeviation);
        Assert.Equal(0m, s.UpsideDeviation);
        Assert.Null(s.RollingSortino);
        Assert.Null(s.SemivarianceAsymmetry);
    }

    [Fact]
    public void DownsideStatistics_EmptyWindowAndOverflow_AreRejected()
    {
        Assert.False(AsymmetricRiskMath.TryComputeDownsideStatistics(ReadOnlySpan<decimal>.Empty, 0m, out _));
        decimal[] huge = { decimal.MaxValue, -decimal.MaxValue, decimal.MaxValue };
        Assert.False(AsymmetricRiskMath.TryComputeDownsideStatistics(huge, 0m, out _));
    }

    [Fact]
    public void ConditionalBeta_MatchesTheOlsSlopeInsideEachRegime()
    {
        Assert.Equal(1.498868778280543m, AsymmetricRiskMath.ConditionalBeta(Asset, Benchmark, BenchmarkRegime.Down)!.Value, 12);
        Assert.Equal(1.4408783783783785m, AsymmetricRiskMath.ConditionalBeta(Asset, Benchmark, BenchmarkRegime.Up)!.Value, 12);
    }

    [Fact]
    public void ConditionalBeta_OfTheBenchmarkItself_IsOneInBothRegimes()
    {
        Assert.Equal(1m, AsymmetricRiskMath.ConditionalBeta(Benchmark, Benchmark, BenchmarkRegime.Down)!.Value, 20);
        Assert.Equal(1m, AsymmetricRiskMath.ConditionalBeta(Benchmark, Benchmark, BenchmarkRegime.Up)!.Value, 20);
    }

    [Fact]
    public void ConditionalBeta_IsNullForASmallRegime_AMismatchedLength_AndAConstantBenchmark()
    {
        decimal[] asset = { 0.01m, 0.02m, -0.01m, 0.0m };
        decimal[] twoDown = { -0.01m, -0.02m, 0.01m, 0.02m };
        Assert.Null(AsymmetricRiskMath.ConditionalBeta(asset, twoDown, BenchmarkRegime.Down)); // two dates only
        Assert.Null(AsymmetricRiskMath.ConditionalBeta(asset.AsSpan(0, 3), twoDown, BenchmarkRegime.Down)); // lengths differ

        decimal[] constantDown = { -0.01m, -0.01m, -0.01m, -0.01m };
        Assert.Null(AsymmetricRiskMath.ConditionalBeta(asset, constantDown, BenchmarkRegime.Down)); // variance 0

        decimal[] threeDown = { -0.01m, -0.02m, -0.03m, 0.02m };
        Assert.NotNull(AsymmetricRiskMath.ConditionalBeta(asset, threeDown, BenchmarkRegime.Down));
        Assert.Equal(3, AsymmetricRiskMath.MinConditionalBetaCount);
    }

    [Fact]
    public void ConditionalBeta_ZeroBenchmarkDays_BelongToNeitherRegime()
    {
        // Only zero returns: both regimes are empty.
        decimal[] zeros = new decimal[6];
        Assert.Null(AsymmetricRiskMath.ConditionalBeta(Asset.AsSpan(0, 6), zeros, BenchmarkRegime.Down));
        Assert.Null(AsymmetricRiskMath.ConditionalBeta(Asset.AsSpan(0, 6), zeros, BenchmarkRegime.Up));
    }

    [Fact]
    public void Kernels_AllocateNothing()
    {
        AsymmetricRiskMath.TryComputeDownsideStatistics(Window, 0m, out _);
        AsymmetricRiskMath.ConditionalBeta(Asset, Benchmark, BenchmarkRegime.Down);
        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                AsymmetricRiskMath.TryComputeDownsideStatistics(Window, 0m, out _);
                AsymmetricRiskMath.ConditionalBeta(Asset, Benchmark, BenchmarkRegime.Down);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
