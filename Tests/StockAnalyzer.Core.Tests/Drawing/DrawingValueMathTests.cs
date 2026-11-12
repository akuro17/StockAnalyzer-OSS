using System;
using StockAnalyzer.Core.Drawing;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>Boundary behaviour of the pure drawing math that the GUI drawing objects and the headless drawing-value calculators share.</summary>
public sealed class DrawingValueMathTests
{
    private static readonly DateTime T1 = new(2026, 1, 1);
    private static readonly DateTime T2 = new(2026, 1, 11);

    [Fact]
    public void InterpolatePrice_InsideBeforeAndAfterTheSegment_IsLinear()
    {
        Assert.Equal(125m, DrawingValueMath.InterpolatePrice(T1, 100m, T2, 150m, new DateTime(2026, 1, 6)));
        Assert.Equal(50m, DrawingValueMath.InterpolatePrice(T1, 100m, T2, 150m, new DateTime(2025, 12, 22)));
        Assert.Equal(200m, DrawingValueMath.InterpolatePrice(T1, 100m, T2, 150m, new DateTime(2026, 1, 21)));
    }

    [Fact]
    public void InterpolatePrice_TimesCloserThanTheEpsilon_ReturnsTheFirstPrice()
    {
        // The epsilon is 1e-4 s = 0.1 ms = 1000 ticks: 0 and 500 ticks count as equal times, 2000 ticks do not.
        Assert.Equal(100m, DrawingValueMath.InterpolatePrice(T1, 100m, T1, 150m, T2));
        Assert.Equal(100m, DrawingValueMath.InterpolatePrice(T1, 100m, T1.AddTicks(500), 150m, T2));
        Assert.NotEqual(100m, DrawingValueMath.InterpolatePrice(T1, 100m, T1.AddTicks(2000), 150m, T2));
    }

    [Fact]
    public void InterpolatePrice_ReversedSegment_GivesTheSamePriceAsTheOrderedOne()
    {
        var mid = new DateTime(2026, 1, 3, 6, 0, 0);
        Assert.Equal(
            DrawingValueMath.InterpolatePrice(T1, 100m, T2, 150m, mid),
            DrawingValueMath.InterpolatePrice(T2, 150m, T1, 100m, mid));
    }

    [Fact]
    public void CalculateSlopePerDay_IsPriceChangePerTwentyFourHours_AndNullForEqualTimes()
    {
        Assert.Equal(5m, DrawingValueMath.CalculateSlopePerDay(T1, 100m, T2, 150m));
        Assert.Equal(5m, DrawingValueMath.CalculateSlopePerDay(T2, 150m, T1, 100m));
        Assert.Null(DrawingValueMath.CalculateSlopePerDay(T1, 100m, T1, 150m));
        Assert.Null(DrawingValueMath.CalculateSlopePerDay(T1, 100m, T1.AddTicks(500), 150m));
    }

    [Fact]
    public void CalculatePercentageChange_StartZero_IsZero_AndNegativeStartKeepsTheFormula()
    {
        Assert.Equal(0m, DrawingValueMath.CalculatePercentageChange(0m, 50m));
        Assert.Equal(50m, DrawingValueMath.CalculatePercentageChange(100m, 150m));
        Assert.Equal(-50m, DrawingValueMath.CalculatePercentageChange(100m, 50m));
        Assert.Equal(50m, DrawingValueMath.CalculatePercentageChange(-100m, -150m));
    }

    [Fact]
    public void SafeAddSeconds_NaNAndInfinities_AndClamping()
    {
        var time = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.Equal(time, DrawingValueMath.SafeAddSeconds(time, double.NaN));
        Assert.Equal(DateTime.MaxValue, DrawingValueMath.SafeAddSeconds(time, double.PositiveInfinity));
        Assert.Equal(DateTime.MinValue, DrawingValueMath.SafeAddSeconds(time, double.NegativeInfinity));
        Assert.Equal(DateTime.MaxValue, DrawingValueMath.SafeAddSeconds(time, 1e18));
        Assert.Equal(DateTime.MinValue, DrawingValueMath.SafeAddSeconds(time, -1e18));
        Assert.Equal(time.AddSeconds(90), DrawingValueMath.SafeAddSeconds(time, 90));
        Assert.Equal(time, DrawingValueMath.SafeAddSeconds(time, 0));
    }
}
