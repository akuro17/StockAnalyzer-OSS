using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Visible-range behaviour of the equity layout: the whole range is the legacy output, a zoomed range draws the in-range points plus
/// one neighbour per side and fits Y over that slice, and the series-level rules (UTC, ordering, overflow, constant band) still hold.
/// </summary>
public class EquityCurveVisibleLayoutTests
{
    private static readonly DateTime Day0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImmutableArray<EquityPoint> Series(params decimal[] equities) =>
        equities.Select((equity, i) => new EquityPoint(i, Day0.AddDays(i), equity, equity, 0m, 0m)).ToImmutableArray();

    [Fact]
    public void WholeRange_IsIdenticalToTheLegacyBuild()
    {
        ImmutableArray<EquityPoint> points = Series(100m, 140m, 90m, 160m, 120m);

        EquityCurveLayout legacy = EquityCurveLayout.Build(points);
        EquityCurveLayout visible = EquityCurveLayout.Build(
            points, points[0].Timestamp.Ticks, points[^1].Timestamp.Ticks);

        Assert.Equal(legacy.State, visible.State);
        Assert.Equal(0, visible.FirstIndex);
        Assert.Equal(legacy.Points, visible.Points);
        Assert.Equal(legacy.YMin, visible.YMin);
        Assert.Equal(legacy.YMax, visible.YMax);
        Assert.Equal(legacy.ViewportStartUtc, visible.ViewportStartUtc);
        Assert.Equal(legacy.ViewportEndUtc, visible.ViewportEndUtc);
        Assert.Equal(legacy.ViewportStartLabel, visible.ViewportStartLabel);
        Assert.Equal(legacy.ViewportEndLabel, visible.ViewportEndLabel);
        Assert.Equal(DateTimeKind.Utc, visible.ViewportStartUtc.Kind);
    }

    [Fact]
    public void ZoomedRange_DrawsNeighboursAndFitsYOverTheDrawnSlice()
    {
        // Visible = day 2..day 4 (points 2,3,4). Neighbours: point 1 (500) and point 5 (110).
        ImmutableArray<EquityPoint> points = Series(100m, 500m, 50m, 300m, 120m, 110m);

        EquityCurveLayout layout = EquityCurveLayout.Build(points, Day0.AddDays(2).Ticks, Day0.AddDays(4).Ticks);

        Assert.Equal(EquityCurveDisplayState.Line, layout.State);
        Assert.Equal(1, layout.FirstIndex);
        Assert.Equal(5, layout.Points.Length);
        // The Y range is fitted over the drawn slice, neighbours included, so the line never leaves the plot vertically.
        Assert.Equal(50m, layout.YMin);
        Assert.Equal(500m, layout.YMax);
        Assert.Equal(-0.5, layout.Points[0].XFraction, precision: 12);
        Assert.Equal(0.0, layout.Points[1].XFraction, precision: 12);
        Assert.Equal(1.0, layout.Points[3].XFraction, precision: 12);
        Assert.Equal(1.5, layout.Points[4].XFraction, precision: 12);
        Assert.Equal(1.0, layout.Points[0].YFraction, precision: 12);
        Assert.Equal(0.0, layout.Points[1].YFraction, precision: 12);
    }

    [Fact]
    public void ZoomingIn_RecomputesYFromTheVisibleSliceOnly()
    {
        ImmutableArray<EquityPoint> points = Series(1_000m, 1_100m, 1_200m, 1_300m, 1_400m, 5_000m, 1_450m, 1_460m);

        EquityCurveLayout whole = EquityCurveLayout.Build(points);
        // Day 1..3 -> slice points 0..4 (1000..1400); the 5000 spike is neither visible nor a neighbour.
        EquityCurveLayout zoomed = EquityCurveLayout.Build(points, Day0.AddDays(1).Ticks, Day0.AddDays(3).Ticks);

        Assert.Equal(5_000m, whole.YMax);
        Assert.Equal(1_000m, zoomed.YMin);
        Assert.Equal(1_400m, zoomed.YMax);
    }

    [Fact]
    public void RangeBetweenTwoPoints_DrawsTheBracketingPair()
    {
        ImmutableArray<EquityPoint> points = Series(100m, 110m, 120m, 130m);

        EquityCurveLayout layout = EquityCurveLayout.Build(
            points, Day0.AddDays(1).AddHours(6).Ticks, Day0.AddDays(1).AddHours(18).Ticks);

        Assert.Equal(EquityCurveDisplayState.Line, layout.State);
        Assert.Equal(1, layout.FirstIndex);
        Assert.Equal(2, layout.Points.Length);
        Assert.Equal(-0.5, layout.Points[0].XFraction, precision: 12);
        Assert.Equal(1.5, layout.Points[1].XFraction, precision: 12);
    }

    [Theory]
    [InlineData(-10, -5)]
    [InlineData(10, 20)]
    public void RangeOutsideTheData_DrawsNothingAndFitsYOverTheSeries(int startDay, int endDay)
    {
        ImmutableArray<EquityPoint> points = Series(100m, 110m, 120m, 130m);

        EquityCurveLayout layout = EquityCurveLayout.Build(points, Day0.AddDays(startDay).Ticks, Day0.AddDays(endDay).Ticks);

        Assert.Equal(EquityCurveDisplayState.Line, layout.State);
        Assert.Empty(layout.Points);
        Assert.Equal(100m, layout.YMin);
        Assert.Equal(130m, layout.YMax);
    }

    [Fact]
    public void ZoomedConstantSlice_UsesThePlusMinusOneBand()
    {
        ImmutableArray<EquityPoint> points = Series(500m, 100m, 100m, 100m, 900m);

        // Visible 1.5..2.5 -> points 2 only in range; neighbours 1 and 3 (all 100).
        EquityCurveLayout layout = EquityCurveLayout.Build(points, Day0.AddDays(1).AddHours(12).Ticks, Day0.AddDays(2).AddHours(12).Ticks);

        Assert.Equal(99m, layout.YMin);
        Assert.Equal(101m, layout.YMax);
        Assert.All(layout.Points, p => Assert.Equal(0.5, p.YFraction, precision: 12));
    }

    [Fact]
    public void RangeOverflow_DependsOnTheDrawnSliceNotTheWholeSeries()
    {
        var points = ImmutableArray.Create(
            new EquityPoint(0, Day0, decimal.MinValue, 0m, 0m, 0m),
            new EquityPoint(1, Day0.AddDays(1), 1m, 0m, 0m, 0m),
            new EquityPoint(2, Day0.AddDays(2), 2m, 0m, 0m, 0m),
            new EquityPoint(3, Day0.AddDays(3), 3m, 0m, 0m, 0m),
            new EquityPoint(4, Day0.AddDays(4), decimal.MaxValue, 0m, 0m, 0m));

        Assert.Equal(EquityCurveDisplayState.Unavailable, EquityCurveLayout.Build(points).State);

        EquityCurveLayout zoomed = EquityCurveLayout.Build(points, Day0.AddDays(2).Ticks, Day0.AddDays(2).AddHours(1).Ticks);
        Assert.Equal(EquityCurveDisplayState.Line, zoomed.State);
        Assert.Equal(1m, zoomed.YMin);
        Assert.Equal(3m, zoomed.YMax);
    }

    [Fact]
    public void InvalidSeries_IsUnavailableEvenWhenTheInvalidPartIsOutsideTheRange()
    {
        var points = ImmutableArray.Create(
            new EquityPoint(0, Day0, 1m, 0m, 0m, 0m),
            new EquityPoint(1, Day0.AddDays(1), 2m, 0m, 0m, 0m),
            new EquityPoint(2, Day0.AddDays(3), 3m, 0m, 0m, 0m),
            new EquityPoint(3, Day0.AddDays(2), 4m, 0m, 0m, 0m));

        Assert.Equal(
            EquityCurveDisplayState.Unavailable,
            EquityCurveLayout.Build(points, Day0.Ticks, Day0.AddDays(1).Ticks).State);
    }

    [Fact]
    public void ZoomedRange_LabelsDescribeTheVisibleRange()
    {
        ImmutableArray<EquityPoint> points = Series(1m, 2m, 3m, 4m);

        EquityCurveLayout layout = EquityCurveLayout.Build(points, Day0.AddDays(1).Ticks, Day0.AddDays(2).AddHours(6).Ticks);

        Assert.Equal("2024-01-02 00:00", layout.ViewportStartLabel);
        Assert.Equal("2024-01-03 06:00", layout.ViewportEndLabel);
    }

    [Fact]
    public void ResolveVisibleTicks_SnapsToTheExactDataBoundsOnlyForTheirDoubleImage()
    {
        ImmutableArray<EquityPoint> points = Series(1m, 2m, 3m);
        EquityViewportBinder.TryResolveDataBounds(points, out EquityDataBounds bounds);

        (long start, long end) = EquityViewportBinder.ResolveVisibleTicks(bounds.First.Ticks, bounds.Last.Ticks, bounds);
        Assert.Equal(bounds.First.Ticks, start);
        Assert.Equal(bounds.Last.Ticks, end);

        double inside = bounds.First.AddHours(7).Ticks;
        (long zoomedStart, long zoomedEnd) = EquityViewportBinder.ResolveVisibleTicks(inside, bounds.Last.Ticks, bounds);
        Assert.Equal((long)inside, zoomedStart);
        Assert.Equal(bounds.Last.Ticks, zoomedEnd);

        Assert.Equal(
            (bounds.First.Ticks, bounds.Last.Ticks),
            EquityViewportBinder.ResolveVisibleTicks(double.NaN, bounds.Last.Ticks, bounds));
        Assert.Equal(
            (bounds.First.Ticks, bounds.Last.Ticks),
            EquityViewportBinder.ResolveVisibleTicks(bounds.Last.Ticks, bounds.First.Ticks, bounds));
    }
}
