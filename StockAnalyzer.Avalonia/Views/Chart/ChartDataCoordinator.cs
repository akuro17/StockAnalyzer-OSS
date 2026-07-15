using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Avalonia.Views.Chart.Renderers; // Added
using StockAnalyzer.Avalonia.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Services;
using CommunityToolkit.Mvvm.Messaging;

namespace StockAnalyzer.Avalonia.Views.Chart;

[Flags]
public enum ChartUpdateAction
{
    None = 0,
    Invalidate = 1 << 0,
    UpdateSnapshot = 1 << 1,
    RecalculateReverseWatch = 1 << 2,
    FullRefresh = Invalidate | UpdateSnapshot
}

/// <summary>
/// Coordinates chart data management, zoom/pan state, and snapshot creation.
/// Extracted from ChartBaseControl to separate data management responsibilities.
/// </summary>
public class ChartDataCoordinator
{
    /// <summary>
    /// Gets the coordinate transform used for charting.
    /// </summary>
    public ICoordinateTransform Transform { get; private set; }

    private readonly Microsoft.Extensions.Logging.ILogger<ChartDataCoordinator>? _logger;

    private readonly Core.Services.Analysis.IAnalysisPipelineService _analysisService;
    private readonly IMessenger _messenger;

    /// <summary>
    /// Triggered whenever the chart data snapshot is recalculated.
    /// Subscribers like ConfluenceDashboardViewModel use this to sync with latest data.
    /// </summary>
    public event Action? SnapshotUpdated;

    public ChartDataCoordinator(IPythonService? pythonService = null, IMessenger? messenger = null, Microsoft.Extensions.Logging.ILogger<ChartDataCoordinator>? logger = null)
    {
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        // Initialize with default transform
        Transform = new GenericCoordinateTransform(
            ChartAxisMode.Time,
            1000, 500);
            
        // Use injected service (Phase 2 Component Coupling Fix)
        _analysisService = new Core.Services.Analysis.AnalysisPipelineService(pythonService);
    }
    #region Constants (moved to ChartTheme)

    // Layout constants are now centralized in ChartTheme class
    // Use ChartLayoutService.CreateLayout() for layout generation

    #endregion

    #region State

    private int _startIndex;
    private int _visibleCandleCount = 100;
    private int _convertedCandleCount; // Actual candle count for special chart types
    
    // Phase 1 Zero Allocation Fix: Reusable buffers for special charts
    private readonly List<CoreCandleData> _renkoBuffer = new();
    private readonly List<CoreCandleData> _heikinAshiBuffer = new();
    private readonly List<CoreCandleData> _kagiBuffer = new(2000);
    private readonly List<int> _kagiColumnMapBuffer = new(2000);
    private readonly List<CoreCandleData> _tlbBuffer = new();
    
    // Phase 5 Zero Allocation: Caching for index-based conversions
    private int _lastTlbLineCount = -1;
    private decimal _lastTlbMinMove = -1m;
    private int _lastSourceCandleCount = -1;
    private string? _lastSymbol = null;
    private string? _lastTimeFrame = null;
    private IReadOnlyDictionary<string, IIndicatorResult>? _lastAlignedResults = null;
    private IReadOnlyDictionary<string, IIndicatorResult>? _lastSourceIndicatorResults = null;
    
    // Phase 1 Zero Allocation Fix: Reusable buffers for special charts
    private readonly List<CoreCandleData> _pnfBuffer = new();
    private readonly List<CoreCandleData> _standardCandleBuffer = new(5000);
    
    // Phase 2 Zero Allocation Fix: Intermediate buffers for converters returning specialized types
    private readonly List<KagiCandleData> _kagiIntermediateBuffer = new();
    private readonly List<ThreeLineBreakBlock> _tlbBlockBuffer = new();
    private readonly List<PnfSignal> _pnfSignalsBuffer = new(32);
    private readonly List<PnfTrendline> _pnfActiveLinesBuffer = new(32);
    

    private ChartDataSnapshot _currentSnapshot = ChartDataSnapshot.Empty;
    private ChartType _currentChartType = ChartType.Candlestick;
    private ChartFamily _previousChartFamily = ChartFamily.TimeSeries;
    private ReverseWatchCurveData? _reverseWatchData;
    private ComparisonAlignedData? _comparisonData;
    private static readonly IReadOnlyList<decimal?> EmptySeries = Array.Empty<decimal?>();
    private Rect _reverseWatchViewport = default; // Viewport for ReverseWatch (Volume/Price)
    private int _currentFutureOffset = 5; // Default buffer
    private bool _needsViewAdaptation; // Flag: only adapt view on data/chart-type changes
    private string? _lastDataSymbol; // Track symbol to detect genuine data source changes
    private string? _lastDataTimeFrame; // Track timeframe to detect genuine data source changes

    /// <summary>
    /// Represents a persistent view state (zoom/pan) for a specific chart family.
    /// </summary>
    private struct ZoomSlot
    {
        public DateTime? StartTime;
        public DateTime? EndTime;
        public double StartUnit; // For index-based units
        public double EndUnit;   // For index-based units
        public double ItemCount; // Relative zoom factor (candles visible)
        public bool IsValid => StartTime.HasValue || ItemCount > 0;
    }

    private readonly Dictionary<ChartFamily, ZoomSlot> _familyZoomStore = new();

    /// <summary>
    /// Manages the viewport state (Time-based).
    /// </summary>
    public ViewportManager Viewport { get; } = new();

    private const decimal StandardPricePaddingPx = 10.0m;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the current chart data snapshot.
    /// </summary>
    public ChartDataSnapshot CurrentSnapshot => _currentSnapshot;

    /// <summary>
    /// Gets or sets the current start index for visible candles.
    /// </summary>
    /// <summary>
    /// Gets or sets the current start index for visible candles.
    /// Derived from Viewport state in UpdateSnapshot.
    /// </summary>
    public int StartIndex
    {
        get => _startIndex;
        private set => _startIndex = value;
    }

    /// <summary>
    /// Gets or sets the current visible candle count.
    /// Derived from Viewport state in UpdateSnapshot.
    /// </summary>
    public int VisibleCandleCount
    {
        get => _visibleCandleCount;
        private set => _visibleCandleCount = value;
    }

    /// <summary>
    /// Gets the ReverseWatch curve data (for XY plot mode).
    /// </summary>
    public ReverseWatchCurveData? ReverseWatchData => _reverseWatchData;

    /// <summary>
    /// Gets the RelativePerformance aligned data.
    /// </summary>
    public ComparisonAlignedData? ComparisonData => _comparisonData;

    #endregion

    #region Public Methods

    /// <summary>
    /// Resets the view to show the most recent candles.
    /// </summary>
    /// <param name="totalCandles">Total number of candles available.</param>
    public void ResetView(int totalCandles)
    {
        Viewport.PanToLatest();
    }

    private DateTime? _adaptationStartTime;
    private DateTime? _adaptationEndTime;
    
    // WebAI Fix: Item-Count based Zoom Memory (Coordinate Family Invariant)
    private double _lastVisibleItemCount = 100;
    
    // Cached engine for Zero-Allocation scanning
    private readonly MultiWavePatternEngine _mwEngine = new();

    // Signal Orchestration Facade (FR-39-12)
    private readonly Core.Services.SignalOrchestrator _orchestrator = new();
    private readonly List<ConfluenceSignal> _signalBuffer = new(32);
    private readonly List<ConfluenceSignal> _deduplicatedSignalBuffer = new(32);

    private DateTime GetInterpolatedTime(double index, IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0) return DateTime.MinValue;
        
        // WebAI Fix: Prevent massive time extrapolation (e.g. Year 3600) when index-based charts
        // have huge empty margins due to 1:1 aspect ratio locks (like P&F).
        // By clamping to the actual data indices, transitioning back to Time-Based charts will
        // safely anchor to the first or last available data point.
        index = Math.Clamp(index, 0, candles.Count - 1);
        
        int idxFlow = (int)Math.Floor(index);
        int idxCeil = (int)Math.Ceiling(index);
        
        if (idxFlow == idxCeil) return candles[idxFlow].Timestamp;
        
        long t1 = candles[idxFlow].Timestamp.Ticks;
        long t2 = candles[idxCeil].Timestamp.Ticks;
        
        double fraction = index - idxFlow;
        long tInterp = t1 + (long)((t2 - t1) * fraction);
        
        return new DateTime(Math.Max(DateTime.MinValue.Ticks, Math.Min(DateTime.MaxValue.Ticks, tInterp)));
    }

    private bool IsStandardChart(ChartType type) => 
        type == ChartType.Candlestick || type == ChartType.OHLCBar || 
        type == ChartType.Line || type == ChartType.Area || type == ChartType.HeikinAshi ||
        type == ChartType.RelativePerformance;

    /// <summary>
    /// Updates the snapshot based on the current ViewModel state.
    /// </summary>
    public void UpdateSnapshot(ChartViewModel viewModel, Rect bounds, bool suppressMessages = false)
    {
        try
        {
            if (viewModel == null || viewModel.Candles == null || viewModel.Candles.Count == 0)
            {
                _currentSnapshot = ChartDataSnapshot.Empty;
                return;
            }



        // Relative Performance: Hide if no comparison targets added
        // Removed: Early return for RP with 0 targets. 
        // We now allow rendering the primary ticker even without comparison targets
        // to provide a baseline and correct percentage scaling.
        
        bool isInitialLoad = _currentSnapshot.Candles == null || _currentSnapshot.Candles.Count == 0;

        // WebAI Fix (Redock): On initial data load, always trigger view adaptation to ensure
        // ForceVisibleRange is called. Without this, redocked tabs get a blank chart because
        // the new ChartDataCoordinator never establishes an initial viewport range.
        if (isInitialLoad)
        {
            _needsViewAdaptation = true;
        }

        // WebAI Fix: Capture structural zoom memory as a "Count of Items" instead of raw Units.
        var oldFamily = GetChartFamily(_currentChartType);
        var newFamily = GetChartFamily(viewModel.ChartType);
        bool isFamilyTransition = (oldFamily != newFamily) && !isInitialLoad;
        
        // WebAI Fix (Step 3): Refined reset detection. 
        // Always force a full-range fill reset when moving TO an IndexSeries (e.g., Renko, P&F) 
        // to ensure they always fill the screen correctly despite different brick/column counts.
        // WebAI Fix (Regression): Only reset if the ACTUAL type changed, otherwise we break manual zoom.
        bool forceReset = isFamilyTransition || (newFamily == ChartFamily.IndexSeries && _currentChartType != viewModel.ChartType);

        // WebAI Fix (Step 3): Persistent Zoom Memory Isolation
        // 1. Capture the current view state into the old family's slot before any changes
        if (!isInitialLoad)
        {
            // WebAI Fix: Capture structural zoom memory as a "Count of Items".
            // CRITICAL (V3): Use the *nominal* interval of the OLD snapshot (CurrentSnapshot) 
            // to correctly translate the OLD duration into an invariant ItemCount.
            var snapshotInterval = GetTimeIntervalFromSnapshot(_currentSnapshot);
            double preSwitchDuration = Viewport.VisibleEndUnit - Viewport.VisibleStartUnit;
            
            _familyZoomStore[oldFamily] = new ZoomSlot
            {
                StartTime = (oldFamily == ChartFamily.TimeSeries) ? Viewport.VisibleStartTime : GetInterpolatedTime(Viewport.VisibleStartUnit, viewModel.Candles),
                EndTime = (oldFamily == ChartFamily.TimeSeries) ? Viewport.VisibleEndTime : GetInterpolatedTime(Viewport.VisibleEndUnit, viewModel.Candles),
                StartUnit = Viewport.VisibleStartUnit,
                EndUnit = Viewport.VisibleEndUnit,
                ItemCount = (oldFamily == ChartFamily.TimeSeries) ? (preSwitchDuration / (double)snapshotInterval.Ticks) : preSwitchDuration
            };
        }

        // Update state EARLY but keep the transition variables for downstream logic if needed
        var previousType = _currentChartType;
        _currentChartType = viewModel.ChartType;

        // 0. Calculate Indicators EARLY to determine future offset requirement
        var indicatorValues = viewModel.IndicatorResults;
        
        int maxDataCount = viewModel.Candles.Count;
        if (indicatorValues != null)
        {
            foreach (var result in indicatorValues.Values)
            {
                if (result.IsSuccessful)
                {
                    foreach (var seriesName in result.SeriesNames)
                    {
                        var series = result.GetSeries(seriesName);
                        // Indicators like Ichimoku might extend beyond candles
                        if (series.Count > maxDataCount) maxDataCount = series.Count;
                    }
                }
            }
        }

        // 1. Update Viewport Bounds and Limits
        // CRITICAL: Layout and ScreenBounds MUST be computed BEFORE the adaptation block,
        // because ResetScale() → ForceVisibleRange() needs _screenBounds.Width > 0 to
        // compute a valid _pixelsPerUnit. Previously this was after the adaptation block,
        // causing ForceVisibleRange to silently skip PixelsPerUnit computation on redock.
        var profile = ChartTypeProfileRegistry.Get(viewModel.ChartType);
        var layout = profile.LayoutProvider.CreateLayout(bounds, viewModel.Indicators, 
            viewModel.EffectiveIsSubWindowVisible,
            viewModel.IsMainWindowVisible,
            viewModel.ChartMarginTop, viewModel.ChartMarginBottom, viewModel.ChartMarginRight);
        
        // WebAI Fix: Sync ScreenBounds IMMEDIATELY so any subsequent PixelsPerUnit or 
        // ForceVisibleRange calls use the correct physical width for the NEW layout.
        bool layoutBecameAvailable = layout.ChartArea.Width > 0 && Viewport.ScreenBounds.Width <= 0;
        Viewport.ScreenBounds = layout.ChartArea;
        if (layoutBecameAvailable)
        {
            _needsViewAdaptation = true;
        }

        // WebAI: Decouple from event-ordering by forcing adaptation on Family Switch or Internal Index Switch
        if (_needsViewAdaptation || forceReset)
        {
            // 2. Retrieve or initialize the adaptation state based on the Hybrid Zoom Policy:
            // - Within the same coordinate family (e.g. Candle <-> HeikinAshi): Restore Zoom exactly.
            // - Between different families (e.g. Candle <-> Renko): Reset to Full Range for stability.
            
            if (!forceReset && _familyZoomStore.TryGetValue(newFamily, out var savedSlot))
            {
                _adaptationStartTime = savedSlot.StartTime;
                _adaptationEndTime = savedSlot.EndTime;
                _lastVisibleItemCount = savedSlot.ItemCount;
            }
            else
            {
                // Inter-family transition or first time: Perform a clean reset.
                _adaptationStartTime = null;
                _adaptationEndTime = null;
                _lastVisibleItemCount = 0; 
                
                // Reset scale flag to 0.0001 to trigger auto-calculation when ScreenBounds is next assigned
                Viewport.ResetScale();

                // WebAI Fix (Step 6): Use ForceVisibleRange to set logical units (Bypass "Width > 0" Gate in ResetView)
                if (newFamily == ChartFamily.TimeSeries)
                {
                    var interval = GetTimeInterval(viewModel.SelectedTimeFrame);
                    int count = Math.Max(200, viewModel.MaxCandleCount); // Default view
                    if (count > viewModel.Candles.Count) count = viewModel.Candles.Count;
                    
                    double end = viewModel.Candles.Last().Timestamp.Ticks;
                    double duration = interval.Ticks * count;
                    Viewport.ForceVisibleRange(end - duration, end);
                    _visibleCandleCount = count; // SYNC BACK
                }
                else if (newFamily == ChartFamily.IndexSeries)
                {
                    // For IndexSeries, we delay range initialization until AFTER conversion 
                    // (inside AdjustSpecialChartView) to use the correct brick/column count.
                    _visibleCandleCount = 0; 
                }
                else if (newFamily == ChartFamily.Comparison)
                {
                    _adaptationEndTime = null;
                    _lastVisibleItemCount = 0;

                }
            }
        }
        
        // WebAI Fix: Clamp future offset to prevent excessive margins (especially on daily charts with weekend gaps).
        int requiredExtension = Math.Max(0, maxDataCount - viewModel.Candles.Count);
        int effectiveOffsetCount = Math.Min(100, Math.Max(1, Math.Max(viewModel.RequiredFutureOffset, requiredExtension)));
        
        // Comparison charts don't need future projection space (no indicators/ghost projections)
        if (viewModel.ChartType == ChartType.RelativePerformance)
        {
            effectiveOffsetCount = 0;
        }
        
        _currentFutureOffset = effectiveOffsetCount; // Update EARLY so AdjustSpecialChartView uses the latest value

        if (viewModel.ChartType.IsTimeBased())
        {
            var firstCandle = viewModel.Candles.First();
            var lastCandle = viewModel.Candles.Last();
            Viewport.SetDataBounds(firstCandle.Timestamp, lastCandle.Timestamp);
            
            // WebAI Fix (V3): Use strict nominal interval based on timeframe to prevent zoom crawl/drift.
            TimeSpan interval = GetTimeInterval(viewModel.SelectedTimeFrame);
            
            var futureOffset = TimeSpan.FromTicks(interval.Ticks * effectiveOffsetCount);
            _logger?.LogDebug("[FutureOffset] Interval={Interval:F2}d, OffsetCount={OffsetCount}, FutureOffset={FutureOffset:F2}d", interval.TotalDays, effectiveOffsetCount, futureOffset.TotalDays);
            Viewport.SetFutureOffset(futureOffset);
            Viewport.SetDataInterval(interval);

            // WebAI Fix: IsRightPinned MUST be set AFTER SetDataBounds and SetFutureOffset.
            // The setter triggers UpdateVisibleRange() which uses _futureEndUnit to clamp the viewport.
            // If set before data bounds exist, _futureEndUnit holds stale values from the previous chart type,
            // causing the viewport to show incorrect range on initial transition.
            Viewport.IsRightPinned = (viewModel.ChartType == ChartType.RelativePerformance);

            // 2. Finalize adaptation if the flag is set (Symbol change) or we just switched families
            if (_needsViewAdaptation || isFamilyTransition)
            {
                // WebAI Fix: Comparison chart ALWAYS gets full range in adaptation block.
                // ForceVisibleRange property changes trigger re-entry via _needsViewAdaptation,
                // and without this guard, the `else if (_adaptationEndTime.HasValue)` path
                // restores a stale zoom state from the previous chart type.
                if (isInitialLoad || newFamily == ChartFamily.Comparison || !_adaptationEndTime.HasValue)
                {
                    // WebAI Fix: To show the FULL initial range, we must calculate the duration spanning from EXACTLY
                    // the first candle to the last. Due to weekends/holidays (GaplessTime), simply multiplying candle count 
                    // by interval grossly underestimates the calendar duration and cuts off the older half of the dataset. 
                    double start = firstCandle.Timestamp.Ticks - interval.Ticks;
                    double end = viewModel.Candles.Last().Timestamp.Ticks;
                    double offsetDuration = interval.Ticks * _currentFutureOffset;
                    
                    Viewport.ForceVisibleRange(start, end + offsetDuration);
                }
                else if (_adaptationEndTime.HasValue)
                {
                    // If we have a saved slot count, restore it perfectly
                    double restoredItemCount = (_lastVisibleItemCount > 0) ? _lastVisibleItemCount : 100;
                    
                    // WebAI Fix (V3): Use NEW nominal interval to restore consistent bar count.
                    double preferredVisibleUnits = restoredItemCount * interval.Ticks;
                    
                    // WebAI Fix: Ensure a sane minimum duration (at least 20 candles)
                    long minTicks = interval.Ticks * 20;
                    if (preferredVisibleUnits < minTicks) preferredVisibleUnits = minTicks;
                    
                    // WebAI Fix: Prevent insane maximum durations
                    long maxTicks = interval.Ticks * Math.Max(500, viewModel.Candles.Count * 2);
                    if (preferredVisibleUnits > maxTicks) preferredVisibleUnits = maxTicks;
                    
                    long targetEndTicks = _adaptationEndTime.Value.Ticks + (interval.Ticks / 2);
                    long targetStartTicks = targetEndTicks - (long)preferredVisibleUnits;
                    
                    Viewport.ForceVisibleRange(targetStartTicks, targetEndTicks);

                }
                // Only clear the adaptation flag if the viewport was actually initialized
                // with a valid scale. If ScreenBounds.Width was 0 (e.g., control not yet laid out),
                // ForceVisibleRange silently skips PixelsPerUnit computation, leaving it at sentinel 0.0001.
                // In that case, we MUST keep the flag set so the next pass (with valid bounds) retries.
                if (Viewport.PixelsPerUnit > 0 && Viewport.PixelsPerUnit != 0.0001)
                {
                    _needsViewAdaptation = false;
                }
            }
            
            // Dynamic comparison chart rebase (WebAI/User Enhancement)
            // Comparison chart always renders ALL data points (full range rebase).
            // We force startIndex=0 and visibleCount=ALL regardless of viewport state.
            // The actual X-axis scaling is handled separately in the transform override below.
            // Removed: Forced startIndex=0 for RelativePerformance. 
            // Standard viewport-based indexing now applies, enabling dynamic 0% baseline re-basing to the leftmost visible candle.
        }
        else
        {
            // Index-based charts (Renko/P&F/Kagi/TLB)
            // Actual scaling is delayed until CreateChartTypeSnapshot finishes conversion.
            // We only set temporary interval here so Viewport isn't left fully uninitialized.
            Viewport.IsRightPinned = false; // Index charts never use right-pinning
            Viewport.SetDataIntervalUnits(1);
            
            if (!_needsViewAdaptation)
            {
                // Only pull from Viewport if not adapting from a time-based chart
                _startIndex = (int)Math.Floor(Viewport.VisibleStartUnit);
                _visibleCandleCount = (int)Math.Ceiling(Viewport.VisibleEndUnit - Viewport.VisibleStartUnit) + 1;
            }
        }

        // 1. Calculate Effective Indices from Viewport Time for Time-Based Charts
        if (viewModel.ChartType.IsTimeBased())
        {
            var firstCandle = viewModel.Candles.First();
            var lastCandle = viewModel.Candles.Last();
            TimeSpan interval = GetTimeInterval(viewModel.SelectedTimeFrame);

            int newStartIndex = FindClosestIndex(viewModel.Candles, Viewport.VisibleStartTime);
            
            if (Viewport.VisibleStartTime < firstCandle.Timestamp)
            {
                 double diffTicks = (Viewport.VisibleStartTime - firstCandle.Timestamp).Ticks;
                 newStartIndex = (int)(diffTicks / interval.Ticks);
            }
            else if (Viewport.VisibleStartTime > lastCandle.Timestamp)
            {
                 double diffTicks = (Viewport.VisibleStartTime - lastCandle.Timestamp).Ticks;
                 newStartIndex = (int)(diffTicks / interval.Ticks) + (viewModel.Candles.Count - 1);
            }
            
            long visibleTicks = (long)(Viewport.VisibleEndUnit - Viewport.VisibleStartUnit);
            int newVisibleCount = (int)Math.Ceiling((double)visibleTicks / interval.Ticks) + 2;
            if (newVisibleCount < 5) newVisibleCount = 5;
            _startIndex = newStartIndex;
            _visibleCandleCount = newVisibleCount;
        }

        // 3. Create Snapshot (using pre-calculated indicators)
        // WebAI Fix (Step 3): Pass unified forceReset signal to ensure consistent zoom policy
        _currentSnapshot = CreateChartTypeSnapshot(viewModel, indicatorValues!, _visibleCandleCount, layout, forceReset);

        // P&F Viewport constraint (Data bounds clamping)
        // WebAI Fix (FR-PNF-04): Previously this snapped only VisibleEndUnit to an
        // integer (maxUnits) once the overshoot exceeded 1 unit, which abruptly changed
        // the visible duration (width) and produced a jitter/jump at the right edge.
        // Now we shift BOTH edges by the same overshoot amount, preserving the current
        // zoom width and keeping fractional (sub-pixel) positions intact for smooth scroll.
        if (viewModel.ChartType == ChartType.PointAndFigure)
        {
            double total = _convertedCandleCount > 0 ? _convertedCandleCount : viewModel.Candles.Count;
            double maxUnits = total + effectiveOffsetCount;
            double overshoot = Viewport.VisibleEndUnit - maxUnits;
            if (overshoot > 0)
            {
Viewport.ForceVisibleRange(Viewport.VisibleStartUnit - overshoot, Viewport.VisibleEndUnit - overshoot);
            }
        }
        
        // 4. Update Transform BEFORE propagating Snapshot to ViewModel
        // This ensures the renderer sees the correct price range (percentages) for the new snapshot
        var finalCandle = viewModel.Candles.LastOrDefault();
        if (finalCandle != null)
        {
            UpdateCoordinateTransform(layout, Transform, viewModel.Candles, finalCandle.Timestamp, 
                viewModel.Candles.Count > 1 ? (finalCandle.Timestamp - viewModel.Candles[viewModel.Candles.Count-2].Timestamp) : TimeSpan.FromDays(1), 
                viewModel.Candles.Count, viewModel.ChartType, 
                viewModel.EffectiveIsSubWindowVisible,
                viewModel.IsMainWindowVisible,
                viewModel.PriceScale,
                _currentSnapshot.MinPrice, _currentSnapshot.MaxPrice);
        }

        // Propagate to ViewModel for UI access (DataWindow, Tooltip)
        // Now renderers will get the Snapshot and have a Transform already synced with its range
        viewModel.VisibleStartIndex = _startIndex;
        viewModel.VisibleCandleCount = _visibleCandleCount;
        viewModel.IndicatorResults = indicatorValues;
        viewModel.CurrentSnapshot = _currentSnapshot;

        // Notify subscribers that a new snapshot is available (Zero-Allocation Trigger)
        if (!suppressMessages)
        {
            SnapshotUpdated?.Invoke();
            _messenger.Send(new ConfluenceSnapshotUpdatedMessage(indicatorValues!, viewModel.Indicators.ToList(), viewModel.Candles.Count));
        }

        // DataBounds and FutureOffset for Index charts are now fully managed inside AdjustSpecialChartView 
        // (called during CreateChartTypeSnapshot if forceReset is true).
        // Standard TimeBased bounds are managed at the beginning of UpdateSnapshot.
        // Thus, the redundant SetDataBounds call here is removed to prevent extra layout cycles.

        // Update comparison data if applicable
        _comparisonData = viewModel.ComparisonData;
        _previousChartFamily = GetChartFamily(_currentChartType);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(@"Y:\Temp\sa_crash.txt", $"Coordinator UpdateSnapshot Error: {ex}");
            throw;
        }
    }
    
    private int FindClosestIndex(IEnumerable<CoreCandleData> candles, DateTime targetTime)
    {
        var list = candles as IList<CoreCandleData> ?? candles.ToList();
        return FindClosestIndex(list, targetTime);
    }

    /// <summary>
    /// Applies zoom operation based on mouse wheel input.
    /// </summary>
    public void ApplyZoom(double deltaY, double mouseRelX, int totalCandles, ChartType chartType = ChartType.Candlestick)
    {
        if (chartType == ChartType.ReverseWatch) return;

        double chartWidth = Viewport.ScreenBounds.Width;
        if (chartWidth <= 0) return;
        
        double screenX = Viewport.ScreenBounds.X + mouseRelX * chartWidth;
        double zoomFactor = deltaY > 0 ? 1.1 : 0.9;
        
        Viewport.Zoom(zoomFactor, screenX);
    }

    /// <summary>
    /// Applies pan operation based on drag delta.
    /// </summary>
    public bool ApplyPan(double deltaX, double chartWidth, int totalCandles, ChartType chartType = ChartType.Candlestick, int futureOffset = 0)
    {
        if (chartWidth <= 0 || chartType == ChartType.ReverseWatch) return false;

        var oldStartValue = Viewport.VisibleStartUnit;
        Viewport.Pan(deltaX);
        if (Viewport.VisibleStartUnit != oldStartValue)
        {

            return true;
        }
        return false;
    }

    /// <summary>
    /// Adjusts view to follow the most recent candle if user was already at the end.
    /// </summary>
    /// <param name="totalCandles">The new total candle count.</param>
    /// <param name="wasAtEnd">Whether the view was at the end before the update.</param>
    /// <summary>
    /// Adjusts view to follow the most recent candle if user was already at the end.
    /// </summary>
    public void AdjustForNewCandles(int totalCandles, bool wasAtEnd)
    {
        if (wasAtEnd)
        {
            Viewport.PanToLatest();
        }
    }

    /// <summary>
    /// Checks if the current view is at the end of the data (or close enough to auto-scroll).
    /// </summary>
    public bool IsAtEnd => _startIndex + _visibleCandleCount >= _currentSnapshot.Candles.Count;

    /// <summary>
    /// Recalculates ReverseWatch data from candles.
    /// </summary>
    /// <param name="viewModel">The chart view model.</param>
    public void CalculateReverseWatch(ChartViewModel? viewModel)
    {
        if (viewModel?.Candles == null || viewModel.Candles.Count == 0)
        {
            _reverseWatchData = null;
            return;
        }

        _reverseWatchData = _analysisService.CalculateReverseWatch(
            viewModel.Candles,
            viewModel.ReverseWatchPeriod,
            viewModel.Symbol,
            viewModel.ReverseWatchIsMaBased,
            viewModel.ReverseWatchIsLogScaleVolume);
            
        // Reset viewport to default to enable auto-scaling on the next render
        _reverseWatchViewport = default;
    }

    /// <summary>
    /// Gets the index of the candle at the specified X coordinate.
    /// </summary>
    /// <param name="x">The X coordinate relative to the chart control.</param>
    /// <param name="bounds">The chart control bounds.</param>
    /// <returns>The index of the candle, or -1 if invalid.</returns>
    public int GetCandleIndexAt(double relativeX, Rect controlBounds)
    {
        if (_currentSnapshot.Candles == null || _currentSnapshot.Candles.Count == 0) return -1;
        
        // Handle ReverseWatch (No candle index concept essentially, returns -1)
        if (IsReverseWatchOutput()) return -1;

        // Strict boundary check: If relativeX is outside the Chart Area (Viewport), return -1
        if (relativeX < 0 || relativeX > Transform.ViewportWidth) return -1;

        // Use Transform to map Screen X -> Logical X
        // Transform is configured for the Chart Area dimensions (during UpdateCoordinateTransform)
        // relativeX should be 0..ChartWidth
        
        var point = Transform.ScreenToChart(new global::Avalonia.Point(relativeX, 0));
        
        // Determine mode from Transform
        bool isIndexMode = false;
        if (Transform is GenericCoordinateTransform gct)
        {
            if (gct.Mode == ChartAxisMode.Index) isIndexMode = true;
        }
        else
        {
             // Fallback for legacy transforms
             isIndexMode = IsIndexBasedOutput(_currentChartType);
        }

        if (isIndexMode)
        {
             // Index Mode: Time.Ticks contains the absolute logical column index
             long indexLng = point.Time.Ticks;
             int absoluteIdx;
             if (_currentChartType == ChartType.Renko || _currentChartType == ChartType.PointAndFigure || _currentChartType == ChartType.ThreeLineBreak)
             {
                 absoluteIdx = (int)Math.Floor((double)indexLng);
             }
             else
             {
                 absoluteIdx = (int)Math.Round((double)indexLng);
             }
             
             int localIdx = 0;
             if (_currentChartType == ChartType.Kagi)
             {
                 // Kagi: Multiple segments can share the same column. 
                 // We must simulate the renderer's column offset logic to find the corresponding segment.
                 int currentColumnOffset = 0;
                 bool lastIsUp = true;
                 if (_currentSnapshot.Candles.Count > 0)
                 {
                     lastIsUp = _currentSnapshot.Candles[0].Close >= _currentSnapshot.Candles[0].Open;
                 }

                 int bestMatch = 0;
                 for (int i = 0; i < _currentSnapshot.Candles.Count; i++)
                 {
                     var seg = _currentSnapshot.Candles[i];
                     bool isUp = seg.Close >= seg.Open;
                     if (i > 0 && isUp != lastIsUp)
                     {
                         currentColumnOffset++;
                         lastIsUp = isUp;
                     }
                     
                     int logicalX = _currentSnapshot.StartIndex + currentColumnOffset;
                     if (logicalX > absoluteIdx) break; // Passed the target column
                     
                     bestMatch = i; // Save the last segment that matched this column (or earlier)
                 }
                 localIdx = bestMatch;
             }
             else
             {
                 // Renko, P&F, TLB: 1-to-1 mapping between snapshot index and column offset
                 localIdx = absoluteIdx - _currentSnapshot.StartIndex;
             }
             
             // Clamp to valid local range
             if (localIdx < 0) localIdx = 0;
             if (localIdx >= _currentSnapshot.Candles.Count) localIdx = _currentSnapshot.Candles.Count - 1;
             
             return localIdx;
        }
        
        // Time Mode
        return FindClosestIndex(_currentSnapshot.Candles, point.Time);
    }
    
    // Helper for binary search - Modified to use Round logic (absolute closest) 
    // This perfectly centers the selection zone around the visual candlestick, 
    // switching halfway in the empty gap between candlesticks.
    private int FindClosestIndex(IList<CoreCandleData> candles, DateTime targetTime)
    {
        if (candles == null || candles.Count == 0) return -1;
        
        // Binary search
        int min = 0;
        int max = candles.Count - 1;
        
        if (targetTime < candles[min].Timestamp) return min;
        if (targetTime > candles[max].Timestamp) return max;
        
        while (min <= max)
        {
            int mid = (min + max) / 2;
            DateTime midTime = candles[mid].Timestamp;
            
            if (midTime == targetTime) return mid;
            if (midTime < targetTime) min = mid + 1;
            else max = mid - 1;
        }
        
        // min is now the first index strictly greater than targetTime.
        // min - 1 is the largest index less than targetTime.
        if (min >= candles.Count) return candles.Count - 1;
        if (min <= 0) return 0;
        
        DateTime t1 = candles[min - 1].Timestamp;
        DateTime t2 = candles[min].Timestamp;
        
        // Compare absolute time distances to perform mathematical Rounding
        if ((targetTime - t1) <= (t2 - targetTime))
        {
            return min - 1;
        }
        
        return min;
    }


    /// <summary>
    /// Gets the nearest ReverseWatch curve point to the specified coordinates.
    /// </summary>
    public ReverseWatchCurvePoint? GetNearestReverseWatchPoint(double chartX, double chartY, Rect bounds)
    {
        if (_reverseWatchData == null || _reverseWatchData.Points.Count == 0 || Transform == null) return null;
        
        var transform = Transform;
        double minDistSq = double.MaxValue;
        ReverseWatchCurvePoint? bestPoint = null;

        int pointCount = _reverseWatchData.Points.Count;
        int renderCount = Math.Min(pointCount, 120);
        int startIndex = pointCount - renderCount;

        for (int i = startIndex; i < pointCount; i++)
        {
             var point = _reverseWatchData.Points[i];
             var sp = transform.NumericToScreen((double)point.VolumeAverage, (double)point.PriceAverage);
             // chartX and chartY are already relative to ChartArea
             double dx = chartX - sp.X;
             double dy = chartY - sp.Y;
             double distSq = dx * dx + dy * dy;

             if (distSq < minDistSq)
             {
                 minDistSq = distSq;
                 bestPoint = point;
             }
        }
        
        if (minDistSq > 50 * 50) return _reverseWatchData.Points.LastOrDefault();
        return bestPoint ?? _reverseWatchData.Points.LastOrDefault();
    }
    /// <summary>
    /// Syncs user settings to effective values used by chart calculations.
    /// Accesses AtrCalculator to compute dynamic parameter values.
    /// </summary>
    /// <param name="viewModel">The ChartViewModel to update.</param>
    public void SyncEffectiveValues(ChartViewModel? viewModel)
    {
        if (viewModel == null || viewModel.Candles == null || !viewModel.Candles.Any()) return;
        
        // Renko
        if (viewModel.RenkoSizingMode == ChartSizingMode.Fixed)
        {
            viewModel.EffectiveRenkoBrickSize = viewModel.RenkoBrickSize;
        }
        else if (viewModel.RenkoSizingMode == ChartSizingMode.Percentage)
        {
            var lastPrice = viewModel.Candles.LastOrDefault()?.Close ?? 100m;
            viewModel.EffectiveRenkoBrickSize = lastPrice * (viewModel.RenkoBrickPercent / 100m);
        }
        else if (viewModel.RenkoSizingMode == ChartSizingMode.AutoAtr)
        {
             // Calculate ATR
             decimal atr = _analysisService.CalculateAtr(viewModel.Candles, viewModel.RenkoAtrPeriod);
             viewModel.EffectiveRenkoBrickSize = atr * viewModel.RenkoAtrMultiplier;
             if (viewModel.EffectiveRenkoBrickSize <= 0) viewModel.EffectiveRenkoBrickSize = 1m; // Safety
        }
        
        // P&F
        if (viewModel.PnfSizingMode == ChartSizingMode.Fixed)
        {
            viewModel.EffectivePnfBoxSize = viewModel.PnfBoxSize;
        }
        else if (viewModel.PnfSizingMode == ChartSizingMode.Percentage)
        {
            var lastPrice = viewModel.Candles.LastOrDefault()?.Close ?? 100m;
            viewModel.EffectivePnfBoxSize = lastPrice * (viewModel.PnfBoxPercent / 100m);
            if (viewModel.EffectivePnfBoxSize <= 0) viewModel.EffectivePnfBoxSize = 1m;
        }
        else if (viewModel.PnfSizingMode == ChartSizingMode.AutoAtr)
        {
             decimal atr = _analysisService.CalculateAtr(viewModel.Candles, viewModel.PnfAtrPeriod);
             viewModel.EffectivePnfBoxSize = atr * viewModel.PnfAtrMultiplier;
             if (viewModel.EffectivePnfBoxSize <= 0) viewModel.EffectivePnfBoxSize = 1m;
        }
        
        // Kagi
        if (viewModel.KagiReversalMode == ChartSizingMode.Fixed)
        {
            viewModel.EffectiveKagiReversalAmount = viewModel.KagiReversalAmount;
        }
        else if (viewModel.KagiReversalMode == ChartSizingMode.Percentage)
        {
            // Percentage mode: Calculate from current price
            var lastPrice = viewModel.Candles.LastOrDefault()?.Close ?? 100m;
            viewModel.EffectiveKagiReversalAmount = lastPrice * (viewModel.KagiReversalPercent / 100m);
        }
        else if (viewModel.KagiReversalMode == ChartSizingMode.AutoAtr)
        {
             decimal atr = _analysisService.CalculateAtr(viewModel.Candles, viewModel.KagiAtrPeriod);
             viewModel.EffectiveKagiReversalAmount = atr * viewModel.KagiAtrMultiplier;
             if (viewModel.EffectiveKagiReversalAmount <= 0) viewModel.EffectiveKagiReversalAmount = 1m;
        }
    }

    #endregion


    /// <summary>
    /// Handles ViewModel property changes and determines necessary UI updates.
    /// Also executes internal data synchronization (e.g. SyncEffectiveValues).
    /// </summary>
    public ChartUpdateAction OnViewModelPropertyChanged(string? propertyName, ChartViewModel? viewModel)
    {
        if (string.IsNullOrEmpty(propertyName) || viewModel == null) return ChartUpdateAction.None;

        var action = ChartUpdateAction.None;

        if (propertyName == nameof(ChartViewModel.Candles))
        {
            // Candles changed -> Need full recalc
            _lastAlignedResults = null;
            _lastSourceIndicatorResults = null;
            SyncEffectiveValues(viewModel); // Fix: Determine effective values (ATR, etc.) for new data
            viewModel.RecalculateFutureOffset(); // Ensure future scroll offset is set for all charts
            
            // Only reset view adaptation when symbol or timeframe ACTUALLY changes.
            // Background data refreshes (same symbol/timeframe) should preserve user zoom/pan state.
            string currentSymbol = viewModel.Symbol ?? "";
            string currentTimeFrame = viewModel.SelectedTimeFrame.ToString();
            if (currentSymbol != _lastDataSymbol || currentTimeFrame != _lastDataTimeFrame)
            {
                _needsViewAdaptation = true; // Genuine data source change -> reset view
                _lastDataSymbol = currentSymbol;
                _lastDataTimeFrame = currentTimeFrame;

            }
            else
            {

            }
            
            CalculateReverseWatch(viewModel);
            return ChartUpdateAction.FullRefresh;
        }

        if (propertyName == nameof(ChartViewModel.IndicatorResults))
        {
            viewModel.RecalculateFutureOffset(); 
            action |= ChartUpdateAction.UpdateSnapshot;
        }

        // Check if property affects Effective Values (Settings)
        if (IsEffectiveValueProperty(propertyName))
        {
            _lastAlignedResults = null;
            _lastSourceIndicatorResults = null;
            SyncEffectiveValues(viewModel);
            
            // For Index-based charts (Renko, P&F, Kagi), changing parameters (Brick Size, ATR)
            // REQUIRES a full data recalculation (FullRefresh), not just a Snapshot update.
            // Snapshot update only re-slices existing converted data, but we need to RE-CONVERT.
            if (viewModel.ChartType.IsIndexBased())
            {
                action = ChartUpdateAction.FullRefresh;
                _needsViewAdaptation = true; // Re-adapt view to new data range
            }
            else
            {
                action |= ChartUpdateAction.UpdateSnapshot;
            }
            
            if (viewModel.ChartType == ChartType.ReverseWatch)
            {
                CalculateReverseWatch(viewModel);
                action = ChartUpdateAction.FullRefresh; // Reverse Watch also needs full recalc
            }
        }
        
        // Other properties that require snapshot update
        if (propertyName == nameof(ChartViewModel.ChartType))
        {
             _lastAlignedResults = null;
             _lastSourceIndicatorResults = null;
             SyncEffectiveValues(viewModel); 
             
             // WebAI Fix: Prevent forced time-synchronization (which snaps floating-point zoom to integers)
             // when switching between chart types that share the exact same 1:1 index array.
             bool Is1To1Mapped(ChartType type) => 
                 type == ChartType.Candlestick || type == ChartType.OHLCBar || 
                 type == ChartType.Line || type == ChartType.Area || type == ChartType.HeikinAshi;
             
             if (Is1To1Mapped(_currentChartType) && Is1To1Mapped(viewModel.ChartType))
             {
                 _needsViewAdaptation = false;
             }
             else
             {
                 _needsViewAdaptation = true; // Trigger view adaptation when crossing data structures (e.g. into Renko)
             }
             
             action |= ChartUpdateAction.UpdateSnapshot;
             
             if (viewModel.ChartType == ChartType.ReverseWatch)
             {
                 CalculateReverseWatch(viewModel);
             }
        }
        
        // Specific ReverseWatch params
        if (propertyName == nameof(ChartViewModel.ReverseWatchPeriod) ||
            propertyName == nameof(ChartViewModel.ReverseWatchDataCount) ||
            propertyName == nameof(ChartViewModel.ReverseWatchIsMaBased) ||
            propertyName == nameof(ChartViewModel.ReverseWatchIsLogScaleVolume))
        {
            if (viewModel.ChartType == ChartType.ReverseWatch)
            {
                CalculateReverseWatch(viewModel);
            }
            action |= ChartUpdateAction.UpdateSnapshot; 
        }

        if (propertyName == nameof(ChartViewModel.ChartMarginTop) ||
            propertyName == nameof(ChartViewModel.ChartMarginBottom) ||
            propertyName == nameof(ChartViewModel.ChartMarginRight) ||
            propertyName == nameof(ChartViewModel.ComparisonData) ||
            propertyName == nameof(ChartViewModel.ComparisonMode) ||
            propertyName == nameof(ChartViewModel.ComparisonZScorePeriod) ||
            propertyName == nameof(ChartViewModel.Indicators) ||
            propertyName == nameof(ChartViewModel.IndicatorResults) || // Trigger on new results arrival
            propertyName == nameof(ChartViewModel.IsSubWindowVisible) ||
            propertyName == nameof(ChartViewModel.EffectiveIsSubWindowVisible) ||
            propertyName == nameof(ChartViewModel.CanToggleIndicators) ||
            propertyName == nameof(ChartViewModel.ShowTickerInsteadOfValue) ||
            propertyName == nameof(ChartViewModel.ShowFloatingTooltip) ||
            propertyName == nameof(ChartViewModel.RenderTrigger))
        {
            action |= ChartUpdateAction.UpdateSnapshot;
        }

        return action;
    }

    private bool IsEffectiveValueProperty(string? prop)
    {
        if (string.IsNullOrEmpty(prop)) return false;
        
        switch (prop)
        {
            // Renko
            case nameof(ChartViewModel.RenkoSizingMode):
            case nameof(ChartViewModel.RenkoBrickSize):
            case nameof(ChartViewModel.RenkoBrickPercent):
            case nameof(ChartViewModel.RenkoAtrMultiplier):
            case nameof(ChartViewModel.RenkoAtrPeriod):
            case nameof(ChartViewModel.RenkoReversal):
            // P&F
            case nameof(ChartViewModel.PnfSizingMode):
            case nameof(ChartViewModel.PnfBoxSize):
            case nameof(ChartViewModel.PnfBoxPercent):
            case nameof(ChartViewModel.PnfAtrMultiplier):
            case nameof(ChartViewModel.PnfAtrPeriod):
            case nameof(ChartViewModel.PnfReversal):
            // Kagi
            case nameof(ChartViewModel.KagiReversalMode):
            case nameof(ChartViewModel.KagiReversalAmount):
            case nameof(ChartViewModel.KagiReversalPercent):
            case nameof(ChartViewModel.KagiAtrMultiplier):
            case nameof(ChartViewModel.KagiAtrPeriod):
            // Three Line Break
            case nameof(ChartViewModel.ThreeLineBreakLineCount):
            case nameof(ChartViewModel.ThreeLineBreakMinMove):
                return true;
        }
        return false;
    }

    #region Private Methods

    private ChartDataSnapshot CreateChartTypeSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        switch (viewModel.ChartType)
        {
            case ChartType.HeikinAshi:
                return CreateHeikinAshiSnapshot(viewModel, indicatorResults, effectiveVisibleCount, layout, isTransition);

            case ChartType.Renko:
                return CreateRenkoSnapshot(viewModel, indicatorResults, effectiveVisibleCount, layout, isTransition);

            case ChartType.PointAndFigure:
                return CreatePointAndFigureSnapshot(viewModel, indicatorResults, effectiveVisibleCount, layout, isTransition);

            case ChartType.Kagi:
                return CreateKagiSnapshot(viewModel, indicatorResults, effectiveVisibleCount, layout, isTransition);

            case ChartType.ThreeLineBreak:
                return CreateThreeLineBreakSnapshot(viewModel, indicatorResults, effectiveVisibleCount, layout, isTransition);

            case ChartType.ReverseWatch:
                return CreateReverseWatchSnapshot(viewModel, indicatorResults, effectiveVisibleCount, layout, isTransition);

            case ChartType.RelativePerformance:
                return CreateRelativePerformanceSnapshot(viewModel, indicatorResults, effectiveVisibleCount, layout, isTransition);

            default:
                _standardCandleBuffer.Clear();
                if (_standardCandleBuffer.Capacity < viewModel.Candles.Count) _standardCandleBuffer.Capacity = viewModel.Candles.Count;
                foreach (var c in viewModel.Candles)
                {
                    _standardCandleBuffer.Add(new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                }

                _convertedCandleCount = _standardCandleBuffer.Count;
                effectiveVisibleCount = _visibleCandleCount;

                return new ChartDataSnapshot(
                    _standardCandleBuffer,
                    viewModel.Symbol,
                    viewModel.SelectedTimeFrame.ToString(),
                    indicatorResults,
                    viewModel.Indicators,
                    null, // viewModel.Drawings (Legacy)
                    _startIndex,
                    effectiveVisibleCount,
                    paddingTopPx: StandardPricePaddingPx,
                    paddingBottomPx: StandardPricePaddingPx,
                    chartHeightPx: (decimal)layout.ChartArea.Height,
                    chartType: viewModel.ChartType,
                    allCandles: _standardCandleBuffer, // FIX: Use correct parameter name for IReadOnlyList ctor
                    priceRangeCalculator: ChartTypeProfileRegistry.Get(viewModel.ChartType).PriceRangeCalculator,
                    confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators));
        }
    }

    private ChartDataSnapshot CreateHeikinAshiSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        _heikinAshiBuffer.Clear();
        HeikinAshiConverter.Convert(viewModel.Candles, _heikinAshiBuffer);

        _convertedCandleCount = _heikinAshiBuffer.Count;
        effectiveVisibleCount = _visibleCandleCount;

        return new ChartDataSnapshot(
            _heikinAshiBuffer,
            viewModel.Symbol,
            viewModel.SelectedTimeFrame.ToString(),
            indicatorResults,
            viewModel.Indicators,
            null,
            _startIndex,
            effectiveVisibleCount,
            paddingTopPx: StandardPricePaddingPx, 
            paddingBottomPx: StandardPricePaddingPx, 
            chartHeightPx: (decimal)layout.ChartArea.Height,
            chartType: ChartType.HeikinAshi,
            allCandles: _heikinAshiBuffer, // FIX: Use correct parameter name for IReadOnlyList ctor
            priceRangeCalculator: ChartTypeProfileRegistry.Get(ChartType.HeikinAshi).PriceRangeCalculator,
            confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators));
    }
    
    private void AdaptViewToTimeRange(ChartViewModel viewModel, IReadOnlyList<CoreCandleData> transformedCandles, DateTime endTime)
    {
        // Smoothly translates views when switching chart types by anchoring the RIGHT edge to the true time
        // while perfectly maintaining the exact Zoom Scale (VisibleUnits) to prevent UI distortion (zoom in/out bugs).
        
        if (transformedCandles.Count == 0 || endTime == DateTime.MaxValue)
        {
            _visibleCandleCount = 1;
            _startIndex = 0;
            return;
        }

        // Find the index of the candle that matches the RIGHT edge (endTime)
        int anchorIdx = -1;
        for (int i = transformedCandles.Count - 1; i >= 0; i--)
        {
            if (transformedCandles[i].Timestamp <= endTime)
            {
                anchorIdx = i;
                break;
            }
        }
        
        if (anchorIdx == -1) anchorIdx = 0;

        // Restore the exact mathematical zoom level using ITEM COUNT (invariant to coordinate families)
        double preferredVisibleUnits = _lastVisibleItemCount;
        if (IsStandardChart(_currentChartType))
        {
             preferredVisibleUnits *= GetTimeInterval(viewModel.SelectedTimeFrame).Ticks;
        }

        // WebAI Fix: Prevent astronomical zoom-outs (coordinate extrapolation bugs)
        // For Ticks (Standard), cap to 2x total data time. 
        // For Index (Special), cap to a tighter ratio (1.5x) to maintain visual clarity on transitions.
        double maxDurationUnits = IsStandardChart(_currentChartType) 
            ? Math.Max(TimeSpan.FromDays(30).Ticks, (long)transformedCandles.Count * GetTimeInterval(viewModel.SelectedTimeFrame).Ticks * 2)
            : Math.Max(50, (double)transformedCandles.Count * 1.5);

        if (preferredVisibleUnits > maxDurationUnits) preferredVisibleUnits = maxDurationUnits;
        if (preferredVisibleUnits < 2) preferredVisibleUnits = 2; // Sanity minimum




        // Target End Unit (anchor to the right side, add a slight padding so candle isn't cut off)
        // WebAI Fix: If transferring into a TimeSeries map, the Viewport runs on raw absolute Ticks, not Array Indices.
        double targetEndUnit;
        if (IsStandardChart(_currentChartType))
        {
            long intervalTicks = GetTimeInterval(viewModel.SelectedTimeFrame).Ticks;
            targetEndUnit = transformedCandles[anchorIdx].Timestamp.Ticks + (intervalTicks / 2.0);
        }
        else
        {
            targetEndUnit = anchorIdx + 0.5;
        }
        
        double targetStartUnit = targetEndUnit - preferredVisibleUnits;

        // Removed soft left clamp. We want to preserve the right-aligned anchor (targetEndUnit).
        // If targetStartUnit is negative, the left half of the screen will naturally be empty, 
        // which perfectly simulates zooming out further than the available data.

        // WebAI Fix: Intentionally Force the range to securely mutate the Viewport bounds in one mathematical atomic wave.
        Viewport.ForceVisibleRange(targetStartUnit, targetEndUnit);

        // Update standard integer bounds for Snapshot slice generation
        _startIndex = Math.Max(0, (int)Math.Floor(targetStartUnit));
        _visibleCandleCount = (int)Math.Ceiling(preferredVisibleUnits + (targetStartUnit - _startIndex)) + 1;
        
        if (_startIndex + _visibleCandleCount > transformedCandles.Count)
        {
             _startIndex = Math.Max(0, transformedCandles.Count - Math.Max(1, _visibleCandleCount));
             if (_startIndex + _visibleCandleCount > transformedCandles.Count)
             {
                 _visibleCandleCount = transformedCandles.Count - _startIndex;
             }
        }
    }

    /// <summary>
    /// Adjusts _startIndex and _visibleCandleCount for special (non-time-based) charts.
    /// On data reload or chart-type switch (_needsViewAdaptation), adapts to the Viewport time range.
    /// Otherwise, simply clamps bounds to preserve user zoom/pan state.
    /// </summary>
    private void AdjustSpecialChartView(ChartViewModel viewModel, IReadOnlyList<CoreCandleData> transformedCandles, bool isTransition)
    {
        // Update true data bounds in Viewport based on converted count
        Viewport.SetDataBounds(0, Math.Max(1, transformedCandles.Count) - 1);
        // WebAI Fix: Set future offset for index charts so Pan/Zoom right-clamp works correctly.
        // Without this, _futureEndUnit == count-1 and any zoom clamps the chart to the left.
        Viewport.SetFutureOffsetUnits(_currentFutureOffset);

        if (_needsViewAdaptation || isTransition)
        {
            var newFamily = GetChartFamily(viewModel.ChartType);
            
            // WebAI Fix: Use "Full Range" for Index-Series on ACTUAL coordinate transitions (chart family switch)
            // ReverseWatch (Excluded) is an XY plot that NEVER supports zoom, so it MUST ALWAYS be forced to Full Range 
            // to ensure its PriceRangeCalculator sees all data points.
            if ((isTransition && newFamily == ChartFamily.IndexSeries) || newFamily == ChartFamily.Excluded)
            {
                _startIndex = 0;
                _visibleCandleCount = transformedCandles.Count;
                
                // Force viewport to cover the entire data set
                // WebAI Fix: Disable pinning to allow the chart to fill the screen from index 0 
                // without being shifted by the future offset margin.
                if (newFamily == ChartFamily.IndexSeries)
                {
                    Viewport.IsRightPinned = false;
                }
                
                double targetEnd = Math.Max(1, transformedCandles.Count);
                Viewport.ForceVisibleRange(0, targetEnd);
                

            }
            else if (_adaptationEndTime.HasValue && newFamily != ChartFamily.IndexSeries)
            {
                // Smoothly translate views when switching within the same family or time-based sync
                // WebAI Fix: NEVER call this for IndexSeries - time anchors don't map to index positions
                AdaptViewToTimeRange(viewModel, transformedCandles, _adaptationEndTime.Value);
            }
            else
            {
                // Very first load: Use naturally initialized Viewport bounds (Index Mode - default 100 items)
                _startIndex = Math.Max(0, (int)Math.Floor(Viewport.VisibleStartUnit));
                _visibleCandleCount = (int)Math.Ceiling(Viewport.VisibleEndUnit - Viewport.VisibleStartUnit) + 1;
                
                if (_startIndex + _visibleCandleCount > transformedCandles.Count)
                {
                    _startIndex = Math.Max(0, transformedCandles.Count - Math.Max(1, _visibleCandleCount));
                    if (_startIndex + _visibleCandleCount > transformedCandles.Count)
                    {
                        _visibleCandleCount = transformedCandles.Count - _startIndex;
                    }
                }
                
                // Sync the actual viewport to our clamped values
                Viewport.ForceVisibleRange(_startIndex, _startIndex + _visibleCandleCount - 1);
            }
            
            _needsViewAdaptation = false;
        }
        else
        {
            // Normal operation: clamp to bounds (preserve user zoom/pan)
            if (transformedCandles.Count == 0)
            {
                _visibleCandleCount = 1;
                _startIndex = 0;
            }
            else
            {
                // WebAI Fix: Special chart bounds must include the Future Offset (maxOffset) 
                // to allow smooth right scrolling and visible right margins.
                // We use double-precision boundaries for clamping to prevent jitter.
                double effectiveTotalCount = transformedCandles.Count + _currentFutureOffset;

                // Validate _startIndex and _visibleCandleCount against effective bounds
                // but DO NOT force Viewport back to integer indices during normal operation.
                // This preserves sub-unit panning precision and eliminates jitter.
                
                double visibleDuration = Viewport.VisibleEndUnit - Viewport.VisibleStartUnit;
                if (visibleDuration > effectiveTotalCount) visibleDuration = effectiveTotalCount;
                if (visibleDuration < 2) visibleDuration = 2;

                double maxStart = effectiveTotalCount - visibleDuration;
                if (maxStart < 0) maxStart = 0;

                double currentStart = Viewport.VisibleStartUnit;
                if (currentStart > maxStart) currentStart = maxStart;
                if (currentStart < 0) currentStart = 0;

                // Update internal indices for SNAPSHOT creation (integer based)
                _startIndex = (int)Math.Floor(currentStart);
                _visibleCandleCount = (int)Math.Ceiling(visibleDuration + (currentStart - _startIndex)) + 1;
                
                // Ensure we don't pick more than available
                if (_startIndex + _visibleCandleCount > transformedCandles.Count)
                {
                     // Snapshot indices don't include future offset (renderer handles that via Transform)
                     // but they can go up to Count.
                     if (_startIndex >= transformedCandles.Count)
                     {
                          _startIndex = Math.Max(0, transformedCandles.Count - 1);
                          _visibleCandleCount = 1;
                     }
                     else if (_startIndex + _visibleCandleCount > transformedCandles.Count)
                     {
                          _visibleCandleCount = transformedCandles.Count - _startIndex;
                     }
                }
            }
        }

        // REMOVED: Viewport.ForceVisibleRange(_startIndex, _startIndex + _visibleCandleCount - 1);
        // This was the cause of scrolling jitter as it snapped fractional panning back to integers.
    }

    private ChartDataSnapshot CreateRenkoSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        _renkoBuffer.Clear();
        
        if (viewModel.RenkoDataAdapter != null)
        {
            int count = viewModel.RenkoDataAdapter.Count;
            var times = viewModel.RenkoDataAdapter.Timestamps.Span;
            var opens = viewModel.RenkoDataAdapter.Opens.Span;
            var highs = viewModel.RenkoDataAdapter.Highs.Span;
            var lows = viewModel.RenkoDataAdapter.Lows.Span;
            var closes = viewModel.RenkoDataAdapter.Closes.Span;
            var vols = viewModel.RenkoDataAdapter.Volumes.Span;
            
            // Pre-allocate capacity if needed
            if (_renkoBuffer.Capacity < count) _renkoBuffer.Capacity = count;
            
            for(int i = 0; i < count; i++)
            {
                _renkoBuffer.Add(new CoreCandleData(times[i], opens[i], highs[i], lows[i], closes[i], vols[i]));
            }
        }
        else
        {
            RenkoConverter.Convert(viewModel.Candles, viewModel.EffectiveRenkoBrickSize, _renkoBuffer, (ChartRoundingMode)viewModel.RenkoRoundingMode);
        }

        _convertedCandleCount = _renkoBuffer.Count;

        
        // Adapt or clamp view
        AdjustSpecialChartView(viewModel, _renkoBuffer, isTransition);
        effectiveVisibleCount = _visibleCandleCount;

        // Execute Multi-Wave Pattern Engine on the whole buffer only if enabled
        IReadOnlyList<MultiWaveSignal>? mwSignals = null;
        if ((viewModel.RenkoShowMultiWavePatterns || viewModel.RenkoShowGhostProjections) && _renkoBuffer.Count > 0)
        {
            _mwEngine.Clear();
            foreach (var b in _renkoBuffer) _mwEngine.ProcessNextBlock(b);
            mwSignals = _mwEngine.Signals.ToArray(); // Materialize to a new array to prevent clearing
        }

        return new ChartDataSnapshot(
            _renkoBuffer,
            viewModel.Symbol,
            viewModel.SelectedTimeFrame.ToString(),
            indicatorResults,
            viewModel.Indicators,
            null, // drawings
            startIndex: _startIndex,
            count: effectiveVisibleCount,
            paddingTopPx: StandardPricePaddingPx, 
            paddingBottomPx: StandardPricePaddingPx, 
            chartHeightPx: (decimal)layout.ChartArea.Height,
            minBrickSize: viewModel.EffectiveRenkoBrickSize,
            pnfAnalysis: null,
            multiWaveSignals: mwSignals,
            allCandles: _renkoBuffer,
            chartType: ChartType.Renko,
            priceRangeCalculator: ChartTypeProfileRegistry.Get(ChartType.Renko).PriceRangeCalculator,
            confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators));
    }




    private ChartDataSnapshot CreatePointAndFigureSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        _pnfBuffer.Clear();
        
        if (viewModel.PnfDataAdapter != null)
        {
            int count = viewModel.PnfDataAdapter.Count;
            var times = viewModel.PnfDataAdapter.Timestamps.Span;
            var opens = viewModel.PnfDataAdapter.Opens.Span;
            var highs = viewModel.PnfDataAdapter.Highs.Span;
            var lows = viewModel.PnfDataAdapter.Lows.Span;
            var closes = viewModel.PnfDataAdapter.Closes.Span;
            var vols = viewModel.PnfDataAdapter.Volumes.Span;
            
            if (_pnfBuffer.Capacity < count) _pnfBuffer.Capacity = count;
            
            for(int i = 0; i < count; i++)
            {
                _pnfBuffer.Add(new CoreCandleData(times[i], opens[i], highs[i], lows[i], closes[i], vols[i]));
            }
        }
        else
        {
            PointAndFigureConverter.Convert(
                viewModel.Candles, viewModel.EffectivePnfBoxSize, viewModel.PnfReversal, _pnfBuffer, (ChartRoundingMode)viewModel.PnfRoundingMode);
        }

        _convertedCandleCount = _pnfBuffer.Count;


        // Adapt or clamp view
        AdjustSpecialChartView(viewModel, _pnfBuffer, isTransition);
        effectiveVisibleCount = _visibleCandleCount;

        // Try to retrieve pre-calculated P&F analysis from indicator CustomData
        PnfAnalysisResult? pnfAnalysis = null;
        if (indicatorResults != null)
        {
            var pnfIndicator = indicatorResults.Values.FirstOrDefault(r => r.CustomData is PnfAnalysisResult);
            if (pnfIndicator != null)
            {
                pnfAnalysis = (PnfAnalysisResult)pnfIndicator.CustomData!;
            }
        }

        // Fallback to manual analysis only if indicator is not present
        if (pnfAnalysis == null)
        {
            pnfAnalysis = PointAndFigurePatternEngine.Analyze(_pnfBuffer, viewModel.EffectivePnfBoxSize, _pnfSignalsBuffer, _pnfActiveLinesBuffer);
        }

        // Execute Multi-Wave Pattern Engine on P&F blocks
        IReadOnlyList<MultiWaveSignal>? mwSignals = null;
        if ((viewModel.PnfShowMultiWavePatterns || viewModel.PnfShowGhostProjections) && _pnfBuffer.Count > 0)
        {
            _mwEngine.Clear();
            foreach (var b in _pnfBuffer) _mwEngine.ProcessNextBlock(b);
            mwSignals = _mwEngine.Signals.ToArray(); // Materialize to a new array to prevent clearing
        }

        return new ChartDataSnapshot(
            _pnfBuffer,
            viewModel.Symbol,
            viewModel.SelectedTimeFrame.ToString(),
            indicatorResults,
            viewModel.Indicators,
            null, // drawings
            startIndex: _startIndex,
            count: effectiveVisibleCount,
            paddingTopPx: StandardPricePaddingPx, 
            paddingBottomPx: StandardPricePaddingPx, 
            chartHeightPx: (decimal)layout.ChartArea.Height,
            minBrickSize: viewModel.EffectivePnfBoxSize,
            maxBrickSize: viewModel.EffectivePnfBoxSize,
            pnfAnalysis: pnfAnalysis,
            multiWaveSignals: mwSignals,
            allCandles: _pnfBuffer,
            chartType: ChartType.PointAndFigure,
            // FR-PNF-02: ボックスサイズに基づくマージン込み価格範囲（最高値+1ボックス、最安値はスナップ切り捨て）
            priceRangeCalculator: PnfProfile.GetPriceRangeCalculator(viewModel.EffectivePnfBoxSize),
            confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators));
    }

    private ChartDataSnapshot CreateKagiSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        _kagiBuffer.Clear();

        if (viewModel.KagiDataAdapter != null)
        {
            int count = viewModel.KagiDataAdapter.Count;
            var times = viewModel.KagiDataAdapter.Timestamps.Span;
            var opens = viewModel.KagiDataAdapter.Opens.Span;
            var highs = viewModel.KagiDataAdapter.Highs.Span;
            var lows = viewModel.KagiDataAdapter.Lows.Span;
            var closes = viewModel.KagiDataAdapter.Closes.Span;
            var vols = viewModel.KagiDataAdapter.Volumes.Span;
            
            if (_kagiBuffer.Capacity < count) _kagiBuffer.Capacity = count;
            
            for(int i = 0; i < count; i++)
            {
                _kagiBuffer.Add(new CoreCandleData(times[i], opens[i], highs[i], lows[i], closes[i], vols[i]));
            }
        }
        else
        {
            var kagiType = viewModel.KagiReversalMode switch
            {
                ChartSizingMode.Percentage => KagiConverter.ReversalType.Percent,
                ChartSizingMode.AutoAtr => KagiConverter.ReversalType.ATR,
                _ => KagiConverter.ReversalType.Fixed
            };

            KagiConverter.Convert(
                viewModel.Candles, 
                viewModel.KagiReversalAmount, 
                kagiType, 
                viewModel.KagiAtrPeriod, 
                viewModel.KagiAtrMultiplier,
                (ChartRoundingMode)viewModel.KagiRoundingMode,
                (AutoFallbackMode)viewModel.KagiFallbackMode,
                _kagiIntermediateBuffer);

            if (_kagiBuffer.Capacity < _kagiIntermediateBuffer.Count) _kagiBuffer.Capacity = _kagiIntermediateBuffer.Count;
            for (int i = 0; i < _kagiIntermediateBuffer.Count; i++)
            {
                var c = _kagiIntermediateBuffer[i];
                // IMPORTANT: MultiWavePatternEngine requires real High/Low prices to calculate targets.
                // We use Volume to store the IsYang (State) flag for the KagiRenderer.
                _kagiBuffer.Add(new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.IsYang ? 1L : 0L));
            }
        }

        _convertedCandleCount = _kagiBuffer.Count;
        viewModel.ConvertedKagiCandles = _kagiBuffer;
        
        // Calculate total Columns for viewport adjustment & build ColumnMap
        int totalColumns = 0;
        _kagiColumnMapBuffer.Clear();
        if (_kagiBuffer.Count > 0)
        {
            totalColumns = 1;
            int currentColumnIdx = viewModel.KagiInitialColumn;
            _kagiColumnMapBuffer.Add(currentColumnIdx);

            for (int i = 1; i < _kagiBuffer.Count; i++)
            {
                // Detect flat anchor at index 0 (Open == Close): always place index 1 on a new column
                bool isAnchorTransition = (i == 1 && _kagiBuffer[0].Open == _kagiBuffer[0].Close);
                bool isDirectionChange = (_kagiBuffer[i].Close >= _kagiBuffer[i].Open) != (_kagiBuffer[i - 1].Close >= _kagiBuffer[i - 1].Open);
                
                if (isDirectionChange || isAnchorTransition)
                {
                    totalColumns++;
                    currentColumnIdx++;
                }
                _kagiColumnMapBuffer.Add(currentColumnIdx);
            }
        }

        // Kagi-specific viewport management: uses totalColumns (not segment count) for scroll bounds.
        // Cannot use AdjustSpecialChartView because it clamps against segment count, which is much 
        // larger than totalColumns and would allow scrolling past the actual Kagi data.
        int safeColumns = Math.Max(1, totalColumns);
        Viewport.SetDataBounds(0, safeColumns - 1);
        Viewport.SetFutureOffsetUnits(_currentFutureOffset);

        if (_needsViewAdaptation || isTransition)
        {
            var newFamily = GetChartFamily(viewModel.ChartType);
            if ((isTransition && newFamily == ChartFamily.IndexSeries) || newFamily == ChartFamily.Excluded)
            {
                Viewport.IsRightPinned = false;
            }
            Viewport.ForceVisibleRange(0, safeColumns);
            _needsViewAdaptation = false;
        }
        else
        {
            // Normal operation: clamp viewport to column-based bounds
            double effectiveTotal = safeColumns + _currentFutureOffset;
            double visibleDuration = Viewport.VisibleEndUnit - Viewport.VisibleStartUnit;
            if (visibleDuration > effectiveTotal) visibleDuration = effectiveTotal;
            if (visibleDuration < 2) visibleDuration = 2;

            double maxStart = effectiveTotal - visibleDuration;
            if (maxStart < 0) maxStart = 0;

            double currentStart = Viewport.VisibleStartUnit;
            if (currentStart > maxStart) currentStart = maxStart;
            if (currentStart < 0) currentStart = 0;

            // Only force if out of bounds to preserve sub-unit panning precision
            if (Viewport.VisibleStartUnit < 0 || Viewport.VisibleEndUnit > effectiveTotal)
            {
                Viewport.ForceVisibleRange(currentStart, currentStart + visibleDuration);
            }
        }

        // Kagi always renders ALL segments regardless of viewport zoom
        _startIndex = 0;
        _visibleCandleCount = _kagiBuffer.Count;
        effectiveVisibleCount = _visibleCandleCount;

        // Calculate absolute column index for the visible start
        int initialColumn = 0;
        for (int i = 1; i <= _startIndex && i < _kagiBuffer.Count; i++)
        {
            var curr = _kagiBuffer[i];
            var prev = _kagiBuffer[i - 1];
            bool currUp = curr.Close >= curr.Open;
            bool prevUp = prev.Close >= prev.Open;
            if (currUp != prevUp)
            {
                initialColumn++;
            }
        }
        viewModel.KagiInitialColumn = initialColumn;

        // Execute Multi-Wave Pattern Engine
        IReadOnlyList<MultiWaveSignal>? mwSignals = null;
        if ((viewModel.KagiShowMultiWavePatterns || viewModel.KagiShowGhostProjections) && _kagiBuffer.Count > 0)
        {
            _mwEngine.Clear();
            foreach (var b in _kagiBuffer) _mwEngine.ProcessNextBlock(b);
            mwSignals = _mwEngine.Signals.ToArray(); // Materialize to a new array to prevent clearing
        }

        return new ChartDataSnapshot(
            candles: _kagiBuffer,
            symbol: viewModel.Symbol,
            timeframe: viewModel.SelectedTimeFrame.ToString(),
            indicatorResults: indicatorResults,
            indicatorSettings: viewModel.Indicators,
            drawings: null,
            startIndex: _startIndex,
            count: effectiveVisibleCount,
            paddingTopPx: StandardPricePaddingPx,
            paddingBottomPx: StandardPricePaddingPx,
            chartHeightPx: (decimal)layout.ChartArea.Height,
            minBrickSize: viewModel.EffectiveKagiReversalAmount,
            maxBrickSize: viewModel.EffectiveKagiReversalAmount,
            pnfAnalysis: null,
            multiWaveSignals: mwSignals,
            allPnfCandles: _kagiBuffer,
            chartType: ChartType.Kagi,
            threeLineBreakCount: null,
            priceRangeCalculator: ChartTypeProfileRegistry.Get(ChartType.Kagi).PriceRangeCalculator,
            confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators),
            renderingMetadata: null,
            comparisonSeries: null,
            overrideMinPrice: null,
            overrideMaxPrice: null,
            visibleCandleCount: effectiveVisibleCount,
            kagiColumnMap: _kagiColumnMapBuffer.ToArray());
    }

    private ChartDataSnapshot CreateRelativePerformanceSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        try
        {
            _standardCandleBuffer.Clear();
            if (_standardCandleBuffer.Capacity < viewModel.Candles.Count) _standardCandleBuffer.Capacity = viewModel.Candles.Count;
            foreach (var c in viewModel.Candles)
            {
                _standardCandleBuffer.Add(new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
            }

            _convertedCandleCount = _standardCandleBuffer.Count;
            decimal globalMin = decimal.MaxValue;
            decimal globalMax = decimal.MinValue;
            int safeStart = Math.Max(0, _startIndex);
            int visibleSliceCount = Math.Min(_visibleCandleCount, _standardCandleBuffer.Count - safeStart);
            if (visibleSliceCount <= 0) visibleSliceCount = 0;
            effectiveVisibleCount = visibleSliceCount;

            if (viewModel.ComparisonData != null)
            {
                var priceCalc = new ComparisonPriceRangeCalculator(viewModel.ComparisonData, _startIndex, viewModel.ComparisonMode, viewModel.ComparisonZScorePeriod);
                var comparisonSeries = new Dictionary<string, decimal?[]>();
                var comparisonData = viewModel.ComparisonData;
                comparisonData.Series.TryGetValue(comparisonData.PrimarySymbol, out var primarySeries);

                var renderingMetadata = new Dictionary<string, string>();
                var symbols = comparisonData.Series.Keys.OrderBy(s => s).ToList();
                
                foreach (var symbol in symbols)
                {
                    int colorIndex = viewModel.SeriesColorIndex.GetOrAdd(symbol);
                    var series = comparisonData.Series[symbol];
                    if (series == null || series.Length == 0) continue;

                    int baseSearchStart = Math.Max(0, _startIndex); 
                    decimal? foundBase = null;
                    if (series.Length > baseSearchStart && series[baseSearchStart] is { } exact) 
                    {
                        foundBase = exact.Close;
                    }
                    else if (series.Length > 0)
                    {
                        int actualBaseIdx = Math.Min(baseSearchStart, series.Length - 1);
                        for (int i = actualBaseIdx; i >= 0; i--) { if (series[i] is { } prev) { foundBase = prev.Close; break; } }
                        if (foundBase == null)
                        {
                            for (int i = actualBaseIdx + 1; i < series.Length; i++) { if (series[i] is { } next) { foundBase = next.Close; break; } }
                        }
                    }
                    decimal basePrice = foundBase ?? 0;

                    var buffer = new decimal?[visibleSliceCount];
                    decimal? lastVisibleValue = null;

                    for (int i = 0; i < visibleSliceCount; i++)
                    {
                        int sourceIdx = safeStart + i;
                        if (sourceIdx >= series.Length || !series[sourceIdx].HasValue) continue;

                        var currentCandle = series[sourceIdx]!.Value;
                        decimal val = 0;
                        bool isValid = false;

                        switch (viewModel.ComparisonMode)
                        {
                            case ComparisonMode.Performance:
                                if (basePrice != 0) { val = (currentCandle.Close - basePrice) / basePrice * 100m; isValid = true; }
                                break;
                            case ComparisonMode.Ratio:
                                if (primarySeries != null && sourceIdx < primarySeries.Length && primarySeries[sourceIdx] is { } ratioPc && ratioPc.Close != 0)
                                { val = currentCandle.Close / ratioPc.Close; isValid = true; }
                                break;
                            case ComparisonMode.ZScore:
                                if (ComparisonPriceRangeCalculator.ComputeZScore(series, sourceIdx, viewModel.ComparisonZScorePeriod, out var z))
                                { val = (decimal)z; isValid = true; }
                                break;
                            case ComparisonMode.Spread:
                                if (primarySeries != null && sourceIdx < primarySeries.Length && primarySeries[sourceIdx] is { } spreadPc)
                                { val = currentCandle.Close - spreadPc.Close; isValid = true; }
                                break;
                        }

                        if (isValid)
                        {
                            buffer[i] = val;
                            lastVisibleValue = val;
                            bool isStaticBase = (viewModel.ComparisonMode == ComparisonMode.Ratio || viewModel.ComparisonMode == ComparisonMode.Spread) && symbol == comparisonData.PrimarySymbol;
                            if (!isStaticBase)
                            {
                                if (val < globalMin) globalMin = val;
                                if (val > globalMax) globalMax = val;
                            }
                        }
                    }

                    comparisonSeries[symbol] = buffer;

                    if (lastVisibleValue.HasValue)
                    {
                        string trendPrefix = lastVisibleValue.Value >= 0 ? "+" : "";
                        renderingMetadata[symbol] = viewModel.ComparisonMode switch
                        {
                            ComparisonMode.Performance => $"{trendPrefix}{lastVisibleValue.Value.ToString(ChartConstants.DefaultRelativePerformanceFormat)}{ChartConstants.DefaultRelativePerformanceSuffix}",
                            ComparisonMode.Ratio => lastVisibleValue.Value.ToString(ChartConstants.DefaultRatioFormat),
                            ComparisonMode.ZScore => $"{ChartConstants.ZScorePrefix}{lastVisibleValue.Value.ToString(ChartConstants.DefaultZScoreFormat)}",
                            ComparisonMode.Spread => lastVisibleValue.Value.ToString(ChartConstants.DefaultSpreadFormat),
                            _ => lastVisibleValue.Value.ToString("F2")
                        };
                    }
                }

                return new ChartDataSnapshot(
                    _standardCandleBuffer,
                    viewModel.Symbol,
                    viewModel.SelectedTimeFrame.ToString(),
                    indicatorResults,
                    viewModel.Indicators,
                    null,
                    _startIndex,
                    effectiveVisibleCount,
                    paddingTopPx: (decimal)layout.MarginTop,
                    paddingBottomPx: (decimal)layout.MarginBottom,
                    chartHeightPx: (decimal)layout.ChartArea.Height,
                    chartType: viewModel.ChartType,
                    priceRangeCalculator: priceCalc,
                    confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators),
                    renderingMetadata: renderingMetadata,
                    comparisonSeries: comparisonSeries,
                    overrideMinPrice: globalMin != decimal.MaxValue ? globalMin : null,
                    overrideMaxPrice: globalMax != decimal.MinValue ? globalMax : null);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ComparisonChart] Snapshot creation failed: {Message}", ex.Message);
            System.Diagnostics.Debug.WriteLine($"[ComparisonChart] Snapshot creation failed: {ex.Message}");
        }

        // FALLBACK: Return standard candle snapshot if comparison fails or is missing
        return new ChartDataSnapshot(
            _standardCandleBuffer,
            viewModel.Symbol,
            viewModel.SelectedTimeFrame.ToString(),
            indicatorResults,
            viewModel.Indicators,
            null,
            _startIndex,
            effectiveVisibleCount,
            paddingTopPx: StandardPricePaddingPx,
            paddingBottomPx: StandardPricePaddingPx,
            chartHeightPx: (decimal)layout.ChartArea.Height,
            chartType: viewModel.ChartType,
            allCandles: _standardCandleBuffer,
            priceRangeCalculator: StandardPriceRangeCalculator.Instance,
            confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators));
    }

    private ChartDataSnapshot CreateThreeLineBreakSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        bool needsReconvert = _tlbBuffer.Count == 0 || 
                             viewModel.Symbol != _lastSymbol ||
                             viewModel.SelectedTimeFrame.ToString() != _lastTimeFrame ||
                             viewModel.Candles.Count != _lastSourceCandleCount ||
                             viewModel.ThreeLineBreakLineCount != _lastTlbLineCount ||
                             viewModel.ThreeLineBreakMinMove != _lastTlbMinMove;

        if (needsReconvert)
        {
            ThreeLineBreakConverter.ConvertToCoreCandleData(viewModel.Candles, _tlbBuffer, _tlbBlockBuffer, viewModel.ThreeLineBreakLineCount, viewModel.ThreeLineBreakMinMove);
            
            _lastSymbol = viewModel.Symbol;
            _lastTimeFrame = viewModel.SelectedTimeFrame.ToString();
            _lastSourceCandleCount = viewModel.Candles.Count;
            _lastTlbLineCount = viewModel.ThreeLineBreakLineCount;
            _lastTlbMinMove = viewModel.ThreeLineBreakMinMove;
            
            // Invalidate indicator cache if blocks changed
            _lastAlignedResults = null;
            _lastSourceIndicatorResults = null;
        }

        _convertedCandleCount = _tlbBuffer.Count;
        
        // Adapt or clamp view
        AdjustSpecialChartView(viewModel, _tlbBuffer, isTransition);
        effectiveVisibleCount = _visibleCandleCount;

        // Execute Multi-Wave Pattern Engine
        IReadOnlyList<MultiWaveSignal>? mwSignals = null;
        if ((viewModel.TlbShowMultiWavePatterns || viewModel.TlbShowGhostProjections) && _tlbBuffer.Count > 0)
        {
            _mwEngine.Clear();
            foreach (var b in _tlbBuffer) _mwEngine.ProcessNextBlock(b);
            mwSignals = _mwEngine.Signals.ToArray(); // Materialize to a new array to prevent clearing
        }

        // Phase 5: Zero-Allocation Indicator Alignment Cache
        // Only re-align if indicator results changed or blocks were re-converted
        if (_lastAlignedResults == null || !ReferenceEquals(indicatorResults, _lastSourceIndicatorResults))
        {
            int[] blockToCandleMapping = CalculateIndexMapping(_tlbBuffer, viewModel.Candles);
            _lastAlignedResults = AlignIndicatorsToIndexBased(indicatorResults, _tlbBuffer, viewModel.Candles, blockToCandleMapping);
            _lastSourceIndicatorResults = indicatorResults;
        }

        return new ChartDataSnapshot(
            _tlbBuffer,
            viewModel.Symbol,
            viewModel.SelectedTimeFrame.ToString(),
            _lastAlignedResults,
            viewModel.Indicators,
            null, // drawings
            startIndex: _startIndex,
            count: effectiveVisibleCount,
            paddingTopPx: StandardPricePaddingPx, 
            paddingBottomPx: StandardPricePaddingPx, 
            chartHeightPx: (decimal)layout.ChartArea.Height,
            pnfAnalysis: null,
            multiWaveSignals: mwSignals,
            allCandles: _tlbBuffer,
            chartType: ChartType.ThreeLineBreak,
            priceRangeCalculator: ChartTypeProfileRegistry.Get(ChartType.ThreeLineBreak).PriceRangeCalculator,
            confluence: GetConfluenceForIndex(_convertedCandleCount - 1, indicatorResults, viewModel.Indicators));
    }

    private int[] CalculateIndexMapping(IReadOnlyList<CoreCandleData> targetBlocks, IReadOnlyList<CoreCandleData> originalCandles)
    {
        if (targetBlocks.Count == 0 || originalCandles.Count == 0) return Array.Empty<int>();
        
        int[] mapping = new int[targetBlocks.Count];
        int candleIdx = 0;
        for (int i = 0; i < targetBlocks.Count; i++)
        {
            var blockTime = targetBlocks[i].Timestamp;
            while (candleIdx < originalCandles.Count - 1 && originalCandles[candleIdx + 1].Timestamp <= blockTime)
            {
                candleIdx++;
            }
            mapping[i] = candleIdx;
        }
        return mapping;
    }

    private IReadOnlyDictionary<string, IIndicatorResult>? AlignIndicatorsToIndexBased(
        IReadOnlyDictionary<string, IIndicatorResult>? originalResults,
        IReadOnlyList<CoreCandleData> targetBlocks,
        IReadOnlyList<CoreCandleData> originalCandles,
        int[] indexMapping)
    {
        if (originalResults == null || originalResults.Count == 0 || targetBlocks.Count == 0)
            return originalResults ?? new Dictionary<string, IIndicatorResult>();

        var alignedResults = new Dictionary<string, IIndicatorResult>(originalResults.Count);
        foreach (var kvp in originalResults)
        {
            alignedResults[kvp.Key] = new IndexAlignedIndicatorResult(kvp.Value, indexMapping);
        }
        return alignedResults;
    }

    private class IndexAlignedIndicatorResult : IIndicatorResult
    {
        private readonly IIndicatorResult _original;
        private readonly int[] _indexMapping;
        private readonly Dictionary<string, IReadOnlyList<decimal?>> _listCache = new();

        public IndexAlignedIndicatorResult(IIndicatorResult original, int[] indexMapping)
        {
            _original = original;
            _indexMapping = indexMapping;
        }

        public bool IsSuccessful => _original.IsSuccessful;
        public string? ErrorMessage => _original.ErrorMessage;
        public IReadOnlyList<decimal?> MainValues => GetSeries("Main");
        public bool HasSeries(string name) => _original.HasSeries(name);
        public IReadOnlyList<decimal?> GetSeries(string name) 
        {
            if (_listCache.TryGetValue(name, out var cached)) return cached;

            var originalSeries = _original.GetSeries(name);
            if (originalSeries.Count == 0) return originalSeries;

            var virtualList = new VirtualAlignedList(originalSeries, _indexMapping);
            _listCache[name] = virtualList;
            return virtualList;
        }
        public IEnumerable<string> SeriesNames => _original.SeriesNames;
        public IReadOnlyList<string> SeriesNamesList => _original.SeriesNamesList;
        public IReadOnlyDictionary<string, string> SeriesLabels => _original.SeriesLabels;
        public object? CustomData => _original.CustomData;
        public IConfluenceSignalProvider? SignalProvider => _original.SignalProvider;
        public decimal? this[int index] => MainValues[index];
        public int Count => MainValues.Count;
        public IEnumerator<decimal?> GetEnumerator() => MainValues.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private class VirtualAlignedList : IReadOnlyList<decimal?>
        {
            private readonly IReadOnlyList<decimal?> _original;
            private readonly int[] _indexMapping;

            public VirtualAlignedList(IReadOnlyList<decimal?> original, int[] indexMapping)
            {
                _original = original;
                _indexMapping = indexMapping;
            }

            public decimal? this[int index] => (index >= 0 && index < _indexMapping.Length) 
                ? (_indexMapping[index] < _original.Count ? _original[_indexMapping[index]] : null)
                : null;

            public int Count => _indexMapping.Length;

            public IEnumerator<decimal?> GetEnumerator()
            {
                // WebAI Fix: Manual loop to avoid yield return overhead in hot path
                return new AlignedEnumerator(this);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            private struct AlignedEnumerator : IEnumerator<decimal?>
            {
                private readonly VirtualAlignedList _list;
                private int _index;
                public AlignedEnumerator(VirtualAlignedList list) { _list = list; _index = -1; }
                public decimal? Current => _list[_index];
                object? System.Collections.IEnumerator.Current => Current;
                public bool MoveNext() => ++_index < _list.Count;
                public void Reset() => _index = -1;
                public void Dispose() { }
            }
        }
    }

    public int GetThreeLineBreakCount(int index)
    {
        if (_tlbBuffer == null || index < 0 || index >= _tlbBuffer.Count) return 0;
        
        var targetBlock = _tlbBuffer[index];
        bool isUp = targetBlock.Close >= targetBlock.Open;
        int count = 0;
        
        // Count consecutive blocks of the same color backwards from the current index
        for (int i = index; i >= 0; i--)
        {
            var block = _tlbBuffer[i];
            bool currentIsUp = block.Close >= block.Open;
            if (currentIsUp == isUp)
            {
                count++;
            }
            else
            {
                break;
            }
        }
        return count;
    }

    /// <summary>
    /// Calculates the confluence score for a specific bar index by harvesting signals
    /// from all active indicators. Enforces ZeroAllocation via internal buffers.
    /// </summary>
    public ConfluenceResult? GetConfluenceForIndex(int index, IReadOnlyDictionary<string, IIndicatorResult>? indicatorResults, IEnumerable<CoreIndicatorSettings>? settings)
    {
        if (indicatorResults == null || indicatorResults.Count == 0 || settings == null || index < 0) return null;

        _signalBuffer.Clear();

        foreach (var setting in settings)
        {
            if (indicatorResults.TryGetValue(setting.Id, out var result) && result.SignalProvider != null)
            {
                // Harvest signals for the specified index with context (result + settings)
                foreach (var signal in result.SignalProvider.GetSignals(index, result, setting))
                {
                    _signalBuffer.Add(signal);
                }
            }
        }

        if (_signalBuffer.Count == 0) 
        {
            return new ConfluenceResult(index, 50, SignalDirection.Neutral, 0, "Monitoring...");
        }

        return _orchestrator.Orchestrate(index, _signalBuffer, _deduplicatedSignalBuffer);
    }

    private int CalculateLatestTlbCount(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles.Count == 0) return 0;
        
        var lastBlock = candles[^1];
        bool lastUp = lastBlock.Close >= lastBlock.Open;
        int count = 0;
        
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            var block = candles[i];
            bool isUp = block.Close >= block.Open;
            if (isUp == lastUp)
            {
                count++;
            }
            else
            {
                break;
            }
        }
        return count;
    }

    private ChartDataSnapshot CreateReverseWatchSnapshot(
        ChartViewModel viewModel,
        IReadOnlyDictionary<string, IIndicatorResult> indicatorResults,
        int effectiveVisibleCount,
        ChartLayoutContext layout,
        bool isTransition)
    {
        // WebAI Fix: ReverseWatch is an XY plot that never supports zoom/pan subsets.
        // It must completely ignore the viewport-derived effectiveVisibleCount inherited from TimeBased charts.
        _startIndex = 0;
        _visibleCandleCount = viewModel.Candles.Count;
        effectiveVisibleCount = _visibleCandleCount;

        // ReverseWatch: XY plot (not time-series)
        _reverseWatchData = _analysisService.CalculateReverseWatch(
            viewModel.Candles, 
            viewModel.ReverseWatchPeriod, 
            viewModel.Symbol,
            viewModel.ReverseWatchIsMaBased,
            viewModel.ReverseWatchIsLogScaleVolume);

        // Still need a minimal snapshot for other components
        return new ChartDataSnapshot(
            viewModel.Candles,
            viewModel.Symbol,
            viewModel.SelectedTimeFrame.ToString(),
            indicatorResults,
            viewModel.Indicators,
            null,
            _startIndex,
            effectiveVisibleCount,
            paddingTopPx: 0, 
            paddingBottomPx: 0, 
            chartHeightPx: (decimal)layout.ChartArea.Height,
            chartType: ChartType.ReverseWatch,
            priceRangeCalculator: ChartTypeProfileRegistry.Get(ChartType.ReverseWatch).PriceRangeCalculator,
            confluence: GetConfluenceForIndex(viewModel.Candles.Count - 1, indicatorResults, viewModel.Indicators),
            overrideMinPrice: _reverseWatchData?.Bounds.MinPrice,
            overrideMaxPrice: _reverseWatchData?.Bounds.MaxPrice)
        {
             // Populate ReverseWatch specific data in Snapshot (Requires Snapshot update or RenderContext update)
             // The Snapshot doesn't strictly have a RWData field, but ChartRenderContext DOES.
             // We need to ensure ChartBaseControl puts this _reverseWatchData into the context.
             // ChartDataCoordinator exposes _reverseWatchData via property ReverseWatchData.
             // ChartBaseControl reads coordinator.ReverseWatchData when creating context.
             // So this method just needs to ensure _reverseWatchData is updated (which it does above).
        };
    }

    private void UpdateCoordinateTransform(
        ChartLayoutContext layout, 
        ICoordinateTransform coordinateTransform,
        IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData> allCandles,
        DateTime lastKnownTime,
        TimeSpan approxInterval,
        int totalCandles,
        ChartType chartType,
        bool isSubWindowVisible,
        bool isMainWindowVisible,
        PriceScaleType priceScale,
        decimal? overrideMinPrice = null,
        decimal? overrideMaxPrice = null)
    {
        var bounds = layout.ChartArea;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            if (coordinateTransform is GenericCoordinateTransform gct)
            {
                gct.PriceScale = priceScale;

                // Pass BOTH the total canvas size and the inner ChartArea for scaling
                gct.UpdateCanvasSize(layout.TotalBounds.Width, layout.TotalBounds.Height, 
                    layout.ChartArea.X, layout.ChartArea.Y, layout.ChartArea.Width, layout.ChartArea.Height);

                // Sync Metadata
                gct.Metadata = new TransformMetadata(isSubWindowVisible, isMainWindowVisible, chartType);

                // Configure based on ChartType
                if (chartType == ChartType.ReverseWatch)
                {
                    gct.SetMode(ChartAxisMode.Volume);
                    // Reverse Watch
                    ConfigureReverseWatchTransform(gct, overrideMinPrice, overrideMaxPrice);
                }
                else if (IsIndexBasedOutput(chartType))
                {
                    gct.SetMode(ChartAxisMode.Index);
                    if (_currentSnapshot.AllCandles != null && _currentSnapshot.AllCandles.Count > 0)
                    {
                        gct.SetTimeMap(new CandleTimeMapAdapter(_currentSnapshot.AllCandles));
                    }
                    else if (_currentSnapshot.Candles != null && _currentSnapshot.Candles.Count > 0)
                    {
                        gct.SetTimeMap(new CandleTimeMapAdapter(_currentSnapshot.Candles));
                    }
                    // Index Based (Renko, P&F, Kagi)
                    ConfigureIndexTransform(gct, chartType, layout, overrideMinPrice, overrideMaxPrice);
                }
                else
                {
                    gct.SetMode(ChartAxisMode.GaplessTime);
                    gct.SetTimeMap(new CandleTimeMapAdapter(allCandles));
                    // Time Based (Standard, HeikinAshi) - Now running via Gapless mathematical bridge
                    ConfigureTimeTransform(gct, lastKnownTime, approxInterval, totalCandles, overrideMinPrice, overrideMaxPrice);
                }
            }
        }
    }

    private bool IsReverseWatchOutput()
    {
        return _currentChartType == ChartType.ReverseWatch; 
    }
    
    private bool IsIndexBasedOutput(ChartType type)
    {
        return type.IsIndexBased();
    }

    private void ConfigureReverseWatchTransform(GenericCoordinateTransform gct, decimal? overrideMinPrice = null, decimal? overrideMaxPrice = null)
    {
        if (_reverseWatchData == null) return;
        
        // Use Viewport if set, otherwise full bounds
        if (_reverseWatchViewport == default)
        {
              var b = _reverseWatchData.Bounds;
              gct.SetVolumeRange(b.MinVolume, b.MaxVolume);
              gct.SetPriceRange(overrideMinPrice ?? b.MinPrice, overrideMaxPrice ?? b.MaxPrice);
        }
         else
         {
              gct.SetVolumeRange((decimal)_reverseWatchViewport.X, (decimal)(_reverseWatchViewport.X + _reverseWatchViewport.Width));
              gct.SetPriceRange(overrideMinPrice ?? (decimal)_reverseWatchViewport.Y, overrideMaxPrice ?? (decimal)(_reverseWatchViewport.Y + _reverseWatchViewport.Height));
         }
    }

    private void ConfigureIndexTransform(GenericCoordinateTransform gct, ChartType chartType, ChartLayoutContext layout, decimal? overrideMinPrice = null, decimal? overrideMaxPrice = null)
    {
        // Use Viewport units (Absolute Index)
        // Add 0.5 padding to align with bar centering logic
        gct.SetIndexRange(Viewport.VisibleStartUnit - 0.5, Viewport.VisibleEndUnit + 0.5);
        
        // Price Range from Snapshot
        decimal min = overrideMinPrice ?? _currentSnapshot.MinPrice;
        decimal max = overrideMaxPrice ?? _currentSnapshot.MaxPrice;

        gct.FixedPricePerIndex = null;
        gct.SetPriceRange(min, max);
    }

    private void ConfigureTimeTransform(GenericCoordinateTransform gct, DateTime lastKnownTime, TimeSpan approxInterval, int totalCandles, decimal? overrideMinPrice = null, decimal? overrideMaxPrice = null)
    {
        // Use Viewport units (Absolute Ticks)
        long startTicks = (long)Viewport.VisibleStartUnit;
        long endTicks = (long)Viewport.VisibleEndUnit;
        
        var halfIntervalTicks = approxInterval.Ticks / 2;

        long safeStartTicks = Math.Max(DateTime.MinValue.Ticks, Math.Min(DateTime.MaxValue.Ticks, startTicks - halfIntervalTicks));
        long safeEndTicks = Math.Max(DateTime.MinValue.Ticks, Math.Min(DateTime.MaxValue.Ticks, endTicks + halfIntervalTicks));
        
        gct.SetTimeRange(new DateTime(safeStartTicks), new DateTime(safeEndTicks));
        gct.SetPriceRange(overrideMinPrice ?? _currentSnapshot.MinPrice, overrideMaxPrice ?? _currentSnapshot.MaxPrice);    
    }

    // Moved to AnalysisPipelineService
    // private static Dictionary<string, List<decimal?>> CalculateIndicators(...)


    private static bool IsValidAdaptationTime(DateTime? time)
    {
        if (!time.HasValue) return false;
        // Safety guard: Reject Year 0001 (Tick contamination) or far future (Extrapolation bug)
        return time.Value.Year > 1900 && time.Value.Year < 3000;
    }

    private static TimeSpan GetTimeIntervalFromSnapshot(ChartDataSnapshot snapshot)
    {
        if (Enum.TryParse<TimeframeType>(snapshot.Timeframe, out var tf))
        {
            return GetTimeInterval(tf);
        }
        return TimeSpan.FromDays(1);
    }

    private static TimeSpan GetTimeInterval(TimeframeType timeframe)
    {
        return timeframe switch
        {
            TimeframeType.M1 => TimeSpan.FromMinutes(1),
            TimeframeType.M5 => TimeSpan.FromMinutes(5),
            TimeframeType.M15 => TimeSpan.FromMinutes(15),
            TimeframeType.H1 => TimeSpan.FromHours(1),
            TimeframeType.H4 => TimeSpan.FromHours(4),
            TimeframeType.Daily => TimeSpan.FromDays(1),
            TimeframeType.Weekly => TimeSpan.FromDays(7),
            TimeframeType.Monthly => TimeSpan.FromDays(30),
            _ => TimeSpan.FromDays(1)
        };
    }

    private enum ChartFamily
    {
        TimeSeries,      // Standard (Candlestick, HeikinAshi, Line, Area, OHLC)
        IndexSeries,     // Renko, Kagi, P&F, TLB
        Comparison,      // RelativePerformance
        Excluded         // ReverseWatch, etc.
    }

    private static ChartFamily GetChartFamily(ChartType type)
    {
        return type switch
        {
            ChartType.Candlestick or ChartType.HeikinAshi or ChartType.Line or 
            ChartType.Area or ChartType.OHLCBar => ChartFamily.TimeSeries,
            
            ChartType.Renko or ChartType.Kagi or ChartType.PointAndFigure or 
            ChartType.ThreeLineBreak => ChartFamily.IndexSeries,
            
            ChartType.RelativePerformance => ChartFamily.Comparison,
            
            ChartType.ReverseWatch => ChartFamily.Excluded,
            
            _ => ChartFamily.Excluded
        };
    }

    /// <summary>
    /// Explicitly releases internal buffers to prevent memory bloat in multi-tab scenarios.
    /// Called when the chart tab is closed or the view is disposed.
    /// </summary>
    public void ClearAllBuffers()
    {
        _renkoBuffer.Clear();
        _heikinAshiBuffer.Clear();
        _kagiBuffer.Clear();
        _tlbBuffer.Clear();
        _pnfBuffer.Clear();
        _standardCandleBuffer.Clear();
        _kagiIntermediateBuffer.Clear();
        _tlbBlockBuffer.Clear();
        
        _lastAlignedResults = null;
        _lastSourceIndicatorResults = null;
        _currentSnapshot = ChartDataSnapshot.Empty;
        _reverseWatchData = null;
        _comparisonData = null;
        
        _lastSymbol = null;
        _lastSourceCandleCount = -1;
    }

    #endregion
}

file class CandleTimeMapAdapter : System.Collections.Generic.IReadOnlyList<System.DateTime>
{
    private readonly System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData> _candles;
    public CandleTimeMapAdapter(System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData> candles) => _candles = candles;
    public System.DateTime this[int index] => _candles[index].Timestamp;
    public int Count => _candles.Count;
    public System.Collections.Generic.IEnumerator<System.DateTime> GetEnumerator() => new CandleTimeEnumerator(_candles);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private struct CandleTimeEnumerator : System.Collections.Generic.IEnumerator<System.DateTime>
    {
        private readonly System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData> _candles;
        private int _index;
        public CandleTimeEnumerator(System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData> candles) { _candles = candles; _index = -1; }
        public System.DateTime Current => _candles[_index].Timestamp;
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < _candles.Count;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}
