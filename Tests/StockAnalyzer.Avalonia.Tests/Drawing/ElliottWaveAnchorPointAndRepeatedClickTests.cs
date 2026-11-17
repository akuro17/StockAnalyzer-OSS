using System;
using System.Linq;
using Avalonia.Input;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Plan 07 review items 04 and 06 (docs/sa_implementation_plan_WebAiPeerReview_Plans07to08, Run 3):
/// the selection handles of DatePriceRange / ElliottWave highlight the anchor point (AP) like every other tool,
/// the AP cycle and the link snap of an ElliottWave only use its displayed (active) points, and a single click on the
/// coordinate of the previous placed point does not count as a new point.
/// </summary>
[Collection("DrawingThemeContext State")]
public sealed class ElliottWaveAnchorPointAndRepeatedClickTests
{
    private const int CanvasSize = 400;

    private static ElliottWaveObject CreateWave(WavePatternMode mode, bool selected = false) => new(new[]
    {
        PixelCoordinateTransform.Chart(40, 220),
        PixelCoordinateTransform.Chart(100, 120),
        PixelCoordinateTransform.Chart(160, 190),
        PixelCoordinateTransform.Chart(220, 80),
        PixelCoordinateTransform.Chart(280, 160),
        PixelCoordinateTransform.Chart(340, 60)
    })
    {
        PatternMode = mode,
        IsSelected = selected
    };

    private static SKColor RenderedHandleColor(IChartObject obj, int pointIndex)
    {
        var transform = new PixelCoordinateTransform();
        using var bitmap = new SKBitmap(CanvasSize, CanvasSize);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, transform);
        canvas.Flush();
        var screen = transform.ChartToScreen(obj.Points[pointIndex]);
        return bitmap.GetPixel((int)screen.X, (int)screen.Y);
    }

    // ---- 04 (a): AP highlight of the selection handles ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DatePriceRange_Selected_HighlightsOnlyTheAnchorPointHandle(int anchorPointIndex)
    {
        using var obj = new DatePriceRangeObject(PixelCoordinateTransform.Chart(80, 300), PixelCoordinateTransform.Chart(260, 120))
        {
            IsSelected = true,
            AnchorPointIndex = anchorPointIndex
        };

        for (int i = 0; i < 2; i++)
        {
            var expected = i == anchorPointIndex ? DrawingThemeContext.AnchorPointColor : DrawingThemeContext.HandleColor;
            Assert.Equal(expected, RenderedHandleColor(obj, i));
        }
    }

    [Fact]
    public void ElliottWave_Selected_HighlightsOnlyTheAnchorPointHandle_AndNoHandleForInactivePoints()
    {
        using var obj = CreateWave(WavePatternMode.CorrectionABC, selected: true);
        obj.AnchorPointIndex = 2;

        for (int i = 0; i < obj.ActivePointCount; i++)
        {
            var expected = i == 2 ? DrawingThemeContext.AnchorPointColor : DrawingThemeContext.HandleColor;
            Assert.Equal(expected, RenderedHandleColor(obj, i));
        }

        // Points 4 and 5 are stored but not displayed: nothing is drawn on them.
        Assert.Equal((byte)0, RenderedHandleColor(obj, 4).Alpha);
        Assert.Equal((byte)0, RenderedHandleColor(obj, 5).Alpha);
    }

    // ---- 04 (b): AP cycle and link snap only use the active points ----

    [Fact]
    public void ElliottWave_CorrectionABC_AdvanceAnchorPoint_CyclesOnlyThroughTheFourActivePoints()
    {
        var manager = new ChartObjectManager();
        using var wave = CreateWave(WavePatternMode.CorrectionABC);
        manager.AddObject(wave);

        var visited = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 10; i++)
        {
            Assert.True(manager.AdvanceAnchorPoint(wave.Id));
            Assert.InRange(wave.AnchorPointIndex, 0, wave.ActivePointCount - 1);
            visited.Add(wave.AnchorPointIndex);
        }

        Assert.Equal(new[] { 0, 1, 2, 3 }, visited.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void ElliottWave_CorrectionABC_AnchorPointOnAnInactivePoint_FallsBackToTheFirstPointForTheLinkSnap()
    {
        using var wave = CreateWave(WavePatternMode.CorrectionABC);
        wave.AnchorPointIndex = 5; // left over from Impulse mode, now not displayed

        Assert.True(AnchorPointSnapper.TryGetAnchorPoint(wave, out var anchor));
        Assert.Equal(wave.Points[0], anchor);
    }

    [Fact]
    public void ElliottWave_Impulse_AdvanceAnchorPoint_StillCyclesThroughAllSixPoints()
    {
        var manager = new ChartObjectManager();
        using var wave = CreateWave(WavePatternMode.Impulse12345);
        manager.AddObject(wave);

        var visited = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 12; i++)
        {
            manager.AdvanceAnchorPoint(wave.Id);
            visited.Add(wave.AnchorPointIndex);
        }

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, visited.OrderBy(v => v).ToArray());
    }

    // ---- 06: a click on the coordinate of the previous placed point ----

    [Fact]
    public void TryAdvanceClick_OnThePreviousPlacedCoordinate_IsNotCountedAndDoesNotFinalize()
    {
        var behavior = new ElliottWaveBehavior();
        var wave = new ElliottWaveObject { PatternMode = WavePatternMode.CorrectionABC };
        var p0 = PixelCoordinateTransform.Chart(100, 100);
        var p1 = PixelCoordinateTransform.Chart(140, 160);
        var p2 = PixelCoordinateTransform.Chart(180, 120);
        var p3 = PixelCoordinateTransform.Chart(220, 200);

        Assert.False(behavior.TryAdvanceClick(wave, p0));
        Assert.False(behavior.TryAdvanceClick(wave, p1));
        Assert.False(behavior.TryAdvanceClick(wave, p2));
        Assert.Equal(4, wave.Points.Count); // 3 placed points + the hover point

        // The 4th click lands exactly on the 3rd point: not a new point, so the wave is not complete.
        Assert.False(behavior.TryAdvanceClick(wave, p2));
        Assert.Equal(4, wave.Points.Count);
        Assert.Equal(p2, wave.Points[2]);

        // A different coordinate completes it with 4 distinct points.
        Assert.True(behavior.TryAdvanceClick(wave, p3));
        Assert.Equal(new[] { p0, p1, p2, p3 }, wave.Points.ToArray());
    }

    [Fact]
    public void TryAdvanceClick_Impulse_OnThePreviousPlacedCoordinate_DoesNotLeaveAZeroLengthSegment()
    {
        var behavior = new ElliottWaveBehavior();
        var wave = new ElliottWaveObject { PatternMode = WavePatternMode.Impulse12345 };
        var p0 = PixelCoordinateTransform.Chart(100, 100);
        var p1 = PixelCoordinateTransform.Chart(140, 160);

        behavior.TryAdvanceClick(wave, p0);
        behavior.TryAdvanceClick(wave, p1);
        behavior.TryAdvanceClick(wave, p1); // same coordinate again
        behavior.TryAdvanceClick(wave, p1); // and again

        Assert.Equal(new[] { p0, p1, p1 }, wave.Points.ToArray()); // 2 placed points + 1 hover point only
    }

    [Fact]
    public void Controller_CorrectionABC_RepeatedClickOnTheThirdPoint_KeepsDrawing_AndTheFourthDistinctClickCompletes()
    {
        var preset = Assert.IsType<ElliottWaveObject>(DrawingToolPresetService.Shared.GetOrCreatePresetObject(DrawingTool.ElliottWave));
        var originalMode = preset.PatternMode;
        try
        {
            preset.PatternMode = WavePatternMode.CorrectionABC;
            var vm = new ChartViewModel { Symbol = "TEST" };
            vm.UpdateDrawingCoordinator();
            vm.CurrentTool = DrawingTool.ElliottWave;
            var controller = new ChartInteractionController();
            var transform = new PixelCoordinateTransform();

            var clicks = new[] { new Point(40, 220), new Point(80, 120), new Point(120, 190) };
            foreach (var c in clicks)
            {
                MovePointer(controller, c, vm, transform);
                controller.HandlePointerPressed(c, c, vm, transform, KeyModifiers.None, clickCount: 1);
            }

            // Second single click on the third point's exact position.
            MovePointer(controller, clicks[2], vm, transform);
            controller.HandlePointerPressed(clicks[2], clicks[2], vm, transform, KeyModifiers.None, clickCount: 1);
            Assert.True(controller.IsDrawingNewShape);
            Assert.Empty(vm.ObjectManager.Objects);

            var last = new Point(160, 80);
            MovePointer(controller, last, vm, transform);
            controller.HandlePointerPressed(last, last, vm, transform, KeyModifiers.None, clickCount: 1);
            Assert.False(controller.IsDrawingNewShape);
            var wave = Assert.IsType<ElliottWaveObject>(Assert.Single(vm.ObjectManager.Objects));
            Assert.Equal(4, wave.Points.Count);
            Assert.Equal(4, wave.Points.Distinct().Count());
        }
        finally
        {
            preset.PatternMode = originalMode;
        }
    }

    private static void MovePointer(ChartInteractionController controller, Point point, ChartViewModel vm, ICoordinateTransform transform)
    {
        controller.HandlePointerMoved(point, point, vm, transform, ChartDataSnapshot.Empty, transform.ScreenRect, 0, 0, out _, KeyModifiers.None);
    }
}
