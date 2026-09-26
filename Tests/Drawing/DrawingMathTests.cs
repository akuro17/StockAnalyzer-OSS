using System;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class DrawingMathTests
{
    [Fact]
    public void InterpolatePrice_Midpoint_ReturnsCorrectPrice()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 200m);

        var midTime = new DateTime(2026, 1, 6);
        decimal price = DrawingMath.InterpolatePrice(p1, p2, midTime);

        Assert.Equal(150m, price);
    }

    [Fact]
    public void InterpolatePrice_Extrapolation_ReturnsCorrectPrice()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11); // 10 days
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 110m); // +1/day

        var futureTime = new DateTime(2026, 1, 21); // +20 days
        decimal price = DrawingMath.InterpolatePrice(p1, p2, futureTime);

        Assert.Equal(120m, price);
    }

    [Fact]
    public void InterpolatePrice_IdenticalTimestamps_ReturnsStartPrice()
    {
        var t = new DateTime(2026, 1, 1);
        var p1 = new ChartPoint(t, 100m);
        var p2 = new ChartPoint(t, 200m);

        decimal price = DrawingMath.InterpolatePrice(p1, p2, t);

        Assert.Equal(100m, price);
    }

    [Fact]
    public void CalculateSlopePerDay_NormalPoints_ReturnsSlope()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11); // 10 days
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 150m); // +50

        var slope = DrawingMath.CalculateSlopePerDay(p1, p2);

        Assert.NotNull(slope);
        Assert.Equal(5m, slope.Value);
    }

    [Fact]
    public void CalculateSlopePerDay_ZeroDuration_ReturnsNull()
    {
        var t = new DateTime(2026, 1, 1);
        var p1 = new ChartPoint(t, 100m);
        var p2 = new ChartPoint(t, 150m);

        var slope = DrawingMath.CalculateSlopePerDay(p1, p2);

        Assert.Null(slope);
    }

    [Fact]
    public void CalculatePercentageChange_StandardValues_ReturnsPercentage()
    {
        Assert.Equal(20m, DrawingMath.CalculatePercentageChange(100m, 120m));
        Assert.Equal(-20m, DrawingMath.CalculatePercentageChange(100m, 80m));
        Assert.Equal(0m, DrawingMath.CalculatePercentageChange(0m, 100m));
    }

    [Fact]
    public void SafeAddSeconds_Normal_AddsSeconds()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        var result = DrawingMath.SafeAddSeconds(t, 60);

        Assert.Equal(new DateTime(2026, 1, 1, 12, 1, 0), result);
    }

    [Fact]
    public void SafeAddSeconds_ExtremePositive_ClampsToMaxValue()
    {
        var t = new DateTime(2026, 1, 1);
        var result = DrawingMath.SafeAddSeconds(t, 1e18);

        Assert.Equal(DateTime.MaxValue, result);
    }

    [Fact]
    public void SafeAddSeconds_ExtremeNegative_ClampsToMinValue()
    {
        var t = new DateTime(2026, 1, 1);
        var result = DrawingMath.SafeAddSeconds(t, -1e18);

        Assert.Equal(DateTime.MinValue, result);
    }

    [Fact]
    public void SafeAddSeconds_NaNAndInfinities_HandledSafely()
    {
        var t = new DateTime(2026, 1, 1);

        Assert.Equal(t, DrawingMath.SafeAddSeconds(t, double.NaN));
        Assert.Equal(DateTime.MaxValue, DrawingMath.SafeAddSeconds(t, double.PositiveInfinity));
        Assert.Equal(DateTime.MinValue, DrawingMath.SafeAddSeconds(t, double.NegativeInfinity));
    }
}
