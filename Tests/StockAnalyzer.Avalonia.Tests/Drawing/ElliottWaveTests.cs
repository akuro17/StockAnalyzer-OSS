using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

[Collection("DrawingThemeContext State")]
public sealed class ElliottWaveTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private static ChartViewModel CreateViewModel(DrawingTool tool = DrawingTool.ElliottWave)
    {
        var vm = new ChartViewModel { Symbol = "TEST" };
        vm.UpdateDrawingCoordinator();
        vm.CurrentTool = tool;
        return vm;
    }

    private static IReadOnlyList<IChartObject> GetStandardObjects(ChartViewModel vm)
    {
        return vm.ObjectManager.Objects;
    }

    private static void MovePointer(
        ChartInteractionController controller,
        Point point,
        ChartViewModel viewModel,
        ICoordinateTransform transform)
    {
        controller.HandlePointerMoved(
            point,
            point,
            viewModel,
            transform,
            ChartDataSnapshot.Empty,
            transform.ScreenRect,
            0,
            0,
            out _,
            KeyModifiers.None);
    }

    [Fact]
    public void Enums_AndRegistrations_MatchSpec()
    {
        Assert.Equal((int)DrawingTool.DatePriceRange + 1, (int)DrawingTool.ElliottWave);

        using var autoObj = new AutoElliottWaveObject();
        Assert.Equal(ChartObjectType.AutoElliottWave, autoObj.Type);
        Assert.NotEqual(ChartObjectType.ElliottWave, autoObj.Type);

        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.ElliottWave);
        Assert.IsType<ElliottWaveBehavior>(behavior);
        Assert.Equal(0, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var categories = DrawingToolCategoryService.GetCategories();
        Assert.Contains(
            categories.SelectMany(c => c.Tools),
            t => t.Tool == DrawingTool.ElliottWave && t.NameKey == "DrawTool_ElliottWave");

        Assert.False(DrawingValueCatalog.TryGet("ElliottWave", out _));
        Assert.Equal(15, DrawingValueCatalog.All.Count);
        Assert.Contains(nameof(ElliottWaveObject), ChartObjectTypeRegistry.GetRegisteredTypes().Keys);
    }

    [Theory]
    [InlineData(WavePatternMode.Impulse12345, 6, true)]
    [InlineData(WavePatternMode.CorrectionABC, 4, true)]
    [InlineData(WavePatternMode.TriangleABCDE, 6, false)]
    public void AutoFinalize_CompletesAtRequiredPointCount_ForEachPatternMode(
        WavePatternMode mode,
        int expectedPointCount,
        bool returnToPointer)
    {
        var originalMode = DrawingThemeContext.DrawingToolContinuationMode;
        var preset = Assert.IsType<ElliottWaveObject>(
            DrawingToolPresetService.Shared.GetOrCreatePresetObject(DrawingTool.ElliottWave));
        var originalPresetMode = preset.PatternMode;

        try
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(returnToPointer
                ? DrawingToolContinuationMode.ReturnToPointer
                : DrawingToolContinuationMode.ContinueDrawing);
            preset.PatternMode = mode;

            var controller = new ChartInteractionController();
            var viewModel = CreateViewModel();
            var transform = new PixelCoordinateTransform();

            var clicks = new[]
            {
                new Point(40, 220),
                new Point(80, 120),
                new Point(120, 190),
                new Point(160, 80),
                new Point(200, 160),
                new Point(240, 60)
            };

            for (int i = 0; i < expectedPointCount; i++)
            {
                MovePointer(controller, clicks[i], viewModel, transform);
                controller.HandlePointerPressed(clicks[i], clicks[i], viewModel, transform, KeyModifiers.None, clickCount: 1);

                bool isLast = i == expectedPointCount - 1;
                Assert.Equal(!isLast, controller.IsDrawingNewShape);
            }

            var obj = Assert.IsType<ElliottWaveObject>(Assert.Single(GetStandardObjects(viewModel)));
            Assert.Equal(mode, obj.PatternMode);
            Assert.Equal(expectedPointCount, obj.Points.Count);
            Assert.Equal(expectedPointCount, obj.ActivePointCount);

            var expectedTool = returnToPointer ? DrawingTool.Pointer : DrawingTool.ElliottWave;
            Assert.Equal(expectedTool, viewModel.CurrentTool);
        }
        finally
        {
            preset.PatternMode = originalPresetMode;
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(originalMode);
        }
    }

    [Fact]
    public void MidWayFinalize_Enter_CommitsCommittedPointsAndDropsHoverPreview()
    {
        var originalMode = DrawingThemeContext.DrawingToolContinuationMode;
        try
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ReturnToPointer);

            var controller = new ChartInteractionController();
            var viewModel = CreateViewModel();
            var transform = new PixelCoordinateTransform();

            var p0 = new Point(50, 200);
            var p1 = new Point(100, 120);
            var p2 = new Point(150, 170);
            var previewP3 = new Point(210, 90);

            controller.HandlePointerPressed(p0, p0, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, p1, viewModel, transform);
            controller.HandlePointerPressed(p1, p1, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, p2, viewModel, transform);
            controller.HandlePointerPressed(p2, p2, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, previewP3, viewModel, transform);

            Assert.True(controller.IsDrawingNewShape);
            Assert.True(controller.HandleCancelRequest(viewModel.ObjectManager, viewModel.LayerService, viewModel.DrawingEditCoordinator));
            Assert.False(controller.IsDrawingNewShape);

            var obj = Assert.IsType<ElliottWaveObject>(Assert.Single(GetStandardObjects(viewModel)));
            Assert.Equal(3, obj.Points.Count);
            Assert.Equal(PixelCoordinateTransform.Chart(150, 170), obj.Points[2]);
        }
        finally
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(originalMode);
        }
    }

    [Fact]
    public void MidWayFinalize_DoubleTap_And_ShiftClick_DeduplicateTrailingDuplicateAndRespectToolMode()
    {
        var originalMode = DrawingThemeContext.DrawingToolContinuationMode;
        try
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ContinueDrawing);

            var controller = new ChartInteractionController();
            var viewModel = CreateViewModel();
            var transform = new PixelCoordinateTransform();

            var p0 = new Point(40, 200);
            var p1 = new Point(90, 110);
            var p2 = new Point(140, 160);

            controller.HandlePointerPressed(p0, p0, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, p1, viewModel, transform);
            controller.HandlePointerPressed(p1, p1, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, p2, viewModel, transform);
            controller.HandlePointerPressed(p2, p2, viewModel, transform, KeyModifiers.None, clickCount: 1);

            Assert.True(controller.HandleDoubleTap(p2, p2, viewModel, transform, snapshot: null!, modifiers: KeyModifiers.None));
            Assert.False(controller.IsDrawingNewShape);
            Assert.Equal(DrawingTool.ElliottWave, viewModel.CurrentTool);

            var first = Assert.IsType<ElliottWaveObject>(Assert.Single(GetStandardObjects(viewModel)));
            Assert.Equal(3, first.Points.Count);
            Assert.Equal(PixelCoordinateTransform.Chart(140, 160), first.Points[2]);

            // Second shape finished mid-way via Shift+Click
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ReturnToPointer);
            var q0 = new Point(60, 210);
            var q1 = new Point(110, 130);
            var hover = new Point(160, 180);

            controller.HandlePointerPressed(q0, q0, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, q1, viewModel, transform);
            controller.HandlePointerPressed(q1, q1, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, hover, viewModel, transform);
            Assert.True(controller.HandlePointerPressed(hover, hover, viewModel, transform, KeyModifiers.Shift, clickCount: 1));

            Assert.False(controller.IsDrawingNewShape);
            Assert.Equal(DrawingTool.Pointer, viewModel.CurrentTool);
            Assert.Equal(2, GetStandardObjects(viewModel).Count);
            var second = Assert.IsType<ElliottWaveObject>(GetStandardObjects(viewModel)[1]);
            Assert.Equal(2, second.Points.Count);
        }
        finally
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(originalMode);
        }
    }

    [Fact]
    public void MidWayFinalize_WithFewerThanTwoDistinctPoints_CancelsCreation()
    {
        var controller = new ChartInteractionController();
        var viewModel = CreateViewModel();
        var transform = new PixelCoordinateTransform();

        var p0 = new Point(100, 150);
        controller.HandlePointerPressed(p0, p0, viewModel, transform, KeyModifiers.None, clickCount: 1);
        Assert.True(controller.IsDrawingNewShape);

        Assert.True(controller.HandleCancelRequest(viewModel.ObjectManager, viewModel.LayerService, viewModel.DrawingEditCoordinator));
        Assert.False(controller.IsDrawingNewShape);
        Assert.Empty(GetStandardObjects(viewModel));
    }

    [Fact]
    public void PatternModeSwitch_PreservesExtraPoints_AndCapsHitTestAndHandles()
    {
        var screenPoints = new[]
        {
            new Point(40, 220),
            new Point(80, 120),
            new Point(120, 190),
            new Point(160, 80),
            new Point(200, 160),
            new Point(240, 60)
        };
        var chartPoints = screenPoints.Select(p => PixelCoordinateTransform.Chart(p.X, p.Y)).ToList();

        var controller = new ChartInteractionController();
        var viewModel = CreateViewModel(DrawingTool.Pointer);
        var transform = new PixelCoordinateTransform();

        using var obj = new ElliottWaveObject(chartPoints)
        {
            PatternMode = WavePatternMode.Impulse12345,
            IsSelected = true
        };
        viewModel.ObjectManager.AddObject(obj);

        Assert.Equal(6, obj.ActivePointCount);
        Assert.Equal(new[] { "0", "(1)", "(2)", "(3)", "(4)", "(5)" },
            Enumerable.Range(0, 6).Select(i => ElliottWaveObject.GetLabel(WavePatternMode.Impulse12345, i)).ToArray());

        // Switch to CorrectionABC (requires 4 points): points 4 and 5 stay in Points,
        // but become inactive for rendering, HitTest, and handle selection.
        obj.PatternMode = WavePatternMode.CorrectionABC;
        Assert.Equal(6, obj.Points.Count);
        Assert.Equal(4, obj.ActivePointCount);
        Assert.Equal(new[] { "0", "(A)", "(B)", "(C)" },
            Enumerable.Range(0, 4).Select(i => ElliottWaveObject.GetLabel(WavePatternMode.CorrectionABC, i)).ToArray());
        Assert.Null(ElliottWaveObject.GetLabel(WavePatternMode.CorrectionABC, 4));

        // Segment 0->1 hits, vertex 5 and segment 4->5 do not hit
        Assert.True(obj.HitTest(new Point(60, 170), transform, tolerance: 6.0));
        Assert.False(obj.HitTest(screenPoints[5], transform, tolerance: 6.0));
        Assert.False(obj.HitTest(new Point(220, 110), transform, tolerance: 6.0));

        // Handle click at hidden vertex 5 does not grab a handle; vertex 3 does
        controller.HandlePointerPressed(screenPoints[5], screenPoints[5], viewModel, transform, KeyModifiers.None, clickCount: 1);
        Assert.Equal(-1, controller.DraggedHandleIndex);
        controller.HandlePointerReleased(viewModel, transform);

        obj.IsSelected = true;
        controller.HandlePointerPressed(screenPoints[3], screenPoints[3], viewModel, transform, KeyModifiers.None, clickCount: 1);
        Assert.Equal(3, controller.DraggedHandleIndex);
        controller.HandlePointerReleased(viewModel, transform);

        // Switching back to Impulse12345 restores points 4 and 5
        obj.PatternMode = WavePatternMode.Impulse12345;
        Assert.Equal(6, obj.ActivePointCount);
        Assert.True(obj.HitTest(screenPoints[5], transform, tolerance: 6.0));

        // TriangleABCDE labels
        obj.PatternMode = WavePatternMode.TriangleABCDE;
        Assert.Equal(6, obj.ActivePointCount);
        Assert.Equal(new[] { "0", "(A)", "(B)", "(C)", "(D)", "(E)" },
            Enumerable.Range(0, 6).Select(i => ElliottWaveObject.GetLabel(WavePatternMode.TriangleABCDE, i)).ToArray());
    }

    [Fact]
    public void PresetService_TransfersPatternMode_ToNewlyCreatedObject()
    {
        var presetService = new DrawingToolPresetService();
        var preset = Assert.IsType<ElliottWaveObject>(presetService.GetOrCreatePresetObject(DrawingTool.ElliottWave));
        preset.PatternMode = WavePatternMode.CorrectionABC;
        preset.ShowRuleValidation = false;

        using var created = new ElliottWaveObject();
        presetService.ApplyPresetToNewObject(DrawingTool.ElliottWave, created);

        Assert.Equal(WavePatternMode.CorrectionABC, created.PatternMode);
        Assert.False(created.ShowRuleValidation);
    }

    [Fact]
    public void Serialization_RoundTripsPointsAndPatternMode_ViaJsonAndDocumentCodec()
    {
        using var original = new ElliottWaveObject(new[]
        {
            PixelCoordinateTransform.Chart(40, 220),
            PixelCoordinateTransform.Chart(80, 120),
            PixelCoordinateTransform.Chart(120, 190),
            PixelCoordinateTransform.Chart(160, 80),
            PixelCoordinateTransform.Chart(200, 160),
            PixelCoordinateTransform.Chart(240, 60)
        })
        {
            PatternMode = WavePatternMode.TriangleABCDE,
            StrokeStyle = DrawingStrokeStyle.Dash,
            Color = Colors.Orange,
            Thickness = 2.5,
            CustomName = "Triangle 1"
        };

        string json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        using var fromJson = Assert.IsType<ElliottWaveObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));

        Assert.Equal(original.Points.Count, fromJson.Points.Count);
        Assert.Equal(WavePatternMode.TriangleABCDE, fromJson.PatternMode);
        Assert.Equal(DrawingStrokeStyle.Dash, fromJson.StrokeStyle);
        Assert.Equal("Triangle 1", fromJson.CustomName);
        for (int i = 0; i < original.Points.Count; i++)
        {
            Assert.Equal(original.Points[i].Time, fromJson.Points[i].Time);
            Assert.Equal(original.Points[i].Price, fromJson.Points[i].Price);
        }

        var manager = new ChartObjectManager();
        manager.AddObject(original);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("TEST", TimeframeType.Daily), revision: 1);
        using var materialized = DrawingDocumentCodec.Materialize(state);
        var restored = Assert.IsType<ElliottWaveObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));
        Assert.Equal(6, restored.Points.Count);
        Assert.Equal(WavePatternMode.TriangleABCDE, restored.PatternMode);
    }

    [Fact]
    public void Translate_ShiftsAllPointsIncludingInactiveOnes()
    {
        var p0 = PixelCoordinateTransform.Chart(40, 220);
        var p5 = PixelCoordinateTransform.Chart(240, 60);
        using var obj = new ElliottWaveObject(new[]
        {
            p0,
            PixelCoordinateTransform.Chart(80, 120),
            PixelCoordinateTransform.Chart(120, 190),
            PixelCoordinateTransform.Chart(160, 80),
            PixelCoordinateTransform.Chart(200, 160),
            p5
        })
        {
            PatternMode = WavePatternMode.CorrectionABC
        };

        obj.Translate(TimeSpan.FromHours(6), 15m);

        Assert.Equal(p0.Time.AddHours(6), obj.Points[0].Time);
        Assert.Equal(p0.Price + 15m, obj.Points[0].Price);
        Assert.Equal(p5.Time.AddHours(6), obj.Points[5].Time);
        Assert.Equal(p5.Price + 15m, obj.Points[5].Price);
    }

    [Fact]
    public void Render_SmokeTest_And_SteadyStateAllocation_And_DisposeSafety()
    {
        const int width = 320;
        const int height = 260;
        var transform = new PixelCoordinateTransform();

        using var obj = new ElliottWaveObject(new[]
        {
            PixelCoordinateTransform.Chart(40, 220),
            PixelCoordinateTransform.Chart(80, 120),
            PixelCoordinateTransform.Chart(120, 190),
            PixelCoordinateTransform.Chart(160, 80),
            PixelCoordinateTransform.Chart(200, 160),
            PixelCoordinateTransform.Chart(240, 60)
        })
        {
            IsSelected = true
        };

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        using var snapshot = surface.Snapshot();
        byte[] renderedBytes = snapshot.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        // Single-point placement renders safely without throwing
        using var single = new ElliottWaveObject(new[] { PixelCoordinateTransform.Chart(120, 120) });
        single.Render(canvas, transform);

        // Steady-state Render allocates no paint/path/array heap garbage
        // (SkiaSharp's MeasureText/DrawText(string) allocates ~216 B UTF-8 buffer per label across 6 labels).
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);
        long renderBytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));
        Assert.InRange(renderBytes, 0L, 2048L);

        // Hidden leaves blank canvas untouched
        using var hiddenSurface = SKSurface.Create(new SKImageInfo(width, height));
        hiddenSurface.Canvas.Clear(SKColors.Black);
        obj.IsVisible = false;
        obj.Render(hiddenSurface.Canvas, transform);
        using var hiddenSnapshot = hiddenSurface.Snapshot();
        byte[] hiddenBytes = hiddenSnapshot.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        Assert.NotEqual(hiddenBytes, renderedBytes);

        // Dispose idempotent and subsequent Render is a safe no-op
        obj.IsVisible = true;
        obj.Dispose();
        obj.Dispose();
        obj.Render(canvas, transform);
    }

    [Fact]
    public void GetCalculatedValues_UsesOnlyActivePoints_IsZeroAllocInSteadyState_And_RespectsShowRuleValidation()
    {
        var transform = new PixelCoordinateTransform();
        var violatingPoints = new[]
        {
            PixelCoordinateTransform.Chart(40, 100),  // p0=100
            PixelCoordinateTransform.Chart(80, 140),  // p1=140
            PixelCoordinateTransform.Chart(120, 95),  // p2=95 <= p0 (R1 violated)
            PixelCoordinateTransform.Chart(160, 200), // p3=200
            PixelCoordinateTransform.Chart(200, 130), // p4=130 <= p1 (R3 violated)
            PixelCoordinateTransform.Chart(240, 220)  // p5=220
        };

        using var obj = new ElliottWaveObject(violatingPoints)
        {
            PatternMode = WavePatternMode.Impulse12345,
            Color = Colors.DodgerBlue,
            ShowRuleValidation = true
        };

        var values = obj.GetCalculatedValues(violatingPoints[0].Time);
        Assert.Equal(4, values.Count);
        Assert.Equal("R1,R3", values[3].FormattedText);

        // Steady-state cached GetCalculatedValues allocates 0 managed bytes
        long valuesBytes = AllocationProbe.InCollectionFreeWindow(
            () => _ = obj.GetCalculatedValues(violatingPoints[0].Time));
        Assert.Equal(0L, valuesBytes);

        // U-16=A: switching to CorrectionABC keeps points but yields all-null non-Impulse values
        obj.PatternMode = WavePatternMode.CorrectionABC;
        var abcValues = obj.GetCalculatedValues(violatingPoints[0].Time);
        Assert.Equal(4, abcValues.Count);
        Assert.All(abcValues, v => Assert.Equal("-", v.FormattedText));

        // Switching back restores Impulse metrics
        obj.PatternMode = WavePatternMode.Impulse12345;
        Assert.Equal("R1,R3", obj.GetCalculatedValues(violatingPoints[0].Time)[3].FormattedText);

        // ShowRuleValidation changes rendered pixels when violations exist
        using var validatedSurface = SKSurface.Create(new SKImageInfo(280, 260));
        validatedSurface.Canvas.Clear(SKColors.Black);
        obj.ShowRuleValidation = true;
        obj.Render(validatedSurface.Canvas, transform);
        byte[] validatedPng = validatedSurface.Snapshot().Encode(SKEncodedImageFormat.Png, 100).ToArray();

        using var unvalidatedSurface = SKSurface.Create(new SKImageInfo(280, 260));
        unvalidatedSurface.Canvas.Clear(SKColors.Black);
        obj.ShowRuleValidation = false;
        obj.Render(unvalidatedSurface.Canvas, transform);
        byte[] unvalidatedPng = unvalidatedSurface.Snapshot().Encode(SKEncodedImageFormat.Png, 100).ToArray();

        Assert.NotEqual(validatedPng, unvalidatedPng);

        // Serialization preserves ShowRuleValidation
        string json = JsonSerializer.Serialize<IChartObject>(obj, JsonOptions);
        using var restored = Assert.IsType<ElliottWaveObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        Assert.False(restored.ShowRuleValidation);
    }

    [Fact]
    public void MidWayFinalize_ShiftClick_AlwaysRevertsToPointer_EvenUnderContinueDrawing()
    {
        var originalMode = DrawingThemeContext.DrawingToolContinuationMode;
        try
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(DrawingToolContinuationMode.ContinueDrawing);

            var controller = new ChartInteractionController();
            var viewModel = CreateViewModel();
            var transform = new PixelCoordinateTransform();

            var p0 = new Point(40, 200);
            var p1 = new Point(90, 120);
            var hover = new Point(150, 170);

            controller.HandlePointerPressed(p0, p0, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, p1, viewModel, transform);
            controller.HandlePointerPressed(p1, p1, viewModel, transform, KeyModifiers.None, clickCount: 1);
            MovePointer(controller, hover, viewModel, transform);

            Assert.True(controller.HandlePointerPressed(hover, hover, viewModel, transform, KeyModifiers.Shift, clickCount: 1));
            Assert.False(controller.IsDrawingNewShape);
            Assert.Equal(DrawingTool.Pointer, viewModel.CurrentTool);

            var created = Assert.IsType<ElliottWaveObject>(Assert.Single(GetStandardObjects(viewModel)));
            Assert.Equal(2, created.Points.Count);
        }
        finally
        {
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(originalMode);
        }
    }

    [Fact]
    public void CorrectionABC_WithSixStoredPoints_ExcludesInactivePointsFromSmartGuidesAndRubberBand_AndStrokeStyleDisplayNameIsStandardized()
    {
        var displayName = typeof(ElliottWaveObject)
            .GetProperty(nameof(ElliottWaveObject.StrokeStyle))?
            .GetCustomAttributes(typeof(System.ComponentModel.DisplayNameAttribute), false)
            .OfType<System.ComponentModel.DisplayNameAttribute>()
            .SingleOrDefault()?.DisplayName;
        Assert.Equal("Stroke Style", displayName);

        var transform = new PixelCoordinateTransform();
        var wave = new ElliottWaveObject(new[]
        {
            PixelCoordinateTransform.Chart(40, 180),
            PixelCoordinateTransform.Chart(80, 110),
            PixelCoordinateTransform.Chart(120, 160),
            PixelCoordinateTransform.Chart(160, 100),
            PixelCoordinateTransform.Chart(420, 320),
            PixelCoordinateTransform.Chart(480, 360)
        })
        {
            PatternMode = WavePatternMode.CorrectionABC
        };

        var dragged = new TrendLineObject(
            PixelCoordinateTransform.Chart(415, 318),
            PixelCoordinateTransform.Chart(435, 330));

        var guides = new SmartGuideService();
        var guideLines = new List<SmartGuideLine>();
        var snapResult = guides.SnapObjectMove(
            dragged,
            new global::Avalonia.Rect(417, 318, 20, 12),
            new IChartObject[] { wave, dragged },
            transform,
            new global::Avalonia.Rect(0, 0, 800, 400),
            snapThreshold: 10.0,
            guideLines);

        Assert.False(snapResult.IsSnappedX);
        Assert.False(snapResult.IsSnappedY);
        Assert.Empty(guideLines);

        var controller = new ChartInteractionController();
        var viewModel = CreateViewModel(DrawingTool.Pointer);
        viewModel.ObjectManager.AddObject(wave);

        // Rubber-band strictly covering inactive points 4..5 (X=380..520, Y=290..380) must not select wave
        var missStart = new Point(380, 290);
        var missEnd = new Point(520, 380);
        controller.HandlePointerPressed(missStart, missStart, viewModel, transform, KeyModifiers.Control, clickCount: 1);
        Assert.True(controller.IsRubberBandSelecting);
        MovePointer(controller, missEnd, viewModel, transform);
        controller.HandlePointerReleased(viewModel, transform);
        Assert.Empty(viewModel.ObjectManager.SelectedObjects);

        // Rubber-band covering active points 0..3 (X=30..180) must select wave
        var hitStart = new Point(30, 90);
        var hitEnd = new Point(180, 200);
        controller.HandlePointerPressed(hitStart, hitStart, viewModel, transform, KeyModifiers.Control, clickCount: 1);
        Assert.True(controller.IsRubberBandSelecting);
        MovePointer(controller, hitEnd, viewModel, transform);
        controller.HandlePointerReleased(viewModel, transform);
        Assert.Single(viewModel.ObjectManager.SelectedObjects, o => o.Id == wave.Id);
    }
}
