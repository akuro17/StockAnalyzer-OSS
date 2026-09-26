using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Regression test for RegressionTrendObject's control-point handle drag on an EXISTING
/// (already-placed, already-selected) object.
///
/// Reported: "dragging to move the control point shows a straight line connecting start
/// and end instead of the regression line; releasing shows the regression line correctly,
/// but the endpoint jumps to the regression line's fitted position instead of staying
/// where the mouse was released." The two-click CREATION flow (UpdateNewShape) already
/// calls Recalculate() on every PointerMoved and was verified NOT to reproduce this, so
/// the report is about editing an existing object -- confirmed by an executable
/// reproduction: ChartInteractionController.HandleObjectDrag intentionally deferred
/// RegressionTrendObject's Recalculate() to HandlePointerReleased (to avoid per-frame LINQ
/// regression-fit recompute causing drag stutter), so Render()'s "isStale" fallback (①-B)
/// -- a raw straight line connecting Points[0]/Points[1] "as-clicked" -- was shown for the
/// entire drag, and only replaced by the real regression line (with the endpoint's price
/// snapped to the fitted value, per Recalculate()'s existing Points[] sync from fix ①) once
/// the mouse was released. That end-of-drag snap is what made the release look like a jump.
///
/// Fixed by having HandleObjectDrag call Recalculate(snapshot.Candles) live during a
/// RegressionTrendObject handle drag too (mirroring the identical fix already applied to
/// RangeSplineObject), so the real regression line tracks the cursor throughout the drag.
///
/// Verification strategy: Recalculate() already syncs Points[]'s PRICE to the regression
/// fit's value (fix ①), so if live recalculation is actually running mid-drag, the dragged
/// handle's Points[].Price must exactly equal an independently-computed RegressionService
/// fit over the post-drag candle range -- whatever that range's exact boundaries end up
/// being after magnet-snap adjusts the raw click position (checking against the object's
/// OWN resulting Points[] avoids needing to predict the snap's exact output).
/// </summary>
public class RegressionTrendHandleDragLiveUpdateTests
{
    private static List<CoreCandleData> BuildNonLinearCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 30; i++)
        {
            // Pseudo-random (non-symmetric) prices so the real regression fit is
            // unambiguously distinguishable from a straight line connecting the raw
            // click points, regardless of which two days happen to be endpoints.
            decimal close = 100m + (i * 37 % 53);
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    [Fact]
    public void HandleDrag_OnExistingSelectedObject_PointsPriceMatchesLiveRegressionFit_NotRawDraggedPrice()
    {
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        var candles = BuildNonLinearCandles();
        var viewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Candles = candles };

        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), 0m, 300m, 1000, 600);

        var obj = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 5), 100m),
            new ChartPoint(new DateTime(2025, 1, 20), 100m));
        var coreCandles = candles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        obj.Recalculate(coreCandles);

        viewModel.ObjectManager.AddObject(obj);
        viewModel.ObjectManager.SelectObject(obj.Id);

        var handleScreenPos = t.ChartToScreen(obj.Points[0]);
        var clickPoint = new Point(handleScreenPos.X, handleScreenPos.Y);
        bool pressed = controller.HandlePointerPressed(clickPoint, clickPoint, viewModel, t, KeyModifiers.None, 1);
        Assert.True(pressed);

        var snapshot = new ChartDataSnapshot(candles);
        var bounds = new Rect(0, 0, 1000, 600);
        var movePoint = t.ChartToScreen(new ChartPoint(new DateTime(2025, 1, 8), 100m));
        controller.HandlePointerMoved(movePoint, movePoint, viewModel, t, snapshot, bounds, 0, 0, out _, KeyModifiers.None);

        // Independently compute what the regression fit SHOULD be for whatever range
        // Points[] now actually spans (post-drag, post-magnet-snap), and compare against
        // Points[0].Price. If live recalculation ran, Recalculate()'s existing Points[]
        // sync (fix ①) would have overwritten it to exactly this fitted value; if it did
        // NOT run (isStale fallback), Points[0].Price stays whatever the drag/snap left it
        // at, which is never going to exactly equal an independently-computed regression
        // fit price (they're unrelated quantities).
        var p0Time = obj.Points[0].Time;
        var p1Time = obj.Points[1].Time;
        var rangeStart = p0Time < p1Time ? p0Time : p1Time;
        var rangeEnd = p0Time < p1Time ? p1Time : p0Time;
        var expectedRange = coreCandles.Where(c => c.Timestamp >= rangeStart && c.Timestamp <= rangeEnd)
            .OrderBy(c => c.Timestamp).ToList();
        var expectedResult = new RegressionService().Calculate(expectedRange);
        Assert.True(expectedResult.IsValid);

        decimal expectedPriceAtP0 = p0Time <= p1Time ? expectedResult.GetValueAt(0) : expectedResult.GetValueAt(expectedResult.Count - 1);

        Assert.Equal(expectedPriceAtP0, obj.Points[0].Price);
    }
}
