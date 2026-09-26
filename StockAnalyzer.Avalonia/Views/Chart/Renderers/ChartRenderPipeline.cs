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
    private readonly Dictionary<string, int> _panelIndexByIdBuffer = new();
    private readonly HashSet<string> _seenGroupsBuffer = new();
    private readonly List<CoreIndicatorSettings> _groupBuffer = new();
    private readonly CrossMarkerRenderer _crossMarkerRenderer;
    private readonly SKPaint _panelBorderPaint;
    private readonly SubWindowTransformCoordinator _subWindowTransformCoordinator = new();
    private int _visibleIndicatorIndex;

    /// <summary>
    /// Panel transforms computed during the most recent <see cref="Execute"/> pass. Interaction code
    /// (pointer routing in <c>ChartBaseControl</c>) reads these to map pointer events inside a
    /// sub-window panel, mirroring how the main transform is reused between frames.
    /// </summary>
    public SubWindowTransformCoordinator SubWindowTransformCoordinator => _subWindowTransformCoordinator;

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

    private readonly Dictionary<StockAnalyzer.Core.Models.Drawing.PanelKey, int> _pipelineKeyToIndex = new();
    private readonly Dictionary<int, StockAnalyzer.Core.Models.Drawing.PanelKey> _pipelineIndexToKey = new();
    private readonly HashSet<string> _pipelineSeenGroups = new();
    private readonly List<CoreIndicatorSettings> _pipelineGroupScratch = new();

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
        _panelBorderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Gray.WithAlpha(80),
            IsAntialias = false,
            StrokeWidth = 1
        };
    }

    public void Dispose()
    {
        _backgroundRenderer.Dispose();
        _gridRenderer.Dispose();
        _axisRenderer.Dispose();
        _indicatorRenderer.Dispose();
        _crosshairRenderer.Dispose();
        _dtwHighlightRenderer.Dispose();
        _axisLabelRenderer.Dispose();
        _multiWavePatternRenderer.Dispose();
        _ghostProjectionRenderer.Dispose();
        _semanticPaintCache.Dispose();
        _crossMarkerRenderer.Dispose();
        _panelBorderPaint.Dispose();
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
        IChartObject? currentDrawingObject,
        LayeredDrawingScene? layeredScene = null)
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

        // 7. Render Drawing Objects & Feedback (Main Chart, PanelIndex == -1)
        canvas.Save();
        canvas.ClipRect(new SKRect((float)chartArea.Left, (float)chartArea.Top, (float)chartArea.Right, (float)chartArea.Bottom));
        canvas.Translate((float)chartArea.Left, (float)chartArea.Top);
        if (layeredScene != null)
        {
            layeredScene.RenderPanel(canvas, StockAnalyzer.Core.Models.Drawing.PanelKey.Main, coordinateTransform);
        }
        else
        {
            objectManager.Render(canvas, coordinateTransform);
        }
        // Preview the in-progress shape here only when it belongs to the main chart; a sub-panel
        // shape is previewed by its own panel pass (Step 7.5), never on the main chart.
        _feedbackRenderer.Render(canvas, snapPoint, currentDrawingObject, coordinateTransform, ChartLayoutExtensions.MainChartPanelIndex);
        canvas.Restore();

        // 7.5 Render Drawing Objects in sub-window panels (PanelIndex >= 0).
        // Panels share the chart's horizontal extent, so X mapping reuses the main transform
        // (via SubWindowCoordinateTransform) while Y maps to the panel's own indicator scale.
        if (layout.PanelAreas != null && layout.PanelAreas.Count > 0)
        {
            _subWindowTransformCoordinator.UpdateTransforms(layout, snapshot, coordinateTransform, renderConfig.IsSubWindowVisible);

            if (layeredScene != null)
            {
                DrawingPanelResolver.BuildPanelMap(
                    snapshot.IndicatorSettings,
                    renderConfig.IsSubWindowVisible,
                    _pipelineKeyToIndex,
                    _pipelineIndexToKey,
                    _pipelineSeenGroups,
                    _pipelineGroupScratch);
            }

            for (int k = 0; k < layout.PanelAreas.Count; k++)
            {
                var subTransform = _subWindowTransformCoordinator.GetTransform(k);
                if (subTransform == null) continue;

                var pa = layout.PanelAreas[k];
                canvas.Save();
                canvas.ClipRect(new SKRect((float)pa.Left, (float)pa.Top, (float)pa.Right, (float)pa.Bottom), SKClipOperation.Intersect, true);
                canvas.Translate((float)pa.Left, (float)pa.Top);
                if (layeredScene != null && _pipelineIndexToKey.TryGetValue(k, out var panelKey))
                {
                    layeredScene.RenderPanel(canvas, panelKey, subTransform);
                }
                else
                {
                    objectManager.RenderPanel(canvas, k, subTransform);
                }
                _feedbackRenderer.Render(canvas, snapPoint, currentDrawingObject, subTransform, k);
                canvas.Restore();
            }
        }

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
        _panelIndexByIdBuffer.Clear();

        var chartArea = layout.ChartArea;

        // 1. Pre-calculate panel ranges. Panel allocation is delegated to PanelLayoutEnumerator
        //    (the SSoT built on IndicatorPlacementResolver) so these indices match RenderIndicators exactly.
        if (snapshot.IndicatorSettings != null)
        {
            PanelLayoutEnumerator.ForEachPanel(
                snapshot.IndicatorSettings,
                renderConfig.IsSubWindowVisible,
                _seenGroupsBuffer,
                _groupBuffer,
                (Snapshot: snapshot, Ranges: _panelRangeBuffer, PanelById: _panelIndexByIdBuffer),
                static (int panelIndex, CoreIndicatorSettings primary, IReadOnlyList<CoreIndicatorSettings>? group,
                        (ChartDataSnapshot Snapshot, Dictionary<int, (decimal Min, decimal Max)> Ranges,
                         Dictionary<string, int> PanelById) st) =>
                {
                    st.Ranges[panelIndex] = group != null
                        ? PanelValueRangeCalculator.CalculateGroup(st.Snapshot, group)
                        : PanelValueRangeCalculator.Calculate(st.Snapshot, primary);
                    if (group != null)
                    {
                        for (int i = 0; i < group.Count; i++) st.PanelById[group[i].Id] = panelIndex;
                    }
                    else
                    {
                        st.PanelById[primary.Id] = panelIndex;
                    }
                });
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
            foreach (var setting in snapshot.IndicatorSettings)
            {
                if (!setting.IsEnabled) continue;

                // Panel ownership is resolved once by PanelLayoutEnumerator in section 1 (the SSoT that
                // matches RenderIndicators); a non-overlay indicator's axis label rides its owning panel.
                int labelPanelIdx = -1;
                if (!setting.IsOverlay && _panelIndexByIdBuffer.TryGetValue(setting.Id, out int resolvedPanelIdx))
                {
                    labelPanelIdx = resolvedPanelIdx;
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

        // Pre-scan: build overlay group info for grouped panels.
        // Only sub-window (non-overlay) indicators use these groups, so skip entirely when the sub-window panel is hidden.
        if (renderConfig.IsSubWindowVisible)
        {
            foreach (var setting in snapshot.IndicatorSettings)
            {
                if (!setting.IsEnabled || setting.IsOverlay) continue;
                if (setting.TypeEnum == IndicatorType.GranvilleLaw || setting.TypeEnum == IndicatorType.VolumeProfile) continue;
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
        }

        // Sub-window panel ownership comes from the PanelLayoutEnumerator SSoT (shared with
        // PrepareAxisLabels / RenderCrosshair), replacing the former hand-threaded ref counter.
        _panelIndexByIdBuffer.Clear();
        PanelLayoutEnumerator.ForEachPanel(
            snapshot.IndicatorSettings,
            renderConfig.IsSubWindowVisible,
            _seenGroupsBuffer,
            _groupBuffer,
            _panelIndexByIdBuffer,
            static (int panelIndex, CoreIndicatorSettings primary, IReadOnlyList<CoreIndicatorSettings>? group,
                    Dictionary<string, int> map) =>
            {
                if (group != null)
                {
                    for (int i = 0; i < group.Count; i++) map[group[i].Id] = panelIndex;
                }
                else
                {
                    map[primary.Id] = panelIndex;
                }
            });

        foreach (var setting in snapshot.IndicatorSettings)
        {
            var placement = IndicatorPlacementResolver.Resolve(setting, renderConfig.IsSubWindowVisible);
            if (placement == IndicatorPlacement.Skipped) continue;

            bool isGranville = setting.TypeEnum == IndicatorType.GranvilleLaw;

            if (isGranville)
            {
                // 1. Always render the MA line on the main chart
                RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, isSubWindowContext: false, panelRanges: _panelRanges);

                // 2. Render the sub-window histogram if enabled AND sub-window is visible
                if (placement == IndicatorPlacement.GranvilleMainAndSubWindow)
                {
                    RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting,
                        isSubWindowContext: true, panelIndex: _panelIndexByIdBuffer[setting.Id], panelRanges: _panelRanges);
                }
            }
            else if (!setting.IsOverlay && setting.TypeEnum != IndicatorType.VolumeProfile)
            {
                int subPanelIdx = _panelIndexByIdBuffer[setting.Id];
                if (!string.IsNullOrEmpty(setting.OverlayPanelId))
                {
                    // Grouped panel indicator: the first member draws the panel background, the rest reuse it.
                    string groupId = setting.OverlayPanelId;
                    var range = _groupRanges[groupId];

                    bool isFirstInGroup = !_assignedPanelIndices.ContainsKey(groupId);
                    if (isFirstInGroup) _assignedPanelIndices[groupId] = subPanelIdx;

                    RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting,
                        isSubWindowContext: true, panelIndex: subPanelIdx, drawPanelBackground: isFirstInGroup,
                        overrideMinValue: range.Min, overrideMaxValue: range.Max, panelRanges: _panelRanges);
                }
                else
                {
                    // Normal sub-window indicator
                    RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting,
                        isSubWindowContext: true, panelIndex: subPanelIdx, panelRanges: _panelRanges);
                }
            }
            else
            {
                // Overlay on main chart
                RenderSingleIndicatorPass(canvas, layout, snapshot, renderConfig, setting, isSubWindowContext: false, panelRanges: _panelRanges);
            }

            // Collect lines for Cross Markers. A sub-window indicator's result rides its owning panel
            // (from the PanelLayoutEnumerator map); overlays and main-only Granville own no panel.
            if (setting.IsEnabled && (!setting.IsOverlay || setting.TypeEnum == IndicatorType.GranvilleLaw))
            {
                int currentPanelIdx = -1;
                if (!setting.IsOverlay && _panelIndexByIdBuffer.TryGetValue(setting.Id, out int ownerPanelIdx))
                {
                    currentPanelIdx = ownerPanelIdx;
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
        bool isSubWindowContext,
        int panelIndex = -1,
        bool drawPanelBackground = true,
        decimal? overrideMinValue = null,
        decimal? overrideMaxValue = null,
        Dictionary<int, (decimal Min, decimal Max)>? panelRanges = null)
    {
        Rect targetRect;

        if (isSubWindowContext)
        {
            int targetPanelIdx = panelIndex;

            if (targetPanelIdx < layout.PanelAreas.Count)
            {
                var pa = layout.PanelAreas[targetPanelIdx];
                targetRect = new global::Avalonia.Rect(0, pa.Y, pa.Width, pa.Height);

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

                    canvas.DrawLine((float)targetRect.Left, (float)targetRect.Top, (float)targetRect.Right, (float)targetRect.Top, _panelBorderPaint);
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
                // Panel allocation is delegated to PanelLayoutEnumerator (the SSoT shared with the
                // indicator/axis passes); its rules already match this loop exactly (Granville gated
                // on ShowSubWindowBar with no panel for the main-only pass; Volume Profile excluded
                // via its forced IsOverlay; grouped panels first-seen in settings order).
                PanelLayoutEnumerator.ForEachPanel(
                    snapshot.IndicatorSettings,
                    renderConfig.IsSubWindowVisible,
                    _seenGroupsBuffer,
                    _groupBuffer,
                    (Pipe: this, Canvas: canvas, Layout: layout, Snapshot: snapshot, Config: renderConfig,
                     Mouse: mousePosition, ChartArea: chartArea),
                    static (int panelIndex, CoreIndicatorSettings primary, IReadOnlyList<CoreIndicatorSettings>? group,
                            (ChartRenderPipeline Pipe, SKCanvas Canvas, ChartLayoutContext Layout, ChartDataSnapshot Snapshot,
                             IChartRenderConfig Config, global::Avalonia.Point Mouse, global::Avalonia.Rect ChartArea) st) =>
                    {
                        if (panelIndex >= st.Layout.PanelAreas.Count) return;
                        var area = st.Layout.PanelAreas[panelIndex];

                        decimal cMin, cMax;
                        string label;
                        if (group != null)
                        {
                            (cMin, cMax) = PanelValueRangeCalculator.CalculateGroup(st.Snapshot, group);

                            var lb = st.Pipe._labelBuilder;
                            lb.Clear();
                            for (int i = 0; i < group.Count; i++)
                            {
                                if (i > 0) lb.Append(" / ");
                                lb.Append(group[i].DisplayName ?? group[i].Id);
                            }
                            label = lb.ToString();
                        }
                        else
                        {
                            (cMin, cMax) = PanelValueRangeCalculator.Calculate(st.Snapshot, primary);
                            label = primary.DisplayName ?? primary.Id;
                        }

                        decimal cRange = cMax - cMin; if (cRange == 0) cRange = 1m;

                        st.Pipe._crosshairRenderer.RenderPanel(st.Canvas, area, st.Mouse, cMin, cRange, label, st.Config);

                        // DTW Oscillator: render highlight bands on main chart when hovering over DTW panel
                        if (primary.TypeEnum == IndicatorType.StructuralDtw
                            && st.Mouse.Y >= area.Top && st.Mouse.Y <= area.Bottom
                            && primary.ParameterObject is CoreStructuralDtwParameter dtwParam)
                        {
                            int hoverIndex = (int)Math.Floor((st.Mouse.X - st.ChartArea.Left) / (st.ChartArea.Width / st.Snapshot.Candles.Count));
                            if (hoverIndex >= 0 && hoverIndex < st.Snapshot.Candles.Count)
                            {
                                st.Canvas.Save();
                                st.Canvas.Translate((float)st.ChartArea.Left, 0);
                                var localChartArea = new global::Avalonia.Rect(0, st.ChartArea.Y, st.ChartArea.Width, st.ChartArea.Height);
                                st.Pipe._dtwHighlightRenderer.Render(st.Canvas, localChartArea, st.Snapshot.Candles.Count, hoverIndex, dtwParam.Period, dtwParam.Lag);
                                st.Canvas.Restore();
                            }
                        }
                    });
            }
        }
    }
}
