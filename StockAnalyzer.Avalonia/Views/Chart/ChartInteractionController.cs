using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Avalonia;
using Avalonia.Media;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.Confluence;

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

    #endregion

    private readonly Services.Drawing.IMagnetSnapService _magnetSnapService;
    private readonly IDialogService _dialogService;

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

    private readonly Common.CrosshairPositionData _crosshairData = new();
    private readonly Common.CrosshairPositionChangedMessage _crosshairMessage;

    public ChartInteractionController(
        Services.Drawing.IMagnetSnapService magnetSnapService,
        IDialogService dialogService)
    {
        _magnetSnapService = magnetSnapService;
        _dialogService = dialogService;
        _crosshairMessage = new Common.CrosshairPositionChangedMessage(_crosshairData);
    }

    /// <summary>
    /// Default constructor for backwards compatibility or design time (if needed).
    /// </summary>
    public ChartInteractionController() : this(
        new Services.Drawing.MagnetSnapService(),
        new DialogService()) // Default implementation
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
                viewModel.ObjectManager.RemoveObject(hitObject.Id);
                return true;
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
        }

        var candleSource = viewModel.Candles?.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));

        // 1.5. Check if we hit an existing handle (to allow moving points even if the tool is selected)
        if (!IsDrawingNewShape && viewModel.ObjectManager.SelectedObject != null)
        {
            var selectedObject = viewModel.ObjectManager.SelectedObject;
            
            // Special handle mapping for objects defined by 2 points but rendered with 4 corner handles
            if (selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject || 
                selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject ||
                selectedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject ||
                selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject)
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
                        if (Math.Abs(handles[i].X - chartPosition.X) <= 10 && 
                            Math.Abs(handles[i].Y - chartPosition.Y) <= 10)
                        {
                            DraggedObject = selectedObject;
                            DraggedHandleIndex = i;
                            LastDragPoint = point;
                            return true;
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < selectedObject.Points.Count; i++)
                {
                    var handleScreenPoint = coordinateTransform.ChartToScreen(selectedObject.Points[i]);
                    if (Math.Abs(handleScreenPoint.X - chartPosition.X) <= 10 && 
                        Math.Abs(handleScreenPoint.Y - chartPosition.Y) <= 10)
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
                
                if (justFinishedMultiStep || justPlacedSingleStep)
                {
                    viewModel.CurrentTool = DrawingTool.Pointer;
                }
            }

            if (effectiveTool == DrawingTool.LongPosition || effectiveTool == DrawingTool.ShortPosition)
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

        if (IsDrawingRuler)
        {
            IsDrawingRuler = false;
            handled = true;
        }

        if (DraggedObject != null)
        {
            var objToNotify = DraggedObject;

            // Recalculate harmonic patterns now that the drag has finished.
            // This is deferred from HandleObjectDrag to prevent large patterns from
            // disappearing due to per-frame re-detection with changing candle subsets.
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

            DraggedObject = null;
            DraggedHandleIndex = -1;

            OnObjectDragged?.Invoke(objToNotify);

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
                if (viewModel != null) viewModel.CurrentTool = DrawingTool.Pointer;
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
            }
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
                viewModel.CurrentTool = DrawingTool.Pointer;
            }
        });
    }

    private void HandleEraser(global::Avalonia.Point chartPosition, ChartViewModel viewModel, ICoordinateTransform transform)
    {
        var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform);
        if (hitObject != null)
        {
            viewModel.ObjectManager.RemoveObject(hitObject.Id);
        }
        
        IsDrawingNewShape = true; // Re-use this flag to indicate "Eraser Active"
    }

    private bool HandlePointerTool(global::Avalonia.Point point, global::Avalonia.Point chartPosition, ChartViewModel viewModel, ICoordinateTransform transform)
    {
        var selectedObject = viewModel.ObjectManager.SelectedObject;
        if (selectedObject != null)
        {
            // 1. Check Handle Hit Test on Selected Object
            if (selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject || 
                selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject ||
                selectedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject ||
                selectedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject)
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
                        if (Math.Abs(handles[i].X - chartPosition.X) <= 10 && 
                            Math.Abs(handles[i].Y - chartPosition.Y) <= 10)
                        {
                            DraggedObject = selectedObject;
                            DraggedHandleIndex = i;
                            LastDragPoint = point;
                            return true;
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < selectedObject.Points.Count; i++)
                {
                    var handleScreenPoint = transform.ChartToScreen(selectedObject.Points[i]);
                    
                    if (Math.Abs(handleScreenPoint.X - chartPosition.X) <= 10 && 
                        Math.Abs(handleScreenPoint.Y - chartPosition.Y) <= 10)
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
                return true;
            }
        }

        return false; // Allow Pan
    }


    /// <summary>
    /// Handles dragging logic for objects (move) or handles (resize).
    /// </summary>
    public bool HandleObjectDrag(
        global::Avalonia.Point mousePos,
        ChartDataSnapshot snapshot,
        ICoordinateTransform transform,
        double chartMarginTop,
        double chartMarginHorizontal)
    {
        if (DraggedObject == null) return false;

        var chartRelativePos = new global::Avalonia.Point(mousePos.X - chartMarginHorizontal, mousePos.Y - chartMarginTop);

        bool isHandleDrag = DraggedHandleIndex >= 0 && 
                            (DraggedHandleIndex < DraggedObject.Points.Count || 
                             (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject && DraggedHandleIndex < 4) ||
                             (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject && DraggedHandleIndex < 4) ||
                             (DraggedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject && DraggedHandleIndex < 4) ||
                             (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject && DraggedHandleIndex < 4));

        if (isHandleDrag)
        {
            // Handle Dragging (Resize) - Apply Magnet Snap
            var snapResult = _magnetSnapService.GetMagnetSnap(chartRelativePos, snapshot.Candles, transform);

            var chartPoint = snapResult.SnappedChartPoint;
            LastSnapChartPoint = snapResult.IsSnapped ? snapResult.SnappedChartPoint : null;

            if (DraggedObject is LongShortPositionObject ls && ls.Points.Count >= 3)
            {
                decimal entryP = (DraggedHandleIndex == 0) ? chartPoint.Price : ls.Points[0].Price;
                if (DraggedHandleIndex == 0) // Entry
                {
                    var timeDelta = chartPoint.Time - ls.Points[0].Time;
                    ls.Points[0] = chartPoint;
                    ls.Points[1] = new ChartPoint(ls.Points[1].Time.Add(timeDelta), ls.Points[1].Price);
                    ls.Points[2] = new ChartPoint(ls.Points[2].Time.Add(timeDelta), ls.Points[2].Price);
                }
                else if (DraggedHandleIndex == 1) // Stop
                {
                    decimal newStop = chartPoint.Price;
                    if (ls.IsLong && newStop >= entryP) newStop = entryP - 0.0001m;
                    if (!ls.IsLong && newStop <= entryP) newStop = entryP + 0.0001m;
                    ls.Points[1] = new ChartPoint(ls.Points[0].Time, newStop);
                }
                else if (DraggedHandleIndex == 2) // Target
                {
                    decimal newTarget = chartPoint.Price;
                    if (ls.IsLong && newTarget <= entryP) newTarget = entryP + 0.0001m;
                    if (!ls.IsLong && newTarget >= entryP) newTarget = entryP - 0.0001m;
                    ls.Points[2] = new ChartPoint(ls.Points[0].Time, newTarget);
                }
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geom && DraggedHandleIndex == 0)
            {
                var prevPoint = geom.Points[0];
                var timeDelta = chartPoint.Time - prevPoint.Time;
                var priceDelta = chartPoint.Price - prevPoint.Price;
                geom.Translate(timeDelta, priceDelta);
                
                var coreCandles = snapshot.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                geom.Recalculate(coreCandles);
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject || 
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject ||
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.RectangleObject ||
                     DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject)
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
                    
                    // Recalculation deferred to HandlePointerReleased to prevent
                    // large patterns from disappearing during drag due to per-frame re-detection.
                }
            }
            else
            {
                DraggedObject.Points[DraggedHandleIndex] = chartPoint;
            }

            // Recalculate specialized objects
            if (DraggedObject is RegressionTrendObject regObj)
            {
                var coreCandles = snapshot.Candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                regObj.Recalculate(coreCandles);
            }
            else if (DraggedObject is FixedRangeVolumeProfileObject frvp)
            {
                var coreCandles = snapshot.Candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                frvp.Recalculate(coreCandles);
            }
            else if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtw)
            {
                dtw.ProjectedPath?.Clear();
            }

            return true;
        }
        else
        {
            // Object Dragging (Move)
            var prevScreen = LastDragPoint;
            var currScreen = mousePos;

            var prevChart = transform.ScreenToChart(new global::Avalonia.Point(prevScreen.X - chartMarginHorizontal, prevScreen.Y - chartMarginTop));
            var currChart = transform.ScreenToChart(new global::Avalonia.Point(currScreen.X - chartMarginHorizontal, currScreen.Y - chartMarginTop));

            var timeDelta = currChart.Time - prevChart.Time;
            var priceDelta = currChart.Price - prevChart.Price;

            DraggedObject.Translate(timeDelta, priceDelta);
            
            if (DraggedObject is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geom)
            {
                var coreCandles = snapshot.Candles.Select(c => new StockAnalyzer.Core.Models.CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                geom.Recalculate(coreCandles);
            }
            // Harmonic pattern recalculation is deferred to HandlePointerReleased.

            LastDragPoint = mousePos;
            return true;
        }
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
        out bool needsUpdate)
    {
        needsUpdate = false;
        if (viewModel == null) return false;

        // Ruler Update
        if (IsDrawingRuler)
        {
            RulerRenderer.EndPoint = point;
            needsUpdate = true;
            return true;
        }

        // Eraser Drag Logic
        if (IsDrawingNewShape && viewModel.CurrentTool == DrawingTool.Eraser)
        {
            var hitObject = viewModel.ObjectManager.GetObjectAt(chartPosition, transform);
            if (hitObject != null)
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
             if (HandleObjectDrag(point, snapshot, transform, chartMarginTop, chartMarginHorizontal))
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
        ChartDataSnapshot snapshot)
    {
        if (viewModel == null) return false;

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
                         if (Math.Abs(h.X - chartPosition.X) <= 10 && Math.Abs(h.Y - chartPosition.Y) <= 10)
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
                         if (Math.Abs(handleScreenPoint.X - chartPosition.X) <= 10 && Math.Abs(handleScreenPoint.Y - chartPosition.Y) <= 10)
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
            
            // Recalculate specialized objects (Legacy fallback, ideally moved to Behavior)
            if (CurrentDrawingObject is RegressionTrendObject reg)
            {
                 var coreCandles = candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                 reg.Recalculate(coreCandles);
            }
             else if (CurrentDrawingObject is FixedRangeVolumeProfileObject frvp)
            {
                 var coreCandles = candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                 frvp.Recalculate(coreCandles);
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

        // --- Variable-point tools (Polyline/ElliottWave): add points dynamically ---
        if (behavior.RequiredSteps == 0 && IsDrawingNewShape && CurrentDrawingObject is PolylineObject existingPoly)
        {
            existingPoly.AddPoint(chartPoint);
            return true;
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
            else if (CurrentDrawingObject is FixedRangeVolumeProfileObject frvp && coreCandles != null)
            {
                frvp.Recalculate(coreCandles);
            }
        }
    }

    #endregion
}
