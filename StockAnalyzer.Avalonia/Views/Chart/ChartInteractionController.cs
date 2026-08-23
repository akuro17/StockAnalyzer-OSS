using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Avalonia;
using Avalonia.Media;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.Confluence;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Controller for chart interaction logic including magnet snap,
/// drawing tool state, and object manipulation.
/// Extracted from ChartBaseControl to separate interaction responsibilities.
/// Uses IDrawingToolBehavior dispatch for tool-specific logic.
/// </summary>
public class ChartInteractionController
{
    #region Constants

    // Magnet radius
    private const double MagnetRadius = 4.0;


    #endregion

    #region State

    /// <summary>
    /// Whether a new shape is currently being drawn.
    /// </summary>
    public bool IsDrawingNewShape { get; set; }

    /// <summary>
    /// The current drawing object being created.
    /// </summary>
    public IChartObject? CurrentDrawingObject { get; set; }

    /// <summary>
    /// The current step in multi-step drawing tools (e.g., Parallel Channel).
    /// </summary>
    public int DrawingStep { get; set; }

    /// <summary>
    /// The object currently being dragged.
    /// </summary>
    public IChartObject? DraggedObject { get; set; }

    /// <summary>
    /// The index of the handle being dragged (-1 if none).
    /// </summary>
    public int DraggedHandleIndex { get; set; } = -1;

    /// <summary>
    /// Whether the ruler tool is actively being drawn.
    /// </summary>
    public bool IsDrawingRuler { get; set; }

    /// <summary>
    /// Whether the Eraser tool is actively erasing (kept separate from
    /// <see cref="IsDrawingNewShape"/> so that Shift/Ctrl modifier handling for
    /// deleting/reverting objects is not affected by Eraser usage).
    /// </summary>
    public bool IsEraserActive { get; set; }

    /// <summary>
    /// The last point snapped to by the magnet service (in Logical Chart Coordinates).
    /// Used for visual feedback (small circle).
    /// </summary>
    public ChartPoint? LastSnapChartPoint { get; set; }

    /// <summary>
    /// The last recorded mouse drag position.
    /// </summary>
    public global::Avalonia.Point LastDragPoint { get; set; }

    /// <summary>
    /// Whether a pan operation is currently active.
    /// </summary>
    public bool IsDragging { get; set; }

    /// <summary>
    /// Renderer for the Ruler tool.
    /// </summary>
    public RulerRenderer RulerRenderer { get; } = new RulerRenderer();

    /// <summary>
    /// Current axis constraint mode for moving drawing objects (XY = free, X = horizontal only, Y = vertical only).
    /// </summary>
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;

    #endregion

    private readonly Services.Drawing.IMagnetSnapService _magnetSnapService;
    private readonly Services.Drawing.ISmartGuideService _smartGuideService;
    private readonly IDialogService _dialogService;
    private readonly List<SmartGuideLine> _activeSmartGuideLines = new();
    private ChartObjectManager? _activeObjectManager;

    /// <summary>
    /// Snapshot of the dragged object's points at the start of a whole-object
    /// (Move) drag. Used as the fixed reference for computing each frame's total
    /// displacement, so Smart Guide corrections never accumulate across frames.
    /// </summary>
    private List<ChartPoint>? _moveDragAnchorPoints;
    private TimeSpan _moveDragAppliedTimeDelta;
    private decimal _moveDragAppliedPriceDelta;

    /// <summary>
    /// Active smart guide lines generated during object or handle dragging.
    /// Renderers can inspect this collection to overlay visual alignment feedback.
    /// </summary>
    public IReadOnlyList<SmartGuideLine> ActiveSmartGuideLines => _activeSmartGuideLines;

    internal void SetActiveObjectManager(ChartObjectManager? objectManager) => _activeObjectManager = objectManager;

    /// <summary>
    /// The active behavior for the current drawing tool.
    /// Cached when a tool starts to avoid repeated lookups.
    /// </summary>
    private IDrawingToolBehavior? _activeBehavior;

    /// <summary>
    /// Callback invoked after a drawing object's settings dialog closes.
    /// Used by the owning control to trigger visual invalidation.
    /// </summary>
    public Action? OnObjectEdited { get; set; }
    public Action<IChartObject>? OnDrawingFinished { get; set; }
    public Action<IChartObject>? OnObjectDragged { get; set; }

    /// <summary>
    /// Invoked (on the UI thread) after the selection-idle timer auto-deselects an object.
    /// Used by the owning control to trigger visual invalidation.
    /// </summary>
    public Action? OnSelectionIdleTimeout { get; set; }

    /// <summary>
    /// Debounce-style timer (mirrors LayoutSaveScheduler's System.Threading.Timer pattern):
    /// (re)armed on every pointer interaction while something is selected, cleared while nothing
    /// is selected. Fires once after SelectionIdleTimeoutMs of no such interaction.
    /// </summary>
    private System.Threading.Timer? _selectionIdleTimer;
    private ChartObjectManager? _selectionIdleObjectManager;

    private int? _selectionIdleTimeoutMsOverride;

    /// <summary>
    /// Reads the user-configured <see cref="StockAnalyzer.Avalonia.Drawing.DrawingThemeContext.ControlPointHideTimeoutMs"/>
    /// (Settings -> Chart -> Drawing) live on every access, so a settings change takes effect
    /// immediately on an already-open chart instead of only on newly-created ones.
    /// Testability seam: setting this pins a fixed value instead (tests shrink it to avoid
    /// waiting the real timeout).
    /// </summary>
    internal int SelectionIdleTimeoutMs
    {
        get => _selectionIdleTimeoutMsOverride ?? StockAnalyzer.Avalonia.Drawing.DrawingThemeContext.ControlPointHideTimeoutMs;
        set => _selectionIdleTimeoutMsOverride = value;
    }

    /// <summary>
    /// Arms/resets/clears the selection-idle timer based on the current selection state.
    /// Called from every pointer handler so any interaction counts as activity.
    /// Internal (not private) so tests can drive it directly instead of simulating a full
    /// pointer event with all of its unrelated hit-testing side effects.
    /// </summary>
    internal void ArmOrResetSelectionIdleTimer(ChartObjectManager objectManager)
    {
        // HasSelection (O(1), backed by a HashSet) instead of SelectedObject (LINQ scan): this
        // runs on every pointer move/press/release, a high-frequency input path.
        if (!objectManager.HasSelection)
        {
            _selectionIdleTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            return;
        }

        _selectionIdleObjectManager = objectManager;
        _selectionIdleTimer ??= new System.Threading.Timer(OnSelectionIdleTimerElapsed, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _selectionIdleTimer.Change(SelectionIdleTimeoutMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>Runs on a thread-pool timer thread; marshal to the UI thread before touching selection state.</summary>
    private void OnSelectionIdleTimerElapsed(object? state)
    {
        // Static lambda + state parameter (matches DispatcherService.Post<T>'s pattern) to avoid
        // a closure allocation from capturing 'this'.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(static s =>
        {
            var self = (ChartInteractionController)s!;
            self._selectionIdleObjectManager?.DeselectAll();
            self.OnSelectionIdleTimeout?.Invoke();
        }, this);
    }

    private readonly Common.CrosshairPositionData _crosshairData = new();
    private readonly Common.CrosshairPositionChangedMessage _crosshairMessage;

    public ChartInteractionController(
        Services.Drawing.IMagnetSnapService magnetSnapService,
        IDialogService dialogService,
        Services.Drawing.ISmartGuideService? smartGuideService = null)
    {
        _magnetSnapService = magnetSnapService;
        _dialogService = dialogService;
        _smartGuideService = smartGuideService ?? new Services.Drawing.SmartGuideService();
        _crosshairMessage = new Common.CrosshairPositionChangedMessage(_crosshairData);
    }

    /// <summary>
    /// Default constructor for backwards compatibility or design time (if needed).
    /// </summary>
    public ChartInteractionController() : this(
        new Services.Drawing.MagnetSnapService(),
        new DialogService(),
        new Services.Drawing.SmartGuideService())
    {
    }

    #region Public Methods

    /// <summary>
    /// Publishes a crosshair position change message to the event bus.
    /// Resets properties on the cached payload to enforce ZeroAllocation.
    /// </summary>
    public void UpdateCrosshairPosition(int index, CoreCandleData? candle, string symbol, int? tlbCount = null, StockAnalyzer.Core.Models.Analysis.ReverseWatchCurvePoint? rwPoint = null, double? mouseY = null, ConfluenceResult? confluence = null, double? screenX = null, double? screenY = null)
    {
        _crosshairData.CandleIndex = index;
        _crosshairData.HoveredCandle = candle;
        _crosshairData.ChartSymbol = symbol;
        _crosshairData.ThreeLineBreakCount = tlbCount;
        _crosshairData.ReverseWatchPoint = rwPoint;
        _crosshairData.MouseY = mouseY;
        _crosshairData.Confluence = confluence;
        _crosshairData.ScreenX = screenX;
        _crosshairData.ScreenY = screenY;
        
        WeakReferenceMessenger.Default.Send(_crosshairMessage);
    }

    /// <summary>
    /// Clears the crosshair position on the event bus (e.g., when pointer leaves chart).
    /// </summary>
    public void ClearCrosshairPosition(string symbol)
    {
        _crosshairData.CandleIndex = -1;
        _crosshairData.HoveredCandle = null;
        _crosshairData.ChartSymbol = symbol;
        _crosshairData.ThreeLineBreakCount = null;
        _crosshairData.ReverseWatchPoint = null;
        _crosshairData.MouseY = null;
        _crosshairData.Confluence = null;
        _crosshairData.ScreenX = null;
        _crosshairData.ScreenY = null;
        
        WeakReferenceMessenger.Default.Send(_crosshairMessage);
    }

    /// <summary>
    /// Settings -> Chart -> Drawing: "Continue Drawing" keeps a finished fixed-point tool active
    /// so the next click starts a brand-new shape at that click's position, instead of reverting
    /// to Pointer. Used by every tool-completion path (click-to-place, multi-step, drag-to-draw
    /// release, Text dialog) so the setting applies consistently regardless of how a given tool
    /// finishes. Shift/Ctrl-driven cancel/delete are explicit "get me out" gestures and always
    /// revert to Pointer regardless of this setting (not gated by this helper).
    /// </summary>
    private static bool ShouldReturnToPointerAfterFinish()
        => StockAnalyzer.Avalonia.Drawing.DrawingThemeContext.DrawingToolContinuationMode
            != StockAnalyzer.Core.Models.DrawingToolContinuationMode.ContinueDrawing;

    /// <summary>
    /// Handles the Pointer Pressed event for chart interaction.
    /// Delegates to specific tool handlers based on the current tool and state.
    /// </summary>
    /// <param name="point">The pointer position relative to the control.</param>
    /// <param name="chartPosition">The pointer position relative to the chart area (margins subtracted).</param>
    /// <param name="viewModel">The chart view model.</param>
    /// <param name="coordinateTransform">The coordinate transform.</param>
    /// <param name="modifiers">Keyboard modifiers.</param>
    /// <param name="clickCount">Number of clicks (for double-click detection).</param>
    /// <returns>True if the event was handled.</returns>
    public bool HandlePointerPressed(
        global::Avalonia.Point point,
        global::Avalonia.Point chartPosition,
        ChartViewModel? viewModel,
        ICoordinateTransform coordinateTransform,
        KeyModifiers modifiers,
        int clickCount)
    {
        if (viewModel == null) return false;
        ArmOrResetSelectionIdleTimer(viewModel.ObjectManager);

        // 1. Double Click is handled by DoubleTapped event (HandleDoubleTap)

        var effectiveTool = viewModel.CurrentTool;

        var chartPoint = coordinateTransform.ScreenToChart(chartPosition);
        bool wasDrawing = IsDrawingNewShape;

        // Shift modifier overrides
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (IsDrawingNewShape)
            {
                if (_activeBehavior?.RequiredSteps == 0)
                {
                    if (CurrentDrawingObject?.Points.Count > 1)
                        CurrentDrawingObject.Points.RemoveAt(CurrentDrawingObject.Points.Count - 1);
                    FinishDrawing(viewModel.ObjectManager);
                }
                else
                {
                    CancelDrawing();
                }
                viewModel.CurrentTool = DrawingTool.Pointer;
                return true;
            }

            var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, coordinateTransform);
            if (hitObject != null)
            {
                if (!viewModel.ObjectManager.IsLocked(hitObject.Id))
                {
                    viewModel.ObjectManager.RemoveObject(hitObject.Id);
                    return true;
                }
                else
                {
                    viewModel.ObjectManager.SelectObject(hitObject.Id);
                    return true;
                }
            }
            else
            {
                if (viewModel.CurrentTool != DrawingTool.Pointer)
                {
                    viewModel.CurrentTool = DrawingTool.Pointer;
                    return true;
                }
            }
        }

        // Ctrl modifier overrides
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            if (IsDrawingNewShape)
            {
                if (_activeBehavior?.RequiredSteps == 0)
                {
                    if (CurrentDrawingObject?.Points.Count > 1)
                        CurrentDrawingObject.Points.RemoveAt(CurrentDrawingObject.Points.Count - 1);
                    FinishDrawing(viewModel.ObjectManager);
                }
                else if (HandleCancelRequest(viewModel.ObjectManager))
                {
                    viewModel.CurrentTool = DrawingTool.Pointer;
                    return true;
                }
                viewModel.CurrentTool = DrawingTool.Pointer;
                return true;
            }
            else if (viewModel.CurrentTool != DrawingTool.Pointer)
            {
                // Idle state (no drawing in progress): Ctrl+Click also reverts the active tool to Pointer.
                viewModel.CurrentTool = DrawingTool.Pointer;
                return true;
            }
        }

        var candleSource = viewModel.Candles?.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));

        // 1.5. Check if we hit an existing handle (to allow moving points even if the tool is selected)
        if (!IsDrawingNewShape && viewModel.ObjectManager.SelectedObject != null)
        {
            var selectedObject = viewModel.ObjectManager.SelectedObject;
            
            // Special handle mapping for objects defined by 2 points but rendered with 4 corner handles
            if (selectedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject)
            {
                if (selectedObject.Points.Count >= 2)
                {
                    var p1 = coordinateTransform.ChartToScreen(selectedObject.Points[0]);
                    var p2 = coordinateTransform.ChartToScreen(selectedObject.Points[1]);
                    
                    var handles = new global::Avalonia.Point[] {
                        new global::Avalonia.Point(p1.X, p1.Y),
                        new global::Avalonia.Point(p2.X, p1.Y),
                        new global::Avalonia.Point(p2.X, p2.Y),
                        new global::Avalonia.Point(p1.X, p2.Y)
                    };

                    for (int i = 0; i < 4; i++)
                    {
                        if (Math.Abs(handles[i].X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx && 
                            Math.Abs(handles[i].Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                        {
                            DraggedObject = selectedObject;
                            DraggedHandleIndex = i;
                            LastDragPoint = point;
                            return true;
                        }
                    }
                }
            }
            else if (selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject)
            {
                if (selectedObject.Points.Count >= 2)
                {
                    var p0 = coordinateTransform.ChartToScreen(selectedObject.Points[0]);
                    var p1 = coordinateTransform.ChartToScreen(selectedObject.Points[1]);
                    
                    if (Math.Abs(p0.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = 0;
                        LastDragPoint = point;
                        return true;
                    }
                    if (Math.Abs(p1.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = 1;
                        LastDragPoint = point;
                        return true;
                    }
                }
            }
            else if (selectedObject is LongShortPositionObject ls1 && ls1.Points.Count >= 3)
            {
                var handles = GetLongShortPositionHandles(ls1, coordinateTransform);
                for (int i = 0; i < handles.Length; i++)
                {
                    if (Math.Abs(handles[i].X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx &&
                        Math.Abs(handles[i].Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = i;
                        LastDragPoint = point;
                        return true;
                    }
                }
            }
            else if (selectedObject is EllipseObject ellipseHandles1)
            {
                var handles = ellipseHandles1.GetSelectionHandleScreenPositions(coordinateTransform);
                // Scan from the highest index down: the circumference angle handles (2/3) are drawn
                // after (on top of) the center/corner handles (0/1) in Render/RenderArc, so when a
                // circumference handle has been dragged to overlap the corner, the topmost (higher-
                // index) one must win the click — otherwise it becomes permanently ungrabbable, always
                // resolving to the corner underneath it.
                for (int i = handles.Length - 1; i >= 0; i--)
                {
                    if (Math.Abs(handles[i].X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx &&
                        Math.Abs(handles[i].Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = i;
                        LastDragPoint = point;
                        return true;
                    }
                }
            }
            else
            {
                for (int i = 0; i < selectedObject.Points.Count; i++)
                {
                    var handleScreenPoint = coordinateTransform.ChartToScreen(selectedObject.Points[i]);
                    if (Math.Abs(handleScreenPoint.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx &&
                        Math.Abs(handleScreenPoint.Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = i;
                        LastDragPoint = point;
                        return true;
                    }
                }
            }
        }

        // 2. Delegate Creation to Behavior (Lines, Shapes, Fibonacci, Gann, etc.)
        if (StartNewShape(
            effectiveTool, 
            chartPoint, 
            viewModel.ObjectManager, 
            candleSource,
            viewModel.LastDrawingTool,
            modifiers.HasFlag(KeyModifiers.Shift)))
        {
            var behavior = DrawingToolBehaviorRegistry.GetBehavior(effectiveTool);
            if (behavior != null)
            {
                // Return to pointer if it's a multi-step tool that just finished,
                // OR if it's a single-step (click-to-place) tool that was just placed.
                bool justFinishedMultiStep = behavior.RequiredSteps >= 2 && wasDrawing && !IsDrawingNewShape;
                bool justPlacedSingleStep = behavior.RequiredSteps == 1 && !wasDrawing && !IsDrawingNewShape;

                if ((justFinishedMultiStep || justPlacedSingleStep) && ShouldReturnToPointerAfterFinish())
                {
                    viewModel.CurrentTool = DrawingTool.Pointer;
                }
            }

            if ((effectiveTool == DrawingTool.LongPosition || effectiveTool == DrawingTool.ShortPosition) && ShouldReturnToPointerAfterFinish())
            {
                 viewModel.CurrentTool = DrawingTool.Pointer;
            }
            return true;
        }

        // 3. Handle specific tools NOT handled by behaviors
        switch (effectiveTool)
        {
            case DrawingTool.Ruler:
                IsDrawingRuler = true;
                RulerRenderer.IsActive = true;
                RulerRenderer.StartPoint = point; // Ruler uses Control-Relative coordinates
                RulerRenderer.EndPoint = point;
                return true;

            case DrawingTool.Text:
                HandleTextTool(chartPoint, viewModel);
                return true;

            case DrawingTool.Eraser:
                HandleEraser(chartPosition, viewModel, coordinateTransform);
                return true;

            case DrawingTool.Pointer:
                return HandlePointerTool(point, chartPosition, viewModel, coordinateTransform);
        }

        return false;
    }


    /// <summary>
    /// Handles the Pointer Released event.
    /// </summary>
    /// <param name="viewModel">The chart view model.</param>
    /// <returns>True if the event was handled and requires a visual update.</returns>
    public bool HandlePointerReleased(ChartViewModel? viewModel)
    {
        bool handled = false;

        if (viewModel != null) ArmOrResetSelectionIdleTimer(viewModel.ObjectManager);

        if (IsDrawingRuler)
        {
            IsDrawingRuler = false;
            handled = true;
        }

        if (DraggedObject != null)
        {
            var objToNotify = DraggedObject;

            // Recalculate patterns/analyses now that the drag has finished, instead of
            // on every PointerMoved frame during the drag (LINQ + regression/histogram/
            // pattern-detection recompute per frame caused visible drag stutter, and for
            // Harmonic/Elliott it also made large patterns flicker as candle subsets changed).
            if (objToNotify is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject harmonicDragged && viewModel?.Candles != null)
            {
                var coreCandles = viewModel.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                harmonicDragged.Recalculate(coreCandles);
            }
            else if (objToNotify is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject elliottDragged && viewModel?.Candles != null)
            {
                var coreCandles = viewModel.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                elliottDragged.Recalculate(coreCandles);
            }
            else if (objToNotify is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geomDragged && viewModel?.Candles != null)
            {
                var coreCandles = viewModel.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                geomDragged.Recalculate(coreCandles);
            }
            else if (viewModel?.Candles != null)
            {
                // RegressionTrendObject / RangeSplineObject / FixedRangeVolumeProfileObject
                // (or a no-op for any other object type). viewModel.Candles is already
                // IReadOnlyList<CoreCandleData> -- DeferredComputationRecalculator dispatches
                // straight to it, no .Select() re-wrap needed.
                DeferredComputationRecalculator.TryRecalculate(objToNotify, viewModel.Candles);
            }

            DraggedObject = null;
            DraggedHandleIndex = -1;
            _activeSmartGuideLines.Clear();
            _moveDragAnchorPoints = null;

            OnObjectDragged?.Invoke(objToNotify);

            // Point/handle drags mutate an existing object's Points in place (no Add/Remove),
            // so ChartObjectManager.Changed doesn't fire for them; persist explicitly here.
            viewModel?.PersistCurrentDrawings();

            handled = true;
        }

        if (IsDragging)
        {
            IsDragging = false;
            handled = true;
        }

        if (IsDrawingNewShape && CurrentDrawingObject != null && viewModel?.ObjectManager != null)
        {
            // tools that finish on Release (Drag-to-Draw)
            if (_activeBehavior != null && _activeBehavior.FinishesOnRelease)
            {
                FinishDrawing(viewModel.ObjectManager);
                if (viewModel != null && ShouldReturnToPointerAfterFinish()) viewModel.CurrentTool = DrawingTool.Pointer;
                handled = true;
            }
        }

        return handled;
    }

    private void HandleEditObject(IChartObject hitObject, ChartViewModel viewModel)
    {
        DraggedObject = null;
        
        _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => 
        {
            decimal? oldThreshold = null;
            int? oldFutureSteps = null;
            if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geomBefore)
            {
                oldThreshold = geomBefore.ZigZagThreshold;
            }
            else if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject harmonicBefore)
            {
                oldThreshold = harmonicBefore.ZigZagThreshold;
            }
            else if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject elliottBefore)
            {
                oldThreshold = elliottBefore.ZigZagThreshold;
            }
            else if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtwBefore)
            {
                oldFutureSteps = dtwBefore.FutureSteps;
            }

            var result = await _dialogService.ShowDrawingSettingsDialogAsync(hitObject);
            
            if (result == DrawingSettingsResult.Deleted)
            {
                viewModel.ObjectManager.RemoveObject(hitObject.Id);
            }
            else if (result == DrawingSettingsResult.Changed)
            {
                if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geomObj)
                {
                    if ((oldThreshold.HasValue != geomObj.ZigZagThreshold.HasValue) || 
                        (oldThreshold.HasValue && geomObj.ZigZagThreshold.HasValue && oldThreshold.Value != geomObj.ZigZagThreshold.Value))
                    {
                        if (viewModel.Candles != null)
                        {
                            var coreCandles = viewModel.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                            geomObj.Recalculate(coreCandles);
                        }
                    }
                }
                else if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject harmonicObj)
                {
                    if ((oldThreshold.HasValue != harmonicObj.ZigZagThreshold.HasValue) || 
                        (oldThreshold.HasValue && harmonicObj.ZigZagThreshold.HasValue && oldThreshold.Value != harmonicObj.ZigZagThreshold.Value))
                    {
                        if (viewModel.Candles != null)
                        {
                            var coreCandles = viewModel.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                            harmonicObj.Recalculate(coreCandles);
                        }
                    }
                }
                else if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject elliottObj)
                {
                    if ((oldThreshold.HasValue != elliottObj.ZigZagThreshold.HasValue) || 
                        (oldThreshold.HasValue && elliottObj.ZigZagThreshold.HasValue && oldThreshold.Value != elliottObj.ZigZagThreshold.Value))
                    {
                        if (viewModel.Candles != null)
                        {
                            var coreCandles = viewModel.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                            elliottObj.Recalculate(coreCandles);
                        }
                    }
                }
                else if (hitObject is RangeSplineObject rangeSpline)
                {
                    if (viewModel.Candles != null)
                    {
                        var coreCandles = viewModel.Candles.Select(c => new StockAnalyzer.Core.Models.CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                        rangeSpline.Recalculate(coreCandles);
                    }
                }
                else if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtwObj)
                {
                    // Unlike the Recalculate() calls above (synchronous, local pattern-detection over
                    // the candle set already in memory), the DTW projection is populated by an async ML
                    // search (ChartViewModel.ExecuteDtwSearchAsync) reached only via the OnDrawingFinished
                    // delegate -- the same path used when the object is first drawn. Re-invoking it here
                    // re-runs that search with the newly committed FutureSteps value.
                    if (oldFutureSteps.HasValue && oldFutureSteps.Value != dtwObj.FutureSteps)
                    {
                        OnDrawingFinished?.Invoke(dtwObj);
                    }
                }
                else if (hitObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject kalmanObj)
                {
                    if (viewModel.Candles != null)
                    {
                        kalmanObj.Recalculate(viewModel.Candles, viewModel.SelectedTimeFrame.ToTimeSpan());
                        viewModel.RequestRender(RenderReason.DataChanged);
                    }
                }
            }

            // "Changed" edits mutate the existing object's properties in place (no Add/Remove),
            // so ChartObjectManager.Changed doesn't fire for them; persist explicitly here.
            // ("Deleted" already persists via RemoveObject's Changed event above.)
            viewModel.PersistCurrentDrawings();
            OnObjectEdited?.Invoke();
        });
    }

    private void HandleTextTool(ChartPoint chartPoint, ChartViewModel viewModel)
    {
        _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => 
        {
            var textObj = new TextObject(chartPoint, "Text");
            var result = await _dialogService.ShowDrawingSettingsDialogAsync(textObj);
            
            if (result == DrawingSettingsResult.Changed)
            {
                viewModel.ObjectManager.AddObject(textObj);
                viewModel.ObjectManager.SelectObject(textObj.Id);
                if (ShouldReturnToPointerAfterFinish()) viewModel.CurrentTool = DrawingTool.Pointer;
            }
        });
    }

    private void HandleEraser(global::Avalonia.Point chartPosition, ChartViewModel viewModel, ICoordinateTransform transform)
    {
        var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform);
        if (hitObject != null && !viewModel.ObjectManager.IsLocked(hitObject.Id))
        {
            viewModel.ObjectManager.RemoveObject(hitObject.Id);
        }
        
        IsEraserActive = true;
    }

    /// <summary>
    /// Captures the reference state for a whole-object (Move) drag: a snapshot of the
    /// object's points at this exact moment, and a zeroed "already applied" delta.
    /// See the anchor-based displacement comment in <see cref="HandleObjectDrag"/> for why.
    /// </summary>
    private void BeginMoveDragAnchor(IChartObject obj)
    {
        _moveDragAnchorPoints = new List<ChartPoint>(obj.Points);
        _moveDragAppliedTimeDelta = TimeSpan.Zero;
        _moveDragAppliedPriceDelta = 0m;
    }

    private bool HandlePointerTool(global::Avalonia.Point point, global::Avalonia.Point chartPosition, ChartViewModel viewModel, ICoordinateTransform transform)
    {
        var selectedObject = viewModel.ObjectManager.SelectedObject;
        if (selectedObject != null)
        {
            // 1. Check Handle Hit Test on Selected Object
            if (selectedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject)
            {
                if (selectedObject.Points.Count >= 2)
                {
                    var p1 = transform.ChartToScreen(selectedObject.Points[0]);
                    var p2 = transform.ChartToScreen(selectedObject.Points[1]);
                    
                    var handles = new global::Avalonia.Point[] {
                        new global::Avalonia.Point(p1.X, p1.Y), // TopLeft or BottomLeft
                        new global::Avalonia.Point(p2.X, p1.Y), // TopRight or BottomRight
                        new global::Avalonia.Point(p2.X, p2.Y), // BottomRight or TopRight
                        new global::Avalonia.Point(p1.X, p2.Y)  // BottomLeft or TopLeft
                    };

                    for (int i = 0; i < 4; i++)
                    {
                        if (Math.Abs(handles[i].X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx && 
                            Math.Abs(handles[i].Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                        {
                            DraggedObject = selectedObject;
                            DraggedHandleIndex = i;
                            LastDragPoint = point;
                            return true;
                        }
                    }
                }
            }
            else if (selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject)
            {
                if (selectedObject.Points.Count >= 2)
                {
                    var p0 = transform.ChartToScreen(selectedObject.Points[0]);
                    var p1 = transform.ChartToScreen(selectedObject.Points[1]);
                    
                    if (Math.Abs(p0.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = 0;
                        LastDragPoint = point;
                        return true;
                    }
                    if (Math.Abs(p1.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = 1;
                        LastDragPoint = point;
                        return true;
                    }
                }
            }
            else if (selectedObject is LongShortPositionObject ls2 && ls2.Points.Count >= 3)
            {
                var handles = GetLongShortPositionHandles(ls2, transform);
                for (int i = 0; i < handles.Length; i++)
                {
                    if (Math.Abs(handles[i].X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx &&
                        Math.Abs(handles[i].Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = i;
                        LastDragPoint = point;
                        return true;
                    }
                }
            }
            else if (selectedObject is EllipseObject ellipseHandles2)
            {
                var handles = ellipseHandles2.GetSelectionHandleScreenPositions(transform);
                // See the matching loop above: scan highest-index-first so an overlapping circumference
                // handle (drawn on top of the corner) remains grabbable instead of being permanently
                // shadowed by the handle underneath it.
                for (int i = handles.Length - 1; i >= 0; i--)
                {
                    if (Math.Abs(handles[i].X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx &&
                        Math.Abs(handles[i].Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        DraggedObject = selectedObject;
                        DraggedHandleIndex = i;
                        LastDragPoint = point;
                        return true;
                    }
                }
            }
            else
            {
                for (int i = 0; i < selectedObject.Points.Count; i++)
                {
                    var handleScreenPoint = transform.ChartToScreen(selectedObject.Points[i]);

                    if (Math.Abs(handleScreenPoint.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx &&
                        Math.Abs(handleScreenPoint.Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                         DraggedObject = selectedObject;
                         DraggedHandleIndex = i;
                         LastDragPoint = point;
                         return true;
                    }
                }
            }

            // 2. Check if we hit an object to Select it
            var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform);
            if (hitObject != null)
            {
                if (hitObject.Id != selectedObject.Id)
                {
                    viewModel.ObjectManager.SelectObject(hitObject.Id);
                }
                DraggedObject = hitObject;
                LastDragPoint = point;
                DraggedHandleIndex = -1;
                BeginMoveDragAnchor(hitObject);
                return true;
            }
        }
        else
        {
             // No selection, try to select
            var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform);
            if (hitObject != null)
            {
                viewModel.ObjectManager.SelectObject(hitObject.Id);
                DraggedObject = hitObject;
                LastDragPoint = point;
                DraggedHandleIndex = -1;
                BeginMoveDragAnchor(hitObject);
                return true;
            }
        }

        return false; // Allow Pan
    }

    /// <summary>
    /// Settings -> Chart -> Drawing: Alt bypasses magnet (candle/price-time) snapping during a
    /// handle drag, matching the "Rule of Zero Interference" already applied to Smart Guide
    /// (object-to-object) snapping. When bypassed, <see cref="LastSnapChartPoint"/> is cleared so
    /// no stale snap marker renders.
    /// </summary>
    private ChartPoint GetMagnetSnappedOrRawPoint(global::Avalonia.Point chartRelativePos, ChartDataSnapshot snapshot, ICoordinateTransform transform, bool isAltBypass)
    {
        if (isAltBypass)
        {
            LastSnapChartPoint = null;
            return transform.ScreenToChart(chartRelativePos);
        }

        var snapResult = _magnetSnapService.GetMagnetSnap(chartRelativePos, snapshot.Candles, transform);
        LastSnapChartPoint = snapResult.IsSnapped ? snapResult.SnappedChartPoint : null;
        return snapResult.SnappedChartPoint;
    }

    /// <summary>
    /// Handles dragging logic for objects (move) or handles (resize).
    /// </summary>
    public bool HandleObjectDrag(
        global::Avalonia.Point mousePos,
        ChartDataSnapshot snapshot,
        ICoordinateTransform transform,
        double chartMarginTop,
        double chartMarginHorizontal,
        global::Avalonia.Rect bounds = default,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var dragged = DraggedObject;
        if (dragged == null) return false;

        var chartRelativePos = new global::Avalonia.Point(mousePos.X - chartMarginHorizontal, mousePos.Y - chartMarginTop);

        bool isHandleDrag = DraggedHandleIndex >= 0 && 
                            (DraggedHandleIndex < dragged.Points.Count || 
                             (dragged is StockAnalyzer.Avalonia.Drawing.RectangleObject && DraggedHandleIndex < 4));

        var activeMoveMode = (_activeObjectManager != null && _activeObjectManager.HasExplicitMoveAxisMode(dragged.Id))
            ? _activeObjectManager.GetMoveAxisMode(dragged.Id)
            : MoveAxisMode;

        bool isAltBypass = modifiers.HasFlag(KeyModifiers.Alt);
        bool isSmartGuidesActive = DrawingThemeContext.SmartGuidesEnabled && !isAltBypass;
        double snapDistance = DrawingThemeContext.SmartGuideSnapDistance;
        var chartArea = bounds.Width > 0 && bounds.Height > 0 
            ? new global::Avalonia.Rect(0, 0, bounds.Width, bounds.Height)
            : transform.ScreenRect;

        if (isHandleDrag)
        {
            // Handle Dragging (Resize) - Apply Smart Guides or Magnet Snap in XY mode, bypass and constrain in X/Y modes
            ChartPoint chartPoint;
            if (activeMoveMode == DrawingMoveAxisMode.XY)
            {
                if (isSmartGuidesActive && _activeObjectManager != null)
                {
                    var smartSnap = _smartGuideService.SnapHandleMove(
                        dragged,
                        DraggedHandleIndex,
                        chartRelativePos,
                        _activeObjectManager.Objects,
                        transform,
                        chartArea,
                        snapDistance,
                        _activeSmartGuideLines);

                    if (smartSnap.IsSnapped)
                    {
                        chartPoint = smartSnap.SnappedChartPoint;
                        LastSnapChartPoint = null;
                    }
                    else
                    {
                        _activeSmartGuideLines.Clear();
                        chartPoint = GetMagnetSnappedOrRawPoint(chartRelativePos, snapshot, transform, isAltBypass);
                    }
                }
                else
                {
                    _activeSmartGuideLines.Clear();
                    chartPoint = GetMagnetSnappedOrRawPoint(chartRelativePos, snapshot, transform, isAltBypass);
                }
            }
            else
            {
                _activeSmartGuideLines.Clear();
                LastSnapChartPoint = null;
                var rawPoint = transform.ScreenToChart(chartRelativePos);
                var origPoint = DraggedHandleIndex < DraggedObject.Points.Count ? DraggedObject.Points[DraggedHandleIndex] : rawPoint;
                chartPoint = activeMoveMode == DrawingMoveAxisMode.X
                    ? new ChartPoint(rawPoint.Time, origPoint.Price)
                    : new ChartPoint(origPoint.Time, rawPoint.Price);
            }

            if (DraggedObject is LongShortPositionObject ls && ls.Points.Count >= 3)
            {
                decimal entryP = (DraggedHandleIndex == 0) ? chartPoint.Price : ls.Points[0].Price;
                if (DraggedHandleIndex == 0) // Entry
                {
                    var timeDelta = chartPoint.Time - ls.Points[0].Time;
                    decimal stopP = ls.Points[1].Price;
                    decimal targetP = ls.Points[2].Price;

                    // Clamp so Entry cannot cross past Stop/Target (mirrors the existing
                    // Stop/Target clamps below, which already prevent them from crossing Entry).
                    decimal newEntry = LongShortPositionObject.ClampEntryPrice(chartPoint.Price, stopP, targetP, ls.IsLong);

                    ls.Points[0] = new ChartPoint(chartPoint.Time, newEntry);
                    ls.Points[1] = new ChartPoint(ls.Points[1].Time.Add(timeDelta), ls.Points[1].Price);
                    ls.Points[2] = new ChartPoint(ls.Points[2].Time.Add(timeDelta), ls.Points[2].Price);
                }
                else if (DraggedHandleIndex == 1) // Stop
                {
                    decimal newStop = LongShortPositionObject.ClampStopPrice(chartPoint.Price, entryP, ls.IsLong);
                    ls.Points[1] = new ChartPoint(ls.Points[0].Time, newStop);
                    ls.BoxWidth = ComputeLongShortBoxWidth(ls, chartRelativePos, transform);
                }
                else if (DraggedHandleIndex == 2) // Target
                {
                    decimal newTarget = LongShortPositionObject.ClampTargetPrice(chartPoint.Price, entryP, ls.IsLong);
                    ls.Points[2] = new ChartPoint(ls.Points[0].Time, newTarget);
                    ls.BoxWidth = ComputeLongShortBoxWidth(ls, chartRelativePos, transform);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geom && DraggedHandleIndex == 0)
            {
                var prevPoint = geom.Points[0];
                var timeDelta = chartPoint.Time - prevPoint.Time;
                var priceDelta = chartPoint.Price - prevPoint.Price;
                geom.Translate(timeDelta, priceDelta);
                // Recalculation is deferred to HandlePointerReleased (see comment there);
                // per-frame LINQ + pattern re-detection here caused drag stutter.
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtwObj)
            {
                if (dtwObj.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    dtwObj.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, dtwObj.Points[DraggedHandleIndex].Price);
                    dtwObj.IsUnmatched = false;
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject kalmanDrag)
            {
                if (kalmanDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    kalmanDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, kalmanDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject || 
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject)
            {
                if (DraggedObject.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    DraggedObject.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, DraggedObject.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject)
            {
                if (DraggedObject.Points.Count >= 2)
                {
                    var p0 = DraggedObject.Points[0];
                    var p1 = DraggedObject.Points[1];
                    
                    if (DraggedHandleIndex == 0) {
                        DraggedObject.Points[0] = chartPoint;
                    } else if (DraggedHandleIndex == 2) {
                        DraggedObject.Points[1] = chartPoint;
                    } else if (DraggedHandleIndex == 1) {
                        DraggedObject.Points[1] = new ChartPoint(chartPoint.Time, p1.Price);
                        DraggedObject.Points[0] = new ChartPoint(p0.Time, chartPoint.Price);
                    } else if (DraggedHandleIndex == 3) {
                        DraggedObject.Points[0] = new ChartPoint(chartPoint.Time, p0.Price);
                        DraggedObject.Points[1] = new ChartPoint(p1.Time, chartPoint.Price);
                    }
                }
            }
            else if (DraggedObject is CatenaryCurveObject catObj && catObj.Points.Count >= 3)
            {
                if (DraggedHandleIndex == 0) // Start point (P0)
                {
                    catObj.Points[0] = chartPoint;
                    catObj.SynchronizeMidpoint();
                }
                else if (DraggedHandleIndex == 1) // End point (P1)
                {
                    catObj.Points[1] = chartPoint;
                    catObj.SynchronizeMidpoint();
                }
                else if (DraggedHandleIndex == 2) // Sag control point (P2)
                {
                    long midTicks = catObj.Points[0].Time.Ticks + (catObj.Points[1].Time.Ticks - catObj.Points[0].Time.Ticks) / 2;
                    catObj.Points[2] = new ChartPoint(new DateTime(midTicks), chartPoint.Price);
                }
            }
            else if (DraggedObject is CurveTrendObject curveTrend && curveTrend.Points.Count >= 3)
            {
                if (DraggedHandleIndex == 0) // Start point (P0)
                {
                    long initialMidTicks = curveTrend.Points[0].Time.Ticks + (curveTrend.Points[1].Time.Ticks - curveTrend.Points[0].Time.Ticks) / 2;
                    long dTime = curveTrend.Points[2].Time.Ticks - initialMidTicks;
                    decimal dPrice = curveTrend.Points[2].Price - (curveTrend.Points[0].Price + curveTrend.Points[1].Price) / 2m;

                    curveTrend.Points[0] = chartPoint;
                    long newMidTicks = chartPoint.Time.Ticks + (curveTrend.Points[1].Time.Ticks - chartPoint.Time.Ticks) / 2;
                    long newTicks = Math.Clamp(newMidTicks + dTime, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
                    decimal newPrice = (chartPoint.Price + curveTrend.Points[1].Price) / 2m + dPrice;
                    curveTrend.Points[2] = new ChartPoint(new DateTime(newTicks), newPrice);
                }
                else if (DraggedHandleIndex == 1) // End point (P1)
                {
                    long initialMidTicks = curveTrend.Points[0].Time.Ticks + (curveTrend.Points[1].Time.Ticks - curveTrend.Points[0].Time.Ticks) / 2;
                    long dTime = curveTrend.Points[2].Time.Ticks - initialMidTicks;
                    decimal dPrice = curveTrend.Points[2].Price - (curveTrend.Points[0].Price + curveTrend.Points[1].Price) / 2m;

                    curveTrend.Points[1] = chartPoint;
                    long newMidTicks = curveTrend.Points[0].Time.Ticks + (chartPoint.Time.Ticks - curveTrend.Points[0].Time.Ticks) / 2;
                    long newTicks = Math.Clamp(newMidTicks + dTime, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
                    decimal newPrice = (curveTrend.Points[0].Price + chartPoint.Price) / 2m + dPrice;
                    curveTrend.Points[2] = new ChartPoint(new DateTime(newTicks), newPrice);
                }
                else if (DraggedHandleIndex == 2) // Control point (P2)
                {
                    curveTrend.Points[2] = chartPoint;
                }
            }
            else if (DraggedObject is CurveChannelObject curveChan && curveChan.Points.Count >= 4)
            {
                if (DraggedHandleIndex == 0) // Start point (P0)
                {
                    long initialMidTicks = curveChan.Points[0].Time.Ticks + (curveChan.Points[1].Time.Ticks - curveChan.Points[0].Time.Ticks) / 2;
                    long dTime = curveChan.Points[2].Time.Ticks - initialMidTicks;
                    decimal dPrice = curveChan.Points[2].Price - (curveChan.Points[0].Price + curveChan.Points[1].Price) / 2m;

                    curveChan.Points[0] = chartPoint;
                    long newMidTicks = chartPoint.Time.Ticks + (curveChan.Points[1].Time.Ticks - chartPoint.Time.Ticks) / 2;
                    long newTicks = Math.Clamp(newMidTicks + dTime, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
                    decimal newPrice = (chartPoint.Price + curveChan.Points[1].Price) / 2m + dPrice;
                    curveChan.Points[2] = new ChartPoint(new DateTime(newTicks), newPrice);
                }
                else if (DraggedHandleIndex == 1) // End point (P1)
                {
                    long initialMidTicks = curveChan.Points[0].Time.Ticks + (curveChan.Points[1].Time.Ticks - curveChan.Points[0].Time.Ticks) / 2;
                    long dTime = curveChan.Points[2].Time.Ticks - initialMidTicks;
                    decimal dPrice = curveChan.Points[2].Price - (curveChan.Points[0].Price + curveChan.Points[1].Price) / 2m;

                    curveChan.Points[1] = chartPoint;
                    long newMidTicks = curveChan.Points[0].Time.Ticks + (chartPoint.Time.Ticks - curveChan.Points[0].Time.Ticks) / 2;
                    long newTicks = Math.Clamp(newMidTicks + dTime, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
                    decimal newPrice = (curveChan.Points[0].Price + chartPoint.Price) / 2m + dPrice;
                    curveChan.Points[2] = new ChartPoint(new DateTime(newTicks), newPrice);
                }
                else if (DraggedHandleIndex == 2) // Control point (P2)
                {
                    curveChan.Points[2] = chartPoint;
                }
                else if (DraggedHandleIndex == 3) // Offset point (P3)
                {
                    curveChan.Points[3] = chartPoint;
                }
            }
            else if (DraggedObject is EllipseObject ellipseObj)
            {
                if (DraggedHandleIndex == 0) // Center: fixed pivot, dragging translates the whole shape
                {
                    var oldCenter = ellipseObj.Points[0];
                    ellipseObj.Translate(chartPoint.Time - oldCenter.Time, chartPoint.Price - oldCenter.Price);
                }
                else if (DraggedHandleIndex == 1) // Corner: defines Rx/Ry/rotation, drags freely
                {
                    // Circumference control points must rotate/resize rigidly together with the
                    // ellipse rather than staying fixed in absolute screen space while the ellipse
                    // changes under them — otherwise the arc visually "detaches" from the ellipse as
                    // soon as the corner is dragged. Preserve each point's local (ellipse-relative)
                    // parametric angle across the drag: capture it against the OLD center/corner
                    // frame, update the corner, then re-derive each point's absolute position at the
                    // SAME local angle against the NEW frame.
                    float[]? localAngles = null;
                    if (ellipseObj.Points.Count >= 4)
                    {
                        var (oldCenter, oldRotation, oldLocalRect) = ComputeEllipseLocalFrame(ellipseObj, transform);
                        localAngles = new float[2];
                        for (int i = 0; i < 2; i++)
                        {
                            var pointScreen = transform.ChartToScreen(ellipseObj.Points[2 + i]);
                            var localPoint = EllipseArcGeometry.RotatePoint(
                                new SKPoint((float)pointScreen.X, (float)pointScreen.Y), oldCenter, -oldRotation);
                            localAngles[i] = EllipseArcGeometry.AngleFromPoint(oldLocalRect, localPoint);
                        }
                    }

                    ellipseObj.Points[1] = chartPoint;

                    if (localAngles != null)
                    {
                        var (newCenter, newRotation, newLocalRect) = ComputeEllipseLocalFrame(ellipseObj, transform);
                        for (int i = 0; i < 2; i++)
                        {
                            var newLocalPoint = EllipseArcGeometry.PointOnEllipse(newLocalRect, localAngles[i]);
                            var newGlobalPoint = EllipseArcGeometry.RotatePoint(newLocalPoint, newCenter, newRotation);
                            ellipseObj.Points[2 + i] = transform.ScreenToChart(new global::Avalonia.Point(newGlobalPoint.X, newGlobalPoint.Y));
                        }
                    }
                }
                else if (DraggedHandleIndex == 2 || DraggedHandleIndex == 3)
                {
                    // Circumference-constrained angle handles: project the raw drag position onto the
                    // current (possibly rotated) ellipse boundary so these control points can only
                    // slide around it. The projection itself happens in the ellipse's local (unrotated)
                    // frame — matching EllipseObject's own Render/HitTest — since AngleFromPoint/
                    // PointOnEllipse operate on an axis-aligned rect; the drag point is mapped in and
                    // the resulting boundary point mapped back out via the same rotation angle.
                    var (centerPoint, rotationAngle, localRect) = ComputeEllipseLocalFrame(ellipseObj, transform);

                    var dragScreen = transform.ChartToScreen(chartPoint);
                    var dragPoint = new SKPoint((float)dragScreen.X, (float)dragScreen.Y);
                    var localDragPoint = EllipseArcGeometry.RotatePoint(dragPoint, centerPoint, -rotationAngle);

                    float angle = EllipseArcGeometry.AngleFromPoint(localRect, localDragPoint);
                    var localBoundary = EllipseArcGeometry.PointOnEllipse(localRect, angle);
                    var boundaryScreen = EllipseArcGeometry.RotatePoint(localBoundary, centerPoint, rotationAngle);
                    ellipseObj.Points[DraggedHandleIndex] = transform.ScreenToChart(new global::Avalonia.Point(boundaryScreen.X, boundaryScreen.Y));
                }
            }
            else
            {
                DraggedObject.Points[DraggedHandleIndex] = chartPoint;
            }

            // FixedRangeVolumeProfileObject recalculation is deferred to
            // HandlePointerReleased (see comment there); its 50-bin histogram recompute
            // there caused drag stutter.
            //
            // RangeSplineObject / RegressionTrendObject are exceptions: unlike FRVP's
            // histogram, their Recalculate() is cheap (RangeSplineObject: O(log N)
            // binary-search range lookup; RegressionTrendObject: a single O(N) LINQ filter
            // + O(k) least-squares fit over just the candles in range) -- as long as they're
            // given the real IReadOnlyList<CoreCandleData> snapshot, NOT the LINQ
            // .Select()-wrapped IEnumerable used at release time (which for RangeSplineObject
            // specifically degrades its binary search to an O(N) linear scan -- the actual
            // source of the original stutter concern for that tool). Running these live here
            // means the curve/line itself -- not just a straight-line placeholder connecting
            // the raw click points -- tracks the drag in real time, and the endpoint no
            // longer visibly snaps to a different position only once the mouse is released
            // (their isStale fallback in DrawGeometry()/Render() otherwise draws a raw
            // straight line, since it has no way to know the real shape without this
            // recalculation).
            if (DraggedObject is RangeSplineObject rangeSplineDragged)
            {
                rangeSplineDragged.Recalculate(snapshot.Candles);
            }
            else if (DraggedObject is RegressionTrendObject regressionDragged)
            {
                regressionDragged.Recalculate(snapshot.Candles);
            }

            if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtw)
            {
                dtw.ProjectedPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject kalman)
            {
                kalman.ProjectedPath?.Clear();
            }

            return true;
        }
        else
        {
            // Object Dragging (Move).
            // LastDragPoint is the mouse position at the START of this drag gesture
            // (set once in HandlePointerTool and intentionally never updated per-frame
            // here, mirroring how handle drags already treat it). Every frame recomputes
            // the total displacement since that anchor rather than accumulating
            // per-frame deltas, so a Smart Guide correction applied in one frame can
            // never permanently detach the object from the cursor once it is no longer
            // near a snap target.
            var anchorScreen = LastDragPoint;
            var anchorChartRel = new global::Avalonia.Point(anchorScreen.X - chartMarginHorizontal, anchorScreen.Y - chartMarginTop);
            var currChartRel = new global::Avalonia.Point(mousePos.X - chartMarginHorizontal, mousePos.Y - chartMarginTop);

            double deltaScreenX = currChartRel.X - anchorChartRel.X;
            double deltaScreenY = currChartRel.Y - anchorChartRel.Y;

            var anchorPoints = _moveDragAnchorPoints ?? dragged.Points;

            if (isSmartGuidesActive && _activeObjectManager != null && activeMoveMode == DrawingMoveAxisMode.XY)
            {
                // Bounding box of the object's position AT DRAG START (never includes a
                // previously-applied correction), shifted by the total mouse
                // displacement since the anchor.
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                if (dragged is LongShortPositionObject ls && anchorPoints.Count >= 3)
                {
                    var ep = transform.ChartToScreen(anchorPoints[0]);
                    var sp = transform.ChartToScreen(anchorPoints[1]);
                    var tp = transform.ChartToScreen(anchorPoints[2]);
                    minX = ep.X;
                    maxX = ep.X + ls.BoxWidth;
                    minY = Math.Min(ep.Y, Math.Min(sp.Y, tp.Y));
                    maxY = Math.Max(ep.Y, Math.Max(sp.Y, tp.Y));
                }
                else
                {
                    for (int p = 0; p < anchorPoints.Count; p++)
                    {
                        var sp = transform.ChartToScreen(anchorPoints[p]);
                        if (double.IsNaN(sp.X) || double.IsInfinity(sp.X) || double.IsNaN(sp.Y) || double.IsInfinity(sp.Y)) continue;
                        if (sp.X < minX) minX = sp.X;
                        if (sp.X > maxX) maxX = sp.X;
                        if (sp.Y < minY) minY = sp.Y;
                        if (sp.Y > maxY) maxY = sp.Y;
                    }
                }

                if (minX != double.MaxValue)
                {
                    var proposedBounds = new global::Avalonia.Rect(minX + deltaScreenX, minY + deltaScreenY, Math.Max(0.0, maxX - minX), Math.Max(0.0, maxY - minY));
                    var snapRes = _smartGuideService.SnapObjectMove(
                        dragged,
                        proposedBounds,
                        _activeObjectManager.Objects,
                        transform,
                        chartArea,
                        snapDistance,
                        _activeSmartGuideLines);

                    deltaScreenX += snapRes.CorrectionX;
                    deltaScreenY += snapRes.CorrectionY;
                }
                else
                {
                    _activeSmartGuideLines.Clear();
                }
            }
            else
            {
                _activeSmartGuideLines.Clear();
            }

            var anchorChart = transform.ScreenToChart(anchorChartRel);
            var targetChart = transform.ScreenToChart(new global::Avalonia.Point(anchorChartRel.X + deltaScreenX, anchorChartRel.Y + deltaScreenY));

            var totalTimeDelta = targetChart.Time - anchorChart.Time;
            var totalPriceDelta = targetChart.Price - anchorChart.Price;

            if (activeMoveMode == DrawingMoveAxisMode.X)
            {
                totalPriceDelta = 0m;
            }
            else if (activeMoveMode == DrawingMoveAxisMode.Y)
            {
                totalTimeDelta = TimeSpan.Zero;
            }

            // Translate() is a relative shift (some overrides, e.g. RangeSplineObject,
            // invalidate caches as a side effect that must run on every call), so apply
            // only the incremental step beyond what this drag has already applied.
            var stepTimeDelta = totalTimeDelta - _moveDragAppliedTimeDelta;
            var stepPriceDelta = totalPriceDelta - _moveDragAppliedPriceDelta;
            dragged.Translate(stepTimeDelta, stepPriceDelta);
            _moveDragAppliedTimeDelta = totalTimeDelta;
            _moveDragAppliedPriceDelta = totalPriceDelta;

            // GeometricPatternObject / Harmonic pattern recalculation is deferred to
            // HandlePointerReleased (see comment there); per-frame LINQ + pattern
            // re-detection here caused drag stutter.

            return true;
        }
    }

    /// <summary>
    /// Computes the screen-space positions of a LongShortPositionObject's 3 handles,
    /// matching how they are drawn in LongShortPositionObject.Render(): Entry at the
    /// box's left edge, Stop/Target at the (BoxWidth-derived) right edge.
    /// </summary>
    internal static global::Avalonia.Point[] GetLongShortPositionHandles(LongShortPositionObject ls, ICoordinateTransform transform)
    {
        var entryScreen = transform.ChartToScreen(ls.Points[0]);
        var stopScreen = transform.ChartToScreen(ls.Points[1]);
        var targetScreen = transform.ChartToScreen(ls.Points[2]);
        double rightX = entryScreen.X + ls.BoxWidth;

        return new[]
        {
            new global::Avalonia.Point(entryScreen.X, entryScreen.Y), // 0: Entry (left edge)
            new global::Avalonia.Point(rightX, stopScreen.Y),         // 1: Stop (right edge)
            new global::Avalonia.Point(rightX, targetScreen.Y)        // 2: Target (right edge)
        };
    }

    /// <summary>
    /// Resolves an EllipseObject's current screen-space center, rotation angle, and local
    /// (unrotated) bounding rect from Points[0]/[1] — the same computation EllipseObject's own
    /// Render/HitTest use internally, duplicated here (rather than exposed publicly) since it is only
    /// ever needed for the drag-handle math below, mirroring GetLongShortPositionHandles's role for
    /// LongShortPositionObject.
    /// </summary>
    private static (SKPoint center, float rotationAngle, SKRect localRect) ComputeEllipseLocalFrame(EllipseObject ellipseObj, ICoordinateTransform transform)
    {
        var centerScreen = transform.ChartToScreen(ellipseObj.Points[0]);
        var cornerScreen = transform.ChartToScreen(ellipseObj.Points[1]);
        var centerPoint = new SKPoint((float)centerScreen.X, (float)centerScreen.Y);
        var cornerPoint = new SKPoint((float)cornerScreen.X, (float)cornerScreen.Y);

        SKPoint? referenceCornerScreen = null;
        if (ellipseObj.DynamicAspectRatioByDistance && ellipseObj.DynamicAspectRatioReferenceCorner is { } referenceCorner)
        {
            var refScreen = transform.ChartToScreen(referenceCorner);
            referenceCornerScreen = new SKPoint((float)refScreen.X, (float)refScreen.Y);
        }

        SKPoint? activationCornerScreen = null;
        if (ellipseObj.EllipticityActivationCorner is { } activationCorner)
        {
            var actScreen = transform.ChartToScreen(activationCorner);
            activationCornerScreen = new SKPoint((float)actScreen.X, (float)actScreen.Y);
        }

        float rotationAngle = EllipseArcGeometry.ComputeRotationAngle(centerPoint, cornerPoint);
        EllipseArcGeometry.ComputeRotatedSemiAxes(centerPoint, cornerPoint, ellipseObj.IsCircular, ellipseObj.AspectRatio, referenceCornerScreen, activationCornerScreen, out float rx, out float ry);
        var localRect = new SKRect(centerPoint.X - rx, centerPoint.Y - ry, centerPoint.X + rx, centerPoint.Y + ry);

        return (centerPoint, rotationAngle, localRect);
    }

    /// <summary>
    /// Computes the new BoxWidth for a LongShortPositionObject while dragging its
    /// Stop/Target handle (drawn at the box's right edge). Width is a fixed pixel
    /// quantity (independent of the time axis/zoom), so it is derived directly from
    /// the horizontal screen-space distance between the current pointer position and
    /// Entry's screen position, clamped to a minimum so the right edge cannot cross
    /// back past Entry (which would invert the box).
    /// </summary>
    internal static double ComputeLongShortBoxWidth(LongShortPositionObject ls, global::Avalonia.Point chartRelativePos, ICoordinateTransform transform)
    {
        var entryScreen = transform.ChartToScreen(ls.Points[0]);
        double newWidth = chartRelativePos.X - entryScreen.X;
        return Math.Max(ChartConstants.LongShortMinBoxWidth, newWidth);
    }

    /// <summary>
    /// Handles the Pointer Moved event.
    /// Updates drawing tools, handles panning, and manages object interaction.
    /// </summary>
    public bool HandlePointerMoved(
        global::Avalonia.Point point,
        global::Avalonia.Point chartPosition,
        ChartViewModel? viewModel,
        ICoordinateTransform transform,
        ChartDataSnapshot snapshot,
        global::Avalonia.Rect bounds,
        double chartMarginHorizontal,
        double chartMarginTop,
        out bool needsUpdate,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        needsUpdate = false;
        if (viewModel == null) return false;
        _activeObjectManager = viewModel.ObjectManager;
        MoveAxisMode = viewModel.MoveAxisMode;
        ArmOrResetSelectionIdleTimer(viewModel.ObjectManager);

        // Ruler Update
        if (IsDrawingRuler)
        {
            RulerRenderer.EndPoint = point;
            needsUpdate = true;
            return true;
        }

        // Eraser Drag Logic
        if (IsEraserActive && viewModel.CurrentTool == DrawingTool.Eraser)
        {
            var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform);
            if (hitObject != null && !viewModel.ObjectManager.IsLocked(hitObject.Id))
            {
                viewModel.ObjectManager.RemoveObject(hitObject.Id);
                needsUpdate = true;
                return true;
            }
        }

        // Drawing New Shape Update
        if (IsDrawingNewShape && CurrentDrawingObject != null)
        {
            if (UpdateNewShape(chartPosition, snapshot.Candles, transform, chartMarginTop, chartMarginHorizontal))
            {
                needsUpdate = true;
                return true;
            }
        }

        // Dragging Existing Object
        if (DraggedObject != null)
        {
             if (HandleObjectDrag(point, snapshot, transform, chartMarginTop, chartMarginHorizontal, bounds, modifiers))
             {
                 needsUpdate = true;
                 return true;
             }
        }

        // Panning Logic (if capturing mouse)
        if (IsDragging)
        {
            return false; // State managed, but actual pan application is in Control
        }

        // Harmonic Pattern / Elliott Wave label hover detection (idle state)
        foreach (var obj in viewModel.ObjectManager.Objects)
        {
            if (obj is HarmonicPatternObject hObj)
            {
                int hitIndex = hObj.HitTestLabel(chartPosition, transform);
                if (hitIndex != hObj.HoveredResultIndex)
                {
                    hObj.HoveredResultIndex = hitIndex;
                    needsUpdate = true;
                }
            }
            else if (obj is AutoElliottWaveObject ewObj)
            {
                int hitIndex = ewObj.HitTestLabel(chartPosition, transform);
                if (hitIndex != ewObj.HoveredResultIndex)
                {
                    ewObj.HoveredResultIndex = hitIndex;
                    needsUpdate = true;
                }
            }
        }

        LastDragPoint = point;
        return needsUpdate;
    }

    /// <summary>
    /// Handles the Double Tap event.
    /// Opens settings for the object under the cursor or finishes a polyline.
    /// </summary>
    public bool HandleDoubleTap(
        global::Avalonia.Point point,
        global::Avalonia.Point chartPosition,
        ChartViewModel? viewModel,
        ICoordinateTransform transform,
        ChartDataSnapshot snapshot,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        if (viewModel == null) return false;

        // Shift/Ctrl are reserved for delete/cancel semantics (see HandlePointerPressed).
        // Avalonia's double-tap gesture recognition is based purely on click position/timing
        // and ignores modifier keys, so a Shift+Click landing within the double-tap window of
        // a prior click on the same object can still raise DoubleTapped. In that case this must
        // NOT open the settings/edit dialog, or a delete attempt appears to silently "fail" and
        // pop up the editor instead.
        if (modifiers.HasFlag(KeyModifiers.Shift) || modifiers.HasFlag(KeyModifiers.Control))
        {
            return false;
        }

        // Finish Polyline
        if (IsDrawingNewShape)
        {
             if (viewModel.ObjectManager != null)
             {
                 FinishDrawing(viewModel.ObjectManager);
                 return true;
             }
        }
        else
        {


             // Show Settings Dialog for Drawing Objects
             var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform);
             if (hitObject != null && viewModel.DialogService != null)
             {
                 HandleEditObject(hitObject, viewModel);
                 return true;
             }
             
             // Fallback: Check if we double-clicked a handle on the currently selected object
             var selectedObject = viewModel.ObjectManager.SelectedObject;
             if (selectedObject != null && viewModel.DialogService != null)
             {
                 if ((selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject || selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject) && selectedObject.Points.Count >= 2)
                 {
                     var p1 = transform.ChartToScreen(selectedObject.Points[0]);
                     var p2 = transform.ChartToScreen(selectedObject.Points[1]);
                     var handles = new global::Avalonia.Point[] {
                         new global::Avalonia.Point(p1.X, p1.Y), new global::Avalonia.Point(p2.X, p1.Y),
                         new global::Avalonia.Point(p2.X, p2.Y), new global::Avalonia.Point(p1.X, p2.Y)
                     };
                     foreach (var h in handles)
                     {
                         if (Math.Abs(h.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx && Math.Abs(h.Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                         {
                             HandleEditObject(selectedObject, viewModel);
                             return true;
                         }
                     }
                 }
                 else
                 {
                     foreach (var pt in selectedObject.Points)
                     {
                         var handleScreenPoint = transform.ChartToScreen(pt);
                         if (Math.Abs(handleScreenPoint.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx && Math.Abs(handleScreenPoint.Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                         {
                             HandleEditObject(selectedObject, viewModel);
                             return true;
                         }
                     }
                 }
             }
        }
        return false;
    }



    private bool CheckIndicatorHitTest(
        CoreIndicatorSettings setting, 
        int index, 
        global::Avalonia.Point screenPoint, 
        ICoordinateTransform transform, 
        ChartLayoutContext layout, 
        ref int panelIndex, 
        bool isSubWindowContext, 
        int? panelIndexOverride,
        ChartDataSnapshot? snapshot = null)
    {
        Rect targetRect;
        double minVal = 0, maxVal = 100;

        if (isSubWindowContext)
        {
            int targetPanelIdx = panelIndexOverride ?? panelIndex;
            if (targetPanelIdx < layout.PanelAreas.Count)
            {
                targetRect = layout.PanelAreas[targetPanelIdx];
                if (!panelIndexOverride.HasValue) panelIndex++;

                if (snapshot != null)
                {
                    // Need to calculate panel range
                    if (!string.IsNullOrEmpty(setting.OverlayPanelId))
                    {
                        var groupMembers = snapshot.IndicatorSettings.Where(s => s.IsEnabled && s.OverlayPanelId == setting.OverlayPanelId).ToList();
                        var range = Renderers.PanelValueRangeCalculator.CalculateGroup(snapshot, groupMembers);
                        minVal = (double)range.Min; maxVal = (double)range.Max;
                    }
                    else
                    {
                        var range = Renderers.PanelValueRangeCalculator.Calculate(snapshot, setting);
                        minVal = (double)range.Min; maxVal = (double)range.Max;
                    }
                }
            }
            else
            {
                targetRect = layout.ChartArea;
            }
        }
        else
        {
            // For main chart overlay, use chartArea and snapshot's price range
            targetRect = layout.ChartArea;
            if (snapshot != null)
            {
                minVal = (double)snapshot.MinPrice;
                maxVal = (double)snapshot.MaxPrice;
            }
        }

        // If point is outside the Y area of this panel, skip early
        if (screenPoint.Y < targetRect.Top - 10 || screenPoint.Y > targetRect.Bottom + 10)
        {
            return false;
        }

        // Get value
        if (snapshot?.IndicatorResults != null && snapshot.IndicatorResults.TryGetValue(setting.Id, out var result))
        {
            if (!result.IsSuccessful || index < 0 || index >= result.MainValues.Count) return false;
            
            // Check all exported series for this indicator
            foreach (var seriesName in result.SeriesNames)
            {
                var series = result.GetSeries(seriesName);
                if (series == null || index >= series.Count) continue;

                var val = series[index];
                if (!val.HasValue) continue;

                // Use price-range linear interpolation for both sub-window and main chart.
                // This avoids dependency on transform.CanvasHeight which may not match
                // the actual rendering chart area when sub-window panels exist.
                double range = maxVal - minVal;
                if (range == 0) range = 1;
                double ratio = ((double)val.Value - minVal) / range;
                double screenY = targetRect.Bottom - (ratio * targetRect.Height);

                if (Math.Abs(screenPoint.Y - screenY) <= 10.0)
                {
                    return true;
                }
            }
        }
        return false;
    }


    /// <summary>
    /// Updates the current drawing object with the new mouse position.
    /// </summary>
    public bool UpdateNewShape(
        global::Avalonia.Point chartPosition,
        IReadOnlyList<CoreCandleData> candles,
        ICoordinateTransform transform,
        double chartMarginTop,
        double chartMarginHorizontal)
    {
        var chartRelativePos = chartPosition;
        if (CurrentDrawingObject == null) return false;
        
        // Apply Magnet Snap for new shapes
        var snapResult = _magnetSnapService.GetMagnetSnap(chartRelativePos, candles, transform);
        var chartPoint = snapResult.SnappedChartPoint;
        LastSnapChartPoint = snapResult.IsSnapped ? snapResult.SnappedChartPoint : null;

        // Delegate to active behavior if available (New Architecture)
        if (_activeBehavior != null)
        {
            _activeBehavior.UpdatePoint(CurrentDrawingObject, DrawingStep, chartPoint, candles);
            
            // Recalculate specialized objects (Legacy fallback, ideally moved to Behavior).
            // candles is already IReadOnlyList<CoreCandleData>, so DeferredComputationRecalculator
            // dispatches straight to it -- no .Select() re-wrap (which would be LINQ inside
            // this PointerMoved-frequency handler, per SA_RENDERING_PERFORMANCE.md "LINQ in
            // Hot Paths", and would also defeat Recalculate()'s O(log N) binary-search fast
            // path for IReadOnlyList input).
            if (DeferredComputationRecalculator.TryRecalculate(CurrentDrawingObject, candles))
            {
                // Handled (RegressionTrendObject / RangeSplineObject / FixedRangeVolumeProfileObject)
            }
            else if (CurrentDrawingObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject harmonic)
            {
                 var coreCandles = candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                 harmonic.Recalculate(coreCandles);
            }
            else if (CurrentDrawingObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject elliottObj)
            {
                 var coreCandles = candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                 elliottObj.Recalculate(coreCandles);
            }
            return true;
        }

        // Fallback Logic (Legacy - Update Last Point)
        if (CurrentDrawingObject.Points.Count > 0)
        {
             // Update the last point
             CurrentDrawingObject.Points[CurrentDrawingObject.Points.Count - 1] = chartPoint;
             
             // Recalculate if needed
             if (CurrentDrawingObject is RegressionTrendObject reg)
             {
                 var coreCandles = candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                 reg.Recalculate(coreCandles);
             }
             else if (CurrentDrawingObject is RangeSplineObject rangeSpline)
             {
                 var coreCandles = candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                 rangeSpline.Recalculate(coreCandles);
             }
             
             return true;
        }
        
        return false;
    }

    private void StopDragging()
    {
        DraggedObject = null;
        DraggedHandleIndex = -1;
        IsDragging = false;
        LastSnapChartPoint = null;
        _moveDragAnchorPoints = null;
    }

    /// <summary>
    /// Resets the snap marker state.
    /// Should be called when data is reloaded or the chart context changes significantly.
    /// </summary>
    public void ResetSnapState()
    {
        LastSnapChartPoint = null;
    }



    /// <summary>
    /// Starts a new drawing operation or advances a multi-step drawing.
    /// Uses IDrawingToolBehavior dispatch for tool-specific logic.
    /// </summary>
    /// <param name="tool">The drawing tool to use.</param>
    /// <param name="chartPoint">The chart coordinates of the click.</param>
    /// <param name="objectManager">The object manager to add objects to.</param>
    /// <param name="candles">Candle data needed for pattern extraction (e.g. BarPattern).</param>
    /// <param name="lastDrawingTool">The last used tool (for Shift modifier support).</param>
    /// <param name="isShiftCheck">Whether Shift key is pressed (for Polyline finish).</param>
    /// <returns>True if the event was handled.</returns>
    public bool StartNewShape(
        DrawingTool tool, 
        ChartPoint chartPoint, 
        ChartObjectManager? objectManager,
        IEnumerable<CoreCandleData>? candles = null,
        DrawingTool? lastDrawingTool = null,
        bool isShiftCheck = false)
    {
        if (objectManager == null) return false;

        // Look up the behavior for this tool
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(tool);
        if (behavior == null) return false;

        // --- Variable-point tools (Polyline/ElliottWave/NurbsTrendCurve/CurveLineText): add points dynamically ---
        if (behavior.RequiredSteps == 0 && IsDrawingNewShape)
        {
            if (CurrentDrawingObject is PolylineObject existingPoly)
            {
                existingPoly.AddPoint(chartPoint);
                return true;
            }
            if (CurrentDrawingObject is NurbsTrendCurveObject existingNurbs)
            {
                existingNurbs.AddPoint(chartPoint);
                return true;
            }
            if (CurrentDrawingObject is CurveLineTextObject existingCurveText)
            {
                existingCurveText.AddPoint(chartPoint);
                return true;
            }
        }

        // --- BarPattern: special multi-step with data extraction ---
        if (tool == DrawingTool.BarPattern)
        {
            return HandleBarPatternStep(behavior, chartPoint, objectManager, candles);
        }

        // --- Click-to-place tools (1 step): create and immediately finish ---
        if (behavior.RequiredSteps == 1)
        {
            // Enforce single instance for macro tools like Geometric Pattern
            if (behavior is StockAnalyzer.Avalonia.Drawing.Behaviors.GeometricPatternBehavior)
            {
                var existing = objectManager.Objects
                    .Where(o => o is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject)
                    .ToList();
                foreach (var oldObj in existing)
                {
                    objectManager.RemoveObject(oldObj.Id);
                }
            }

            var obj = behavior.CreateObject(chartPoint, candles);
            StartDrawingInternal(obj, behavior);
            FinishDrawing(objectManager);
            return true;
        }

        // --- Multi-step tools (3+ steps): advance through steps ---
        if (behavior.RequiredSteps >= 3)
        {
            if (!IsDrawingNewShape)
            {
                var obj = behavior.CreateObject(chartPoint, candles);
                StartDrawingInternal(obj, behavior);
                DrawingStep = 1;
            }
            else
            {
                AdvanceDrawingStep();
                if (DrawingStep > behavior.RequiredSteps - 1)
                {
                    FinishDrawing(objectManager);
                }
            }
            return true;
        }

        // --- 2-step tools (DragToDraw or TwoClick) ---
        if (!IsDrawingNewShape)
        {
            var obj = behavior.CreateObject(chartPoint, candles);
            StartDrawingInternal(obj, behavior);
            if (!behavior.FinishesOnRelease) 
            {
                DrawingStep = 1; // P1 is moving, wait for next click to finish
            }
        }
        else
        {
            if (!behavior.FinishesOnRelease && DrawingStep == 1)
            {
                // Second click for TwoClickBehavior
                FinishDrawing(objectManager);
            }
            else if (behavior.FinishesOnRelease)
            {
                // This shouldn't be reached on click for DragToDraw (it finishes on release),
                // but if it is, finish it.
                FinishDrawing(objectManager);
            }
        }
        return true;
    }

    /// <summary>
    /// Handles the special multi-step BarPattern tool.
    /// Step 1: Set range start. Step 2: Extract candle data. Step 3: Place anchor.
    /// </summary>
    private bool HandleBarPatternStep(
        IDrawingToolBehavior behavior,
        ChartPoint chartPoint,
        ChartObjectManager objectManager,
        IEnumerable<CoreCandleData>? candles)
    {
        if (!IsDrawingNewShape)
        {
            var obj = behavior.CreateObject(chartPoint, candles);
            StartDrawingInternal(obj, behavior);
            DrawingStep = 1;
        }
        else if (DrawingStep == 1)
        {
            // Step 1 -> 2: P2 Confirmed. Extract Data.
            if (candles != null)
            {
                var barPattern = CurrentDrawingObject as BarPatternObject;
                if (barPattern != null && barPattern.Points.Count > 1)
                {
                     DateTime t1 = barPattern.Points[0].Time;
                     DateTime t2 = chartPoint.Time;
                     if (t1 > t2) (t1, t2) = (t2, t1);

                     var source = candles
                         .Where(c => c.Timestamp >= t1 && c.Timestamp <= t2)
                         .OrderBy(c => c.Timestamp)
                         .ToList();
                         
                     barPattern.Initialize(source);

                     barPattern.Points.Clear();
                     barPattern.Points.Add(chartPoint); // Anchor
                     AdvanceDrawingStep(); // To Step 2
                }
            }
        }
        else if (DrawingStep == 2)
        {
            // Step 2 -> Finish: P3 Confirmed.
            if (CurrentDrawingObject != null && CurrentDrawingObject.Points.Count > 0)
                CurrentDrawingObject.Points[0] = chartPoint;
            FinishDrawing(objectManager);
        }
        return true;
    }

    /// <summary>
    /// Starts a new drawing operation (Internal helper).
    /// </summary>
    private void StartDrawingInternal(IChartObject drawingObject, IDrawingToolBehavior behavior)
    {
        CurrentDrawingObject = drawingObject;
        IsDrawingNewShape = true;
        DrawingStep = 0;
        _activeBehavior = behavior;
    }

    /// <summary>
    /// Advances to the next step in a multi-step drawing tool.
    /// </summary>
    public void AdvanceDrawingStep()
    {
        DrawingStep++;
    }

    /// <summary>
    /// Finishes the current drawing operation and registers the object.
    /// </summary>
    public void FinishDrawing(ChartObjectManager objectManager)
    {
        if (CurrentDrawingObject != null)
        {
            objectManager.AddObject(CurrentDrawingObject);
            objectManager.SelectObject(CurrentDrawingObject.Id);
            OnDrawingFinished?.Invoke(CurrentDrawingObject);
        }

        CurrentDrawingObject = null;
        IsDrawingNewShape = false;
        DrawingStep = 0;
        LastSnapChartPoint = null;
        _activeBehavior = null;
    }

    /// <summary>
    /// Handles a cancellation request (e.g., Ctrl key press).
    /// For variable-step tools (RequiredSteps == 0), this finishes the shape if valid.
    /// For fixed-step tools, this cancels the operation.
    /// </summary>
    public bool HandleCancelRequest(ChartObjectManager objectManager)
    {
        if (!IsDrawingNewShape || CurrentDrawingObject == null) return false;

        if (_activeBehavior?.RequiredSteps == 0)
        {
            if (CurrentDrawingObject.Points.Count > 0)
            {
                CurrentDrawingObject.Points.RemoveAt(CurrentDrawingObject.Points.Count - 1);
            }

            if (CurrentDrawingObject.Points.Count >= 2)
            {
                FinishDrawing(objectManager);
                return true;
            }
        }

        CancelDrawing();
        return true;
    }

    /// <summary>
    /// Cancels the current drawing operation.
    /// </summary>
    public void CancelDrawing()
    {
        CurrentDrawingObject = null;
        IsDrawingNewShape = false;
        DrawingStep = 0;
        _activeSmartGuideLines.Clear();
        LastSnapChartPoint = null;
        _activeBehavior = null;
    }

    /// <summary>
    /// Starts dragging an object.
    /// </summary>
    /// <param name="obj">The object to drag.</param>
    /// <param name="handleIndex">The handle index being dragged (-1 for whole object).</param>
    public void StartDragging(IChartObject obj, int handleIndex = -1)
    {
        DraggedObject = obj;
        DraggedHandleIndex = handleIndex;
    }



    /// <summary>
    /// Updates the current shape being drawn based on mouse position.
    /// Delegates to the active behavior for tool-specific update logic.
    /// </summary>
    /// <param name="mouseScreenPoint">The mouse position in chart-relative coordinates.</param>
    /// <param name="candleSource">Candle data for magnet snap and recalculations.</param>
    /// <param name="transform">Coordinate transform.</param>
    /// <returns>True if the shape was updated.</returns>
    public bool UpdateNewShape(
        global::Avalonia.Point mouseScreenPoint,
        IEnumerable<CoreCandleData>? candleSource,
        ICoordinateTransform transform)
    {
         if (!IsDrawingNewShape || CurrentDrawingObject == null) return false;

         var candlesList = (candleSource as IReadOnlyList<CoreCandleData>) ?? candleSource?.ToList() ?? new List<CoreCandleData>();

         // Magnet Snap
         var snapResult = _magnetSnapService.GetMagnetSnap(mouseScreenPoint, candlesList, transform);
         LastSnapChartPoint = snapResult.IsSnapped ? snapResult.SnappedChartPoint : null;
         
         // Delegate to active behavior for tool-specific point update
         if (_activeBehavior != null)
         {
             _activeBehavior.UpdatePoint(CurrentDrawingObject, DrawingStep, snapResult.SnappedChartPoint, candlesList);
         }
         else
         {
             // Fallback: update last point directly (legacy path)
             UpdateDrawingPoint(snapResult.SnappedChartPoint, candlesList);
         }
         
         return true;
    }

    /// <summary>
    /// Updates a drawing object's point based on current mouse position.
    /// Legacy fallback for cases where no behavior is active.
    /// </summary>
    /// <param name="chartPoint">The new chart point.</param>
    /// <param name="coreCandles">Core candles for regression recalculation (if needed).</param>
    public void UpdateDrawingPoint(ChartPoint chartPoint, IEnumerable<CoreCandleData>? coreCandles = null)
    {
        if (CurrentDrawingObject == null) return;

        // Simple default: update the last point
        if (CurrentDrawingObject.Points.Count > 0)
        {
            CurrentDrawingObject.Points[CurrentDrawingObject.Points.Count - 1] = chartPoint;

            if (CurrentDrawingObject is RegressionTrendObject regression && coreCandles != null)
            {
                regression.Recalculate(coreCandles);
            }
            else if (CurrentDrawingObject is RangeSplineObject rangeSpline && coreCandles != null)
            {
                rangeSpline.Recalculate(coreCandles);
            }
            else if (CurrentDrawingObject is FixedRangeVolumeProfileObject frvp && coreCandles != null)
            {
                frvp.Recalculate(coreCandles);
            }
        }
    }

    #endregion
}
