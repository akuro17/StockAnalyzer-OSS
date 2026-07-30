using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Skia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Encapsulates the rendering flow for the chart.
/// Owns the reusable renderers to reduce per-frame allocations.
/// </summary>
public sealed class ChartRenderPipeline : IDisposable
{
    private readonly BackgroundRenderer _backgroundRenderer;
    private readonly GridRenderer _gridRenderer;
    private readonly AxisRenderer _axisRenderer;
    private readonly IndicatorRenderer _indicatorRenderer;
    private readonly CrosshairRenderer _crosshairRenderer;
    private readonly InteractionFeedbackRenderer _feedbackRenderer;
    private readonly DtwHighlightRenderer _dtwHighlightRenderer;
    private readonly AxisLabelRenderer _axisLabelRenderer;
    private readonly MultiWavePatternRenderer _multiWavePatternRenderer;
    private readonly GhostProjectionRenderer _ghostProjectionRenderer;
    private readonly SemanticPaintCache _semanticPaintCache;
    private readonly List<AxisLabelRequest> _axisLabelBuffer = new();
    private readonly Dictionary<int, (decimal Min, decimal Max)> _panelRangeBuffer = new();
    private readonly HashSet<string> _seenGroupsBuffer = new();
    private readonly HashSet<string> _localSeenBuffer = new();
    private readonly List<CoreIndicatorSettings> _groupBuffer = new();
    private readonly CrossMarkerRenderer _crossMarkerRenderer;
    private int _visibleIndicatorIndex;

    // Reusable buffers to eliminate per-frame allocations
    private readonly Dictionary<string, List<CoreIndicatorSettings>> _overlayGroups = new();
    private readonly Dictionary<string, (decimal Min, decimal Max)> _groupRanges = new();
    private readonly Dictionary<string, int> _assignedPanelIndices = new();
    private readonly Dictionary<int, List<IIndicatorResult>> _panelIndicators = new();
    private readonly Dictionary<int, bool> _panelHasCrossMarkers = new();
    private readonly Dictionary<int, (decimal Min, decimal Max)> _panelRanges = new();
    private readonly List<List<CoreIndicatorSettings>> _overlayGroupListsPool = new();
    private int _overlayGroupListsUsed;
    private readonly List<List<IIndicatorResult>> _panelIndicatorListsPool = new();
    private int _panelIndicatorListsUsed;
    private readonly System.Text.StringBuilder _labelBuilder = new();

    public ChartRenderPipeline()
        : this(new IndicatorRenderer())
    {
    }

    public ChartRenderPipeline(IndicatorRenderer indicatorRenderer)
    {
        _backgroundRenderer = new BackgroundRenderer();
        _gridRenderer = new GridRenderer();
        _axisRenderer = new AxisRenderer();
        _indicatorRenderer = indicatorRenderer ?? throw new ArgumentNullException(nameof(indicatorRenderer));
        _crosshairRenderer = new CrosshairRenderer();
        _feedbackRenderer = new InteractionFeedbackRenderer();
        _dtwHighlightRenderer = new DtwHighlightRenderer();
        _axisLabelRenderer = new AxisLabelRenderer();
        _multiWavePatternRenderer = new MultiWavePatternRenderer();
        _ghostProjectionRenderer = new GhostProjectionRenderer();
        _semanticPaintCache = new SemanticPaintCache();
        _crossMarkerRenderer = new CrossMarkerRenderer();
    }

    public void Dispose()
    {
        _backgroundRenderer.Dispose();
        _gridRenderer.Dispose();
        _axisRenderer.Dispose();
        _indicatorRenderer.Dispose();
        _crosshairRenderer.Dispose();
        _feedbackRenderer.Dispose();
        _dtwHighlightRenderer.Dispose();
        _axisLabelRenderer.Dispose();
        _multiWavePatternRenderer.Dispose();
        _ghostProjectionRenderer.Dispose();
        _semanticPaintCache.Dispose();
        _crossMarkerRenderer.Dispose();
    }

    public void Execute(
        SKCanvas canvas,
        ChartLayoutContext layout,
        ChartDataSnapshot snapshot,
        IChartRenderer mainRenderer,
        IChartRenderConfig renderConfig,
        ChartType chartType,
        ICoordinateTransform coordinateTransform,
        ChartObjectManager objectManager,
        RulerRenderer rulerRenderer,
        global::Avalonia.Point mousePosition,
        bool isCrosshairVisible,
        global::Avalonia.Rect controlBounds,
        global::Avalonia.Point? snapPoint,
        IChartObject? currentDrawingObject)
    {
        // 0. Update Semantic Paint Cache with current theme
        var theme = renderConfig.ThemeManager?.CurrentTheme ?? ThemeColors.Light;
        _semanticPaintCache.Update(theme);

        // 1. Render Background
        _backgroundRenderer.Render(canvas, layout, renderConfig);

        var chartArea = layout.ChartArea;
        var volumeArea = layout.VolumeArea;
        bool showVolume = volumeArea.Height > 0;

        // 2. Prepare and Render Grid & Axis (Behind data, now with occlusion)
        PrepareAxisLabels(layout, snapshot, objectManager, renderConfig, mainRenderer);
        _gridRenderer.Render(canvas, chartArea, snapshot, renderConfig);
        _axisRenderer.Render(canvas, layout, snapshot, renderConfig, _axisLabelRenderer.GetResolvedLabels());

        // 3. Render Main Chart (Clipped)
        canvas.Save();
        // Shift clipping boundary 0.5px left to prevent stroke bleed into labels
        // Use antialias: true to enforce exact pixel clipping on stroke boundaries.
        canvas.ClipRect(SKRect.Create((float)chartArea.Left, (float)chartArea.Top, (float)chartArea.Width - 0.5f, (float)chartArea.Height), SKClipOperation.Intersect, true);
        canvas.Translate((float)chartArea.Left, 0);
        
        // Localized rect for renderer
        var localizedChartArea = new global::Avalonia.Rect(0, chartArea.Y, chartArea.Width, chartArea.Height);
        mainRenderer.Render(canvas, localizedChartArea, snapshot, renderConfig);
        
        // 3.5 Render MultiWave Patterns overlay on main chart
        if (renderConfig.ShowMultiWavePatterns)
        {
            _multiWavePatternRenderer.Render(canvas, localizedChartArea, snapshot, renderConfig);
        }
        
        if (renderConfig.ShowGhostProjections)
        {
            _ghostProjectionRenderer.Render(canvas, localizedChartArea, snapshot, renderConfig);
        }
        
        canvas.Restore();

        // 4. Render Volume (Deprecated: Now rendered as an indicator)
        // Hardcoded volume area rendering has been removed to allow custom indicators and avoid empty space on FX


        // 5. Render Indicators (Panels & Overlays)
        if (snapshot.IndicatorSettings != null && ChartHelpers.SupportsIndicators(chartType))
        {
            RenderIndicators(canvas, layout, snapshot, renderConfig);
        }

        // 6. Render Ruler
        rulerRenderer.Render(canvas, layout.ChartArea, snapshot, renderConfig);
 


        // 7. Render Drawing Objects & Feedback
        canvas.Save();
        canvas.ClipRect(new SKRect((float)chartArea.Left, (float)chartArea.Top, (float)chartArea.Right, (float)chartArea.Bottom));
        canvas.Translate((float)chartArea.Left, (float)chartArea.Top);
        objectManager.Render(canvas, coordinateTransform);
        _feedbackRenderer.Render(canvas, snapPoint, currentDrawingObject, coordinateTransform);
        canvas.Restore();

        // 8. Render Axis Labels (Y-Axis)
        DrawAxisLabels(canvas, chartArea);

        // 9. Render Crosshair
        if (isCrosshairVisible)
        {
            RenderCrosshair(canvas, layout, chartArea, volumeArea, mousePosition, snapshot, controlBounds, renderConfig, chartType);
        }

        // 10. Render Header
        // Removed per PR-38-8: HUD text overlay is abolished in favor of DataWindowViewModel.
    }

    /// <summary>
    /// Collects axis label requests and resolves their screen positions
    /// to be used for background occlusion.
    /// </summary>
    private void PrepareAxisLabels(
        ChartLayoutContext layout,
        ChartDataSnapshot snapshot,
        ChartObjectManager objectManager,
        IChartRenderConfig renderConfig,
        IChartRenderer mainRenderer)
    {
        _axisLabelBuffer.Clear();
        _panelRangeBuffer.Clear();

        var chartArea = layout.ChartArea;

        // 1. Pre-calculate panel ranges (Must match RenderIndicators layout logic)
        if (snapshot.IndicatorSettings != null)
        {
            int pIdx = 0;
            _seenGroupsBuffer.Clear();
            foreach (var setting in snapshot.IndicatorSettings)
            {
                if (!setting.IsEnabled || setting.IsOverlay) continue;

                if (!string.IsNullOrEmpty(setting.OverlayPanelId))
                {
                    if (_seenGroupsBuffer.Contains(setting.OverlayPanelId)) continue;
                    _seenGroupsBuffer.Add(setting.OverlayPanelId);
                    
                    _groupBuffer.Clear();
                    foreach (var s in snapshot.IndicatorSettings)
                    {
                        if (s.IsEnabled && !s.IsOverlay && s.OverlayPanelId == setting.OverlayPanelId)
                        {
                            _groupBuffer.Add(s);
                        }
                    }
                    _panelRangeBuffer[pIdx] = PanelValueRangeCalculator.CalculateGroup(snapshot, _groupBuffer);
                }
                else
                {
                    _panelRangeBuffer[pIdx] = PanelValueRangeCalculator.Calculate(snapshot, setting);
                }
                pIdx++;
            }
        }

        // 2. Built-in: Current (latest) price label
        if (renderConfig.ChartType == ChartType.ReverseWatch && renderConfig is IReverseWatchRenderConfig rwConfig && rwConfig.ReverseWatchData != null && rwConfig.Transform != null)
        {
            var data = rwConfig.ReverseWatchData;
            if (data.Points.Count > 0)
            {
                float mouseX = (float)renderConfig.MousePosition.X;
                float mouseY = (float)renderConfig.MousePosition.Y;
                
                int pointCount = data.Points.Count;
                int configCount = rwConfig.ReverseWatchDataCount;
                int renderCount = configCount > 0 ? Math.Min(pointCount, configCount) : pointCount;
                int startIndex = pointCount - renderCount;

                ReverseWatchCurvePoint? bestPoint = null;
                double minDistSq = double.MaxValue;
                for (int i = startIndex; i < pointCount; i++)
                {
                    var point = data.Points[i];
                    var sp = rwConfig.Transform.NumericToScreen((double)point.VolumeAverage, (double)point.PriceAverage);
                    float px = (float)sp.X + StockAnalyzer.Avalonia.Views.Chart.ChartTheme.MarginLeft;
                    float py = (float)sp.Y + (float)chartArea.Y;

                    double dx = mouseX - px;
                    double dy = mouseY - py;
                    double distSq = dx * dx + dy * dy;

                    if (distSq < minDistSq) { minDistSq = distSq; bestPoint = point; }
                }

                var targetPoint = (minDistSq <= 50 * 50) ? bestPoint : data.Points.LastOrDefault();
                if (targetPoint != null)
                {
                    var color = rwConfig.GetPhaseColor(targetPoint.Phase).ToSkColor();
                    _axisLabelBuffer.Add(new AxisLabelRequest(targetPoint.PriceAverage, color, targetPoint.PriceAverage.ToString("F2"), AxisLabelStyle.CurrentPrice));
                }
            }
        }
        else if (snapshot.Candles.Count > 0)
        {
            var theme = renderConfig.ThemeManager?.CurrentTheme ?? new Core.Theme.ThemeColors();
            var globalLatestCandle = (snapshot.AllCandles != null && snapshot.AllCandles.Count > 0) 
                ? snapshot.AllCandles[snapshot.AllCandles.Count - 1] : snapshot.Candles[snapshot.Candles.Count - 1];

            bool isGlobalBullish = globalLatestCandle.Close >= globalLatestCandle.Open;
            var globalPriceColor = isGlobalBullish ? theme.Bullish : theme.Bearish;
            decimal marketPrice = (decimal)renderConfig.CurrentPrice;
            if (marketPrice == 0) marketPrice = globalLatestCandle.Close;

            // In RelativePerformance mode, the absolute price is usually irrelevant/off-screen.
            // We let the ComparisonChartRenderer handle its own relative labels instead.
            if (renderConfig.ChartType != ChartType.RelativePerformance)
            {
                _axisLabelBuffer.Add(new AxisLabelRequest(marketPrice, globalPriceColor.ToSkColor(), marketPrice.ToString("F2"), AxisLabelStyle.CurrentPrice));
            }

            int lastVisibleIdx = snapshot.Candles.Count - 1;
            var lastVisibleCandle = snapshot.Candles[lastVisibleIdx];
            var transform = renderConfig.Transform;
            if (transform != null)
            {
                for (int i = snapshot.Candles.Count - 1; i >= 0; i--)
                {
                    var c = snapshot.Candles[i];
                    double x;
                    if (renderConfig.ChartType.IsIndexBased())
                    {
                        var index = snapshot.StartIndex + i;
                        x = transform.ChartToScreen(new ChartPoint(new DateTime(index), 0)).X + chartArea.Left;
                    }
                    else
                    {
                        x = transform.ChartToScreen(new ChartPoint(c.Timestamp, 0)).X + chartArea.Left;
                    }
                    if (x <= chartArea.Right + 2) { lastVisibleCandle = c; lastVisibleIdx = i; break; }
                }
            }

            // Reference for indicators matching the visual edge
            _visibleIndicatorIndex = lastVisibleIdx;

            if (lastVisibleCandle.Close != marketPrice && renderConfig.ChartType != ChartType.RelativePerformance)
            {
                bool isVisibleBullish = lastVisibleCandle.Close >= lastVisibleCandle.Open;
                var visiblePriceColor = isVisibleBullish ? theme.Bullish : theme.Bearish;
                _axisLabelBuffer.Add(new AxisLabelRequest(lastVisibleCandle.Close, visiblePriceColor.ToSkColor(), lastVisibleCandle.Close.ToString("F2"), AxisLabelStyle.TargetPrice));
            }
        }

        // 3. Collect projections from drawing objects
        var objects = objectManager.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is IAxisProjectable projectable)
            {
                foreach (var request in projectable.GetAxisProjections(snapshot, renderConfig))
                    _axisLabelBuffer.Add(request);
            }
        }

        if (mainRenderer is IAxisProjectable mainProjectable)
        {
            foreach (var request in mainProjectable.GetAxisProjections(snapshot, renderConfig))
                _axisLabelBuffer.Add(request);
        }

        // 4. Collect latest values from ALL indicators with ShowAxisLabel enabled
        if (snapshot.IndicatorSettings != null && snapshot.IndicatorResults != null)
        {
            _seenGroupsBuffer.Clear();

            foreach (var setting in snapshot.IndicatorSettings)
            {
                if (!setting.IsEnabled) continue;
                
                int labelPanelIdx = -1;
                if (!setting.IsOverlay)
                {
                    int calcIdx = 0;
                    _localSeenBuffer.Clear();
                    foreach(var s in snapshot.IndicatorSettings)
                    {
                        if (s.IsOverlay || !s.IsEnabled) continue;
                        
                        string? gid = s.OverlayPanelId;
                        if (!string.IsNullOrEmpty(gid))
                        {
                            if (!_localSeenBuffer.Contains(gid))
                            {
                                if (gid == setting.OverlayPanelId) { labelPanelIdx = calcIdx; break; }
                                _localSeenBuffer.Add(gid);
                                calcIdx++;
                            }
                        }
                        else
                        {
                            if (s == setting) { labelPanelIdx = calcIdx; break; }
                            calcIdx++;
                        }
                    }
                }

                if (setting.ShowAxisLabel && renderConfig.ChartType != ChartType.RelativePerformance)
                {
                    if (snapshot.IndicatorResults.TryGetValue(setting.Id, out var result) && result.IsSuccessful)
                    {
                        // Match the index logic in IndicatorRenderer (taking Offset into account)
                        int valIdx = _visibleIndicatorIndex - setting.Offset;
                        
                        decimal? displayValue = null;
                        if (valIdx >= 0 && valIdx < result.MainValues.Count)
                        {
                            displayValue = result.MainValues[valIdx];
                        }
                        else if (valIdx >= result.MainValues.Count)
                        {
                            // Extrapolate/Pick last available for future projections (Ichimoku Span B)
                            for (int j = result.MainValues.Count - 1; j >= 0; j--)
                            {
                                if (result.MainValues[j].HasValue)
                                {
                                    displayValue = result.MainValues[j];
                                    break;
                                }
                            }
                        }

                        if (displayValue.HasValue)
                        {
                            var indicatorColor = setting.Color.ToSkColor();
                            _axisLabelBuffer.Add(new AxisLabelRequest(displayValue.Value, indicatorColor, displayValue.Value.ToString("F2"), AxisLabelStyle.Default, labelPanelIdx));
                        }
                    }
                }
            }
        }

        // 5. Finalize resolution using newly calculated layout and ranges
        var pipelineTransform = renderConfig.Transform;
        if (pipelineTransform != null && pipelineTransform.PriceScale == PriceScaleType.Percent && snapshot.Candles.Count > 0)
        {
            decimal referencePrice = snapshot.Candles[0].Close;
            if (referencePrice != 0)
            {
                for (int i = 0; i < _axisLabelBuffer.Count; i++)
                {
                    var req = _axisLabelBuffer[i];
                    if (req.PanelIndex < 0)
                    {
                        decimal percentChange = (req.Value - referencePrice) / referencePrice * 100m;
                        string newLabelText = percentChange.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";
                        _axisLabelBuffer[i] = req with { Label = newLabelText };
                    }
                }
            }
        }

        _axisLabelRenderer.ResolveLabels(layout, snapshot, renderConfig, _axisLabelBuffer, _panelRangeBuffer);
    }

    /// <summary>
    /// Renders the previously resolved axis labels.
    /// </summary>
    private void DrawAxisLabels(SKCanvas canvas, global::Avalonia.Rect chartArea)
    {
        _axisLabelRenderer.Draw(canvas, (float)chartArea.Right);
    }

    private void RenderIndicators(SKCanvas canvas, ChartLayoutContext layout, ChartDataSnapshot snapshot, IChartRenderConfig renderConfig)
    {
        // Step 68-1-4: Global Visibility Gate
        // If the sub-window setting is toggled off, we hide ALL indicators (including overlays)
        // to provide a completely clean main chart experience.
        if (!renderConfig.IsSubWindowVisible) return;

        canvas.Save();
        canvas.Translate((float)layout.ChartArea.Left, 0);

        // Clear and prepare field-level buffers to avoid allocations
        _overlayGroups.Clear();
        foreach (var l in _overlayGroupListsPool) l.Clear();
        _overlayGroupListsUsed = 0;

        _groupRanges.Clear();
        _assignedPanelIndices.Clear();

        _panelIndicators.Clear();
        foreach (var l in _panelIndicatorListsPool) l.Clear();
        _panelIndicatorListsUsed = 0;

        _panelHasCrossMarkers.Clear();
        _panelRanges.Clear();

        // Pre-scan: build overlay group info for grouped panels
        foreach (var setting in snapshot.IndicatorSettings)
        {
            if (!setting.IsEnabled || setting.IsOverlay) continue;
            if (setting.TypeEnum == IndicatorType.GranvilleLaw) continue;
            if (!string.IsNullOrEmpty(setting.OverlayPanelId))
            {
                if (!_overlayGroups.TryGetValue(setting.OverlayPanelId, out var group))
                {
                    if (_overlayGroupListsUsed < _overlayGroupListsPool.Count)
                    {
                        group = _overlayGroupListsPool[_overlayGroupListsUsed++];
                    }
                    else
                    {
                        group = new List<CoreIndicatorSettings>();
                        _overlayGroupListsPool.Add(group);
                        _overlayGroupListsUsed++;
                    }
                    _overlayGroups[setting.OverlayPanelId] = group;
                }
                group.Add(setting);
            }
        }

        // Pre-compute merged ranges for each group
        foreach (var kvp in _overlayGroups)
        {
            _groupRanges[kvp.Key] = PanelValueRangeCalculator.CalculateGroup(snapshot, kvp.Value);
        }

        int panelIndex = 0;
        foreach (var setting in snapshot.IndicatorSettings)
        {
            if (!setting.IsEnabled) continue;

            bool isGranville = setting.TypeEnum == IndicatorType.GranvilleLaw;

            if (isGranville)
            {
                // 1. Always render the MA line on the main chart
                RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, ref panelIndex, isSubWindowContext: false, panelRanges: _panelRanges);

                // 2. Render the sub-window histogram if enabled AND sub-window is visible
                bool showSubWindowBar = setting.ParameterObject is CoreGranvilleLawParameter p && p.ShowSubWindowBar;
                if (showSubWindowBar && renderConfig.IsSubWindowVisible)
                {
                    RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, ref panelIndex, isSubWindowContext: true, panelRanges: _panelRanges);
                }
            }
            else if (!setting.IsOverlay)
            {
                // Sub-window indicator: Skip if sub-window is toggled off
                if (!renderConfig.IsSubWindowVisible) continue;

                if (!string.IsNullOrEmpty(setting.OverlayPanelId))
                {
                    // Grouped panel indicator
                    string groupId = setting.OverlayPanelId;
                    var range = _groupRanges[groupId];

                    if (!_assignedPanelIndices.TryGetValue(groupId, out int groupPanelIdx))
                    {
                        // First indicator in this group: allocate a new panel
                        groupPanelIdx = panelIndex;
                        _assignedPanelIndices[groupId] = groupPanelIdx;
                        RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, ref panelIndex,
                            isSubWindowContext: true, panelIndexOverride: null, drawPanelBackground: true,
                            overrideMinValue: range.Min, overrideMaxValue: range.Max, panelRanges: _panelRanges);
                    }
                    else
                    {
                        // Subsequent indicator in same group: reuse existing panel, skip grid/border
                        int dummy = groupPanelIdx;
                        RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, ref dummy,
                            isSubWindowContext: true, panelIndexOverride: groupPanelIdx, drawPanelBackground: false,
                            overrideMinValue: range.Min, overrideMaxValue: range.Max, panelRanges: _panelRanges);
                    }
                }
                else
                {
                    // Normal sub-window indicator
                    RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, ref panelIndex, isSubWindowContext: true, panelRanges: _panelRanges);
                }
            }
            else
            {
                // Overlay on main chart
                RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, ref panelIndex, isSubWindowContext: false, panelRanges: _panelRanges);
            }

            // Collect lines for Cross Markers.
            // For subwindows, the targetPanelIdx is (panelIndex - 1) because panelIndex was incremented inside RenderSingleIndicatorPass.
            // But if it used gruopPanelIdx, it didn't increment.
            // Let's compute actualSubPanelIdx safely.
            if (setting.IsEnabled && (!setting.IsOverlay || setting.TypeEnum == IndicatorType.GranvilleLaw))
            {
                int currentPanelIdx = setting.IsOverlay ? -1 : (panelIndex - 1);
                
                if (!isGranville && !setting.IsOverlay && !string.IsNullOrEmpty(setting.OverlayPanelId))
                {
                    if (_assignedPanelIndices.TryGetValue(setting.OverlayPanelId, out int groupIdx))
                    {
                        currentPanelIdx = groupIdx;
                    }
                }

                if (currentPanelIdx >= 0) // Only process for sub-windows
                {
                    if (snapshot.IndicatorResults != null && snapshot.IndicatorResults.TryGetValue(setting.Id, out var result))
                    {
                        if (result.IsSuccessful)
                        {
                            if (!_panelIndicators.TryGetValue(currentPanelIdx, out var inds))
                            {
                                if (_panelIndicatorListsUsed < _panelIndicatorListsPool.Count)
                                {
                                    inds = _panelIndicatorListsPool[_panelIndicatorListsUsed++];
                                }
                                else
                                {
                                    inds = new List<IIndicatorResult>();
                                    _panelIndicatorListsPool.Add(inds);
                                    _panelIndicatorListsUsed++;
                                }
                                _panelIndicators[currentPanelIdx] = inds;
                            }
                            inds.Add(result);
                        }
                    }

                    if (setting.ShowCrossMarkers)
                    {
                        _panelHasCrossMarkers[currentPanelIdx] = true;
                    }
                }
            }
        }

        // Render Cross Markers for sub-windows
        foreach (var kvp in _panelHasCrossMarkers)
        {
            int pIdx = kvp.Key;
            if (kvp.Value && _panelIndicators.TryGetValue(pIdx, out var inds) && pIdx < layout.PanelAreas.Count)
            {
                IReadOnlyList<decimal?>? shortSeries = null;
                IReadOnlyList<decimal?>? longSeries = null;

                if (inds.Count >= 2)
                {
                    // Inter-indicator cross (e.g., RSI 14 vs RSI 7)
                    shortSeries = inds[0].MainValues;
                    longSeries = inds[1].MainValues;
                }
                else if (inds.Count == 1)
                {
                    // Intra-indicator cross (e.g., MACD vs Signal)
                    var ind = inds[0];
                    if (ind.SeriesNames.Count() >= 2)
                    {
                        shortSeries = ind.GetSeries(ind.SeriesNames.ElementAt(0));
                        longSeries = ind.GetSeries(ind.SeriesNames.ElementAt(1));
                    }
                }

                if (shortSeries != null && longSeries != null)
                {
                    var crosses = DivergenceCrossDetector.DetectCrosses(shortSeries, longSeries);
                    if (crosses.Count > 0)
                    {
                        var pa = layout.PanelAreas[pIdx];
                        Rect targetRect = new Rect(0, pa.Y, pa.Width, pa.Height);

                        // Use actual panel range recorded during indicator paths
                        decimal minVal = 0m, maxVal = 100m;
                        if (_panelRanges.TryGetValue(pIdx, out var realRange))
                        {
                            minVal = realRange.Min;
                            maxVal = realRange.Max;
                            
                            // Add same margin applied by panel renderer (if any... actually ChartHelpers handles raw value, 
                            // but gridRenderer adds margin. Let's add 10% margin manually to match grid rendering for non-overlay)
                            decimal rangePrice = maxVal - minVal;
                            if (rangePrice == 0) rangePrice = 1m;
                            maxVal += rangePrice * 0.1m;
                            minVal -= rangePrice * 0.1m;
                        }

                        canvas.Save();
                        canvas.Translate(0, (float)pa.Y);
                        canvas.ClipRect(SKRect.Create(0, 0, (float)pa.Width, (float)pa.Height), SKClipOperation.Intersect, true);
                        
                        Rect relativeRect = new Rect(0, 0, pa.Width, pa.Height);

                        _crossMarkerRenderer.Render(
                            canvas, relativeRect, crosses,
                            shortSeries, longSeries,
                            minVal, maxVal,
                            snapshot.Candles, TimeSpan.FromDays(1), renderConfig.Transform, renderConfig);
                            
                        canvas.Restore();
                    }
                }
            }
        }

        canvas.Restore();
    }

    private void RenderSingleIndicatorPass(
        SKCanvas canvas, 
        ChartLayoutContext layout, 
        ChartDataSnapshot snapshot, 
        IChartRenderConfig renderConfig, 
        CoreIndicatorSettings setting, 
        ref int panelIndex, 
        bool isSubWindowContext,
        int? panelIndexOverride = null,
        bool drawPanelBackground = true,
        decimal? overrideMinValue = null,
        decimal? overrideMaxValue = null,
        Dictionary<int, (decimal Min, decimal Max)>? panelRanges = null)
    {
        Rect targetRect;

        if (isSubWindowContext)
        {
            int targetPanelIdx = panelIndexOverride ?? panelIndex;

            if (targetPanelIdx < layout.PanelAreas.Count)
            {
                var pa = layout.PanelAreas[targetPanelIdx];
                targetRect = new global::Avalonia.Rect(0, pa.Y, pa.Width, pa.Height);

                // Only increment panelIndex if we're not using an override
                if (!panelIndexOverride.HasValue)
                {
                    panelIndex++;
                }

                if (drawPanelBackground)
                {
                    var (minVal, maxVal) = overrideMinValue.HasValue && overrideMaxValue.HasValue
                        ? (overrideMinValue.Value, overrideMaxValue.Value)
                        : PanelValueRangeCalculator.Calculate(snapshot, setting);
                    
                    if (panelRanges != null)
                    {
                        panelRanges[targetPanelIdx] = (minVal, maxVal);
                    }

                    decimal priceRange = maxVal - minVal;
                    if (priceRange == 0) priceRange = 1m;

                    bool isFixedRange = (setting.MinValue.HasValue && setting.MaxValue.HasValue)
                        || (overrideMinValue.HasValue && overrideMaxValue.HasValue);
                    _gridRenderer.RenderPanel(canvas, targetRect, minVal, priceRange, snapshot.Candles.Count, renderConfig, isFixedRange);

                    using var borderPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = SKColors.Gray.WithAlpha(80),
                        IsAntialias = false,
                        StrokeWidth = 1
                    };
                    canvas.DrawLine((float)targetRect.Left, (float)targetRect.Top, (float)targetRect.Right, (float)targetRect.Top, borderPaint);
                }
            }
            else
            {
                // No panel area allocated for this sub-window indicator (likely hidden via visibility override).
                // Do not render on main chart.
                return;
            }
        }
        else
        {
            targetRect = new global::Avalonia.Rect(0, layout.ChartArea.Y, layout.ChartArea.Width, layout.ChartArea.Height);
        }

        canvas.Save();
        // Shift clipping boundary 0.5px left to prevent stroke bleed into labels
        // Use antialias: true to enforce exact pixel clipping on stroke boundaries.
        if (isSubWindowContext)
        {
            canvas.ClipRect(SKRect.Create((float)targetRect.X, (float)targetRect.Y, (float)targetRect.Width - 0.5f, (float)targetRect.Height), SKClipOperation.Intersect, true);
        }
        else
        {
            canvas.ClipRect(SKRect.Create((float)targetRect.X, (float)targetRect.Y, (float)targetRect.Width - 0.5f, (float)targetRect.Height), SKClipOperation.Intersect, true);
        }
        _indicatorRenderer.RenderIndicator(canvas, targetRect, snapshot, setting, renderConfig, isSubWindowContext,
            overrideMinValue, overrideMaxValue);
        canvas.Restore();
    }

    private void RenderCrosshair(
        SKCanvas canvas, 
        ChartLayoutContext layout, 
        global::Avalonia.Rect chartArea, 
        global::Avalonia.Rect volumeArea, 
        global::Avalonia.Point mousePosition, 
        ChartDataSnapshot snapshot, 
        global::Avalonia.Rect controlBounds, 
        IChartRenderConfig renderConfig, 
        ChartType chartType)
    {
        if (chartType == ChartType.ReverseWatch && renderConfig is IReverseWatchRenderConfig rwConfig)
        {
            if (rwConfig.ReverseWatchData != null)
            {
                _crosshairRenderer.RenderReverseWatch(canvas, chartArea, mousePosition, rwConfig.ReverseWatchData, renderConfig);
            }
        }
        else
        {
            if (chartType == ChartType.RelativePerformance && renderConfig is IComparisonRenderConfig compConfig)
            {
                _crosshairRenderer.RenderComparison(canvas, chartArea, mousePosition, snapshot, compConfig);
            }
            else
            {
                _crosshairRenderer.Render(canvas, chartArea, volumeArea, mousePosition, snapshot, controlBounds, renderConfig);
            }

            if (snapshot.IndicatorSettings != null && layout.PanelAreas.Count > 0 && ChartHelpers.SupportsIndicators(chartType))
            {
                int panelIdx = 0;
                _seenGroupsBuffer.Clear();

                foreach (var setting in snapshot.IndicatorSettings)
                {
                    if (!setting.IsEnabled || setting.IsOverlay) continue;
                    if (setting.TypeEnum == IndicatorType.GranvilleLaw)
                    {
                        // GranvilleLaw sub-window panel (if enabled)
                        bool showSubWindowBar = setting.ParameterObject is CoreGranvilleLawParameter p && p.ShowSubWindowBar;
                        if (showSubWindowBar)
                        {
                            if (panelIdx >= layout.PanelAreas.Count) break;
                            var panelArea = layout.PanelAreas[panelIdx];
                            panelIdx++;
                            var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snapshot, setting);
                            decimal range = maxVal - minVal; if (range == 0) range = 1m;
                            _crosshairRenderer.RenderPanel(canvas, panelArea, mousePosition, minVal, range, setting.DisplayName ?? setting.Id, renderConfig);
                        }
                        continue;
                    }

                    // Check for grouped panel
                    if (!string.IsNullOrEmpty(setting.OverlayPanelId))
                    {
                        if (_seenGroupsBuffer.Contains(setting.OverlayPanelId))
                        {
                            // Already rendered crosshair for this group's panel
                            continue;
                        }
                        _seenGroupsBuffer.Add(setting.OverlayPanelId);
                    }

                    if (panelIdx >= layout.PanelAreas.Count) break;

                    var area = layout.PanelAreas[panelIdx];
                    panelIdx++;

                    // Use group range if grouped, otherwise individual
                    decimal cMin, cMax;
                    string label;
                    if (!string.IsNullOrEmpty(setting.OverlayPanelId))
                    {
                        _groupBuffer.Clear();
                        foreach (var s in snapshot.IndicatorSettings)
                        {
                            if (s.IsEnabled && !s.IsOverlay && s.OverlayPanelId == setting.OverlayPanelId)
                            {
                                _groupBuffer.Add(s);
                            }
                        }
                        (cMin, cMax) = PanelValueRangeCalculator.CalculateGroup(snapshot, _groupBuffer);

                        _labelBuilder.Clear();
                        for (int i = 0; i < _groupBuffer.Count; i++)
                        {
                            if (i > 0)
                            {
                                _labelBuilder.Append(" / ");
                            }
                            _labelBuilder.Append(_groupBuffer[i].DisplayName ?? _groupBuffer[i].Id);
                        }
                        label = _labelBuilder.ToString();
                    }
                    else
                    {
                        (cMin, cMax) = PanelValueRangeCalculator.Calculate(snapshot, setting);
                        label = setting.DisplayName ?? setting.Id;
                    }

                    decimal cRange = cMax - cMin; if (cRange == 0) cRange = 1m;

                    _crosshairRenderer.RenderPanel(canvas, area, mousePosition, cMin, cRange, label, renderConfig);

                    // DTW Oscillator: render highlight bands on main chart when hovering over DTW panel
                    if (setting.TypeEnum == IndicatorType.StructuralDtw
                        && mousePosition.Y >= area.Top && mousePosition.Y <= area.Bottom
                        && setting.ParameterObject is CoreStructuralDtwParameter dtwParam)
                    {
                        int hoverIndex = (int)Math.Floor((mousePosition.X - chartArea.Left) / (chartArea.Width / snapshot.Candles.Count));
                        if (hoverIndex >= 0 && hoverIndex < snapshot.Candles.Count)
                        {
                            canvas.Save();
                            canvas.Translate((float)chartArea.Left, 0);
                            var localChartArea = new global::Avalonia.Rect(0, chartArea.Y, chartArea.Width, chartArea.Height);
                            _dtwHighlightRenderer.Render(canvas, localChartArea, snapshot.Candles.Count, hoverIndex, dtwParam.Period, dtwParam.Lag);
                            canvas.Restore();
                        }
                    }
                }
            }
        }
    }
}
