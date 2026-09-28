using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// AT02 on the real chart control with PointerType.Touch. Avalonia.Headless has no touch helper, so events are
/// built from the public pointer event types and raised on the control.
/// </summary>
[Collection("DrawingThemeContext State")]
public class FreehandTouchChartInteractionTests
{
    private sealed class TouchHarness : IDisposable
    {
        private readonly Window _window;
        private readonly ChartBaseControl _chart;

        public ChartViewModel ViewModel { get; }
        public Pointer Owner { get; } = new(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
        public Pointer Other { get; } = new(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: false);

        public TouchHarness()
        {
            var candles = new List<CoreCandleData>();
            var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 40; i++)
                candles.Add(new CoreCandleData(time.AddDays(i), 100m, 110m, 90m, 105m, 1000));
            ViewModel = new ChartViewModel { Candles = candles, CurrentTool = DrawingTool.Freehand };
            ViewModel.UpdateDrawingCoordinator();
            _chart = new ChartBaseControl { ViewModel = ViewModel };
            _window = new Window { Width = 800, Height = 600, Content = _chart };
            _window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        private static ulong Now => (ulong)Environment.TickCount64;

        public void Press(Pointer pointer, Point position)
        {
            var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            _chart.RaiseEvent(new PointerPressedEventArgs(_chart, pointer, _window, position, Now, props, KeyModifiers.None));
        }

        public void Move(Pointer pointer, Point position)
        {
            var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
            _chart.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, _chart, pointer, _window, position, Now, props, KeyModifiers.None));
        }

        public void Release(Pointer pointer, Point position)
        {
            var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
            _chart.RaiseEvent(new PointerReleasedEventArgs(_chart, pointer, _window, position, Now, props, KeyModifiers.None, MouseButton.Left));
        }

        public void LoseCapture(Pointer pointer) =>
            _chart.RaiseEvent(new PointerCaptureLostEventArgs(_chart, pointer));

        public void Dispose()
        {
            ViewModel.DrawingEditCoordinator?.Dispose();
            _window.Close();
        }
    }

    [AvaloniaFact]
    public void TouchStroke_SecondContact_IsIgnored_AndOwnerCommitsExactlyOneObjectAndHistoryEntry()
    {
        using var h = new TouchHarness();
        var coordinator = h.ViewModel.DrawingEditCoordinator!;

        h.Press(h.Owner, new Point(100, 120));
        Assert.True(coordinator.IsEditing);

        h.Press(h.Other, new Point(300, 200));
        Assert.True(coordinator.IsEditing);
        Assert.Equal(0, coordinator.UndoCount);

        h.Move(h.Other, new Point(310, 210));
        h.Move(h.Owner, new Point(120, 150));
        h.Release(h.Other, new Point(320, 220));
        Assert.True(coordinator.IsEditing); // the second contact lifting does not end the owner's stroke

        h.Release(h.Owner, new Point(160, 160));

        Assert.False(coordinator.IsEditing);
        Assert.Equal(1, coordinator.UndoCount);
        var stroke = Assert.Single(h.ViewModel.ObjectManager.Objects);
        Assert.Equal(3, stroke.Points.Count);
        Assert.Equal(DrawingTool.Freehand, h.ViewModel.CurrentTool);
    }

    [AvaloniaFact]
    public void TouchStroke_CaptureLostOfNonOwnerContact_DoesNotCancelTheOwnersStroke()
    {
        using var h = new TouchHarness();
        var coordinator = h.ViewModel.DrawingEditCoordinator!;

        h.Press(h.Owner, new Point(100, 120));
        h.Press(h.Other, new Point(300, 200));
        h.LoseCapture(h.Other); // e.g. the second finger lifts and its implicit capture is released

        Assert.True(coordinator.IsEditing);

        h.Move(h.Owner, new Point(120, 150));
        h.Release(h.Owner, new Point(160, 160));

        Assert.False(coordinator.IsEditing);
        Assert.Equal(1, coordinator.UndoCount);
        Assert.Single(h.ViewModel.ObjectManager.Objects);
    }

    [AvaloniaFact]
    public void TouchStroke_CaptureLostOfOwnerContact_CancelsTheStrokeWithoutHistory()
    {
        using var h = new TouchHarness();
        var coordinator = h.ViewModel.DrawingEditCoordinator!;

        h.Press(h.Owner, new Point(100, 120));
        Assert.True(coordinator.IsEditing);

        h.LoseCapture(h.Owner);

        Assert.False(coordinator.IsEditing);
        Assert.Equal(0, coordinator.UndoCount);
        Assert.Empty(h.ViewModel.ObjectManager.Objects);
    }
}
