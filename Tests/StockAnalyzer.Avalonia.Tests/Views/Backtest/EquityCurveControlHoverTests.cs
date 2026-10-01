using System;
using System.Collections.Immutable;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.Views.Backtest.EquityChartTestHost;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>Crosshair + value readout (MP-2) and trade markers (MP-3) of <see cref="EquityCurveControl"/>.</summary>
// Reads/changes the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class EquityCurveControlHoverTests
{
    private static BacktestTradeRow Trade(long id, DateTime entry, DateTime exit, bool forced = false) => new()
    {
        TradeId = id,
        SideKind = TradeSide.Long,
        EntryTime = entry,
        EntryPrice = 100m,
        ExitTime = exit,
        ExitPrice = 110m,
        Quantity = 1m,
        ClosedNet = 10m,
        PnLSemantic = BacktestMetricSemantic.Plus,
        IsForcedLiquidation = forced,
    };

    [AvaloniaFact]
    public void PointerOverThePlot_SnapsACrosshairToTheNearestPointWithoutRebuildingTheSnapshot()
    {
        ImmutableArray<EquityPoint> points = Daily(60);
        (Window window, EquityCurveControl control) = Show(points);
        try
        {
            int builds = control.SnapshotBuildCount;
            (long start, long end) = control.VisibleTicks;
            const double x = 176d;
            long pointerTicks = start + (long)((x - PlotLeft) / (PlotRight - PlotLeft) * (end - start));
            int expectedIndex = EquityNearestPoint.FindIndex(points, pointerTicks);

            MoveTo(window, control, new Point(x, 100d));

            EquityHoverView hover = Assert.IsType<EquityHoverView>(control.ActiveHover);
            Assert.Equal(EquityHoverKind.Point, hover.Kind);
            Assert.Equal(expectedIndex, hover.Index);
            double expectedX = PlotLeft + ((points[expectedIndex].Timestamp.Ticks - start) / (double)(end - start) * (PlotRight - PlotLeft));
            Assert.Equal(expectedX, hover.Anchor.X, precision: 6);
            Assert.InRange(hover.Anchor.Y, 16d, 208d);

            MoveTo(window, control, new Point(x + 40d, 120d));
            Assert.Equal(builds, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PointerOverTheMarginOrOutsideTheControl_ShowsNoCrosshair()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            MoveTo(window, control, InsidePlot);
            Assert.NotNull(control.ActiveHover);

            MoveTo(window, control, InAxisMargin);
            Assert.Null(control.ActiveHover);

            MoveTo(window, control, InsidePlot);
            Assert.NotNull(control.ActiveHover);

            window.MouseMove(new Point(5d, 5d));
            Settle();
            Assert.Null(control.ActiveHover);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ZoomingWhileHovering_ReResolvesTheCrosshairAgainstTheNewView()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            MoveTo(window, control, InsidePlot);
            EquityHoverView before = Assert.IsType<EquityHoverView>(control.ActiveHover);

            window.MouseWheel(InWindow(window, control, InsidePlot), new Vector(0, 1));
            window.MouseWheel(InWindow(window, control, InsidePlot), new Vector(0, 1));
            window.MouseWheel(InWindow(window, control, InsidePlot), new Vector(0, 1));
            Settle();

            EquityHoverView after = Assert.IsType<EquityHoverView>(control.ActiveHover);
            Assert.Equal(EquityHoverKind.Point, after.Kind);
            Assert.InRange(after.Anchor.X, PlotLeft, PlotRight);
            // Three zoom steps spread the points apart: the snapped point is a different one or at a different place.
            Assert.True(after.Index != before.Index || Math.Abs(after.Anchor.X - before.Anchor.X) > 0.01);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SinglePointSeries_ShowsTheCrosshairOnThatPoint()
    {
        (Window window, EquityCurveControl control) = Show(Daily(1));
        try
        {
            MoveTo(window, control, InsidePlot);

            EquityHoverView hover = Assert.IsType<EquityHoverView>(control.ActiveHover);
            Assert.Equal(0, hover.Index);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EmptySeries_ShowsNothing()
    {
        (Window window, EquityCurveControl control) = Show(Daily(0));
        try
        {
            MoveTo(window, control, InsidePlot);

            Assert.Null(control.ActiveHover);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Markers_ArePlacedOnTheEquityValueOfTheirPointAndHoverShowsTheTrade()
    {
        ImmutableArray<EquityPoint> points = Daily(60);
        var trades = ImmutableArray.Create(Trade(1, Day0.AddDays(10), Day0.AddDays(20)));
        (Window window, EquityCurveControl control) = Show(points, trades);
        try
        {
            Assert.Equal(2, control.Markers.Length);
            Assert.Equal(2, control.RenderedMarkerPlacements.Length);

            (long start, long end) = control.VisibleTicks;
            EquityMarkerPlacement entry = control.RenderedMarkerPlacements[0];
            double expectedX = PlotLeft + ((points[10].Timestamp.Ticks - start) / (double)(end - start) * (PlotRight - PlotLeft));
            Assert.Equal(expectedX, entry.X, precision: 6);

            int builds = control.SnapshotBuildCount;
            MoveTo(window, control, new Point(entry.X + 3d, entry.Y + 2d));

            EquityHoverView hover = Assert.IsType<EquityHoverView>(control.ActiveHover);
            Assert.Equal(EquityHoverKind.Marker, hover.Kind);
            Assert.Equal(entry.MarkerIndex, hover.Index);
            Assert.Equal(EquityMarkerKind.Entry, control.Markers[hover.Index].Kind);

            MoveTo(window, control, new Point(entry.X + 60d, entry.Y));
            Assert.Equal(EquityHoverKind.Point, control.ActiveHover?.Kind);
            Assert.Equal(builds, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Markers_OutsideTheVisibleRangeAreClippedButStayInTheMarkerList()
    {
        var trades = ImmutableArray.Create(Trade(1, Day0.AddDays(10), Day0.AddDays(20)));
        (Window window, EquityCurveControl control) = Show(Daily(60), trades);
        try
        {
            Point rightEdge = InWindow(window, control, new Point(PlotRight - 1d, 100d));
            window.MouseMove(rightEdge);
            for (int i = 0; i < 10; i++)
            {
                window.MouseWheel(rightEdge, new Vector(0, 1));
            }
            Settle();

            Assert.Equal(2, control.Markers.Length);
            Assert.Empty(control.RenderedMarkerPlacements);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TradesBeforeTheEvaluatedSeries_HaveNoMarkerAtThatTime()
    {
        var trades = ImmutableArray.Create(Trade(1, Day0.AddDays(-5), Day0.AddDays(8)));
        (Window window, EquityCurveControl control) = Show(Daily(60), trades);
        try
        {
            EquityTradeMarker only = Assert.Single(control.Markers);
            Assert.Equal(EquityMarkerKind.Exit, only.Kind);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NoTrades_MeansNoMarkers()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            Assert.Empty(control.Markers);
            Assert.Empty(control.RenderedMarkerPlacements);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NewResult_ReplacesMarkersAndClearsTheHover()
    {
        var trades = ImmutableArray.Create(Trade(1, Day0.AddDays(10), Day0.AddDays(20)));
        (Window window, EquityCurveControl control) = Show(Daily(60), trades);
        try
        {
            MoveTo(window, control, InsidePlot);
            Assert.NotNull(control.ActiveHover);

            control.Trades = ImmutableArray<BacktestTradeRow>.Empty;
            control.ResultRevision++;
            Settle();

            Assert.Empty(control.Markers);
            Assert.Empty(control.RenderedMarkerPlacements);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResultRevisionBump_WithUnchangedTrades_DoesNotRebuildTheMarkers()
    {
        var trades = ImmutableArray.Create(Trade(1, Day0.AddDays(10), Day0.AddDays(20)));
        (Window window, EquityCurveControl control) = Show(Daily(60), trades);
        try
        {
            ImmutableArray<EquityTradeMarker> before = control.Markers;
            int builds = control.SnapshotBuildCount;

            control.ResultRevision++;
            Settle();

            Assert.Equal(before, control.Markers);
            Assert.Equal(builds + 1, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HoverPanelFontSize_FollowsTheTooltipFontSizeResource_AndTheDrawnPanelScalesWithIt()
    {
        const double sentinel = 37d;
        IResourceDictionary resources = Application.Current!.Resources;
        bool hadValue = resources.TryGetValue("TooltipFontSize", out object? previous);
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            resources.Remove("TooltipFontSize");
            Assert.Equal(FontDefaults.Tooltip, control.HoverPanelFontSize);
            MoveTo(window, control, InsidePlot);
            double defaultHeight = Assert.IsType<Rect>(control.HoverPanelRect).Height;

            resources["TooltipFontSize"] = sentinel;
            Assert.Equal(sentinel, control.HoverPanelFontSize);
            // The panel is rebuilt at the next paint when the setting changed, without moving the pointer.
            using (var bitmap = new RenderTargetBitmap(new PixelSize(400, 240)))
            {
                bitmap.Render(control);
            }
            double largeHeight = Assert.IsType<Rect>(control.HoverPanelRect).Height;

            Assert.True(largeHeight > defaultHeight, $"panel height {largeHeight} did not grow from {defaultHeight} with the larger tooltip font");
        }
        finally
        {
            if (hadValue) resources["TooltipFontSize"] = previous;
            else resources.Remove("TooltipFontSize");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HoverPanel_StaysInsideAControlNarrowerThanItsText()
    {
        const double hugeTooltipFont = 40d;
        IResourceDictionary resources = Application.Current!.Resources;
        bool hadValue = resources.TryGetValue("TooltipFontSize", out object? previous);
        resources["TooltipFontSize"] = hugeTooltipFont;
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            control.Width = 300;
            Settle();

            MoveTo(window, control, new Point(150d, 100d));

            Rect panel = Assert.IsType<Rect>(control.HoverPanelRect);
            Assert.True(panel.Left >= 0d && panel.Right <= control.Bounds.Width + 1e-9, $"panel {panel} leaves the control {control.Bounds.Size}");
        }
        finally
        {
            if (hadValue) resources["TooltipFontSize"] = previous;
            else resources.Remove("TooltipFontSize");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PaintingHoverAndMarkers_DoesNotThrow_ForEveryHoverKind()
    {
        ImmutableArray<EquityPoint> points = Daily(60);
        var trades = ImmutableArray.Create(
            Trade(1, Day0.AddDays(10), Day0.AddDays(20)),
            Trade(2, Day0.AddDays(25), Day0.AddDays(30), forced: true));
        (Window window, EquityCurveControl control) = Show(points, trades);
        try
        {
            EquityMarkerPlacement marker = control.RenderedMarkerPlacements[0];
            foreach (Point target in new[] { InsidePlot, new Point(marker.X, marker.Y), new Point(PlotRight - 1d, 100d), new Point(PlotLeft + 1d, 100d) })
            {
                MoveTo(window, control, target);
                Assert.NotNull(control.ActiveHover);
                using var bitmap = new RenderTargetBitmap(new PixelSize(400, 240));
                bitmap.Render(control);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
