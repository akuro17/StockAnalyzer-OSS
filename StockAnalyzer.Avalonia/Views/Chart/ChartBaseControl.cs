using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity; // Added for RoutingStrategies
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CommunityToolkit.Mvvm.Messaging;
using SkiaSharp;
using StockAnalyzer.Avalonia.ViewModels;
using System;
using System.Collections.Specialized;
using System.Linq;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using Avalonia.Reactive;
using Avalonia.ReactiveUI;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using System.Reactive.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System.Collections.Generic;

using StockAnalyzer.Avalonia.Models; // Ensure Models usage
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Utilities;
using ColorHelper = StockAnalyzer.Avalonia.Utilities.ColorHelper;
// using StockAnalyzer.Core.Models; // Removed duplicate
using StockAnalyzer.Core.Models.Analysis;
using System.Diagnostics;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// High-performance chart drawing base control.
/// Uses SkiaSharp and ICustomDrawOperation pattern for
/// efficient rendering of large time-series data.
/// </summary>
public class ChartBaseControl : Control, ICustomHitTest
{
    #region Effective Layout Margins

    protected float EffectiveMarginLeft => ChartTheme.MarginLeft;
    protected float EffectiveMarginRight => (float)ChartMarginRight;
    protected float EffectiveMarginTop => (float)ChartMarginTop;
    protected float EffectiveMarginBottom => (float)ChartMarginBottom;
    protected float EffectiveMarginHorizontal => EffectiveMarginLeft + EffectiveMarginRight;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<ChartViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ChartBaseControl, ChartViewModel?>(nameof(ViewModel));

    public ChartViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// Candle data under mouse cursor. For TwoWay binding with ViewModel.
    /// </summary>
    public static readonly StyledProperty<CoreCandleData?> HoveredCandleProperty =
        AvaloniaProperty.Register<ChartBaseControl, CoreCandleData?>(
            nameof(HoveredCandle),
            defaultBindingMode: BindingMode.TwoWay);

    public CoreCandleData? HoveredCandle
    {
        get => GetValue(HoveredCandleProperty);
        set => SetValue(HoveredCandleProperty, value);
    }

    public static readonly StyledProperty<float> ChartMarginTopProperty =
        AvaloniaProperty.Register<ChartBaseControl, float>(nameof(ChartMarginTop), 20f);

    public float ChartMarginTop
    {
        get => GetValue(ChartMarginTopProperty);
        set => SetValue(ChartMarginTopProperty, value);
    }

    public static readonly StyledProperty<float> ChartMarginBottomProperty =
        AvaloniaProperty.Register<ChartBaseControl, float>(nameof(ChartMarginBottom), 10f);

    public float ChartMarginBottom
    {
        get => GetValue(ChartMarginBottomProperty);
        set => SetValue(ChartMarginBottomProperty, value);
    }

    public static readonly StyledProperty<float> ChartMarginRightProperty =
        AvaloniaProperty.Register<ChartBaseControl, float>(nameof(ChartMarginRight), 50f);

    public float ChartMarginRight
    {
        get => GetValue(ChartMarginRightProperty);
        set => SetValue(ChartMarginRightProperty, value);
    }

    public static readonly StyledProperty<bool> IsSubWindowVisibleProperty =
        AvaloniaProperty.Register<ChartBaseControl, bool>(nameof(IsSubWindowVisible), true);

    public bool IsSubWindowVisible
    {
        get => GetValue(IsSubWindowVisibleProperty);
        set => SetValue(IsSubWindowVisibleProperty, value);
    }

    #endregion

    #region Fields

    /// <summary>
    /// Current snapshot for rendering - Delegated to ChartDataCoordinator (Phase 3.5).
    /// </summary>
    private ChartDataSnapshot _currentSnapshot => _dataCoordinator.CurrentSnapshot;

    /// <summary>
    /// Current mouse position for crosshair.
    /// </summary>
    private global::Avalonia.Point _currentMousePosition;

    /// <summary>
    /// Whether the crosshair is currently visible.
    /// </summary>
    private bool _isCrosshairVisible;

    /// <summary>
    /// Phase 70-12-Fix: Lifecycle flag to suppress all messaging during ViewModel re-attachment.
    /// When true, ALL UpdateSnapshot calls will force suppressMessages=true to prevent
    /// render-pass invalidation caused by cascading PropertyChanged/CollectionChanged events.
    /// </summary>
    private bool _isReattaching;

    /// <summary>
    /// Phase 70-12-Fix: Ensures viewport initialization happens exactly once when layout is ready.
    /// </summary>
    private bool _isViewInitialized = false;

    /// <summary>
    /// True only while <see cref="Render"/> is executing. Its Layout Sync Guard can call
    /// UpdateSnapshot synchronously, which mutates ViewModel properties (e.g. VisibleStartIndex)
    /// and re-enters OnViewModelPropertyChanged. Calling InvalidateVisual from that reentrant
    /// path throws "Visual was invalidated during the render pass", since the frame being
    /// painted right now already reflects the just-applied state.
    /// </summary>
    private bool _isRendering;

    /// <summary>
    /// Phase 70-12-Fix: Strict event-unsubscription tracking to prevent ghost control leaks.
    /// </summary>
    private ChartViewModel? _subscribedViewModel;

    /// <summary>
    /// Manages PointerMoved event subscription.
    /// </summary>
    private IDisposable? _pointerMovedSubscription;
    

    
    // New Architecture
    // _coordinateTransform is now managed by _dataCoordinator

    
    // Drawing State - Delegated to ChartInteractionController (Phase 3.5)
    private StockAnalyzer.Avalonia.Drawing.IChartObject? _currentDrawingObject
    {
        get => _interactionController.CurrentDrawingObject;
        set => _interactionController.CurrentDrawingObject = value;
    }
    private bool _isDrawingNewShape
    {
        get => _interactionController.IsDrawingNewShape;
        set => _interactionController.IsDrawingNewShape = value;
    }
    private StockAnalyzer.Avalonia.Drawing.IChartObject? _draggedObject
    {
        get => _interactionController.DraggedObject;
        set => _interactionController.DraggedObject = value;
    }
    private int _draggedHandleIndex
    {
        get => _interactionController.DraggedHandleIndex;
        set => _interactionController.DraggedHandleIndex = value;
    }
    private int _drawingStep
    {
        get => _interactionController.DrawingStep;
        set => _interactionController.DrawingStep = value;
    }

    // Zoom/Pan State - Delegated to ChartDataCoordinator (Phase 3.5)
    private int _startIndex => _dataCoordinator.StartIndex;
    private int _visibleCandleCount => _dataCoordinator.VisibleCandleCount;


    // ReverseWatch specific data - Delegated to ChartDataCoordinator (Phase 3.5)
    private ReverseWatchCurveData? _reverseWatchData => _dataCoordinator.ReverseWatchData;
    
    // RelativePerformance specific data
    private ComparisonAlignedData? _comparisonData => _dataCoordinator.ComparisonData;

    // Delegated Controllers (Phase 3.5 Decomposition)
    private readonly ChartInteractionController _interactionController;
    private readonly ChartDataCoordinator _dataCoordinator;
    private readonly ChartRendererRegistry _rendererRegistry = new();
    private readonly ChartRenderPipeline _pipeline;

    #endregion



    public ChartBaseControl()
    {
        // Initialize InteractionController with DI service if available (Design-time fallback)
        var services = (Application.Current as App)?.Services;
        var magnetService = services?.GetService<Services.Drawing.IMagnetSnapService>();
        var dialogService = services?.GetService<Services.IDialogService>();
        
        var indicatorRenderer = services?.GetService<IndicatorRenderer>() ?? new IndicatorRenderer();
        _pipeline = new ChartRenderPipeline(indicatorRenderer);
        
        _interactionController = (magnetService != null && dialogService != null)
            ? new ChartInteractionController(magnetService, dialogService) 
            : new ChartInteractionController();

        // Resolve IPythonService and IMessenger for Coordinator (Phase 2 Component Coupling Fix)
        var pythonService = services?.GetService<IPythonService>();
        var messenger = services?.GetService<IMessenger>();
        _dataCoordinator = new ChartDataCoordinator(pythonService, messenger);

        // Repaint when drawing object settings change (color, thickness, etc.)
        _interactionController.OnObjectEdited = () => InvalidateVisual();

        // Forward drawing finished event to ViewModel for tool-specific actions (e.g., DTW search)
        _interactionController.OnDrawingFinished = (obj) => 
        {
            if (ViewModel != null)
            {
                ViewModel.OnDrawingFinished(obj);
            }
        };

        _interactionController.OnObjectDragged = (obj) =>
        {
            if (ViewModel != null && (obj is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject
                || obj is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject))
            {
                // Re-trigger search/detection upon drop
                ViewModel.OnDrawingFinished(obj);
            }
        };

        // Enable SkiaSharp rendering

        ClipToBounds = true;
        // _rulerRenderer is now in InteractionController

        
        // Enable Focus for Key Events
        Focusable = true;

        DoubleTapped += OnDoubleTappedHandler;
    }

    // === Hit Test Fix: Ensure this control ALWAYS receives pointer events ===
    // Control (without Background property) does not hit-test in empty areas.
    // Implement ICustomHitTest to make the entire control area interactive.
    public bool HitTest(global::Avalonia.Point point)
    {
        return true; // Always accept hits within the control bounds
    }

    #region Constructor

    static ChartBaseControl()
    {
        ViewModelProperty.Changed.AddClassHandler<ChartBaseControl>((control, e) =>
        {
            control.OnViewModelChanged(e.OldValue as ChartViewModel, e.NewValue as ChartViewModel);
        });
    }

    #endregion

    #region ViewModel Change Handling

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.Indicators.CollectionChanged -= OnIndicatorsCollectionChanged;
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            
            // Critical Fix: Clear large buffers when VM is detached to prevent memory bloat
            // Especially important since ChartBaseControl may be cached by Avalonia's ViewSwitcher.
            _dataCoordinator.ClearAllBuffers();
            
            _subscribedViewModel = null;
        }
    }

    private void SubscribeToViewModel(ChartViewModel? vm)
    {
        UnsubscribeFromViewModel();
        if (vm != null)
        {
            _subscribedViewModel = vm;
            _subscribedViewModel.Indicators.CollectionChanged += OnIndicatorsCollectionChanged;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelChanged(ChartViewModel? oldVm, ChartViewModel? newVm)
    {
        SubscribeToViewModel(newVm);

        if (newVm != null)
        {
            // Phase 70-12-Fix: Set reattaching flag BEFORE logic execution.
            _isReattaching = true;

            // Reset view on new ViewModel
            if (newVm.ChartType == ChartType.ReverseWatch)
            {
                 _dataCoordinator.CalculateReverseWatch(newVm);
            }

            if (newVm.Candles.Count > 0)
            {
                 _dataCoordinator.ResetView(newVm.Candles.Count);
            }
            else
            {
                 _dataCoordinator.ResetView(0);
            }

            // Sync layout properties
            ChartMarginTop = newVm.ChartMarginTop;
            ChartMarginBottom = newVm.ChartMarginBottom;
            ChartMarginRight = newVm.ChartMarginRight;
            IsSubWindowVisible = newVm.EffectiveIsSubWindowVisible;

            UpdateSnapshot(suppressMessages: true);

            _isReattaching = false;
        }

        InvalidateVisual();
    }



    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Properties handled specially by Control (interaction/view logic)
        if (e.PropertyName == nameof(ChartViewModel.CurrentTool))
        {
            // Tool logic...
            if (ViewModel?.CurrentTool != DrawingTool.Ruler)
            {
                _interactionController.RulerRenderer.IsActive = false;
            }

            if (_isDrawingNewShape && _currentDrawingObject != null)
            {
                 ViewModel?.ObjectManager.RemoveObject(_currentDrawingObject.Id);
                 _currentDrawingObject = null;
                 _isDrawingNewShape = false;
                 _drawingStep = 0;
            }
            _drawingStep = 0;
            SafeInvalidateVisual();
        }

        else if (e.PropertyName == nameof(ChartViewModel.IsLoading)) // Handle IsLoading completion
        {
             if (ViewModel != null && !ViewModel.IsLoading)
             {
                 // Data loading finished -> Force full update (Logic similar to OnCandlesCollectionChanged but for bulk)
                 _dataCoordinator.SyncEffectiveValues(ViewModel); // Ensure ATR/BoxSize based on full data

                 // Reset view or Adjust?
                 // If new data loaded, we generally want to show the latest.
                 // But for P&F, if Box Size changed, we might need full reset.
                 // Let's use ResetView logic if count changed significantly, or Adjust.
                 // For safety with P&F, let's use AdjustForNewCandles with "AtEnd=true" assumption for new load.
                 if (ViewModel.Candles.Count > 0)
                 {
                      _dataCoordinator.ResetView(ViewModel.Candles.Count); // Reset to end on fresh load
                 }

                 SafeUpdateSnapshot();
                 _dataCoordinator.CalculateReverseWatch(ViewModel);
                 _interactionController.ResetSnapState();
                 SafeInvalidateVisual();
             }
        }
        else if (e.PropertyName == nameof(ChartViewModel.IsSubWindowVisible) || e.PropertyName == nameof(ChartViewModel.EffectiveIsSubWindowVisible))
        {
            // Plain StyledProperty mirror: safe to keep in sync even mid-render (no
            // re-entrant coordinator call), only the repaint request needs guarding.
            IsSubWindowVisible = ViewModel?.EffectiveIsSubWindowVisible ?? true;
            SafeInvalidateVisual();
        }
        else if (e.PropertyName == nameof(ChartViewModel.ChartMarginTop))
        {
            ChartMarginTop = ViewModel?.ChartMarginTop ?? 20f;
            SafeInvalidateVisual();
        }
        else if (e.PropertyName == nameof(ChartViewModel.ChartMarginBottom))
        {
            ChartMarginBottom = ViewModel?.ChartMarginBottom ?? 10f;
            SafeInvalidateVisual();
        }
        else if (e.PropertyName == nameof(ChartViewModel.ChartMarginRight))
        {
            ChartMarginRight = ViewModel?.ChartMarginRight ?? 50f;
            SafeInvalidateVisual();
        }
        else if (e.PropertyName == nameof(ChartViewModel.Candles))
        {
            OnCandlesUpdated();
        }
        else
        {
            // Delegate logic to DataCoordinator
            var updateAction = _dataCoordinator.OnViewModelPropertyChanged(e.PropertyName, ViewModel);

            if (updateAction.HasFlag(ChartUpdateAction.UpdateSnapshot))
            {
                SafeUpdateSnapshot();
            }

            // InvalidateVisual is practically always needed if we are here (property changed on VM)
            SafeInvalidateVisual();
        }
    }

    /// <summary>
    /// Guards against re-entrant snapshot recomputation while <see cref="Render"/> is on the
    /// call stack: Render's Layout Sync Guard already calls UpdateSnapshot once for the
    /// current frame, and a nested call here would re-enter ChartDataCoordinator.UpdateSnapshot
    /// while its own fields are still being mutated by the outer call.
    /// </summary>
    private void SafeUpdateSnapshot()
    {
        if (_isRendering) return;
        UpdateSnapshot();
    }

    /// <summary>
    /// Guards against calling InvalidateVisual while <see cref="Render"/> is on the call stack,
    /// which Avalonia disallows ("Visual was invalidated during the render pass"). The frame
    /// currently being painted already reflects the state that triggered this call.
    /// </summary>
    private void SafeInvalidateVisual()
    {
        if (_isRendering) return;
        InvalidateVisual();
    }

    private void OnIndicatorsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateSnapshot();
        InvalidateVisual();
    }

    private void OnCandlesUpdated()
    {
        if (ViewModel?.Candles == null) return;
        
        // Fix: Suppress updates during bulk data loading (prevent flicker/instability)
        if (ViewModel.IsLoading) return;
        
        // Always reset logic or keep pos logic
        bool wasAtEnd = (_startIndex + _visibleCandleCount >= _currentSnapshot.Candles.Count);
        if (_currentSnapshot.Candles.Count == 0 && ViewModel.Candles.Count > 0) wasAtEnd = true;

        if (ViewModel != null)
        {
             // Fix: Ensure Box/Brick sizes are recalculated based on new data (ATR)
             _dataCoordinator.SyncEffectiveValues(ViewModel);
             _dataCoordinator.AdjustForNewCandles(ViewModel.Candles.Count, wasAtEnd);
        }

        SafeUpdateSnapshot();
        _dataCoordinator.CalculateReverseWatch(ViewModel); // Recalc RW
        _interactionController.ResetSnapState(); // Clear snap marker on data reload
        SafeInvalidateVisual();
    }

    protected void UpdateSnapshot(bool suppressMessages = false)
    {
        // Phase 70-12-Fix: Force suppression during re-attachment lifecycle
        bool effectiveSuppression = suppressMessages || _isReattaching;

        if (ViewModel == null) return;
        // Phase 3.5: Delegate to ChartDataCoordinator
        _dataCoordinator.UpdateSnapshot(ViewModel, new Rect(Bounds.Size), effectiveSuppression);

        if (ViewModel != null && ViewModel.ChartType == ChartType.ReverseWatch)
        {
            ViewModel.LatestReverseWatchPoint = _reverseWatchData?.Points?.LastOrDefault();
        }
    }





    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        _isRendering = true;
        try
        {
        if (ViewModel == null) return;

        if (ViewModel.IsDisplayOff)
        {
            var text = "Display is OFF";
            var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
            var formattedText = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 24, Brushes.Gray);
            var textPos = new global::Avalonia.Point((Bounds.Width - formattedText.Width) / 2, (Bounds.Height - formattedText.Height) / 2);
            context.DrawText(formattedText, textPos);
            return;
        }

        if (_currentSnapshot == null || _currentSnapshot.Candles == null || _currentSnapshot.Candles.Count == 0)
        {
            return;
        }

        if (_dataCoordinator.Transform is GenericCoordinateTransform gct)
        {
            // WebAI Fix: Comprehensive Layout Sync Guard
            // Check for Margin, Base Chart Type, and Physical Dimension changes.
            // This ensures that if Bounds change (Startup/Flyout) or Margins change (User Settings),
            // the Transform and Snapshot are immediately synchronized before the DrawCall.
            if (gct.Metadata.IsSubWindowVisible != ViewModel.EffectiveIsSubWindowVisible || 
                gct.Metadata.IsMainWindowVisible != ViewModel.IsMainWindowVisible ||
                gct.Metadata.BaseChartType != ViewModel.ChartType ||
                Math.Abs(gct.ViewportX - EffectiveMarginLeft) > 0.5 ||
                Math.Abs(gct.ViewportY - EffectiveMarginTop) > 0.5 ||
                Math.Abs(gct.CanvasWidth - Bounds.Width) > 0.5 ||
                Math.Abs(gct.CanvasHeight - Bounds.Height) > 0.5)
            {
                UpdateSnapshot(true);
                // Reference 'gct' may now be stale (new transform built in UpdateSnapshot). Re-fetch.
                if (_dataCoordinator.Transform is GenericCoordinateTransform gctNew)
                {
                    gct = gctNew;
                }
            }
        }

        // Phase 5: Get Profile and create Renderer/Layout/Config via Profile
        var profile = ChartTypeProfileRegistry.Get(ViewModel.ChartType);

        var config = profile.CreateRenderConfig(
            ViewModel,
            ViewModel.Candles.LastOrDefault()?.Close ?? 0,
            _startIndex,
            _visibleCandleCount,
            new StockAnalyzer.Core.Models.Point(_currentMousePosition.X, _currentMousePosition.Y),
            _dataCoordinator.Transform,
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0,
            _reverseWatchData,
            _comparisonData);

        // Create Layout Context (Centralized Layout Logic)
        // FIX: Pass actual effective margins (synchronized with ViewModel) to the layout provider.
        // This ensures the RENDERING layout matches the COORDINATOR layout and the TRANSFORM.
        var layout = profile.LayoutProvider.CreateLayout(new Rect(Bounds.Size), ViewModel.Indicators, config.IsSubWindowVisible,
            ViewModel.IsMainWindowVisible,
            EffectiveMarginTop, EffectiveMarginBottom, EffectiveMarginRight);

        // Get Renderer from Registry
        var mainRenderer = _rendererRegistry.GetRenderer(ViewModel.ChartType);

        context.Custom(new ChartDrawOperation(
            new Rect(Bounds.Size), 
            _currentSnapshot, 
            ViewModel.ChartType,
            mainRenderer,
            _pipeline,
            _currentMousePosition, 
            _isCrosshairVisible, 
            new Rect(Bounds.Size), 
            _interactionController.RulerRenderer, 

            ViewModel.ObjectManager,
            _dataCoordinator.Transform,
            config,
            layout, // Passed Layout
            _lastSnapChartPoint.HasValue && _dataCoordinator.Transform != null ? 
                new global::Avalonia.Point(
                    // Pixel Snap X to match CandleStickRenderer (Math.Floor(x) + 0.5f)
                    Math.Floor(_dataCoordinator.Transform.ChartToScreen(_lastSnapChartPoint.Value).X) + 0.5,
                    _dataCoordinator.Transform.ChartToScreen(_lastSnapChartPoint.Value).Y
                ) : null,
            _currentDrawingObject // Pass Current Drawing Object
            ));
        }
        finally
        {
            _isRendering = false;
        }
    }

    #endregion

    #region Pointer Events

    private float GetRenderMarginTop()
    {
        if (ViewModel != null && ViewModel.ChartType.IsCompactType())
        {
            return 0f;
        }
        return EffectiveMarginTop;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        try
        {
            base.OnSizeChanged(e);
            if (ViewModel != null)
            {
                if (!_isViewInitialized && e.NewSize.Width > 0 && ViewModel.Candles.Count > 0)
                {
                    // WebAI Fix: ScreenBounds must be populated via UpdateSnapshot before ResetView works
                    UpdateSnapshot(suppressMessages: true);
                    _dataCoordinator.ResetView(ViewModel.Candles.Count);
                    _isViewInitialized = true;
                    UpdateSnapshot(suppressMessages: true);
                }
                else
                {
                    UpdateSnapshot(suppressMessages: true);
                }
                InvalidateVisual();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChartBaseControl] OnSizeChanged Error: {ex}");
            throw; // Re-throw to ensure we don't hide it, but we logged it!
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        try
        {
            base.OnAttachedToVisualTree(e);
            if (ViewModel != null)
            {
                SubscribeToViewModel(ViewModel);

                UpdateSnapshot(suppressMessages: true);
                InvalidateVisual();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChartBaseControl] OnAttached Error: {ex}");
            throw;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        
        _pointerMovedSubscription?.Dispose();
        _pointerMovedSubscription = null;

        UnsubscribeFromViewModel();
        
        // Phase 70-12-Fix: DO NOT dispose renderers/pipeline here.
        // ViewSwitcher caches this control and re-attaches it on tab switch.
        // Disposing native SkiaSharp resources (SKPaint, etc.) here causes
        // 0xc0000005 Access Violation when Render() accesses them after re-attachment.
        // These resources are owned by the control instance and should live as long as it does.
    }

    // Drawing State - Delegated to ChartInteractionController (continued)
    // private bool _isDrawingRuler - Moved to InteractionController
    // private bool _isDrawingRuler - Moved to InteractionController
    private ChartPoint? _lastSnapChartPoint
    {
        get => _interactionController.LastSnapChartPoint;
        set => _interactionController.LastSnapChartPoint = value;
    }


    // NOTE: GetMagnetSnap logic has been delegated to ChartInteractionController.
    // The inline method has been removed to reduce code duplication.

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChartMarginTopProperty ||
            change.Property == ChartMarginBottomProperty ||
            change.Property == ChartMarginRightProperty ||
            change.Property == IsSubWindowVisibleProperty)
        {
            UpdateSnapshot(suppressMessages: true);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        
        var position = e.GetPosition(this);
        var renderMarginTop = GetRenderMarginTop();
        var chartX = position.X - EffectiveMarginLeft;
        var chartWidth = Bounds.Width - EffectiveMarginLeft - EffectiveMarginRight;

        // Clamp chartX to [0, chartWidth] to prevent interaction in margins
        if (chartX < 0 || chartX > chartWidth)
        {
            HoveredCandle = null;
            _isCrosshairVisible = false;
            _interactionController.ClearCrosshairPosition(ViewModel?.Symbol ?? string.Empty);
            InvalidateVisual();
            return;
        }

        var chartPosition = new global::Avalonia.Point(chartX, position.Y - renderMarginTop); // Adjust for Chart Area
        
        // Delegate to Controller
        if (_interactionController.HandlePointerMoved(
            position,
            chartPosition,
            ViewModel,
            _dataCoordinator.Transform,
            _currentSnapshot,
            new Rect(Bounds.Size),
            EffectiveMarginLeft,
            renderMarginTop,
            out bool needsUpdate))
        {
            if (needsUpdate) InvalidateVisual();
            return;
        }

        // Panning Application (Controller manages state, Control applies)
        if (_interactionController.IsDragging)
        {
            var deltaX = position.X - _interactionController.LastDragPoint.X;
            
            // Delegate pan calculation vs chart width
            chartWidth = Bounds.Width - EffectiveMarginLeft - EffectiveMarginRight;
            int totalCandles = ViewModel?.Candles?.Count ?? 0;
            
            var futureOffset = ViewModel?.RequiredFutureOffset ?? 0;
            if (_dataCoordinator.ApplyPan(deltaX, chartWidth, totalCandles, ViewModel?.ChartType ?? ChartType.Candlestick, futureOffset))
            {
                _interactionController.LastDragPoint = position; // Commit the move
                UpdateSnapshot();
                InvalidateVisual();
            }
        }
        
        _currentMousePosition = position;
        _isCrosshairVisible = true;
        
        // Update Crosshair Data (previously throttled via Rx)
        if (_currentSnapshot.Candles.Count > 0)
        {
            int index = _dataCoordinator.GetCandleIndexAt(chartX, new Rect(Bounds.Size));

            CoreCandleData? hoveredCandle = null;
            if (index >= 0 && index < _currentSnapshot.Candles.Count)
            {
                hoveredCandle = _currentSnapshot.Candles[index];
                
                if (ViewModel?.ChartType == ChartType.PointAndFigure && _dataCoordinator.Transform != null)
                {
                    float yTop = (float)_dataCoordinator.Transform.ChartToScreen(new ChartPoint(System.DateTime.MinValue, hoveredCandle.High + _currentSnapshot.MinBrickSize)).Y;
                    float yBottom = (float)_dataCoordinator.Transform.ChartToScreen(new ChartPoint(System.DateTime.MinValue, hoveredCandle.Low)).Y;
                    
                    float topEdge = System.Math.Min(yTop, yBottom) + renderMarginTop;
                    float bottomEdge = System.Math.Max(yTop, yBottom) + renderMarginTop;
                    
                    if (position.Y < topEdge || position.Y > bottomEdge)
                    {
                        hoveredCandle = null; // Mouse is outside the P&F column bounds vertically
                    }
                }
            }

            HoveredCandle = hoveredCandle;
            var symbol = ViewModel?.Symbol ?? string.Empty;
            var tlbCount = _dataCoordinator.GetThreeLineBreakCount(index);
            StockAnalyzer.Core.Models.Analysis.ReverseWatchCurvePoint? rwPoint = null;
            if (ViewModel?.ChartType == ChartType.ReverseWatch)
            {
                 rwPoint = _dataCoordinator.GetNearestReverseWatchPoint(chartX, position.Y - renderMarginTop, new Rect(Bounds.Size));
                 if (ViewModel != null)
                 {
                     ViewModel.LatestReverseWatchPoint = _reverseWatchData?.Points?.LastOrDefault();
                 }
            }
            _interactionController.UpdateCrosshairPosition(index, hoveredCandle, symbol, tlbCount, rwPoint, position.Y, _dataCoordinator.GetConfluenceForIndex(index, _currentSnapshot.IndicatorResults, _currentSnapshot.IndicatorSettings), position.X, position.Y);
        }
        
        InvalidateVisual(); // Trigger repaint for Crosshair movement
    }



    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        
        Focus(); // Ensure control receives Key events

        // CANCEL DRAWING LOGIC (Ctrl + Click) - REMOVED
        // Replaced by OnKeyDown (Ctrl Key Press)

        var point = e.GetCurrentPoint(this);
        var position = e.GetPosition(this);
        
        // Match Render margin logic
        // Match Render margin logic
        float renderMarginTop = GetRenderMarginTop();

        // CRITICAL FIX (WebAI Review): e.GetPosition(this) returns control-LOCAL coordinates.
        // Do NOT subtract Bounds.X/Y - that was the cause of the offset mismatch.
        // Only subtract chart margins to get Chart Area relative coordinates.
        var chartPosition = new global::Avalonia.Point(
            position.X - EffectiveMarginLeft, 
            position.Y - renderMarginTop);

            
        // Important: Clamp chartPosition or handle out-of-bounds in Transform?
        // Transform assumes relative to ChartArea (0,0 is TopLeft of Chart).
        
        if (point.Properties.IsLeftButtonPressed)
        {
             // Delegate all "Start Drawing" / "Pick" logic to Controller
             if (_interactionController.HandlePointerPressed(
                 position,
                 chartPosition,
                 ViewModel,
                 _dataCoordinator.Transform,
                 e.KeyModifiers,
                 e.ClickCount))
             {
                 // Sync state
                 _isDrawingNewShape = _interactionController.IsDrawingNewShape;
                 _currentDrawingObject = _interactionController.CurrentDrawingObject;
                 _draggedObject = _interactionController.DraggedObject;
                 // _isDragging removed
                 _drawingStep = _interactionController.DrawingStep;

                 InvalidateVisual();
                 e.Handled = true;
                 return;
             }
             
             // Fallback: Pan
             _interactionController.IsDragging = true;
             _interactionController.LastDragPoint = position;
             e.Pointer.Capture(this);
             e.Handled = true;
        }

                







    }

    private ContextMenu? _indicatorContextMenu;

    private void ShowIndicatorContextMenu(CoreIndicatorSettings indicator, global::Avalonia.Point position)
    {
        _indicatorContextMenu?.Close();

        var menu = new ContextMenu();
        var propertiesItem = new MenuItem
        {
            Header = $"{indicator.DisplayName} - {Services.LocalizationManager.Instance["Menu_IndicatorProperties"]}"
        };
        propertiesItem.Click += (_, _) =>
        {
            _ = ViewModel?.DialogService?.ShowIndicatorPropertiesDialogAsync(indicator);
        };
        menu.Items.Add(propertiesItem);
        _indicatorContextMenu = menu;
        menu.Open(this);
    }

    private void OnDoubleTappedHandler(object? sender, TappedEventArgs e)
    {
        var pos = e.GetPosition(this);
        float renderMarginTop = GetRenderMarginTop();
        var chartPosition = new global::Avalonia.Point(pos.X - EffectiveMarginLeft, pos.Y - renderMarginTop);


        _interactionController.HandleDoubleTap(pos, chartPosition, ViewModel, _dataCoordinator.Transform, _currentSnapshot);
        // Visual update handled by observable or eventual invalidation from VM change
    }

    private void CancelCurrentDrawing()
    {
        // Use Controller's Cancel
        _interactionController.CancelDrawing(); // No argument needed now
        
        // Sync state
        _isDrawingNewShape = _interactionController.IsDrawingNewShape;
        _currentDrawingObject = _interactionController.CurrentDrawingObject;
        
        if (ViewModel != null) ViewModel.CurrentTool = DrawingTool.Pointer; // Reset tool
        InvalidateVisual();
    }


    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Cancel Drawing on Ctrl Key Press
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            if (ViewModel?.ObjectManager != null)
            {
                if (_interactionController.HandleCancelRequest(ViewModel.ObjectManager))
                {
                    // Sync local state
                    _isDrawingNewShape = _interactionController.IsDrawingNewShape;
                    _currentDrawingObject = _interactionController.CurrentDrawingObject;
                    InvalidateVisual();
                    e.Handled = true;
                }
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        
        if (_interactionController.HandlePointerReleased(ViewModel))
        {
             // Sync State
             _isDrawingNewShape = _interactionController.IsDrawingNewShape;
             _currentDrawingObject = _interactionController.CurrentDrawingObject;
             _draggedObject = _interactionController.DraggedObject;
             // _isDragging removed
             
             InvalidateVisual();
             e.Handled = true;
        }
            // Fix for Eraser Bug: Reset state when eraser interaction ends
            else if (ViewModel?.CurrentTool == DrawingTool.Eraser)
            {
                 // Eraser doesn't draw a shape, but uses the flag? 
                 // It seems Eraser logic is handled in OnPointerPressed, so this might be redundant fallback
                 _isDrawingNewShape = false;
                 if (ViewModel != null) ViewModel.CurrentTool = DrawingTool.Pointer;
                 InvalidateVisual();
            }

        
        if (_interactionController.IsDragging || _draggedObject != null)
        {
            _interactionController.IsDragging = false;
            _draggedObject = null;
            _draggedHandleIndex = -1; // Reset
            Cursor = Cursor.Default;
        }
    }
    
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (ViewModel?.Candles == null || ViewModel.Candles.Count == 0) return;

        // DebugLog($"[ChartBaseControl] Wheel Event: Delta={e.Delta.Y}, ChartType={ViewModel.ChartType}");

        // Calculate mouse relative position for zoom-at-cursor
        var mousePos = e.GetPosition(this);
        double chartWidth = Bounds.Width - EffectiveMarginLeft - EffectiveMarginRight;
        if (chartWidth <= 0) return;

        double mouseRel = (mousePos.X - EffectiveMarginLeft) / chartWidth;
        // Clamp 0..1
        if (mouseRel < 0) mouseRel = 0;
        if (mouseRel > 1) mouseRel = 1;

        // Delegate zoom to ChartDataCoordinator (Phase 3.5)
        int totalCandles = ViewModel.Candles.Count;
        _dataCoordinator.ApplyZoom(e.Delta.Y, mouseRel, totalCandles, ViewModel.ChartType);

        // Update UI
        UpdateSnapshot();
        InvalidateVisual();
        
        e.Handled = true;
    }

    // OnPointerMovedThrottled was replaced by direct invocation in OnPointerMoved

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredCandle = null;
        _isCrosshairVisible = false;

        // Publish cursor-exit event (null candle) via InteractionController
        var symbol = ViewModel?.Symbol ?? string.Empty;
        _interactionController.ClearCrosshairPosition(symbol);

        InvalidateVisual();
    }

    #endregion

    #region ChartDrawOperation

    // ChartDrawOperation has been extracted to ChartDrawOperation.cs
    // for improved maintainability and readability.

    #endregion
    


    // ParseColorSafe has been moved to StockAnalyzer.Core.Utilities.ColorHelper
}
