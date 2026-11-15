using System;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The centre line of the disjoint channel lies on the straight line of the Mid value (owner decision D-2 = B, 2026-11-16).</summary>
public sealed class DisjointChannelCenterLineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);
    private static readonly IndicatorColor Color1 = new(255, 10, 20, 30);

    [Fact]
    public void EdgesWithDifferentTimeRanges_GiveEndPointsOnTheMidLine()
    {
        // Edge 1: flat 100 from day 0 to day 10. Edge 2: 100 at day 2 rising 10 per day to 200 at day 12.
        var (start, end) = DisjointChannelValues.GetCenterLine(
            new DrawingAnchor(T0, 100m), new DrawingAnchor(T0.AddDays(10), 100m),
            new DrawingAnchor(T0.AddDays(2), 100m), new DrawingAnchor(T0.AddDays(12), 200m));

        Assert.Equal(T0.AddDays(1), start.Time);
        Assert.Equal(95m, start.Price, 9);    // (100 + 90) / 2
        Assert.Equal(T0.AddDays(11), end.Time);
        Assert.Equal(145m, end.Price, 9);     // (100 + 190) / 2

        // The line through the two end points has the Mid value at day 6 (120; the old midpoint line gave 125).
        decimal onLine = DrawingValueMath.InterpolatePrice(start.Time, start.Price, end.Time, end.Price, T0.AddDays(6));
        var values = DisjointChannelValues.Calculate(
            new DrawingAnchor(T0, 100m), new DrawingAnchor(T0.AddDays(10), 100m),
            new DrawingAnchor(T0.AddDays(2), 100m), new DrawingAnchor(T0.AddDays(12), 200m),
            T0.AddDays(6), Color1);

        Assert.Equal(120m, onLine, 9);
        Assert.Equal(120m, values[2].NumericValue!.Value, 9);
    }

    [Fact]
    public void EdgesWithTheSameTimes_GiveTheMidpointsOfP1P3AndP2P4()
    {
        var p1 = new DrawingAnchor(T0, 100m);
        var p2 = new DrawingAnchor(T0.AddDays(10), 100m);
        var p3 = new DrawingAnchor(T0, 300m);
        var p4 = new DrawingAnchor(T0.AddDays(10), 200m);

        var (start, end) = DisjointChannelValues.GetCenterLine(p1, p2, p3, p4);

        Assert.Equal(new DrawingAnchor(T0, 200m), start);
        Assert.Equal(new DrawingAnchor(T0.AddDays(10), 150m), end);
    }

    [Fact]
    public void TheTimeIsTheIntegerMeanOfTheTicks_AndKeepsTheKindOfTheFirstAnchor()
    {
        var p1 = new DrawingAnchor(new DateTime(1001, DateTimeKind.Utc), 100m);
        var p3 = new DrawingAnchor(new DateTime(1002, DateTimeKind.Local), 100m);
        var p2 = new DrawingAnchor(new DateTime(2001, DateTimeKind.Unspecified), 100m);
        var p4 = new DrawingAnchor(new DateTime(2004, DateTimeKind.Unspecified), 100m);

        var (start, end) = DisjointChannelValues.GetCenterLine(p1, p2, p3, p4);

        Assert.Equal(1001L, start.Time.Ticks);   // (1001 + 1002) / 2 = 1001 (integer division)
        Assert.Equal(DateTimeKind.Utc, start.Time.Kind);
        Assert.Equal(2002L, end.Time.Ticks);     // (2001 + 2004) / 2 = 2002
        Assert.Equal(DateTimeKind.Unspecified, end.Time.Kind);
    }

    [Fact]
    public void AllFourAnchorsAtOneTime_GiveTheMidValueOfThatTime()
    {
        // InterpolatePrice returns the first anchor price of an edge whose two anchors share a time: edge 1 = 100, edge 2 = 150, Mid = 125.
        var (start, end) = DisjointChannelValues.GetCenterLine(
            new DrawingAnchor(T0, 100m), new DrawingAnchor(T0, 120m),
            new DrawingAnchor(T0, 150m), new DrawingAnchor(T0, 170m));

        Assert.Equal(new DrawingAnchor(T0, 125m), start);
        Assert.Equal(new DrawingAnchor(T0, 125m), end);
    }
}
