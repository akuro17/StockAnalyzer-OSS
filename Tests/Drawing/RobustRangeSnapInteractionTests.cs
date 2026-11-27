using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// The end-point snap of the Robust Regression Trend through the REAL interaction controller and the drawing history (settings expansion Run 5, DS-6 = B): placement, handle release, move
/// completion, the one-step undo of each, and the cases that must not snap (a press without movement).
/// </summary>
public sealed class RobustRangeSnapInteractionTests
{
    private static readonly DateTime Start = new(2025, 1, 1);
    private const decimal AnchorPrice = 200m;

    private sealed class Harness
    {
        public readonly ChartInteractionController Controller = new(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        public readonly ChartViewModel ViewModel;
        public readonly LinearCoordinateTransform Transform;
        public readonly List<CoreCandleData> Candles = new();

        public Harness(int bars = 80)
        {
            Transform = new(Start, Start.AddDays(bars), 0m, 300m, 1000, 600);
            for (int i = 0; i < bars; i++)
            {
                decimal close = 100m + ((i * 7) % 23);
                Candles.Add(new CoreCandleData(Start.AddDays(i), close, close + 2m, close - 2m, close, 1000L));
            }

            ViewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Symbol = "SNAP", Candles = Candles };
            ViewModel.UpdateDrawingCoordinator();
            Assert.NotNull(ViewModel.DrawingEditCoordinator);
            Controller.SetActiveObjectManager(ViewModel.ObjectManager);
            Controller.RangeSnapCandlesProvider = () => ViewModel.Candles;
        }

        public ChartObjectManager Manager => ViewModel.ObjectManager;

        public IDrawingEditCoordinatorAccess Coordinator => new(ViewModel);

        public Point ScreenAt(double bar) => Transform.ChartToScreen(new ChartPoint(Start.AddDays(bar), AnchorPrice));

        /// <summary>Adds a Robust object through the history (one undo step), selected, with the anchors at the given bars.</summary>
        public RobustRegressionTrendObject AddRobust(int firstBar, int lastBar, int period = 30)
        {
            var obj = new RobustRegressionTrendObject(new ChartPoint(Candles[firstBar].Timestamp, AnchorPrice), new ChartPoint(Candles[lastBar].Timestamp, AnchorPrice)) { Period = period };
            ViewModel.DrawingEditCoordinator!.ExecuteEdit(DrawingOperationKind.Add, () => Manager.AddObject(obj));
            Manager.SelectObject(obj.Id);
            obj.Recalculate(Candles);
            return obj;
        }

        public void Press(Point at) => Assert.True(Controller.HandlePointerPressed(at, at, ViewModel, Transform, KeyModifiers.Alt, 1));

        public void DragTo(Point to)
        {
            var snapshot = new ChartDataSnapshot(Candles, startIndex: 0, count: Candles.Count);
            Assert.True(Controller.HandleObjectDrag(to, snapshot, Transform, 0, 0, new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.Alt, allCandles: Candles));
        }

        public void Release() => Controller.HandlePointerReleased(ViewModel);

        public int BarIndex(DateTime time) => Candles.FindIndex(c => c.Timestamp == time);
    }

    /// <summary>A tiny wrapper so the tests read <c>h.Coordinator.UndoCount</c>.</summary>
    private readonly struct IDrawingEditCoordinatorAccess
    {
        private readonly ChartViewModel _vm;

        public IDrawingEditCoordinatorAccess(ChartViewModel vm) => _vm = vm;

        public int UndoCount => _vm.DrawingEditCoordinator!.UndoCount;

        public void Undo() => Assert.True(_vm.DrawingEditCoordinator!.Undo().IsSuccess);

        public void Redo() => Assert.True(_vm.DrawingEditCoordinator!.Redo().IsSuccess);
    }

    private static RobustRegressionTrendObject Only(Harness h) => h.Manager.Objects.OfType<RobustRegressionTrendObject>().Single();

    // ----- placement -----

    [Fact]
    public void Placement_SnapsToThePeriod_InsideTheAddTransaction_SoUndoRemovesAndRedoRestoresTheSnappedObject()
    {
        var h = new Harness();
        h.ViewModel.CurrentTool = DrawingTool.RobustRegressionTrend;

        h.Press(h.ScreenAt(10));
        h.Controller.HandlePointerMoved(h.ScreenAt(16), h.ScreenAt(16), h.ViewModel, h.Transform, new ChartDataSnapshot(h.Candles), new global::Avalonia.Rect(0, 0, 1000, 600), 0, 0, out _, KeyModifiers.Alt);
        h.Press(h.ScreenAt(16));

        RobustRegressionTrendObject placed = Only(h);
        int start = h.BarIndex(placed.Points[0].Time);
        int end = h.BarIndex(placed.Points[1].Time);
        Assert.True(start >= 0 && end >= 0, "both anchors lie on a bar");
        Assert.Equal(30, placed.Period);
        Assert.Equal(start + 29, end);
        Assert.Equal(1, h.Coordinator.UndoCount);

        h.Coordinator.Undo();
        Assert.Empty(h.Manager.Objects.OfType<RobustRegressionTrendObject>());

        h.Coordinator.Redo();
        RobustRegressionTrendObject redone = Only(h);
        Assert.Equal(placed.Points[0].Time, redone.Points[0].Time);
        Assert.Equal(placed.Points[1].Time, redone.Points[1].Time);   // the snap survives undo / redo
        Assert.Equal(30, redone.Period);
    }

    [Fact]
    public void Placement_RightToLeft_GivesTheSameKindOfRange()
    {
        var h = new Harness();
        h.ViewModel.CurrentTool = DrawingTool.RobustRegressionTrend;

        h.Press(h.ScreenAt(30));
        h.Controller.HandlePointerMoved(h.ScreenAt(22), h.ScreenAt(22), h.ViewModel, h.Transform, new ChartDataSnapshot(h.Candles), new global::Avalonia.Rect(0, 0, 1000, 600), 0, 0, out _, KeyModifiers.Alt);
        h.Press(h.ScreenAt(22));

        RobustRegressionTrendObject placed = Only(h);
        int a = h.BarIndex(placed.Points[0].Time);
        int b = h.BarIndex(placed.Points[1].Time);
        // Point 0 (the first click) is the later anchor of a range drawn from right to left.
        Assert.Equal(Math.Min(a, b) + 29, Math.Max(a, b));
        Assert.True(b < a || a < b);
    }

    // ----- handle release -----

    [Fact]
    public void LaterHandleRelease_ResizesThePeriod_InOneUndoStep()
    {
        var h = new Harness();
        RobustRegressionTrendObject obj = h.AddRobust(5, 34);
        Assert.Equal(1, h.Coordinator.UndoCount);

        h.Press(h.ScreenAt(34));
        Assert.Equal(1, h.Controller.DraggedHandleIndex);
        h.DragTo(h.ScreenAt(24.4));
        h.Release();

        Assert.Equal(20, obj.Period);
        Assert.Equal(Start.AddDays(5), obj.Points[0].Time);
        Assert.Equal(Start.AddDays(24), obj.Points[1].Time);
        Assert.Equal(20, obj.Fit.EffectiveBars);   // recalculated on the snapped range
        Assert.Equal(2, h.Coordinator.UndoCount);

        h.Coordinator.Undo();
        RobustRegressionTrendObject back = Only(h);
        Assert.Equal(30, back.Period);
        Assert.Equal(Start.AddDays(5), back.Points[0].Time);
        Assert.Equal(Start.AddDays(34), back.Points[1].Time);
        Assert.Equal(1, h.Coordinator.UndoCount);   // exactly one step was undone

        h.Coordinator.Redo();
        RobustRegressionTrendObject again = Only(h);
        Assert.Equal(20, again.Period);
        Assert.Equal(Start.AddDays(24), again.Points[1].Time);
    }

    [Fact]
    public void LaterHandleRelease_PastTheLimit_ClampsThePeriodToTheToolMaximum()
    {
        // 700 bars so that the pointer can go beyond the largest Period (500): the window ends 499 bars after the start.
        var h = new Harness(bars: 700);
        RobustRegressionTrendObject obj = h.AddRobust(5, 24, period: 20);

        h.Press(h.ScreenAt(24));
        h.DragTo(h.ScreenAt(600.2));
        h.Release();

        Assert.Equal(RobustRegressionTrendValues.MaxBars, obj.Period);
        Assert.Equal(Start.AddDays(5 + RobustRegressionTrendValues.MaxBars - 1), obj.Points[1].Time);
    }

    [Fact]
    public void LaterHandleRelease_PastThirtyBars_IsNoLongerClamped_ItWritesTheWidthBackAsThePeriod()
    {
        var h = new Harness();
        RobustRegressionTrendObject obj = h.AddRobust(5, 24, period: 20);

        h.Press(h.ScreenAt(24));
        h.DragTo(h.ScreenAt(60.2));
        h.Release();

        Assert.Equal(56, obj.Period);   // bars 5..60
        Assert.Equal(Start.AddDays(60), obj.Points[1].Time);
    }

    [Fact]
    public void EarlierHandleRelease_SlidesTheWindow_AndKeepsThePeriod()
    {
        var h = new Harness();
        RobustRegressionTrendObject obj = h.AddRobust(5, 34);

        h.Press(h.ScreenAt(5));
        Assert.Equal(0, h.Controller.DraggedHandleIndex);
        h.DragTo(h.ScreenAt(19.6));
        h.Release();

        Assert.Equal(30, obj.Period);
        int start = h.BarIndex(obj.Points.Min(p => p.Time));
        int end = h.BarIndex(obj.Points.Max(p => p.Time));
        Assert.True(start is 19 or 20, $"start bar {start}");
        Assert.Equal(start + 29, end);
        Assert.Equal(2, h.Coordinator.UndoCount);
    }

    // ----- move completion -----

    [Fact]
    public void BodyMove_ReSnapsToExactlyPeriodBars_AndIsOneUndoStep()
    {
        var h = new Harness();
        RobustRegressionTrendObject obj = h.AddRobust(5, 24, period: 20);

        // Press on the fitted line in the middle of the range (not on a handle) and drag the whole object about 7 bars to the right.
        RangeCurve curve = obj.Curve;
        Assert.True(curve.Count > 10);
        Point grab = h.Transform.ChartToScreen(new ChartPoint(curve.Times[10], curve.Prices[10]));
        h.Press(grab);
        Assert.Equal(-1, h.Controller.DraggedHandleIndex);
        h.DragTo(new Point(grab.X + (h.ScreenAt(12.3).X - h.ScreenAt(5).X), grab.Y));
        h.Release();

        Assert.Equal(20, obj.Period);
        int start = h.BarIndex(obj.Points.Min(p => p.Time));
        int end = h.BarIndex(obj.Points.Max(p => p.Time));
        Assert.True(start >= 10, $"moved: start bar {start}");
        Assert.Equal(start + 19, end);
        Assert.Equal(2, h.Coordinator.UndoCount);
    }

    // ----- no movement, no snap -----

    [Fact]
    public void APressAndReleaseWithoutMovement_NeverSnapsALegacyRange()
    {
        var h = new Harness();
        // A range from an older version: 51 bars long although the period is 30.
        RobustRegressionTrendObject legacy = h.AddRobust(3, 53);
        DateTime t0 = legacy.Points[0].Time;
        DateTime t1 = legacy.Points[1].Time;

        h.Press(h.ScreenAt(53));
        h.Release();

        Assert.Equal(t0, legacy.Points[0].Time);
        Assert.Equal(t1, legacy.Points[1].Time);
        Assert.Equal(30, legacy.Period);
    }
}
