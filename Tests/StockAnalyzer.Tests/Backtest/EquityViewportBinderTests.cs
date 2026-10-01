using System;
using System.Collections.Immutable;
using System.Linq;
using Avalonia;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Pins how the shared <see cref="ViewportManager"/> (which keeps hidden state: default scale, reset flag, future limit) is fed for the
/// equity chart, and that its pixel mapping agrees with <see cref="EquityPlotArea"/>.
/// </summary>
public class EquityViewportBinderTests
{
    private static readonly DateTime Day0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Rect Plot = new(64d, 16d, 320d, 200d);

    private static ImmutableArray<EquityPoint> Daily(int count, int firstDayOffset = 0) =>
        Enumerable.Range(0, count)
            .Select(i => new EquityPoint(i, Day0.AddDays(firstDayOffset + i), 100m + i, 100m + i, 0m, 0m))
            .ToImmutableArray();

    private static EquityDataBounds BoundsOf(ImmutableArray<EquityPoint> points)
    {
        Assert.True(EquityViewportBinder.TryResolveDataBounds(points, out EquityDataBounds bounds));
        return bounds;
    }

    private static ViewportManager Loaded(ImmutableArray<EquityPoint> points)
    {
        var viewport = new ViewportManager();
        EquityViewportBinder.Reset(viewport, BoundsOf(points), Plot);
        return viewport;
    }

    [Fact]
    public void DataBounds_AreTheFirstLastAndSmallestPositiveSpacing()
    {
        var points = ImmutableArray.Create(
            new EquityPoint(0, Day0, 1m, 0m, 0m, 0m),
            new EquityPoint(1, Day0.AddDays(1), 1m, 0m, 0m, 0m),
            new EquityPoint(2, Day0.AddDays(1).AddHours(6), 1m, 0m, 0m, 0m),
            new EquityPoint(3, Day0.AddDays(4), 1m, 0m, 0m, 0m));

        EquityDataBounds bounds = BoundsOf(points);

        Assert.Equal(Day0, bounds.First);
        Assert.Equal(Day0.AddDays(4), bounds.Last);
        Assert.Equal(TimeSpan.FromHours(6), bounds.Interval);
    }

    [Fact]
    public void DataBounds_AreRefusedForSeriesTheLayoutCannotDraw()
    {
        DateTime local = DateTime.SpecifyKind(Day0, DateTimeKind.Local);

        Assert.False(EquityViewportBinder.TryResolveDataBounds(ImmutableArray<EquityPoint>.Empty, out _));
        Assert.False(EquityViewportBinder.TryResolveDataBounds(Daily(1), out _));
        Assert.False(EquityViewportBinder.TryResolveDataBounds(
            ImmutableArray.Create(new EquityPoint(0, local, 1m, 0m, 0m, 0m), new EquityPoint(1, local.AddDays(1), 1m, 0m, 0m, 0m)), out _));
        Assert.False(EquityViewportBinder.TryResolveDataBounds(
            ImmutableArray.Create(new EquityPoint(0, Day0, 1m, 0m, 0m, 0m), new EquityPoint(1, Day0, 1m, 0m, 0m, 0m)), out _));
    }

    [Fact]
    public void Reset_ShowsTheWholeSeriesAtTheExactDataBounds()
    {
        ImmutableArray<EquityPoint> points = Daily(11);
        ViewportManager viewport = Loaded(points);
        EquityDataBounds bounds = BoundsOf(points);

        Assert.Equal((double)bounds.First.Ticks, viewport.VisibleStartUnit);
        Assert.Equal((double)bounds.Last.Ticks, viewport.VisibleEndUnit);
        Assert.Equal(Plot.Width / (bounds.Last.Ticks - bounds.First.Ticks), viewport.PixelsPerUnit, precision: 15);
        Assert.Equal(
            (bounds.First.Ticks, bounds.Last.Ticks),
            EquityViewportBinder.ResolveVisibleTicks(viewport.VisibleStartUnit, viewport.VisibleEndUnit, bounds));
    }

    [Fact]
    public void Reset_BeforeTheControlHasASize_DerivesTheScaleWhenTheSizeArrives()
    {
        ImmutableArray<EquityPoint> points = Daily(11);
        EquityDataBounds bounds = BoundsOf(points);
        var viewport = new ViewportManager();

        EquityViewportBinder.Reset(viewport, bounds, default);
        EquityViewportBinder.Resize(viewport, Plot);

        Assert.Equal((double)bounds.First.Ticks, viewport.VisibleStartUnit);
        Assert.Equal((double)bounds.Last.Ticks, viewport.VisibleEndUnit);
        Assert.Equal(Plot.Width / (bounds.Last.Ticks - bounds.First.Ticks), viewport.PixelsPerUnit, precision: 15);
    }

    [Fact]
    public void Zoom_KeepsTheTimeUnderThePointerAndNarrowsTheRange()
    {
        ViewportManager viewport = Loaded(Daily(101));
        double pivotX = Plot.X + (Plot.Width * 0.25);
        double unitBefore = viewport.ScreenXToUnit(pivotX);
        double durationBefore = viewport.VisibleEndUnit - viewport.VisibleStartUnit;

        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), pivotX);

        double durationAfter = viewport.VisibleEndUnit - viewport.VisibleStartUnit;
        Assert.Equal(durationBefore / EquityViewportBinder.ZoomFactor(1d), durationAfter, durationBefore * 1e-9);
        Assert.Equal(unitBefore, viewport.ScreenXToUnit(pivotX), durationBefore * 1e-9);
    }

    [Fact]
    public void ZoomOut_OnTheWholeSeries_ChangesNothing()
    {
        ViewportManager viewport = Loaded(Daily(101));
        double start = viewport.VisibleStartUnit;
        double end = viewport.VisibleEndUnit;

        viewport.Zoom(EquityViewportBinder.ZoomFactor(-1d), Plot.X + 10d);

        Assert.Equal(start, viewport.VisibleStartUnit, TimeSpan.TicksPerSecond);
        Assert.Equal(end, viewport.VisibleEndUnit, TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void ZoomIn_StopsAtTwoDataSpacings()
    {
        ImmutableArray<EquityPoint> points = Daily(101);
        ViewportManager viewport = Loaded(points);

        for (int i = 0; i < 400; i++)
        {
            viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X + (Plot.Width / 2d));
        }

        double duration = viewport.VisibleEndUnit - viewport.VisibleStartUnit;
        Assert.Equal(2d * BoundsOf(points).Interval.Ticks, duration, TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Pan_MovesTheViewByPixelsAndStopsAtTheDataEnd()
    {
        ImmutableArray<EquityPoint> points = Daily(101);
        EquityDataBounds bounds = BoundsOf(points);
        ViewportManager viewport = Loaded(points);
        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X);
        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X);
        double start = viewport.VisibleStartUnit;
        double pixelsPerUnit = viewport.PixelsPerUnit;

        viewport.Pan(-40d);

        Assert.Equal(start + (40d / pixelsPerUnit), viewport.VisibleStartUnit, TimeSpan.TicksPerSecond);

        viewport.Pan(-1_000_000d);

        Assert.True(viewport.VisibleEndUnit <= bounds.Last.Ticks + TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Reset_WithAShorterSeries_DoesNotKeepThePreviousRightLimit()
    {
        var viewport = new ViewportManager();
        EquityViewportBinder.Reset(viewport, BoundsOf(Daily(200)), Plot);

        ImmutableArray<EquityPoint> shorter = Daily(20);
        EquityViewportBinder.Reset(viewport, BoundsOf(shorter), Plot);
        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X);
        viewport.Pan(-1_000_000d);

        Assert.True(viewport.VisibleEndUnit <= BoundsOf(shorter).Last.Ticks + TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void ShowAll_RestoresTheWholeSeries()
    {
        ImmutableArray<EquityPoint> points = Daily(101);
        EquityDataBounds bounds = BoundsOf(points);
        ViewportManager viewport = Loaded(points);
        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X + 50d);
        viewport.Pan(30d);

        EquityViewportBinder.ShowAll(viewport, bounds);

        Assert.Equal(
            (bounds.First.Ticks, bounds.Last.Ticks),
            EquityViewportBinder.ResolveVisibleTicks(viewport.VisibleStartUnit, viewport.VisibleEndUnit, bounds));
    }

    [Fact]
    public void Resize_KeepsTheVisibleRangeAndRescales()
    {
        ViewportManager viewport = Loaded(Daily(101));
        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X + 80d);
        double start = viewport.VisibleStartUnit;
        double end = viewport.VisibleEndUnit;

        var wider = new Rect(Plot.X, Plot.Y, Plot.Width + 200d, Plot.Height);
        EquityViewportBinder.Resize(viewport, wider);

        Assert.Equal(start, viewport.VisibleStartUnit);
        Assert.Equal(end, viewport.VisibleEndUnit, TimeSpan.TicksPerSecond);
        Assert.Equal(wider.Width / (viewport.VisibleEndUnit - viewport.VisibleStartUnit), viewport.PixelsPerUnit, precision: 15);
    }

    [Fact]
    public void ZoomFactor_IsSymmetricAndNeutralForNoWheelMovement()
    {
        Assert.Equal(BacktestUiConstants.EquityZoomInFactor, EquityViewportBinder.ZoomFactor(3d));
        Assert.Equal(1d, EquityViewportBinder.ZoomFactor(-3d) * EquityViewportBinder.ZoomFactor(3d), precision: 12);
        Assert.Equal(1d, EquityViewportBinder.ZoomFactor(0d));
    }

    [Fact]
    public void PlotProjection_AgreesWithTheViewportPixelMapping()
    {
        ImmutableArray<EquityPoint> points = Daily(101);
        EquityDataBounds bounds = BoundsOf(points);
        ViewportManager viewport = Loaded(points);
        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X + 100d);
        viewport.Zoom(EquityViewportBinder.ZoomFactor(1d), Plot.X + 100d);
        viewport.Pan(25d);

        (long start, long end) = EquityViewportBinder.ResolveVisibleTicks(viewport.VisibleStartUnit, viewport.VisibleEndUnit, bounds);
        EquityCurveLayout layout = EquityCurveLayout.Build(points, start, end);
        var plot = new EquityPlotArea(Plot.Left, Plot.Top, Plot.Right, Plot.Bottom);

        Assert.NotEmpty(layout.Points);
        for (int i = 0; i < layout.Points.Length; i++)
        {
            double fromProjection = plot.ToScreen(layout.Points[i]).X;
            double fromViewport = viewport.UnitToScreenX(points[layout.FirstIndex + i].Timestamp.Ticks);
            Assert.Equal(fromViewport, fromProjection, precision: 3);

            // The pointer path (MP-2) goes back through the viewport: screen X -> time must return the point's own instant.
            double unit = viewport.ScreenXToUnit(fromProjection);
            Assert.Equal((double)points[layout.FirstIndex + i].Timestamp.Ticks, unit, TimeSpan.TicksPerSecond);
        }
    }
}
