using System;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>Plan ChartPatterns56 V2, run V2-1: the trapezoid membership and the weighted score of 00_COMMON section 6, with the Gartley intervals of 03_Harmonic/01 as the worked values.</summary>
public class FibonacciScoreTests
{
    // Gartley AB/XA: ideal 0.618 (single point), allowed [0.55, 0.68].
    private const decimal Ideal = 0.618m;
    private const decimal AllowedMin = 0.55m;
    private const decimal AllowedMax = 0.68m;

    private static decimal AbXa(decimal r) => FibonacciScore.Trapezoid(r, Ideal, Ideal, AllowedMin, AllowedMax);

    [Fact]
    public void SinglePoint_IsOneOnlyAtThePoint()
    {
        Assert.Equal(1m, AbXa(0.618m));
        Assert.True(AbXa(0.617m) < 1m);
        Assert.True(AbXa(0.619m) < 1m);
    }

    [Fact]
    public void RisingAndFallingEdges_AreStraightLines()
    {
        Assert.Equal((0.585m - 0.55m) / (0.618m - 0.55m), AbXa(0.585m));
        Assert.Equal((0.68m - 0.65m) / (0.68m - 0.618m), AbXa(0.65m));
    }

    [Theory]
    [InlineData(0.55)]   // the allowed edges score 0
    [InlineData(0.68)]
    [InlineData(0.30)]
    [InlineData(0.90)]
    public void AtAndBeyondTheAllowedEdges_IsZero(double ratio) => Assert.Equal(0m, AbXa((decimal)ratio));

    [Fact]
    public void IdealInterval_IsOneInsideAndOnTheEdges()
    {
        // BC/AB: ideal [0.382, 0.886], allowed [0.33, 0.93]
        Assert.Equal(1m, FibonacciScore.Trapezoid(0.382m, 0.382m, 0.886m, 0.33m, 0.93m));
        Assert.Equal(1m, FibonacciScore.Trapezoid(0.886m, 0.382m, 0.886m, 0.33m, 0.93m));
        Assert.Equal(1m, FibonacciScore.Trapezoid(0.5m, 0.382m, 0.886m, 0.33m, 0.93m));
    }

    [Fact]
    public void AllowedEdgeEqualToIdealEdge_HasNoSlope_AndScoresZeroJustOutside()
    {
        // allowed min == ideal min: the rising branch must not divide by zero
        Assert.Equal(0m, FibonacciScore.Trapezoid(0.49m, 0.5m, 0.6m, 0.5m, 0.7m));
        Assert.Equal(1m, FibonacciScore.Trapezoid(0.5m, 0.5m, 0.6m, 0.5m, 0.7m));
        // allowed max == ideal max
        Assert.Equal(0m, FibonacciScore.Trapezoid(0.61m, 0.5m, 0.6m, 0.4m, 0.6m));
    }

    [Fact]
    public void IntervalsInTheWrongOrder_Throw()
    {
        Assert.Throws<ArgumentException>(() => FibonacciScore.Trapezoid(0.5m, 0.6m, 0.5m, 0.4m, 0.7m));
        Assert.Throws<ArgumentException>(() => FibonacciScore.Trapezoid(0.5m, 0.5m, 0.6m, 0.55m, 0.7m));
        Assert.Throws<ArgumentException>(() => FibonacciScore.Trapezoid(0.5m, 0.5m, 0.6m, 0.4m, 0.55m));
    }

    [Fact]
    public void Weighted_IsTheSumOfScoreTimesWeight()
    {
        // Gartley weights 0.30 / 0.15 / 0.15 / 0.40
        decimal[] scores = { 1m, 0.5m, 0m, 0.25m };
        decimal[] weights = { 0.30m, 0.15m, 0.15m, 0.40m };

        Assert.Equal((1m * 0.30m) + (0.5m * 0.15m) + (0m * 0.15m) + (0.25m * 0.40m), FibonacciScore.Weighted(scores, weights));
    }

    [Fact]
    public void Weighted_BadInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => FibonacciScore.Weighted(new[] { 1m, 1m }, new[] { 0.5m }));
        Assert.Throws<ArgumentException>(() => FibonacciScore.Weighted(Array.Empty<decimal>(), Array.Empty<decimal>()));
        Assert.Throws<ArgumentException>(() => FibonacciScore.Weighted(new[] { 1m, 1m }, new[] { 0.5m, 0.6m }));
    }
}
