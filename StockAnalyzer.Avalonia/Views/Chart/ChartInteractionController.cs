using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Avalonia;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    /// Panel the current pointer gesture targets: -1 = Main Chart, >= 0 = sub-window panel.
    /// Set by the pointer entry points from the value <c>ChartBaseControl</c> resolves; new drawing
    /// objects are stamped with it and hit-testing is scoped to it.
    /// </summary>
    private int _activePanelIndex = -1;

    /// <summary>
    /// Layered drawing scene used for unified rendering and hit-testing sequence per S06 and G4.
    /// </summary>
    public StockAnalyzer.Avalonia.Drawing.LayeredDrawingScene? LayeredScene { get; set; }

    private StockAnalyzer.Core.Models.Drawing.PanelKey? _currentDrawingPanel;
    private Guid? _currentDrawingLayerId;

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

    private DateTime _lastInfoHoverTime;
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

    // Link-group members that follow the dragged object during a whole-object (Move) drag.
    // Resolved once at drag start so the per-frame path allocates nothing; null when the dragged object is not linked.
    private IChartObject[]? _linkedMoveMembers;
    private List<ChartPoint>[]? _linkedMoveAnchorPoints;
    private IReadOnlyList<IChartObject>? _linkedSmartGuideTargets;
    private TimeSpan _linkedAppliedTimeDelta;
    private decimal _linkedAppliedPriceDelta;
    private StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator? _activeCoordinator;
    private StockAnalyzer.Core.Models.Drawing.DrawingEditToken? _activeDragToken;

    private bool StartDragTransaction(ChartViewModel? viewModel, bool isHandleDrag)
    {
        if (viewModel?.DrawingEditCoordinator != null && !_activeDragToken.HasValue)
        {
            var kind = isHandleDrag 
                ? StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.PointEdit 
                : StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Move;
            var begin = viewModel.DrawingEditCoordinator.BeginEdit(kind);
            if (!begin.IsSuccess)
            {
                return false;
            }
            _activeDragToken = begin.Token;
            _activeCoordinator = viewModel.DrawingEditCoordinator;
        }
        return true;
    }

    private bool BeginHandleDrag(IChartObject obj, int handleIndex, global::Avalonia.Point point, ChartViewModel? viewModel)
    {
        if (obj is StockAnalyzer.Avalonia.Drawing.FreehandObject)
        {
            return false;
        }

        if (!StartDragTransaction(viewModel, isHandleDrag: true))
        {
            return false;
        }
        DraggedObject = obj;
        DraggedHandleIndex = handleIndex;
        LastDragPoint = point;
        if ((obj is FixedRangeVolumeProfileObject frvp && frvp.LockRange) || (obj is TimeAtPriceObject tap && tap.LockRange))
        {
            _moveDragAnchorPoints = new List<ChartPoint>(obj.Points);
        }
        return true;
    }

    /// <summary>
    /// Checks if a pointer press hits a control handle of the currently selected object.
    /// If hit, initiates handle dragging and returns true.
    /// </summary>
    public bool TryHandleSelectionHandle(
        global::Avalonia.Point point,
        global::Avalonia.Point chartPosition,
        ChartViewModel viewModel,
        ICoordinateTransform coordinateTransform)
    {
        if (viewModel?.ObjectManager?.SelectedObject == null)
        {
            return false;
        }

        var selectedObject = viewModel.ObjectManager.SelectedObject;

        // Freehand objects do not support vertex handle dragging; positions are fixed in chart coordinates.
        if (selectedObject is StockAnalyzer.Avalonia.Drawing.FreehandObject)
        {
            return false;
        }

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
                        return BeginHandleDrag(selectedObject, i, point, viewModel);
                    }
                }
            }
        }
        else if (selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.ArimaProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HmmProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.PearsonProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.FrechetProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaMultiComponentObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaSupportResistanceObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaAnomalyHighlightObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoTimeCycleObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughAutoLinesObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughParabolicCurveObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughKeyLevelsObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughResonantFanObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughMagneticLineObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.FixedRangeVolumeProfileObject ||
                 selectedObject is StockAnalyzer.Avalonia.Drawing.TimeAtPriceObject)
        {
            if (selectedObject.Points.Count >= 2)
            {
                var p0 = coordinateTransform.ChartToScreen(selectedObject.Points[0]);
                var p1 = coordinateTransform.ChartToScreen(selectedObject.Points[1]);

                if (Math.Abs(p0.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                {
                    return BeginHandleDrag(selectedObject, 0, point, viewModel);
                }
                if (Math.Abs(p1.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                {
                    return BeginHandleDrag(selectedObject, 1, point, viewModel);
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
                    return BeginHandleDrag(selectedObject, i, point, viewModel);
                }
            }
        }
        else if (selectedObject is EllipseObject ellipseHandles1)
        {
            var handles = ellipseHandles1.GetSelectionHandleScreenPositions(coordinateTransform);
            for (int i = handles.Length - 1; i >= 0; i--)
            {
                if (Math.Abs(handles[i].X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx &&
                    Math.Abs(handles[i].Y - chartPosition.Y) <= ChartConstants.HandleClickToleranceScreenPx)
                {
                    return BeginHandleDrag(selectedObject, i, point, viewModel);
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
                    return BeginHandleDrag(selectedObject, i, point, viewModel);
                }
            }
        }

        return false;
    }

    private void CancelActiveDragTransaction(ChartViewModel? viewModel = null)
    {
        ClearLinkedMoveState();
        if (_activeDragToken.HasValue)
        {
            var token = _activeDragToken.Value;
            _activeDragToken = null;
            var coord = _activeCoordinator ?? viewModel?.DrawingEditCoordinator;
            _activeCoordinator = null;
            coord?.Cancel(token);
        }
    }

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
        int clickCount,
        int activePanelIndex = -1)
    {
        _activePanelIndex = activePanelIndex;
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
                    FinishDrawing(viewModel.ObjectManager, viewModel.LayerService, viewModel.DrawingEditCoordinator);
                }
                else
                {
                    CancelDrawing();
                }
                viewModel.CurrentTool = DrawingTool.Pointer;
                return true;
            }

            var hitObject = GetObjectAt(chartPosition, coordinateTransform, viewModel);
            if (hitObject != null)
            {
                var layer = viewModel.LayerService.GetLayerForObject(hitObject.Id);
                bool isLayerLocked = layer?.IsEditLocked ?? false;
                if (!viewModel.ObjectManager.IsLocked(hitObject.Id) && !isLayerLocked)
                {
                    if (viewModel.DrawingEditCoordinator != null)
                    {
                        viewModel.DrawingEditCoordinator.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Delete, () =>
                        {
                            viewModel.ObjectManager.RemoveObject(hitObject.Id);
                            viewModel.LayerService.RemoveObjectFromLayer(hitObject.Id);
                        });
                    }
                    else
                    {
                        viewModel.ObjectManager.RemoveObject(hitObject.Id);
                        viewModel.LayerService.RemoveObjectFromLayer(hitObject.Id);
                    }
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
                    FinishDrawing(viewModel.ObjectManager, viewModel.LayerService, viewModel.DrawingEditCoordinator);
                }
                else if (HandleCancelRequest(viewModel.ObjectManager, viewModel.LayerService, viewModel.DrawingEditCoordinator))
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
        if (!IsDrawingNewShape && TryHandleSelectionHandle(point, chartPosition, viewModel, coordinateTransform))
        {
            return true;
        }

        // 2. Delegate Creation to Behavior (Lines, Shapes, Fibonacci, Gann, etc.)
        if (StartNewShape(
            effectiveTool, 
            chartPoint, 
            viewModel.ObjectManager, 
            candleSource,
            viewModel.LastDrawingTool,
            modifiers.HasFlag(KeyModifiers.Shift),
            viewModel))
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

            case DrawingTool.Information:
                if (viewModel != null)
                {
                    var existing = viewModel.ObjectManager.InformationObject;
                    if (existing == null)
                    {
                        var infoObj = new StockAnalyzer.Avalonia.Drawing.Objects.InformationObject();
                        infoObj.Snapshot = ChartInformationDataProvider.Extract(viewModel, chartPoint.Time, chartPoint.Price);
                        if (viewModel.DrawingEditCoordinator != null)
                        {
                            viewModel.DrawingEditCoordinator.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Add, () =>
                            {
                                viewModel.ObjectManager.AddObject(infoObj);
                                viewModel.ObjectManager.SelectObject(infoObj.Id);
                            });
                        }
                        else
                        {
                            viewModel.ObjectManager.AddObject(infoObj);
                            viewModel.ObjectManager.SelectObject(infoObj.Id);
                        }
                    }
                    else
                    {
                        viewModel.ObjectManager.SelectObject(existing.Id);
                    }

                    viewModel.CurrentTool = DrawingTool.Pointer;
                }
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
            var releaseContext = viewModel?.CreateDrawingCalculationContext() ?? DrawingCalculationContext.Empty;
            DrawingRecalculation.AfterMoveCompleted(objToNotify, releaseContext);

            if (_linkedMoveMembers != null)
            {
                for (int i = 0; i < _linkedMoveMembers.Length; i++)
                {
                    DrawingRecalculation.AfterMoveCompleted(_linkedMoveMembers[i], releaseContext);
                }
            }
            ClearLinkedMoveState();

            DraggedObject = null;
            DraggedHandleIndex = -1;
            _activeSmartGuideLines.Clear();
            _moveDragAnchorPoints = null;

            OnObjectDragged?.Invoke(objToNotify);

            // Point/handle drags mutate an existing object's Points in place (no Add/Remove).
            // If an active drag transaction was opened, commit it; otherwise fall back to explicit persist.
            if (_activeDragToken.HasValue)
            {
                var token = _activeDragToken.Value;
                _activeDragToken = null;
                var coord = _activeCoordinator ?? viewModel?.DrawingEditCoordinator;
                _activeCoordinator = null;
                coord?.Commit(token);
            }
            else
            {
                viewModel?.PersistCurrentDrawings();
            }

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
                FinishDrawing(viewModel.ObjectManager, viewModel?.LayerService, viewModel?.DrawingEditCoordinator);
                if (viewModel != null && ShouldReturnToPointerAfterFinish()) viewModel.CurrentTool = DrawingTool.Pointer;
                handled = true;
            }
        }

        return handled;
    }

    private void HandleEditObject(IChartObject hitObject, ChartViewModel viewModel)
    {
        var existingSnapshot = (hitObject as StockAnalyzer.Avalonia.Drawing.Objects.InformationObject)?.Snapshot
            ?? viewModel.ObjectManager.InformationObject?.Snapshot;

        CancelActiveDragTransaction(viewModel);
        DraggedObject = null;
        DraggedHandleIndex = -1;
        _activeSmartGuideLines.Clear();
        _moveDragAnchorPoints = null;
        ClearLinkedMoveState();

        _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // F01 fix: CancelActiveDragTransaction() above may have already run DrawingEditCoordinator
            // .Cancel -> RestoreContextState, which materializes brand-new runtime IChartObject instances
            // (new Id) via ChartObjectManager.LoadSnapshot and orphans the pre-cancel hitObject reference.
            // Re-resolve the live object identity before binding the dialog, or the dialog edits a
            // detached instance whose changes are never rendered or persisted (manager still holds the
            // untouched materialized copy). TryResolveCurrentRuntimeId returns false (with the original
            // Id echoed back) when no persistent mapping exists yet -- e.g. a just-drawn, never-committed
            // object -- so falling back to hitObject.Id in that case is correct, not a stale reference.
            var editTarget = hitObject;
            if (viewModel.DrawingEditCoordinator != null)
            {
                var liveId = hitObject.Id;
                if (viewModel.DrawingEditCoordinator.TryResolveCurrentRuntimeId(hitObject.Id, out var resolvedLiveId))
                {
                    liveId = resolvedLiveId;
                }
                var liveObject = viewModel.ObjectManager.GetObject(liveId);
                if (liveObject == null)
                {
                    // Object no longer exists under either identity: do not edit a stale reference.
                    return;
                }
                editTarget = liveObject;
            }

            if (editTarget is StockAnalyzer.Avalonia.Drawing.Objects.InformationObject infoTarget)
            {
                if (infoTarget.Snapshot == null)
                {
                    infoTarget.Snapshot = existingSnapshot ?? (viewModel.Candles != null && viewModel.Candles.Count > 0
                        ? ChartInformationDataProvider.Extract(
                            viewModel,
                            viewModel.Candles.Last().Timestamp,
                            viewModel.Candles.Last().Close)
                        : null);
                }
            }
            if (viewModel.ObjectManager.InformationObject is { } mgrInfo && mgrInfo.Snapshot == null)
            {
                mgrInfo.Snapshot = (editTarget as StockAnalyzer.Avalonia.Drawing.Objects.InformationObject)?.Snapshot
                    ?? existingSnapshot
                    ?? (viewModel.Candles != null && viewModel.Candles.Count > 0
                        ? ChartInformationDataProvider.Extract(
                            viewModel,
                            viewModel.Candles.Last().Timestamp,
                            viewModel.Candles.Last().Close)
                        : null);
            }

            decimal? oldThreshold = null;
            int? oldFutureSteps = null;
            if (editTarget is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geomBefore)
            {
                oldThreshold = geomBefore.ZigZagThreshold;
            }
            else if (editTarget is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject harmonicBefore)
            {
                oldThreshold = harmonicBefore.ZigZagThreshold;
            }
            else if (editTarget is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject elliottBefore)
            {
                oldThreshold = elliottBefore.ZigZagThreshold;
            }
            else if (editTarget is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtwBefore)
            {
                oldFutureSteps = dtwBefore.FutureSteps;
            }

            // Generic Apply-without-closing support: commits the just-edited Color/Thickness/panel-
            // specific settings and redraws immediately via the same NotifyObjectChanged path
            // DrawingObjectsViewModel.OpenSettingsForModelAsync uses for the Layers Panel entry
            // point. Deliberately does NOT replicate the type-specific recalculation performed below
            // on final "Changed" close (ZigZag threshold recompute, DTW re-search, Kalman recalc) --
            // those only matter for a handful of specialized tools and remain a known limitation of
            // Apply from this canvas-double-click entry point; the common case (Color/Thickness/
            // generic panel settings) now updates live regardless of which entry point opened the dialog.
            var result = await _dialogService.ShowDrawingSettingsDialogAsync(editTarget, onApply: obj =>
            {
                if (viewModel.Candles != null)
                {
                    DeferredComputationRecalculator.TryRecalculate(obj, viewModel.Candles);
                }
                viewModel.ObjectManager.NotifyObjectChanged(obj.Id);
                if (viewModel.DrawingEditCoordinator == null)
                {
                    viewModel.PersistCurrentDrawings();
                }
                viewModel.RequestRender(RenderReason.DataChanged);
            }, coordinator: viewModel.DrawingEditCoordinator, candles: viewModel.Candles);

            if (result == DrawingSettingsResult.Deleted)
            {
                var targetId = editTarget.Id;
                if (viewModel.DrawingEditCoordinator != null)
                {
                    if (viewModel.DrawingEditCoordinator.TryResolveCurrentRuntimeId(editTarget.Id, out var resolvedId))
                    {
                        targetId = resolvedId;
                    }
                    viewModel.DrawingEditCoordinator.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Delete, () =>
                    {
                        viewModel.ObjectManager.RemoveObject(targetId);
                        viewModel.LayerService.RemoveObjectFromLayer(targetId);
                    });
                }
                else
                {
                    viewModel.ObjectManager.RemoveObject(targetId);
                    viewModel.LayerService.RemoveObjectFromLayer(targetId);
                }
            }
            else if (result == DrawingSettingsResult.Changed)
            {
                if (viewModel.Candles != null)
                {
                    DeferredComputationRecalculator.TryRecalculate(editTarget, viewModel.Candles);
                }

                if (editTarget is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtwObj)
                {
                    if (oldFutureSteps.HasValue && oldFutureSteps.Value != dtwObj.FutureSteps)
                    {
                        OnDrawingFinished?.Invoke(dtwObj);
                    }
                }

                viewModel.ObjectManager.NotifyObjectChanged(editTarget.Id);
                viewModel.RequestRender(RenderReason.DataChanged);
            }

            // If coordinator is active, persistence is handled automatically via CommitSucceeded.
            if (viewModel.DrawingEditCoordinator == null)
            {
                viewModel.PersistCurrentDrawings();
            }
            OnObjectEdited?.Invoke();
        });
    }

    private void HandleTextTool(ChartPoint chartPoint, ChartViewModel viewModel)
    {
        int panelIndex = _activePanelIndex;
        _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var textObj = new TextObject(chartPoint, "Text") { PanelIndex = panelIndex };
            var result = await _dialogService.ShowDrawingSettingsDialogAsync(textObj, coordinator: null);
            
            if (result == DrawingSettingsResult.Changed)
            {
                if (viewModel.DrawingEditCoordinator != null)
                {
                    viewModel.DrawingEditCoordinator.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Add, () =>
                    {
                        viewModel.ObjectManager.AddObject(textObj);
                        if (viewModel.LayerService != null)
                        {
                            var targetPanel = panelIndex >= 0
                                ? StockAnalyzer.Core.Models.Drawing.PanelKey.OverlayGroup(panelIndex)
                                : StockAnalyzer.Core.Models.Drawing.PanelKey.Main;
                            viewModel.LayerService.AddObjectToLayer(textObj.Id, targetPanel);
                        }
                        viewModel.ObjectManager.SelectObject(textObj.Id);
                    });
                }
                else
                {
                    viewModel.ObjectManager.AddObject(textObj);
                    if (viewModel.LayerService != null)
                    {
                        var targetPanel = panelIndex >= 0
                            ? StockAnalyzer.Core.Models.Drawing.PanelKey.OverlayGroup(panelIndex)
                            : StockAnalyzer.Core.Models.Drawing.PanelKey.Main;
                        viewModel.LayerService.AddObjectToLayer(textObj.Id, targetPanel);
                    }
                    viewModel.ObjectManager.SelectObject(textObj.Id);
                }
                if (ShouldReturnToPointerAfterFinish()) viewModel.CurrentTool = DrawingTool.Pointer;
            }
        });
    }

    private IChartObject? GetObjectAt(global::Avalonia.Point chartPosition, ICoordinateTransform transform, ChartViewModel viewModel)
    {
        if (LayeredScene != null)
        {
            var panelKey = _activePanelIndex == -1
                ? StockAnalyzer.Core.Models.Drawing.PanelKey.Main
                : StockAnalyzer.Avalonia.Drawing.DrawingPanelResolver.TryResolvePanelKey(_activePanelIndex, viewModel.Indicators, viewModel.IsSubWindowVisible)
                  ?? StockAnalyzer.Core.Models.Drawing.PanelKey.OverlayGroup(_activePanelIndex);

            var hitId = LayeredScene.HitTest(panelKey, chartPosition, transform);
            if (hitId.HasValue)
            {
                return viewModel.ObjectManager.GetObject(hitId.Value);
            }
            return null;
        }

        return viewModel.ObjectManager.GetObjectAt(chartPosition, transform, _activePanelIndex);
    }

    private bool HandleEraserInternal(global::Avalonia.Point chartPosition, ChartViewModel viewModel, ICoordinateTransform transform)
    {
        Guid? targetIdToRemove = null;
        if (LayeredScene != null)
        {
            var panelKey = _activePanelIndex == -1
                ? StockAnalyzer.Core.Models.Drawing.PanelKey.Main
                : StockAnalyzer.Avalonia.Drawing.DrawingPanelResolver.TryResolvePanelKey(_activePanelIndex, viewModel.Indicators, viewModel.IsSubWindowVisible)
                  ?? StockAnalyzer.Core.Models.Drawing.PanelKey.OverlayGroup(_activePanelIndex);

            if (LayeredScene.HandleEraserAt(panelKey, chartPosition, transform, out var targetId, out _))
            {
                targetIdToRemove = targetId;
            }
        }
        else
        {
            var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform, _activePanelIndex);
            if (hitObject != null && !viewModel.ObjectManager.IsLocked(hitObject.Id))
            {
                targetIdToRemove = hitObject.Id;
            }
        }

        if (targetIdToRemove.HasValue)
        {
            var id = targetIdToRemove.Value;
            if (viewModel.DrawingEditCoordinator != null)
            {
                viewModel.DrawingEditCoordinator.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Delete, () =>
                {
                    viewModel.ObjectManager.RemoveObject(id);
                    viewModel.LayerService.RemoveObjectFromLayer(id);
                });
            }
            else
            {
                viewModel.ObjectManager.RemoveObject(id);
                viewModel.LayerService.RemoveObjectFromLayer(id);
            }
            return true;
        }

        return false;
    }

    private void HandleEraser(global::Avalonia.Point chartPosition, ChartViewModel viewModel, ICoordinateTransform transform)
    {
        HandleEraserInternal(chartPosition, viewModel, transform);
        IsEraserActive = true;
    }

    /// <summary>
    /// Captures the reference state for a whole-object (Move) drag: a snapshot of the
    /// object's points at this exact moment, and a zeroed "already applied" delta.
    /// See the anchor-based displacement comment in <see cref="HandleObjectDrag"/> for why.
    /// </summary>
    private bool BeginMoveDragAnchor(IChartObject obj, ChartViewModel? viewModel = null)
    {
        if (obj is StockAnalyzer.Avalonia.Drawing.FreehandObject)
        {
            return false;
        }

        if (!StartDragTransaction(viewModel, isHandleDrag: false))
        {
            return false;
        }
        _moveDragAnchorPoints = new List<ChartPoint>(obj.Points);
        _moveDragAppliedTimeDelta = TimeSpan.Zero;
        _moveDragAppliedPriceDelta = 0m;
        BeginLinkedMove(obj, viewModel?.ObjectManager ?? _activeObjectManager);
        return true;
    }

    private void ClearLinkedMoveState()
    {
        _linkedMoveMembers = null;
        _linkedMoveAnchorPoints = null;
        _linkedSmartGuideTargets = null;
        _linkedAppliedTimeDelta = TimeSpan.Zero;
        _linkedAppliedPriceDelta = 0m;
    }

    /// <summary>
    /// Resolves the link-group members that must follow <paramref name="dragged"/> and snapshots their start points.
    /// Leaves the linked state cleared (=> behavior identical to an unlinked drag) when the object is not linked.
    /// </summary>
    private void BeginLinkedMove(IChartObject dragged, ChartObjectManager? manager)
    {
        ClearLinkedMoveState();
        if (manager == null || !manager.TryGetLinkedMembers(dragged.Id, out var memberIds)) return;

        var members = new List<IChartObject>(memberIds.Count);
        for (int i = 0; i < memberIds.Count; i++)
        {
            if (memberIds[i] == dragged.Id) continue;
            var member = manager.GetObject(memberIds[i]);
            if (member != null && DrawingLinkEligibility.IsLinkableType(member)) members.Add(member);
        }
        if (members.Count == 0) return;

        _linkedMoveMembers = members.ToArray();
        _linkedMoveAnchorPoints = new List<ChartPoint>[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            _linkedMoveAnchorPoints[i] = new List<ChartPoint>(members[i].Points);
        }

        // Members move together with the dragged object, so they must not act as snap targets for it.
        var targets = new List<IChartObject>(manager.Objects.Count);
        foreach (var candidate in manager.Objects)
        {
            if (Array.IndexOf(_linkedMoveMembers, candidate) < 0) targets.Add(candidate);
        }
        _linkedSmartGuideTargets = targets;
    }

    /// <summary>
    /// Applies the dragged object's displacement to every linked member for the current frame.
    /// Members receive the same (time, price) displacement the dragged object experienced; a bar-locked
    /// dragged object contributes no price displacement. Bar-locked members translate by whole bars instead.
    /// </summary>
    private void ApplyLinkedMoveStep(
        bool draggedMovedByBars,
        TimeSpan totalTimeDelta,
        decimal totalPriceDelta,
        DateTime anchorTime,
        DateTime targetTime,
        IReadOnlyList<CoreCandleData>? fullHistoryCandles,
        ChartDataSnapshot snapshot)
    {
        var members = _linkedMoveMembers;
        var anchors = _linkedMoveAnchorPoints;
        if (members == null || anchors == null) return;

        var memberPriceDelta = draggedMovedByBars ? 0m : totalPriceDelta;
        var stepTime = totalTimeDelta - _linkedAppliedTimeDelta;
        var stepPrice = memberPriceDelta - _linkedAppliedPriceDelta;
        bool hasHistory = fullHistoryCandles != null && fullHistoryCandles.Count > 0;

        for (int i = 0; i < members.Length; i++)
        {
            var member = members[i];
            if (hasHistory && BarLockedTranslation.IsBarLocked(member) && anchors[i].Count >= 2)
            {
                member.Points[0] = anchors[i][0];
                member.Points[1] = anchors[i][1];
                BarLockedTranslation.TryTranslate(member, anchorTime, targetTime, fullHistoryCandles);
            }
            else
            {
                member.Translate(stepTime, stepPrice);
            }

            DrawingRecalculation.AfterMoveFrame(member, snapshot.Candles);
        }

        _linkedAppliedTimeDelta = totalTimeDelta;
        _linkedAppliedPriceDelta = memberPriceDelta;
    }

    private bool HandlePointerTool(global::Avalonia.Point point, global::Avalonia.Point chartPosition, ChartViewModel viewModel, ICoordinateTransform transform)
    {
        var selectedObject = viewModel.ObjectManager.SelectedObject;
        if (selectedObject != null)
        {
            // Freehand objects do not support individual vertex handle dragging; positions are fixed in chart coordinates.
            if (selectedObject is StockAnalyzer.Avalonia.Drawing.FreehandObject)
            {
                // Bypass handle hit testing so object selection or deselection works without initiating drag
            }
            // 1. Check Handle Hit Test on Selected Object
            else if (selectedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject)
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
                            return BeginHandleDrag(selectedObject, i, point, viewModel);
                        }
                    }
                }
            }
            else if (selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.ArimaProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HmmProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.PearsonProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.FrechetProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaMultiComponentObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaSupportResistanceObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaAnomalyHighlightObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoTimeCycleObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughAutoLinesObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughParabolicCurveObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughKeyLevelsObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughResonantFanObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughMagneticLineObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.FixedRangeVolumeProfileObject ||
                     selectedObject is StockAnalyzer.Avalonia.Drawing.TimeAtPriceObject)
            {
                if (selectedObject.Points.Count >= 2)
                {
                    var p0 = transform.ChartToScreen(selectedObject.Points[0]);
                    var p1 = transform.ChartToScreen(selectedObject.Points[1]);
                    
                    if (Math.Abs(p0.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        return BeginHandleDrag(selectedObject, 0, point, viewModel);
                    }
                    if (Math.Abs(p1.X - chartPosition.X) <= ChartConstants.HandleClickToleranceScreenPx)
                    {
                        return BeginHandleDrag(selectedObject, 1, point, viewModel);
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
                        return BeginHandleDrag(selectedObject, i, point, viewModel);
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
                        return BeginHandleDrag(selectedObject, i, point, viewModel);
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
                        return BeginHandleDrag(selectedObject, i, point, viewModel);
                    }
                }
            }

            // 2. Check if we hit an object to Select it
            var hitObject = GetObjectAt(chartPosition, transform, viewModel);
            if (hitObject != null)
            {
                if (hitObject is StockAnalyzer.Avalonia.Drawing.FreehandObject)
                {
                    if (hitObject.Id != selectedObject.Id)
                    {
                        viewModel.ObjectManager.SelectObject(hitObject.Id);
                    }
                    return true;
                }

                if (!BeginMoveDragAnchor(hitObject, viewModel))
                {
                    return false;
                }
                if (hitObject.Id != selectedObject.Id)
                {
                    viewModel.ObjectManager.SelectObject(hitObject.Id);
                }
                DraggedObject = hitObject;
                LastDragPoint = point;
                DraggedHandleIndex = -1;
                return true;
            }
        }
        else
        {
             // No selection, try to select
            var hitObject = GetObjectAt(chartPosition, transform, viewModel);
            if (hitObject != null)
            {
                if (hitObject is StockAnalyzer.Avalonia.Drawing.FreehandObject)
                {
                    viewModel.ObjectManager.SelectObject(hitObject.Id);
                    return true;
                }

                if (!BeginMoveDragAnchor(hitObject, viewModel))
                {
                    return false;
                }
                viewModel.ObjectManager.SelectObject(hitObject.Id);
                DraggedObject = hitObject;
                LastDragPoint = point;
                DraggedHandleIndex = -1;
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
    /// Candle OHLC magnet snap is also skipped inside an indicator sub-panel
    /// (<see cref="_activePanelIndex"/> &gt;= 0): the candle price scale does not apply to an
    /// indicator axis (e.g. RSI 0..100), so the raw transformed point is used there.
    /// </summary>
    private ChartPoint GetMagnetSnappedOrRawPoint(global::Avalonia.Point chartRelativePos, ChartDataSnapshot snapshot, ICoordinateTransform transform, bool isAltBypass)
        => ResolveDrawingChartPoint(chartRelativePos, snapshot.Candles, transform, bypassSnap: isAltBypass);

    /// <summary>
    /// Resolves the chart point for a drawing gesture. Applies candle OHLC magnet snap only on the
    /// main chart with snapping enabled; inside a sub-panel (or when <paramref name="bypassSnap"/>)
    /// returns the raw transformed point and clears <see cref="LastSnapChartPoint"/>.
    /// </summary>
    private ChartPoint ResolveDrawingChartPoint(
        global::Avalonia.Point pointerPos,
        IReadOnlyList<CoreCandleData> candles,
        ICoordinateTransform transform,
        bool bypassSnap)
    {
        if (bypassSnap || _activePanelIndex >= 0)
        {
            LastSnapChartPoint = null;
            return transform.ScreenToChart(pointerPos);
        }

        var snapResult = _magnetSnapService.GetMagnetSnap(pointerPos, candles, transform);
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
        KeyModifiers modifiers = KeyModifiers.None,
        IReadOnlyList<CoreCandleData>? allCandles = null)
    {
        // F11 fix: snapshot.Candles is only the currently-visible slice (thread-safe DTO for the
        // render pass), not the full history. A range-locked FixedRangeVolumeProfileObject drag
        // must resolve bar indices against the full, stable history so that panning the visible
        // window mid-drag (or an anchor sitting off-screen) never changes the resulting bar count.
        // Falls back to snapshot.Candles when the caller has no full history available (e.g. a
        // headless/isolated caller), matching the pre-fix behavior rather than failing outright.
        // See sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F11.
        var fullHistoryCandles = allCandles != null && allCandles.Count > 0 ? allCandles : snapshot.Candles;
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
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.ArimaProjectionObject arimaDrag)
            {
                if (arimaDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    arimaDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, arimaDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject fftDrag)
            {
                if (fftDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    fftDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, fftDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HmmProjectionObject hmmDrag)
            {
                if (hmmDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    hmmDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, hmmDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.PearsonProjectionObject pearsonDrag)
            {
                if (pearsonDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    pearsonDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, pearsonDrag.Points[DraggedHandleIndex].Price);
                    pearsonDrag.IsUnmatched = false;
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.FrechetProjectionObject frechetDrag)
            {
                if (frechetDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    frechetDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, frechetDrag.Points[DraggedHandleIndex].Price);
                    frechetDrag.IsUnmatched = false;
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject ssaDrag)
            {
                if (ssaDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    ssaDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, ssaDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoTimeCycleObject autoCycleDrag)
            {
                if (autoCycleDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    autoCycleDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, autoCycleDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaMultiComponentObject ssaMultiDrag)
            {
                if (ssaMultiDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    ssaMultiDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, ssaMultiDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaSupportResistanceObject ssaSnrDrag)
            {
                if (ssaSnrDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    ssaSnrDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, ssaSnrDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaAnomalyHighlightObject ssaAnomalyDrag)
            {
                if (ssaAnomalyDrag.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    ssaAnomalyDrag.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, ssaAnomalyDrag.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject || 
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject ||
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughAutoLinesObject ||
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughParabolicCurveObject ||
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughKeyLevelsObject ||
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughResonantFanObject ||
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HoughMagneticLineObject)
            {
                if (DraggedObject.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    DraggedObject.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, DraggedObject.Points[DraggedHandleIndex].Price);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.FixedRangeVolumeProfileObject frvp)
            {
                if (frvp.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    if (frvp.LockRange && fullHistoryCandles != null && fullHistoryCandles.Count > 0)
                    {
                        if (_moveDragAnchorPoints == null || _moveDragAnchorPoints.Count < 2)
                        {
                            _moveDragAnchorPoints = new List<ChartPoint>(frvp.Points);
                        }
                        var anchorTime = _moveDragAnchorPoints[DraggedHandleIndex].Time;
                        frvp.Points[0] = _moveDragAnchorPoints[0];
                        frvp.Points[1] = _moveDragAnchorPoints[1];
                        BarLockedTranslation.TryTranslate(frvp, anchorTime, chartPoint.Time, fullHistoryCandles);
                    }
                    else
                    {
                        frvp.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, frvp.Points[DraggedHandleIndex].Price);
                    }
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.TimeAtPriceObject tap)
            {
                if (tap.Points.Count >= 2 && DraggedHandleIndex >= 0 && DraggedHandleIndex < 2)
                {
                    if (tap.LockRange && fullHistoryCandles != null && fullHistoryCandles.Count > 0)
                    {
                        if (_moveDragAnchorPoints == null || _moveDragAnchorPoints.Count < 2)
                        {
                            _moveDragAnchorPoints = new List<ChartPoint>(tap.Points);
                        }
                        var anchorTime = _moveDragAnchorPoints[DraggedHandleIndex].Time;
                        tap.Points[0] = _moveDragAnchorPoints[0];
                        tap.Points[1] = _moveDragAnchorPoints[1];
                        BarLockedTranslation.TryTranslate(tap, anchorTime, chartPoint.Time, fullHistoryCandles);
                    }
                    else
                    {
                        tap.Points[DraggedHandleIndex] = new ChartPoint(chartPoint.Time, tap.Points[DraggedHandleIndex].Price);
                    }
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
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaAnomalyHighlightObject ssaAnomalyDragged)
            {
                ssaAnomalyDragged.Recalculate(snapshot.Candles);
            }
            else if (DraggedObject is not AnchoredVwapObject &&
                     DraggedObject is not FixedRangeVolumeProfileObject &&
                     DraggedObject is not TimeAtPriceObject &&
                     DeferredComputationRecalculator.TryRecalculate(DraggedObject, snapshot.Candles))
            {
            }

            if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtw)
            {
                dtw.ProjectedPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject kalman)
            {
                kalman.ProjectedPath?.Clear();
                kalman.UpperBandPath?.Clear();
                kalman.LowerBandPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.ArimaProjectionObject arima)
            {
                arima.ProjectedPath?.Clear();
                arima.UpperBandPath?.Clear();
                arima.LowerBandPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject fftObj)
            {
                fftObj.ProjectedPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HmmProjectionObject hmmObj)
            {
                hmmObj.ProjectedPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.PearsonProjectionObject pearsonObj)
            {
                pearsonObj.ProjectedPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.FrechetProjectionObject frechetObj)
            {
                frechetObj.ProjectedPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject ssaObj)
            {
                ssaObj.ProjectedPath?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoTimeCycleObject autoCycleObj)
            {
                autoCycleObj.ProjectedBarIndices?.Clear();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaMultiComponentObject ssaMultiObj)
            {
                ssaMultiObj.ClearPaths();
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.SsaSupportResistanceObject ssaSnrObj)
            {
                ssaSnrObj.InvalidateCache();
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
                        _linkedSmartGuideTargets ?? _activeObjectManager.Objects,
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

            bool draggedMovedByBars = false;
            if (BarLockedTranslation.IsBarLocked(dragged) && fullHistoryCandles != null && fullHistoryCandles.Count > 0 && _moveDragAnchorPoints != null && _moveDragAnchorPoints.Count >= 2)
            {
                draggedMovedByBars = true;
                dragged.Points[0] = _moveDragAnchorPoints[0];
                dragged.Points[1] = _moveDragAnchorPoints[1];
                BarLockedTranslation.TryTranslate(dragged, anchorChart.Time, targetChart.Time, fullHistoryCandles);
                _moveDragAppliedTimeDelta = totalTimeDelta;
                _moveDragAppliedPriceDelta = 0m;
            }
            else
            {
                // Translate() is a relative shift (some overrides, e.g. RangeSplineObject,
                // invalidate caches as a side effect that must run on every call), so apply
                // only the incremental step beyond what this drag has already applied.
                var stepTimeDelta = totalTimeDelta - _moveDragAppliedTimeDelta;
                var stepPriceDelta = totalPriceDelta - _moveDragAppliedPriceDelta;
                dragged.Translate(stepTimeDelta, stepPriceDelta);
                _moveDragAppliedTimeDelta = totalTimeDelta;
                _moveDragAppliedPriceDelta = totalPriceDelta;
            }

            if (_linkedMoveMembers != null)
            {
                ApplyLinkedMoveStep(draggedMovedByBars, totalTimeDelta, totalPriceDelta, anchorChart.Time, targetChart.Time, fullHistoryCandles, snapshot);
            }

            DrawingRecalculation.AfterMoveFrame(dragged, snapshot.Candles);

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
        KeyModifiers modifiers = KeyModifiers.None,
        int activePanelIndex = -1)
    {
        _activePanelIndex = activePanelIndex;
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
            if (HandleEraserInternal(chartPosition, viewModel, transform))
            {
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
             if (HandleObjectDrag(point, snapshot, transform, chartMarginTop, chartMarginHorizontal, bounds, modifiers, viewModel.Candles))
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

        // Information Object hover data update (Data tab parity)
        var infoObj = viewModel.ObjectManager.InformationObject;
        if (infoObj != null && infoObj.IsVisible)
        {
            var chartPoint = transform.ScreenToChart(chartPosition);
            if (infoObj.Snapshot == null || chartPoint.Time != _lastInfoHoverTime)
            {
                var newSnapshot = ChartInformationDataProvider.Extract(viewModel, chartPoint.Time, chartPoint.Price);
                if (newSnapshot != null)
                {
                    infoObj.Snapshot = newSnapshot;
                    _lastInfoHoverTime = chartPoint.Time;
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
        KeyModifiers modifiers = KeyModifiers.None,
        int activePanelIndex = -1)
    {
        _activePanelIndex = activePanelIndex;
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
                 FinishDrawing(viewModel.ObjectManager, viewModel.LayerService, viewModel.DrawingEditCoordinator);
                 return true;
             }
        }
        else
        {


             // Show Settings Dialog for Drawing Objects
             var hitObject = GetObjectAt(chartPosition, transform, viewModel);
             if (hitObject != null && viewModel.DialogService != null)
             {
                 var layer = viewModel.LayerService.GetLayerForObject(hitObject.Id);
                 if (layer == null || !layer.Value.IsEditLocked)
                 {
                     HandleEditObject(hitObject, viewModel);
                     return true;
                 }
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
        
        // Apply Magnet Snap for new shapes (skipped inside an indicator sub-panel).
        var chartPoint = ResolveDrawingChartPoint(chartRelativePos, candles, transform, bypassSnap: false);

        // Delegate to active behavior if available (New Architecture)
        if (_activeBehavior != null)
        {
            _activeBehavior.UpdatePoint(CurrentDrawingObject, DrawingStep, chartPoint, candles);

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
        ClearLinkedMoveState();
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
        bool isShiftCheck = false,
        ChartViewModel? viewModel = null)
    {
        if (objectManager == null) return false;

        // F09: Active Layer State Validation for New Drawings
        if (!IsDrawingNewShape && viewModel != null)
        {
            StockAnalyzer.Core.Models.Drawing.PanelKey panelKey;
            if (_activePanelIndex == -1)
            {
                panelKey = StockAnalyzer.Core.Models.Drawing.PanelKey.Main;
            }
            else
            {
                panelKey = StockAnalyzer.Avalonia.Drawing.DrawingPanelResolver.TryResolvePanelKey(_activePanelIndex, viewModel.Indicators, viewModel.IsSubWindowVisible)
                    ?? StockAnalyzer.Core.Models.Drawing.PanelKey.OverlayGroup(_activePanelIndex);
            }

            var activeLayerId = viewModel.LayerService.GetActiveLayerId(panelKey);
            var activeLayer = activeLayerId.HasValue ? viewModel.LayerService.GetLayer(activeLayerId.Value) : null;
            if (activeLayer == null)
            {
                activeLayer = viewModel.LayerService.EnsureDefaultLayer(panelKey);
            }

            if (!activeLayer.Value.IsVisible || activeLayer.Value.IsEditLocked)
            {
                // Active layer is hidden or locked: reject drawing creation immediately
                return false;
            }

            _currentDrawingPanel = panelKey;
            _currentDrawingLayerId = activeLayer.Value.LayerId;
        }

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
            return HandleBarPatternStep(behavior, chartPoint, objectManager, candles, viewModel?.LayerService, viewModel?.DrawingEditCoordinator);
        }

        // --- Click-to-place tools (1 step): create and immediately finish ---
        if (behavior.RequiredSteps == 1)
        {
            var obj = behavior.CreateObject(chartPoint, candles);

            // Enforce single instance for macro tools like Geometric Pattern
            if (behavior is StockAnalyzer.Avalonia.Drawing.Behaviors.GeometricPatternBehavior)
            {
                var existing = objectManager.Objects
                    .Where(o => o is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject)
                    .ToList();

                if (viewModel?.DrawingEditCoordinator != null)
                {
                    var panel = _activePanelIndex == -1 ? StockAnalyzer.Core.Models.Drawing.PanelKey.Main : StockAnalyzer.Core.Models.Drawing.PanelKey.OverlayGroup(_activePanelIndex);
                    var layerId = viewModel.LayerService?.GetActiveLayerId(panel);

                    var result = viewModel.DrawingEditCoordinator.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Add, () =>
                    {
                        foreach (var oldObj in existing)
                        {
                            objectManager.RemoveObject(oldObj.Id);
                            viewModel?.LayerService?.RemoveObjectFromLayer(oldObj.Id);
                        }
                        objectManager.AddObject(obj);
                        if (viewModel?.LayerService != null)
                        {
                            viewModel.LayerService.AddObjectToLayer(obj.Id, panel, layerId);
                        }
                        objectManager.SelectObject(obj.Id);
                    });

                    if (result.IsSuccess)
                    {
                        OnDrawingFinished?.Invoke(obj);
                    }
                    return true;
                }
                else
                {
                    foreach (var oldObj in existing)
                    {
                        objectManager.RemoveObject(oldObj.Id);
                        viewModel?.LayerService?.RemoveObjectFromLayer(oldObj.Id);
                    }
                }
            }

            StartDrawingInternal(obj, behavior);
            FinishDrawing(objectManager, viewModel?.LayerService, viewModel?.DrawingEditCoordinator);
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
                    FinishDrawing(objectManager, viewModel?.LayerService, viewModel?.DrawingEditCoordinator);
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
                FinishDrawing(objectManager, viewModel?.LayerService, viewModel?.DrawingEditCoordinator);
            }
            else if (behavior.FinishesOnRelease)
            {
                // This shouldn't be reached on click for DragToDraw (it finishes on release),
                // but if it is, finish it.
                FinishDrawing(objectManager, viewModel?.LayerService, viewModel?.DrawingEditCoordinator);
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
        IEnumerable<CoreCandleData>? candles,
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingLayerService? layerService = null,
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator? coordinator = null)
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
            FinishDrawing(objectManager, layerService, coordinator);
        }
        return true;
    }

    /// <summary>
    /// Starts a new drawing operation (Internal helper).
    /// </summary>
    private void StartDrawingInternal(IChartObject drawingObject, IDrawingToolBehavior behavior)
    {
        drawingObject.PanelIndex = _activePanelIndex;
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
    public void FinishDrawing(
        ChartObjectManager objectManager, 
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingLayerService? layerService = null,
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator? coordinator = null)
    {
        if (CurrentDrawingObject != null)
        {
            var obj = CurrentDrawingObject;
            var panel = _currentDrawingPanel;
            var layerId = _currentDrawingLayerId;

            if (coordinator != null)
            {
                var result = coordinator.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Add, () =>
                {
                    objectManager.AddObject(obj);
                    if (layerService != null && panel.HasValue)
                    {
                        layerService.AddObjectToLayer(obj.Id, panel.Value, layerId);
                    }
                    objectManager.SelectObject(obj.Id);
                });

                if (result.IsSuccess)
                {
                    OnDrawingFinished?.Invoke(obj);
                }
            }
            else
            {
                objectManager.AddObject(obj);
                if (layerService != null && panel.HasValue)
                {
                    layerService.AddObjectToLayer(obj.Id, panel.Value, layerId);
                }
                objectManager.SelectObject(obj.Id);
                OnDrawingFinished?.Invoke(obj);
            }
        }

        CurrentDrawingObject = null;
        IsDrawingNewShape = false;
        DrawingStep = 0;
        LastSnapChartPoint = null;
        _activeBehavior = null;
        _currentDrawingPanel = null;
        _currentDrawingLayerId = null;
    }

    /// <summary>
    /// Handles a cancellation request (e.g., Ctrl key press).
    /// For variable-step tools (RequiredSteps == 0), this finishes the shape if valid.
    /// For fixed-step tools, this cancels the operation.
    /// </summary>
    public bool HandleCancelRequest(
        ChartObjectManager objectManager,
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingLayerService? layerService = null,
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator? coordinator = null)
    {
        if (!IsDrawingNewShape || CurrentDrawingObject == null) return false;

        if (_activeBehavior?.RequiredSteps == 0)
        {
            if (CurrentDrawingObject.Points.Count > 1)
            {
                CurrentDrawingObject.Points.RemoveAt(CurrentDrawingObject.Points.Count - 1);
            }
            FinishDrawing(objectManager, layerService, coordinator);
            return true;
        }

        CancelDrawing();
        return true;
    }

    /// <summary>
    /// Cancels the current drawing operation.
    /// </summary>
    public void CancelDrawing()
    {
        CancelActiveDragTransaction();
        CurrentDrawingObject = null;
        IsDrawingNewShape = false;
        DrawingStep = 0;
        _activeSmartGuideLines.Clear();
        LastSnapChartPoint = null;
        _activeBehavior = null;
        _currentDrawingPanel = null;
        _currentDrawingLayerId = null;
    }

    /// <summary>
    /// Handles key down events for Undo (Ctrl+Z) and Redo (Ctrl+Y / Ctrl+Shift+Z),
    /// while preserving native editing inside text input controls.
    /// </summary>
    public bool HandleKeyDown(global::Avalonia.Input.KeyEventArgs e, ChartViewModel? viewModel, IDrawingEditCoordinator? coordinator = null)
    {
        if (e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Control) && !e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Alt))
        {
            bool isInsideTextBox = e.Source is global::Avalonia.Controls.TextBox ||
                (e.Source is global::Avalonia.Visual visual && visual.FindAncestorOfType<global::Avalonia.Controls.TextBox>(includeSelf: true) != null);
            if (!isInsideTextBox)
            {
                var activeCoord = viewModel?.DrawingEditCoordinator ?? coordinator;
                if (e.Key == global::Avalonia.Input.Key.Z)
                {
                    if (e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Shift))
                    {
                        // Ctrl+Shift+Z = Redo
                        if (activeCoord != null && activeCoord.CanRedo)
                        {
                            var res = activeCoord.Redo();
                            if (res.IsSuccess)
                            {
                                viewModel?.RequestRender(RenderReason.DataChanged);
                                e.Handled = true;
                                return true;
                            }
                        }
                    }
                    else
                    {
                        // Ctrl+Z = Undo
                        if (activeCoord != null && activeCoord.CanUndo)
                        {
                            var res = activeCoord.Undo();
                            if (res.IsSuccess)
                            {
                                viewModel?.RequestRender(RenderReason.DataChanged);
                                e.Handled = true;
                                return true;
                            }
                        }
                    }
                }
                else if (e.Key == global::Avalonia.Input.Key.Y)
                {
                    // Ctrl+Y = Redo
                    if (activeCoord != null && activeCoord.CanRedo)
                    {
                        var res = activeCoord.Redo();
                        if (res.IsSuccess)
                        {
                            viewModel?.RequestRender(RenderReason.DataChanged);
                            e.Handled = true;
                            return true;
                        }
                    }
                }
            }
        }
        return false;
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

         // Magnet Snap (skipped inside an indicator sub-panel).
         var chartPoint = ResolveDrawingChartPoint(mouseScreenPoint, candlesList, transform, bypassSnap: false);

         // Delegate to active behavior for tool-specific point update
         if (_activeBehavior != null)
         {
             _activeBehavior.UpdatePoint(CurrentDrawingObject, DrawingStep, chartPoint, candlesList);
         }
         else
         {
             // Fallback: update last point directly (legacy path)
             UpdateDrawingPoint(chartPoint, candlesList);
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
            else if (CurrentDrawingObject is TimeAtPriceObject tap && coreCandles != null)
            {
                tap.Recalculate(coreCandles);
            }
        }
    }

    #endregion
}
