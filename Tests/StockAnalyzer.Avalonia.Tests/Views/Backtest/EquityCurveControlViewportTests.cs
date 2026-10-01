using System;
using System.Collections.Immutable;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.Views.Backtest.EquityChartTestHost;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>Wheel zoom, drag pan, double-click reset and the cache contract of <see cref="EquityCurveControl"/>'s own viewport.</summary>
public class EquityCurveControlViewportTests
{
    [AvaloniaFact]
    public void InitialView_ShowsTheWholeSeries()
    {
        ImmutableArray<EquityPoint> points = Daily(60);
        (Window window, EquityCurveControl control) = Show(points);
        try
        {
            Assert.True(control.IsViewportInteractive);
            Assert.Equal((points[0].Timestamp.Ticks, points[^1].Timestamp.Ticks), control.VisibleTicks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Wheel_OverThePlot_ZoomsInAndRebuildsTheSnapshotOnce()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            (long startBefore, long endBefore) = control.VisibleTicks;
            int builds = control.SnapshotBuildCount;
            bool handled = false;
            window.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => handled = e.Handled, RoutingStrategies.Bubble, handledEventsToo: true);

            Wheel(window, control, InsidePlot, 1);
            Settle();

            (long start, long end) = control.VisibleTicks;
            Assert.True(end - start < endBefore - startBefore, "zoom in must narrow the range");
            Assert.True(handled, "a wheel step that zoomed is consumed so the page does not scroll too");
            Assert.Equal(builds + 1, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AWheelEvent_NeverRebuildsTheSnapshotInsideTheEvent_ThePaintDoes()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            int buildsBeforeEvent = -1;
            int buildsWhenTheEventReachedTheWindow = -1;
            window.AddHandler(
                InputElement.PointerWheelChangedEvent,
                (_, _) => buildsWhenTheEventReachedTheWindow = control.SnapshotBuildCount,
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            for (int i = 0; i < 4; i++)
            {
                buildsBeforeEvent = control.SnapshotBuildCount;
                (long startBefore, long endBefore) = control.VisibleTicks;

                Wheel(window, control, InsidePlot, 1);

                // The control has zoomed by the time the event reaches the window, but pan/zoom events arrive faster than frames,
                // so the rebuild is left to the next paint.
                Assert.Equal(buildsBeforeEvent, buildsWhenTheEventReachedTheWindow);
                Assert.True(control.VisibleTicks.EndTicks - control.VisibleTicks.StartTicks < endBefore - startBefore);
                Settle();
                Assert.Equal(buildsBeforeEvent + 1, control.SnapshotBuildCount);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Wheel_ZoomingOutOfTheWholeSeries_IsLeftToTheParentAndChangesNothing()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            (long, long) before = control.VisibleTicks;
            int builds = control.SnapshotBuildCount;
            int wheelEvents = 0;
            bool handled = false;
            window.AddHandler(
                InputElement.PointerWheelChangedEvent,
                (_, e) =>
                {
                    wheelEvents++;
                    handled = e.Handled;
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            Wheel(window, control, InsidePlot, -1);
            Settle();

            Assert.Equal(1, wheelEvents);
            Assert.False(handled);
            Assert.Equal(before, control.VisibleTicks);
            Assert.Equal(builds, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Wheel_OverTheAxisMargin_DoesNothing()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            (long, long) before = control.VisibleTicks;

            Wheel(window, control, InAxisMargin, 1);
            Settle();

            Assert.Equal(before, control.VisibleTicks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Drag_PansTowardsEarlierTimesWhenDraggedRight()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            Point wheelPoint = InWindow(window, control, InsidePlot);
            Wheel(window, control, InsidePlot, 1);
            Wheel(window, control, InsidePlot, 1);
            Wheel(window, control, InsidePlot, 1);
            Settle();
            // Zoomed around the plot's middle, so the view has room on both sides.
            (long start, long end) = control.VisibleTicks;
            int builds = control.SnapshotBuildCount;
            const double dragPixels = 40d;
            const double plotWidth = 320d;

            window.MouseDown(wheelPoint, MouseButton.Left);
            window.MouseMove(new Point(wheelPoint.X + dragPixels, wheelPoint.Y));
            window.MouseUp(new Point(wheelPoint.X + dragPixels, wheelPoint.Y), MouseButton.Left);
            Settle();

            (long panStart, long panEnd) = control.VisibleTicks;
            double expectedShift = dragPixels * (end - start) / plotWidth;
            Assert.Equal(end - start, panEnd - panStart, TimeSpan.TicksPerMinute);
            Assert.Equal(expectedShift, start - panStart, TimeSpan.TicksPerMinute);
            Assert.Equal(builds + 1, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PointerMove_WithoutADrag_DoesNotRebuildTheSnapshot()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            int builds = control.SnapshotBuildCount;

            window.MouseMove(InWindow(window, control, InsidePlot));
            window.MouseMove(InWindow(window, control, new Point(InsidePlot.X + 30d, InsidePlot.Y)));
            Settle();

            Assert.Equal(builds, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DoubleClick_ShowsTheWholeSeriesAgain()
    {
        ImmutableArray<EquityPoint> points = Daily(60);
        (Window window, EquityCurveControl control) = Show(points);
        try
        {
            Point point = InWindow(window, control, InsidePlot);
            Wheel(window, control, InsidePlot, 1);
            Wheel(window, control, InsidePlot, 1);
            Settle();
            Assert.NotEqual((points[0].Timestamp.Ticks, points[^1].Timestamp.Ticks), control.VisibleTicks);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Settle();

            Assert.Equal((points[0].Timestamp.Ticks, points[^1].Timestamp.Ticks), control.VisibleTicks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NewResult_ShowsItWholeInsteadOfCarryingOverThePreviousZoom()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            Wheel(window, control, InsidePlot, 1);
            Settle();

            ImmutableArray<EquityPoint> next = Daily(30, firstDayOffset: 400, baseEquity: 500m);
            control.EquityPoints = next;
            control.ResultRevision++;
            Settle();

            Assert.Equal((next[0].Timestamp.Ticks, next[^1].Timestamp.Ticks), control.VisibleTicks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResultRevisionBump_AfterAZoom_RebuildsExactlyOnce()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            Wheel(window, control, InsidePlot, 1);
            Settle();
            int builds = control.SnapshotBuildCount;

            control.ResultRevision++;
            Settle();

            Assert.Equal(builds + 1, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SinglePointOrEmptySeries_IsNotInteractive()
    {
        foreach (int count in new[] { 0, 1 })
        {
            (Window window, EquityCurveControl control) = Show(Daily(count));
            try
            {
                int builds = control.SnapshotBuildCount;
                bool handled = false;
                window.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => handled = e.Handled, RoutingStrategies.Bubble, handledEventsToo: true);

                Wheel(window, control, InsidePlot, 1);
                Settle();

                Assert.False(control.IsViewportInteractive);
                Assert.False(handled);
                Assert.Equal(builds, control.SnapshotBuildCount);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Resize_KeepsTheZoomedTimeRange()
    {
        (Window window, EquityCurveControl control) = Show(Daily(60));
        try
        {
            Wheel(window, control, InsidePlot, 1);
            Wheel(window, control, InsidePlot, 1);
            Settle();
            (long start, long end) = control.VisibleTicks;

            control.Width = 600;
            Settle();

            (long resizedStart, long resizedEnd) = control.VisibleTicks;
            Assert.Equal(start, resizedStart, TimeSpan.TicksPerMinute);
            Assert.Equal(end, resizedEnd, TimeSpan.TicksPerMinute);
        }
        finally
        {
            window.Close();
        }
    }
}
