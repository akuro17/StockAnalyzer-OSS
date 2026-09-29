using System;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class AnchorPointOrderHelperTests
{
    [Fact]
    public void GetClockwiseCycleOrder_TriangleAlreadyClockwise_ReturnsOriginalOrder()
    {
        // Top, bottom-right, bottom-left: a clockwise sweep in screen space (Y-down).
        var points = new[] { new Point(0, 0), new Point(1, 1), new Point(-1, 1) };

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        Assert.Equal(new[] { 0, 1, 2 }, order);
    }

    [Fact]
    public void GetClockwiseCycleOrder_TriangleCounterClockwise_ReversesButKeepsStartIndexFixed()
    {
        // Same triangle, placement order reversed (Top, bottom-left, bottom-right) -> counter-clockwise.
        var points = new[] { new Point(0, 0), new Point(-1, 1), new Point(1, 1) };

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        // Index 0 (Top) stays first; index 2 (bottom-right) is visited next, then index 1 (bottom-left)
        // -- walking Top -> bottom-right -> bottom-left is clockwise, matching the already-CW case.
        Assert.Equal(new[] { 0, 2, 1 }, order);
    }

    [Fact]
    public void GetClockwiseCycleOrder_SquareAlreadyClockwise_ReturnsOriginalOrder()
    {
        // Top-left, top-right, bottom-right, bottom-left: clockwise in screen space.
        var points = new[] { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) };

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
    }

    [Fact]
    public void GetClockwiseCycleOrder_SquareCounterClockwise_IsNormalizedToClockwise()
    {
        // Top-left, bottom-left, bottom-right, top-right: counter-clockwise in screen space.
        var points = new[] { new Point(0, 0), new Point(0, 1), new Point(1, 1), new Point(1, 0) };

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        Assert.Equal(new[] { 0, 3, 2, 1 }, order);
    }

    [Fact]
    public void GetClockwiseCycleOrder_CollinearPoints_ReturnsOriginalOrderUnchanged()
    {
        var points = new[] { new Point(0, 0), new Point(1, 0), new Point(2, 0) };

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        Assert.Equal(new[] { 0, 1, 2 }, order);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GetClockwiseCycleOrder_FewerThanThreePoints_ReturnsIdentityOrder(int count)
    {
        var points = new Point[count];
        for (int i = 0; i < count; i++) points[i] = new Point(i, i);

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        for (int i = 0; i < count; i++) Assert.Equal(i, order[i]);
        Assert.Equal(count, order.Length);
    }

    // ChartPoint overload (chart-space Time/Price, no ICoordinateTransform available): Time maps to
    // pseudo-screen X (increasing = later) and -Price maps to pseudo-screen Y (higher price = smaller
    // Y, matching a real chart's "up is a higher value" rendering), so these mirror the screen-space
    // triangle cases above using an equivalent Time/Price layout.
    [Fact]
    public void GetClockwiseCycleOrder_ChartPoints_AlreadyClockwise_ReturnsOriginalOrder()
    {
        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0);
        var points = new[]
        {
            new ChartPoint(baseTime, 0m),               // pseudo-screen (0, 0): Top
            new ChartPoint(baseTime.AddSeconds(1), -1m), // pseudo-screen (1, 1): bottom-right
            new ChartPoint(baseTime.AddSeconds(-1), -1m) // pseudo-screen (-1, 1): bottom-left
        };

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        Assert.Equal(new[] { 0, 1, 2 }, order);
    }

    [Fact]
    public void GetClockwiseCycleOrder_ChartPoints_CounterClockwise_ReversesButKeepsStartIndexFixed()
    {
        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0);
        var points = new[]
        {
            new ChartPoint(baseTime, 0m),                // pseudo-screen (0, 0): Top
            new ChartPoint(baseTime.AddSeconds(-1), -1m), // pseudo-screen (-1, 1): bottom-left
            new ChartPoint(baseTime.AddSeconds(1), -1m)   // pseudo-screen (1, 1): bottom-right
        };

        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(points);

        Assert.Equal(new[] { 0, 2, 1 }, order);
    }
}
