using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// The anchor curves (PCHIP Curve, Cubic Spline Curve) must show the smooth curve at once while they are placed and when the placement finishes, never the straight polyline of their anchors
/// (settings follow-up Run 7). A curve object draws the polyline until a result exists for its CURRENT anchors, so the signal used here is <c>GetCalculatedValues</c>, which is empty for a stale curve.
/// Drives the real <see cref="ChartInteractionController"/> and the view model's <c>OnDrawingFinished</c> exactly as the chart control wires them.
/// </summary>
public sealed class InterpolationCurvePlacementRefreshTests
{
    private static readonly DateTime Start = new(2025, 1, 1);

    private sealed class Harness
    {
        public readonly ChartInteractionController Controller = new(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        public readonly ChartViewModel ViewModel;
        public readonly LinearCoordinateTransform Transform = new(Start, Start.AddDays(80), 0m, 300m, 1000, 600);
        public readonly List<CoreCandleData> Candles = new();

        public Harness(DrawingTool tool)
        {
            for (int i = 0; i < 80; i++)
            {
                decimal close = 100m + ((i * 7) % 23);
                Candles.Add(new CoreCandleData(Start.AddDays(i), close, close + 2m, close - 2m, close, 1000L));
            }

            ViewModel = new ChartViewModel { CurrentTool = tool, Symbol = "CURVE", Candles = Candles };
            ViewModel.UpdateDrawingCoordinator();
            Controller.SetActiveObjectManager(ViewModel.ObjectManager);
            Controller.OnDrawingFinished = obj => ViewModel.OnDrawingFinished(obj);   // as ChartBaseControl wires it
        }

        public Point ScreenAt(double bar, decimal price) => Transform.ChartToScreen(new ChartPoint(Start.AddDays(bar), price));

        public void Click(Point at) => Assert.True(Controller.HandlePointerPressed(at, at, ViewModel, Transform, KeyModifiers.Alt, 1));

        public void MoveTo(Point to)
            => Controller.HandlePointerMoved(to, to, ViewModel, Transform, new ChartDataSnapshot(Candles), new global::Avalonia.Rect(0, 0, 1000, 600), 0, 0, out _, KeyModifiers.Alt);

        public InterpolationCurveObjectBase Placing => Assert.IsAssignableFrom<InterpolationCurveObjectBase>(Controller.CurrentDrawingObject);

        public static bool IsCurrent(InterpolationCurveObjectBase curve) => curve.GetCalculatedValues(DateTime.MinValue).Count > 0;
    }

    public static TheoryData<DrawingTool> Tools() => new() { DrawingTool.PchipCurve, DrawingTool.CubicSplineCurve };

    [Theory]
    [MemberData(nameof(Tools))]
    public void ConfirmingAVertex_LeavesTheCurveCurrent_WithoutWaitingForTheNextPointerMove(DrawingTool tool)
    {
        var h = new Harness(tool);

        h.Click(h.ScreenAt(5, 120m));
        h.MoveTo(h.ScreenAt(15, 160m));
        Assert.True(Harness.IsCurrent(h.Placing), "the curve follows the pointer");

        h.Click(h.ScreenAt(15, 160m));   // a vertex is confirmed: an anchor is added
        Assert.Equal(3, h.Placing.Points.Count);
        Assert.True(Harness.IsCurrent(h.Placing), "the curve is calculated for the new anchors at once (no straight-polyline frame)");

        h.MoveTo(h.ScreenAt(30, 110m));
        h.Click(h.ScreenAt(30, 110m));
        Assert.Equal(4, h.Placing.Points.Count);
        Assert.True(Harness.IsCurrent(h.Placing));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void FinishingThePlacement_ShowsTheSmoothCurve_NotTheStraightPolyline(DrawingTool tool)
    {
        var h = new Harness(tool);
        h.Click(h.ScreenAt(5, 120m));
        h.MoveTo(h.ScreenAt(15, 160m));
        h.Click(h.ScreenAt(15, 160m));
        h.MoveTo(h.ScreenAt(30, 110m));
        h.Click(h.ScreenAt(30, 110m));
        h.MoveTo(h.ScreenAt(45, 140m));

        // Ctrl / Shift / Enter: the trailing preview anchor is dropped and the placement is committed.
        Assert.True(h.Controller.HandleCancelRequest(h.ViewModel.ObjectManager, h.ViewModel.LayerService, h.ViewModel.DrawingEditCoordinator));

        InterpolationCurveObjectBase curve = Assert.IsAssignableFrom<InterpolationCurveObjectBase>(h.ViewModel.ObjectManager.Objects.Single());
        Assert.Equal(3, curve.Points.Count);   // the three clicked anchors: the trailing preview anchor is gone
        Assert.True(Harness.IsCurrent(curve), "the curve is calculated for the committed anchors");
        InterpolationCurve result = curve.Curve;
        Assert.True(result.IsOk);
        Assert.Equal(((curve.Points.Count - 1) * InterpolationCurveValues.SamplesPerSegment) + 1, result.Count);   // sampled, not the 4 anchors of the polyline
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void UndoAndRedoOfThePlacement_KeepTheSmoothCurve(DrawingTool tool)
    {
        var h = new Harness(tool);
        h.Click(h.ScreenAt(5, 120m));
        h.MoveTo(h.ScreenAt(20, 160m));
        h.Click(h.ScreenAt(20, 160m));
        h.MoveTo(h.ScreenAt(35, 110m));
        h.Click(h.ScreenAt(35, 110m));
        Assert.True(h.Controller.HandleCancelRequest(h.ViewModel.ObjectManager, h.ViewModel.LayerService, h.ViewModel.DrawingEditCoordinator));

        Assert.True(h.ViewModel.DrawingEditCoordinator!.Undo().IsSuccess);
        Assert.Empty(h.ViewModel.ObjectManager.Objects);
        Assert.True(h.ViewModel.DrawingEditCoordinator.Redo().IsSuccess);

        InterpolationCurveObjectBase restored = Assert.IsAssignableFrom<InterpolationCurveObjectBase>(h.ViewModel.ObjectManager.Objects.Single());
        Assert.Equal(3, restored.Points.Count);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ClickingBeforeThePreviousAnchor_MakesTheNewCurveTurnBack_NotCollapseOntoTheLine(DrawingTool tool)
    {
        var h = new Harness(tool);
        h.Click(h.ScreenAt(10, 100m));
        h.MoveTo(h.ScreenAt(40, 130m));
        h.Click(h.ScreenAt(40, 130m));
        h.MoveTo(h.ScreenAt(25, 150m));   // the pointer goes back past the previous anchor
        Assert.True(Harness.IsCurrent(h.Placing), "the preview is calculated while the pointer moves");
        h.Click(h.ScreenAt(25, 150m));    // and the anchor is confirmed there
        h.MoveTo(h.ScreenAt(30, 120m));
        Assert.True(h.Controller.HandleCancelRequest(h.ViewModel.ObjectManager, h.ViewModel.LayerService, h.ViewModel.DrawingEditCoordinator));

        InterpolationCurveObjectBase curve = Assert.IsAssignableFrom<InterpolationCurveObjectBase>(h.ViewModel.ObjectManager.Objects.Single());
        Assert.Equal(InterpolationCurveMode.Path, curve.CurveMode);   // a newly placed curve follows the placement order
        Assert.Equal(3, curve.Points.Count);
        Assert.True(Harness.IsCurrent(curve));

        InterpolationCurve path = curve.Curve;
        Assert.True(path.IsOk);
        Assert.Equal(3, path.DistinctAnchors);
        Assert.Contains(Enumerable.Range(1, path.Count - 1), i => path.Times[i] < path.Times[i - 1]);   // it turns back instead of being sorted

        // The setting is the owner's switch back to the previous behaviour.
        curve.CurveMode = InterpolationCurveMode.TimeSeries;
        DeferredComputationRecalculator.TryRecalculate(curve, h.Candles);
        InterpolationCurve sorted = curve.Curve;
        for (int i = 1; i < sorted.Count; i++)
        {
            Assert.True(sorted.Times[i] >= sorted.Times[i - 1]);
        }
    }
}
