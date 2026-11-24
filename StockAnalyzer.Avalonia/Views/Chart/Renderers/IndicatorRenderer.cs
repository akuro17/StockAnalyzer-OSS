using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders indicators (SMA, EMA, etc.) on the chart.
/// </summary>
public sealed class IndicatorRenderer : IChartRenderer, IDisposable
{
    private readonly LineIndicatorRenderer _lineRenderer = new();
    private readonly HistogramRenderer _histogramRenderer = new();
    private readonly StepLineRenderer _stepLineRenderer = new();
    private readonly BlockChartOverlayRenderer _blockChartOverlayRenderer = new();
    private readonly VolumeIndicatorRenderer _volumeRenderer = new();
    private readonly ParabolicSarRenderer _sarRenderer = new();
    private readonly IchimokuRenderer _ichimokuRenderer = new();
    private readonly MarketStructureShiftRenderer _mssRenderer = new();
    private readonly GranvilleLawRenderer _granvilleRenderer = new();
    private readonly VolumeProfileRenderer _vpRenderer = new();
    private readonly IndexIndicatorRenderer _indexIndicatorRenderer = new(); // Generalized from RenkoIndicatorRenderer
    private readonly SeriesPrimitiveRendererRegistry _primitiveRegistry = new();
    private readonly Dictionary<string, IReadOnlyList<decimal?>> _ichimokuSeriesCache = new();
    private readonly Dictionary<string, GenericCoordinateTransform> _localTransforms = new();
    private readonly Dictionary<string, SubWindowCoordinateTransform> _subTransforms = new();
    // Reused across RenderIndicator() calls to avoid per-frame SKPaint/SKPath allocations
    // in the sub-window histogram/line branch (SA_RENDERING_PERFORMANCE.md ZeroAllocation rule).
    private readonly SKPaint _subHistPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKPaint _subLinePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _subPath = new();
    private const long DescriptorErrorLogCooldownMs = 60_000;
    private const int MaxTrackedDescriptorErrorKeys = 64;
    private const int MaxSynthesizedDescriptorCacheEntries = 256;
    private const float SynthesizedHistogramWidthRatio = 0.7f;
    private const float SynthesizedHorizontalSegmentWidthRatio = 0.8f;
    private const double SynthesizedSplineTension = 0.5;
    private const byte SynthesizedAreaFillAlpha = 96;
    private const byte SynthesizedOpaqueAlpha = 255;
    private const float SynthesizedMarkerRadiusPx = 4.0f;
    private const float SynthesizedStripHeightPx = 12.0f;
    private const byte SynthesizedStripAlpha = 200;
    private const float SynthesizedDynamicMinWidthPx = 1.0f;
    private const float SynthesizedDynamicMaxWidthPx = 4.0f;
    private const float SynthesizedLollipopStemWidthPx = 1.0f;
    private Dictionary<(string IndicatorId, SeriesPrimitiveType PrimitiveType, Type ExceptionType), long>? _descriptorErrorLastLoggedMs;
    private readonly Dictionary<SynthesizedDescriptorKey, CachedSynthesizedDescriptor> _synthesizedDescriptorCache = new();
    private bool _disposed;

    private readonly record struct SynthesizedDescriptorKey(string IndicatorId, string SeriesName);

    private sealed class CachedSynthesizedDescriptor
    {
        public SeriesPrimitiveType PrimitiveType;
        public CoreLineStyle LineStyle;
        public NullGapPolicy NullPolicy;
        public SeriesPrimitiveDescriptor Descriptor = null!;
    }

    public VolumeProfileViewportCoordinator? ViewportCoordinator { get; set; }
    public TimeAtPriceViewportCoordinator? TimeAtPriceCoordinator { get; set; }

    public IndicatorRenderer()
    {
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        // Legacy/Fallback: Render all setting using the provided chartArea
        if (snapshot.IndicatorSettings == null) return;

        foreach (var setting in snapshot.IndicatorSettings)
        {
            if (!setting.IsEnabled) continue;
            // For backward compatibility, we assume standard render uses chartArea.
            // But now we have Panels, so this method should effectively be deprecated 
            // OR used only for overlays if called with ChartLayoutContext.ChartArea.

            // However, ChartDrawOperation will now manage the loop.
            RenderIndicator(canvas, chartArea, snapshot, setting, config);
        }
    }

    public void RenderIndicator(SKCanvas canvas, Rect targetRect, ChartDataSnapshot snapshot, CoreIndicatorSettings setting, IChartRenderConfig config, bool isSubWindowContext = false, decimal? overrideMinValue = null, decimal? overrideMaxValue = null)
    {
        try
        {
            if (_disposed || !setting.IsEnabled) return;

            // Determine Value Range
            decimal minVal = snapshot.MinPrice;
            decimal maxVal = snapshot.MaxPrice;

            // Coordinate Transform Setup
            ICoordinateTransform? transform = config.Transform;

            // Calculate Timer Interval for Extrapolation
            TimeSpan interval = TimeSpan.FromDays(1);
            if (snapshot.Candles.Count > 1)
            {
                interval = snapshot.Candles[snapshot.Candles.Count - 1].Timestamp - snapshot.Candles[snapshot.Candles.Count - 2].Timestamp;
                if (interval <= TimeSpan.Zero) interval = TimeSpan.FromDays(1);
            }
            else if (snapshot.Candles.Count == 1 && !string.IsNullOrEmpty(snapshot.Timeframe))
            {
                // Try to parse timeframe? Default to 1 day.
            }

            if (transform == null || isSubWindowContext)
            {
                // Use StartIndex as fallback or for sub-window context scaling
                int count = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles.Count;

                if (!_localTransforms.TryGetValue(setting.Id, out var tr))
                {
                    tr = new GenericCoordinateTransform(ChartAxisMode.Time, targetRect.Width, targetRect.Height);
                    _localTransforms[setting.Id] = tr;
                }
                else
                {
                    tr.UpdateCanvasSize(targetRect.Width, targetRect.Height, 0, 0, targetRect.Width, targetRect.Height);
                }

                if (transform != null)
                {
                    tr.PriceScale = (transform.PriceScale == PriceScaleType.Inverted && config.InvertOscillator) ? PriceScaleType.Inverted : PriceScaleType.Linear;
                }

                // Set Data Ranges
                if (snapshot.Candles.Count > 0)
                {
                    var first = snapshot.Candles[0].Timestamp;
                    var last = snapshot.Candles[snapshot.Candles.Count - 1].Timestamp;

                    tr.UpdateRange(first, last, minVal, maxVal);

                    if (transform != null && isSubWindowContext)
                    {
                        if (!_subTransforms.TryGetValue(setting.Id, out var subTr))
                        {
                            subTr = new SubWindowCoordinateTransform(transform, tr);
                            _subTransforms[setting.Id] = subTr;
                        }
                        else
                        {
                            subTr.UpdateTransforms(transform, tr);
                        }
                        transform = subTr;
                    }
                    else
                    {
                        transform = tr;
                    }
                }
                else
                {
                    return;
                }
            }

            float xOffset = 0f;
            {
                int tempVisible = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles.Count;
                float tempCandleWidth;
                if (transform != null && snapshot.Candles != null && snapshot.Candles.Count > 0)
                {
                    double x0 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
                    double x1 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp + interval, 0)).X;
                    tempCandleWidth = (float)(x1 - x0);
                }
                else
                {
                    tempCandleWidth = tempVisible > 0 ? (float)(targetRect.Width / tempVisible) : 0f;
                }

                bool isLineOrArea = config.ChartType == ChartType.Line || config.ChartType == ChartType.Area;
                xOffset = isLineOrArea ? tempCandleWidth / 2f : 0f;
            }

            // Try get multi-series result
            if (snapshot.IndicatorResults != null && snapshot.IndicatorResults.TryGetValue(setting.Id, out var result))
            {
                if (!result.IsSuccessful) return;

                // If not overlay AND rendering in subwindow, calculate local range from visual data
                if (!setting.IsOverlay && isSubWindowContext)
                {
                    // Use override range if provided (for grouped panel overlay)
                    if (overrideMinValue.HasValue && overrideMaxValue.HasValue)
                    {
                        minVal = overrideMinValue.Value;
                        maxVal = overrideMaxValue.Value;
                    }
                    // Check for generic fixed range properties
                    else if (setting.MinValue.HasValue && setting.MaxValue.HasValue)
                    {
                        minVal = setting.MinValue.Value;
                        maxVal = setting.MaxValue.Value;
                    }
                    else
                    {
                        // Auto-scale logic (existing)
                        minVal = decimal.MaxValue;
                        maxVal = decimal.MinValue;
                        bool hasValue = false;

                        // Iterate all candles in the snapshot (already sliced to visible viewport) 
                        // for accurate auto-scaling in separate panel
                        int candleCount = snapshot.Candles!.Count;

                        if (result is IPrimitiveAwareIndicatorResult { PrimitiveDescriptors.Count: > 0 } awareRange)
                        {
                            SeriesPrimitiveValueRangeHelper.AccumulateRange(
                                result,
                                awareRange.PrimitiveDescriptors,
                                setting,
                                candleCount,
                                transform?.PriceScale ?? snapshot.PriceScale,
                                ref minVal,
                                ref maxVal,
                                ref hasValue,
                                offset: setting.Offset);
                        }
                        else
                        {
                            var seriesNames = result.SeriesNamesList;
                            for (int sIdx = 0; sIdx < seriesNames.Count; sIdx++)
                            {
                                var seriesName = seriesNames[sIdx];
                                if (SeriesRenderStyleCatalog.ShouldExcludeFromYRange(setting, seriesName))
                                    continue;

                                var seriesValues = result.GetSeries(seriesName);
                                if (seriesValues == null) continue;

                                for (int i = 0; i < candleCount; i++)
                                {
                                    // Indicators results are calculated from Data, then sliced/padded to match view.
                                    // The slice matches snapshot.Candles one-to-one.
                                    int valIdx = i - setting.Offset;
                                    if (valIdx < 0 || valIdx >= seriesValues.Count) continue;

                                    var v = seriesValues[valIdx];
                                    if (v.HasValue)
                                    {
                                        if (v.Value < minVal) minVal = v.Value;
                                        if (v.Value > maxVal) maxVal = v.Value;
                                        hasValue = true;
                                    }
                                }
                            }
                        }

                        if (!hasValue)
                        {
                            minVal = 0;
                            maxVal = 1; // Default
                        }
                        else
                        {
                            // Add padding (5%)
                            decimal buffer = (maxVal - minVal) * 0.05m;
                            if (buffer == 0) buffer = 1m;
                            minVal -= buffer;
                            maxVal += buffer;
                        }
                    }

                    // CRITICAL: Update the transform with the local price range for ALL sub-window contexts
                    // (Grouped, Fixed range, and Auto-scaled)
                    if (transform is SubWindowCoordinateTransform sub)
                    {
                        sub.YTransform.SetPriceRange(minVal, maxVal);
                    }
                    else if (transform is GenericCoordinateTransform gct)
                    {
                        gct.SetPriceRange(minVal, maxVal);

                        // Also align X-axis padding with main chart
                        if (snapshot.Candles!.Count > 0)
                        {
                            var halfInt = new TimeSpan(interval.Ticks / 2);
                            var minT = snapshot.Candles![0].Timestamp.Subtract(halfInt);
                            var maxT = snapshot.Candles![snapshot.Candles.Count - 1].Timestamp.Add(halfInt);
                            gct.SetTimeRange(minT, maxT);
                        }
                    }
                }

                if (setting.TypeEnum == IndicatorType.Volume)
                {
                    if (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, IndicatorResult.MainSeriesName)) return;

                    var values = result.MainValues;
                    var explicitVolumePrimitive = SeriesRenderStyleCatalog.ResolveExplicitPrimitive(setting, IndicatorResult.MainSeriesName);
                    if (explicitVolumePrimitive.HasValue &&
                        explicitVolumePrimitive.Value != SeriesPrimitiveType.Histogram &&
                        transform != null)
                    {
                        int volumeVisibleCandles = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count;
                        RenderSynthesizedSeries(
                            canvas, targetRect, snapshot, result, setting, config, transform,
                            IndicatorResult.MainSeriesName, values, setting.Color.ToSkColor(),
                            explicitVolumePrimitive.Value, minVal, maxVal, volumeVisibleCandles,
                            setting.Offset, interval, xOffset, isSubWindowContext);
                        return;
                    }

                    _volumeRenderer.Render(canvas, targetRect, values, setting, minVal, maxVal,
                       snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count,
                       setting.Offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform);
                    return;
                }

                // CHECK for Volume Profile using TypeEnum and CustomData
                if (setting.TypeEnum == IndicatorType.VolumeProfile)
                {
                    if (transform == null) return;

                    if (ViewportCoordinator != null)
                    {
                        // F03 fix: once the viewport-aware coordinator pipeline is wired in (always the case
                        // for a real ChartBaseControl instance), it is authoritative for on-chart VP
                        // rendering. Previously, any non-Success state (Empty from the F02 index bug, still
                        // Waiting/Computing, or Failed) fell through to the static result.CustomData profile
                        // below, which silently redrew a stale, unrelated period and masked the F02 defect.
                        // Empty/Failed/not-yet-computed now render nothing rather than a wrong-period profile
                        // (sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F03/A10).
                        //
                        // F05 fix: the coordinator keeps one independent result PER setting.Id, so each
                        // registered VolumeProfile instance renders its own Period/RowCount/Bins instead of
                        // every instance sharing whichever setting last completed computation (F05/A12).
                        var vpResult = ViewportCoordinator.GetResult(setting.Id);
                        if (vpResult != null &&
                            vpResult.Status == VolumeProfileResultStatus.Success &&
                            vpResult.Segments.Count > 0)
                        {
                            _vpRenderer.RenderViewport(canvas, targetRect, vpResult, transform, config, setting);
                        }
                        return;
                    }

                    // Legacy path: no viewport coordinator wired in for this renderer instance (e.g. isolated
                    // usage outside ChartBaseControl). Retains the pre-viewport static rendering contract.
                    if (result.CustomData is List<Core.Analysis.VolumeBin> profile)
                    {
                        // Get Side from parameters if possible
                        bool isRight = true;
                        if (setting.ParameterObject is CoreVolumeProfileParameter vpParam)
                        {
                            isRight = vpParam.Side == DisplaySide.Right || vpParam.Side == DisplaySide.Both;
                            if (vpParam.Side == DisplaySide.Left) isRight = false;
                        }

                        _vpRenderer.Render(canvas, targetRect, profile, minVal, maxVal, transform, isRight, config, setting);

                        if (setting.ParameterObject is CoreVolumeProfileParameter vpParam2 && vpParam2.Side == DisplaySide.Both)
                        {
                            _vpRenderer.Render(canvas, targetRect, profile, minVal, maxVal, transform, false, config, setting);
                        }

                        // UPDATE: User requested ONLY histogram. Return here to skip standard line rendering.
                        return;
                    }
                }

                // CHECK for Time at Price using TypeEnum and CustomData
                if (setting.TypeEnum == IndicatorType.TimeAtPrice)
                {
                    if (transform == null) return;

                    if (TimeAtPriceCoordinator != null)
                    {
                        var tapResult = TimeAtPriceCoordinator.GetResult(setting.Id);
                        if (tapResult != null &&
                            tapResult.Status == VolumeProfileResultStatus.Success &&
                            tapResult.Segments.Count > 0)
                        {
                            _vpRenderer.RenderViewport(canvas, targetRect, tapResult, transform, config, setting);
                        }
                        return;
                    }

                    // Fallback / legacy path
                    if (result.CustomData is List<Core.Analysis.VolumeBin> profile)
                    {
                        bool isRight = true;
                        if (setting.ParameterObject is CoreTimeAtPriceParameter tapParam)
                        {
                            isRight = tapParam.Side == DisplaySide.Right || tapParam.Side == DisplaySide.Both;
                            if (tapParam.Side == DisplaySide.Left) isRight = false;
                        }

                        _vpRenderer.Render(canvas, targetRect, profile, minVal, maxVal, transform, isRight, config, setting);

                        if (setting.ParameterObject is CoreTimeAtPriceParameter tapParam2 && tapParam2.Side == DisplaySide.Both)
                        {
                            _vpRenderer.Render(canvas, targetRect, profile, minVal, maxVal, transform, false, config, setting);
                        }

                        return;
                    }
                }

                // CHECK for Parabolic SAR
                if (setting.TypeEnum == IndicatorType.ParabolicSAR)
                {
                    if (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, IndicatorResult.MainSeriesName)) return;

                    var values = result.MainValues; // SAR values
                    var explicitSarPrimitive = SeriesRenderStyleCatalog.ResolveExplicitPrimitive(setting, IndicatorResult.MainSeriesName);
                    if (explicitSarPrimitive.HasValue && transform != null)
                    {
                        int sarVisibleCandles = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count;
                        RenderSynthesizedSeries(
                            canvas, targetRect, snapshot, result, setting, config, transform,
                            IndicatorResult.MainSeriesName, values, setting.Color.ToSkColor(),
                            explicitSarPrimitive.Value, minVal, maxVal, sarVisibleCandles,
                            setting.Offset, interval, xOffset, isSubWindowContext);
                        return;
                    }

                    var trend = result.GetSeries("IsUpTrend"); // Trend values (1 or 0)

                    _sarRenderer.Render(canvas, targetRect, values, trend, setting, minVal, maxVal,
                      snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count,
                      setting.Offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform);

                    return;
                }

                // Block chart overlays (Renko, P&F, Kagi, Three Line Break as time-series indicators): up/down coloured step line + Reversal/Update dots.
                // Never drawn on a non-time-series main chart (their price axis share is skipped in ChartDataSnapshot too) and never in a sub-window.
                if (BlockChartOverlayTypes.Contains(setting.TypeEnum))
                {
                    if (BlockChartOverlayTypes.IsHiddenOn(snapshot.ChartType, setting.TypeEnum) || isSubWindowContext) return;

                    _blockChartOverlayRenderer.Render(canvas, targetRect,
                        result.MainValues,
                        result.HasSeries(IndicatorSeriesNames.UpTrendFlag) ? result.GetSeries(IndicatorSeriesNames.UpTrendFlag) : null,
                        result.HasSeries(nameof(CoreBlockChartOverlayIndicatorBase.Reversal)) ? result.GetSeries(nameof(CoreBlockChartOverlayIndicatorBase.Reversal)) : null,
                        result.HasSeries(CoreBlockChartOverlayIndicatorBase.UpdateSeriesName) ? result.GetSeries(CoreBlockChartOverlayIndicatorBase.UpdateSeriesName) : null,
                        setting,
                        (SeriesRenderStyleCatalog.FindSeriesConfig(setting, nameof(CoreBlockChartOverlayIndicatorBase.Reversal))?.Color ?? setting.Color).ToSkColor(),
                        (SeriesRenderStyleCatalog.FindSeriesConfig(setting, CoreBlockChartOverlayIndicatorBase.UpdateSeriesName)?.Color ?? setting.Color).ToSkColor(),
                        snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count,
                        setting.Offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform);
                    return;
                }

                // CHECK for StepLine Indicators (Renko, P&F, Kagi, ThreeLineBreak)
                if (setting.TypeEnum == IndicatorType.Renko || setting.TypeEnum == IndicatorType.RenkoOverlay ||
                    setting.TypeEnum == IndicatorType.Kagi || setting.TypeEnum == IndicatorType.KagiOverlay ||
                    setting.TypeEnum == IndicatorType.PointAndFigure || setting.TypeEnum == IndicatorType.PointAndFigureOverlay ||
                    setting.TypeEnum == IndicatorType.ThreeLineBreak)
                {
                    var values = result.MainValues;
                    _stepLineRenderer.Render(canvas, targetRect, values, setting, minVal, maxVal,
                       snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count,
                       setting.Offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform);
                    return;
                }

                // Opt-in declarative rendering primitives (strict non-empty guard so legacy wrappers returning Array.Empty fall through).
                if (result is IPrimitiveAwareIndicatorResult { PrimitiveDescriptors.Count: > 0 } primitiveAware)
                {
                    RenderPrimitiveDescriptors(
                        canvas,
                        targetRect,
                        snapshot,
                        result,
                        primitiveAware.PrimitiveDescriptors,
                        setting,
                        config,
                        transform,
                        minVal,
                        maxVal,
                        interval,
                        xOffset,
                        isSubWindowContext);
                    return;
                }

                // Generalized rendering for Index-based charts (Renko, P&F, Kagi, etc.)
                // CRITICAL: Only route OVERLAY indicators to IndexIndicatorRenderer.
                // Sub-window indicators (RSI, MACD, etc.) must use the standard rendering path
                // with SubWindowCoordinateTransform for proper Y-axis scaling.
                if (snapshot.ChartType.IsIndexBased() && !isSubWindowContext)
                {
                    int indexVisibleCandles = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count;
                    var seriesNames = result.SeriesNamesList;
                    for (int i = 0; i < seriesNames.Count; i++)
                    {
                        var seriesName = seriesNames[i];
                        if (PanelValueRangeCalculator.IsSignalSeries(seriesName) ||
                            IndicatorChartSuppression.IsSuppressed(setting.TypeEnum, seriesName, setting.ParameterObject) ||
                            !SeriesRenderStyleCatalog.IsSeriesVisible(setting, seriesName))
                            continue;

                        var seriesValues = result.GetSeries(seriesName);
                        if (seriesValues == null || seriesValues.Count == 0) continue;

                        var explicitPrimitive = SeriesRenderStyleCatalog.ResolveExplicitPrimitive(setting, seriesName);
                        if (explicitPrimitive.HasValue && transform != null)
                        {
                            var colorConfig = SeriesRenderStyleCatalog.FindSeriesConfig(setting, seriesName);
                            SKColor seriesColor = colorConfig != null
                                ? colorConfig.Color.ToSkColor()
                                : setting.Color.ToSkColor();
                            RenderSynthesizedSeries(
                                canvas, targetRect, snapshot, result, setting, config, transform,
                                seriesName, seriesValues, seriesColor, explicitPrimitive.Value,
                                minVal, maxVal, indexVisibleCandles, setting.Offset, interval, xOffset, isSubWindowContext);
                            continue;
                        }

                        _indexIndicatorRenderer.Render(canvas, targetRect, seriesValues, setting, config, snapshot);
                    }
                    return;
                }

                // Index-based sub-window rendering (RSI, MACD on Renko/Kagi/P&F/TLB)
                // Standard LineIndicatorRenderer uses timestamp-based X mapping which is incompatible
                // with index-based charts. Use GetXFromIndex for X, GetYFromPrice (from sub-window 
                // transform with locally computed min/max) for Y.
                if (snapshot.ChartType.IsIndexBased() && isSubWindowContext)
                {
                    var mainTransform = config.Transform;
                    if (mainTransform == null) return;

                    // Use the already-configured sub-window Y range (minVal/maxVal computed above)
                    decimal subRange = maxVal - minVal;
                    if (subRange == 0) subRange = 1m;

                    float columnWidth = (float)mainTransform.ScaleX;
                    float panelTop = (float)targetRect.Top;
                    float panelHeight = (float)targetRect.Height;
                    int subVisibleCandles = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count;

                    var subSeriesNames = result.SeriesNamesList;
                    for (int sIdx = 0; sIdx < subSeriesNames.Count; sIdx++)
                    {
                        var seriesName = subSeriesNames[sIdx];
                        if (PanelValueRangeCalculator.IsSignalSeries(seriesName))
                            continue;

                        // Not charted (degree/bar scale would dominate the panel). Same set as the
                        // standard sub-panel path. SSoT: IndicatorChartSuppression.
                        if (IndicatorChartSuppression.IsSuppressed(setting.TypeEnum, seriesName, setting.ParameterObject))
                            continue;

                        if (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, seriesName))
                            continue;

                        var values = result.GetSeries(seriesName);
                        if (values == null || values.Count == 0) continue;

                        int count = Math.Min(values.Count, snapshot.Candles!.Count);

                        // Determine series color
                        SKColor seriesColor;
                        var colorConfig = SeriesRenderStyleCatalog.FindSeriesConfig(setting, seriesName);
                        seriesColor = colorConfig != null
                            ? colorConfig.Color.ToSkColor()
                            : setting.Color.ToSkColor();

                        var explicitPrimitive = SeriesRenderStyleCatalog.ResolveExplicitPrimitive(setting, seriesName);
                        if (explicitPrimitive.HasValue && transform != null)
                        {
                            RenderSynthesizedSeries(
                                canvas, targetRect, snapshot, result, setting, config, transform,
                                seriesName, values, seriesColor, explicitPrimitive.Value,
                                minVal, maxVal, subVisibleCandles, setting.Offset, interval, xOffset, isSubWindowContext);
                            continue;
                        }

                        bool isHistogram = seriesName.Contains("Histogram", StringComparison.OrdinalIgnoreCase);

                        if (isHistogram)
                        {
                            // Histogram rendering for sub-window (e.g. MACD Histogram)
                            float zeroY = panelTop + panelHeight * (float)((maxVal - 0m) / subRange);

                            var theme = config.ThemeManager.CurrentTheme;
                            for (int i = 0; i < count; i++)
                            {
                                var val = values[i];
                                if (!val.HasValue) continue;

                                double logicalIdx = snapshot.GetLogicalXIndex(i, config);
                                float x = (float)mainTransform.GetXFromIndex(logicalIdx) + (float)targetRect.Left;
                                float y = panelTop + panelHeight * (float)((maxVal - val.Value) / subRange);
                                float barWidth = MathF.Max(1f, columnWidth * 0.6f);
                                float barX = x + (columnWidth - barWidth) / 2f;

                                _subHistPaint.Color = val.Value >= 0 ? theme.Bullish.ToSkColor() : theme.Bearish.ToSkColor();
                                canvas.DrawRect(barX, Math.Min(y, zeroY), barWidth, Math.Abs(y - zeroY), _subHistPaint);
                            }
                        }
                        else
                        {
                            // Line rendering for sub-window (e.g. RSI, MACD Signal/Main)
                            _subLinePaint.StrokeWidth = (float)setting.Thickness;
                            _subLinePaint.Color = seriesColor;

                            _subPath.Reset();
                            bool isFirst = true;

                            for (int i = 0; i < count; i++)
                            {
                                var val = values[i];
                                if (!val.HasValue)
                                {
                                    isFirst = true;
                                    continue;
                                }

                                double logicalIdx = snapshot.GetLogicalXIndex(i, config);
                                float x = (float)mainTransform.GetXFromIndex(logicalIdx) + (float)targetRect.Left;
                                float centerX = MathF.Floor(x + columnWidth / 2f) + 0.5f;
                                float y = panelTop + panelHeight * (float)((maxVal - val.Value) / subRange);
                                y = MathF.Floor(y) + 0.5f;

                                if (isFirst)
                                {
                                    _subPath.MoveTo(centerX, y);
                                    isFirst = false;
                                }
                                else
                                {
                                    _subPath.LineTo(centerX, y);
                                }
                            }

                            if (!_subPath.IsEmpty)
                            {
                                canvas.DrawPath(_subPath, _subLinePaint);
                            }
                        }
                    }
                    return;
                }

                // CHECK for Market Structure Shift
                if (setting.TypeEnum == IndicatorType.MarketStructureShift)
                {
                    if (result.CustomData is IReadOnlyList<Core.Models.MarketStructure.MarketStructureShift> shifts)
                    {
                        _mssRenderer.Render(canvas, targetRect, shifts, setting, minVal, maxVal, transform, snapshot.Candles!, interval);
                    }
                    return;
                }

                int visibleCandles = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count;

                // Granville Law (Custom dual-renderer)
                if (setting.TypeEnum == IndicatorType.GranvilleLaw)
                {
                    if (isSubWindowContext)
                    {
                        if (transform == null) return;
                        _granvilleRenderer.Render(canvas, targetRect, setting, snapshot, transform, visibleCandles, interval, isSubWindowContext);
                        return;
                    }
                    // Else fall through to let the LineIndicatorRenderer draw the "MA" series
                }

                decimal priceRange = maxVal - minVal;
                if (priceRange == 0) priceRange = 1m;



                int offset = setting.Offset;

                // CHECK for Ichimoku Cloud
                bool isIchimoku = result.HasSeries(StockAnalyzer.Core.Models.Indicators.Trend.CoreIchimokuIndicator.SenkouSpanASeriesName) && result.HasSeries(StockAnalyzer.Core.Models.Indicators.Trend.CoreIchimokuIndicator.SenkouSpanBSeriesName);
                if (isIchimoku)
                {
                    // Force Offset 0 for Ichimoku (Data is already shifted by Logic)
                    offset = 0;

                    _ichimokuSeriesCache.Clear();
                    var ichimokuNames = result.SeriesNamesList;
                    for (int sIdx = 0; sIdx < ichimokuNames.Count; sIdx++)
                    {
                        var name = ichimokuNames[sIdx];
                        _ichimokuSeriesCache[name] = result.GetSeries(name);
                    }

                    _ichimokuRenderer.Render(
                        canvas, targetRect, _ichimokuSeriesCache, setting, minVal, maxVal,
                        visibleCandles, offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform, config,
                        this, snapshot, result, xOffset, isSubWindowContext);
                    return;
                }

                var stdSeriesNames = result.SeriesNamesList;
                for (int sIdx = 0; sIdx < stdSeriesNames.Count; sIdx++)
                {
                    var seriesName = stdSeriesNames[sIdx];

                    // Filter Signal Series (not drawn as continuous lines by LineIndicatorRenderer)
                    if (PanelValueRangeCalculator.IsSignalSeries(seriesName))
                        continue;

                    // Fix: Skip rendering Granville Law signals on the main chart to avoid pollution
                    if (!isSubWindowContext && setting.TypeEnum == IndicatorType.GranvilleLaw && seriesName == "Signals")
                        continue;

                    // Never draw series that are computed & served (screener) but whose degree/bar
                    // scale would dominate the panel's Y axis and hide its charted -1..1 series.
                    // SSoT: IndicatorChartSuppression (also consulted by PanelValueRangeCalculator).
                    if (IndicatorChartSuppression.IsSuppressed(setting.TypeEnum, seriesName, setting.ParameterObject))
                        continue;

                    if (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, seriesName))
                        continue;

                    var values = result.GetSeries(seriesName);
                    if (values == null || values.Count == 0) continue;


                    // Determine Series Color
                    SKColor seriesColor;
                    var colorConfig = SeriesRenderStyleCatalog.FindSeriesConfig(setting, seriesName);
                    if (colorConfig != null)
                    {
                        seriesColor = colorConfig.Color.ToSkColor();
                    }
                    else
                    {
                        seriesColor = setting.Color.ToSkColor();
                    }

                    // FIX: If color is transparent (Alpha=0) for Ichimoku lines, force default colors
                    if (isIchimoku && seriesColor.Alpha == 0)
                    {
                        var theme = config.ThemeManager.CurrentTheme;
                        if (seriesName.Contains("Tenkan", StringComparison.OrdinalIgnoreCase)) seriesColor = theme.IchimokuTenkan.ToSkColor();
                        else if (seriesName.Contains("Kijun", StringComparison.OrdinalIgnoreCase)) seriesColor = theme.IchimokuKijun.ToSkColor();
                        else if (seriesName.Contains("Chikou", StringComparison.OrdinalIgnoreCase)) seriesColor = theme.IchimokuChikou.ToSkColor();
                        else if (seriesName.Contains("Senkou", StringComparison.OrdinalIgnoreCase)) seriesColor = theme.IchimokuSenkou.ToSkColor();
                    }

                    var explicitPrimitive = SeriesRenderStyleCatalog.ResolveExplicitPrimitive(setting, seriesName);
                    if (explicitPrimitive.HasValue && transform != null)
                    {
                        RenderSynthesizedSeries(
                            canvas, targetRect, snapshot, result, setting, config, transform,
                            seriesName, values, seriesColor, explicitPrimitive.Value,
                            minVal, maxVal, visibleCandles, offset, interval, xOffset, isSubWindowContext);
                        continue;
                    }

                    // Determine style based on series name
                    if (seriesName.Contains("Histogram", StringComparison.OrdinalIgnoreCase))
                    {
                        _histogramRenderer.Render(canvas, targetRect, values, setting, minVal, maxVal, visibleCandles, offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform, seriesColor, xOffset);
                    }
                    else
                    {
                        // Use LineIndicatorRenderer
                        _lineRenderer.Render(canvas, targetRect, values, setting, minVal, maxVal, visibleCandles, offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform, seriesColor, xOffset);
                    }
                }

                // Cross marker rendering logic has been moved to ChartRenderPipeline
                // to support cross-indicator detection within the same panel.
            }
            else if (snapshot.IndicatorValues != null && snapshot.IndicatorValues.TryGetValue(setting.Id, out var values))
            {
                // Fallback / Legacy (Single Series without IIndicatorResult; primitive synthesis requires IIndicatorResult)
                if (values == null || values.Count == 0) return;
                if (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, IndicatorResult.MainSeriesName)) return;


                // Standard Line Rendering (for POC or other lines)
                // string mainSeriesName = "Main"; // Not used here

                if (!setting.IsOverlay)
                {
                    if (setting.MinValue.HasValue && setting.MaxValue.HasValue)
                    {
                        minVal = setting.MinValue.Value;
                        maxVal = setting.MaxValue.Value;
                    }
                    else
                    {
                        minVal = decimal.MaxValue;
                        maxVal = decimal.MinValue;
                        bool hasValue = false;
                        foreach (var v in values)
                        {
                            if (v.HasValue)
                            {
                                if (v.Value < minVal) minVal = v.Value;
                                if (v.Value > maxVal) maxVal = v.Value;
                                hasValue = true;
                            }
                        }
                        if (!hasValue)
                        {
                            minVal = 0;
                            maxVal = 1;
                        }
                        else
                        {
                            decimal buffer = (maxVal - minVal) * 0.05m;
                            if (buffer == 0) buffer = 1m;
                            minVal -= buffer;
                            maxVal += buffer;
                        }
                    }
                }
                decimal priceRange = maxVal - minVal;
                if (priceRange == 0) priceRange = 1m;

                int offset = setting.Offset;

                // Calculate Interval again just in case (redundant but safe)
                // ... already done above.

                int legacyVisibleCandles = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count;
                _lineRenderer.Render(canvas, targetRect, values, setting, minVal, maxVal, legacyVisibleCandles, offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform, null, xOffset);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IndicatorRenderer] RenderIndicator failed: {ex}");
        }
    }

    private DateTime GetCandleTime(int index, IReadOnlyList<CoreCandleData> candles, TimeSpan interval)
    {
        if (candles.Count == 0) return DateTime.MinValue; // Should not happen if rendering

        if (index < candles.Count)
        {
            return candles[index].Timestamp;
        }

        // Future Projection
        var lastCandle = candles[candles.Count - 1];
        int diff = index - (candles.Count - 1);
        return lastCandle.Timestamp.AddTicks(interval.Ticks * diff);
    }

    private void RenderPrimitiveDescriptors(
        SKCanvas canvas,
        Rect targetRect,
        ChartDataSnapshot snapshot,
        IIndicatorResult result,
        IReadOnlyList<SeriesPrimitiveDescriptor> descriptors,
        CoreIndicatorSettings setting,
        IChartRenderConfig config,
        ICoordinateTransform? transform,
        decimal minVal,
        decimal maxVal,
        TimeSpan interval,
        float xOffset,
        bool isSubWindowContext)
    {
        if (_disposed || transform == null || snapshot.Candles == null || snapshot.Candles.Count == 0 || descriptors.Count == 0)
        {
            return;
        }

        int visibleCandles = snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles.Count;
        if (visibleCandles <= 0) return;

        float columnWidth = ChartPrimitiveMath.ComputeColumnWidth(
            targetRect,
            snapshot.Candles,
            interval,
            visibleCandles,
            transform,
            config.ChartType.IsIndexBased(),
            config.VisibleStartIndex);
        if (!float.IsFinite(columnWidth) || columnWidth <= 0f) return;

        var theme = config.ThemeManager?.CurrentTheme ?? ThemeColors.Light;
        int count = descriptors.Count;
        int[]? rented = count > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<int>.Shared.Rent(count)
            : null;
        try
        {
            Span<int> order = rented != null ? rented.AsSpan(0, count) : stackalloc int[count];
            for (int i = 0; i < count; i++) order[i] = i;

            for (int i = 1; i < count; i++)
            {
                int key = order[i];
                int j = i - 1;
                while (j >= 0 && CompareDescriptorOrder(descriptors, order[j], key) > 0)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }

            for (int i = 0; i < count; i++)
            {
                var desc = descriptors[order[i]];
                if (IsDescriptorSuppressed(setting, desc)) continue;
                if (!IsDescriptorVisible(setting, desc)) continue;

                SeriesPrimitiveDescriptor effectiveDesc = desc;
                decimal? baselineOverride = null;
                if (SeriesRenderStyleCatalog.IsSelectablePrimitive(desc.PrimitiveType) &&
                    SeriesRenderStyleCatalog.ResolveExplicitPrimitive(setting, desc.PrimarySeriesName) is { } explicitOverride &&
                    explicitOverride != desc.PrimitiveType)
                {
                    if (NeedsSynthesizedBaseline(explicitOverride))
                    {
                        var values = result.GetSeries(desc.PrimarySeriesName);
                        baselineOverride = ComputeSynthesizedBaseline(setting, explicitOverride, transform.PriceScale, values, visibleCandles, setting.Offset);
                    }
                    var lineStyle = ResolveSynthesizedLineStyle(explicitOverride, setting.Style);
                    var nullPolicy = SeriesRenderStyleCatalog.ResolveNullGapPolicy(setting.TypeEnum);
                    effectiveDesc = GetOrCreateSynthesizedDescriptor(
                        setting.Id, desc.PrimarySeriesName, explicitOverride, lineStyle, nullPolicy);
                }

                var renderer = _primitiveRegistry.Resolve(effectiveDesc.PrimitiveType);
                if (renderer == null) continue;

                var colorConfig = SeriesRenderStyleCatalog.FindSeriesConfig(setting, effectiveDesc.PrimarySeriesName);
                SKColor seriesColor = colorConfig != null
                    ? colorConfig.Color.ToSkColor()
                    : setting.Color.ToSkColor();

                var ctx = new SeriesPrimitiveRenderContext(
                    canvas,
                    targetRect,
                    snapshot,
                    result,
                    setting,
                    effectiveDesc,
                    config,
                    transform,
                    seriesColor,
                    minVal,
                    maxVal,
                    visibleCandles,
                    setting.Offset,
                    config.VisibleStartIndex,
                    snapshot.Candles,
                    interval,
                    columnWidth,
                    xOffset,
                    isSubWindowContext,
                    theme,
                    baselineOverride);

                try
                {
                    renderer.Render(in ctx);
                }
                catch (Exception ex)
                {
                    LogDescriptorRenderError(setting.Id, effectiveDesc, ex);
                }
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    private void LogDescriptorRenderError(string indicatorId, SeriesPrimitiveDescriptor desc, Exception ex)
    {
        _descriptorErrorLastLoggedMs ??= new Dictionary<(string, SeriesPrimitiveType, Type), long>();
        if (_descriptorErrorLastLoggedMs.Count >= MaxTrackedDescriptorErrorKeys)
        {
            _descriptorErrorLastLoggedMs.Clear();
        }

        long nowMs = Environment.TickCount64;
        var key = (indicatorId ?? string.Empty, desc.PrimitiveType, ex.GetType());
        if (_descriptorErrorLastLoggedMs.TryGetValue(key, out long lastMs) &&
            nowMs - lastMs < DescriptorErrorLogCooldownMs)
        {
            return;
        }

        _descriptorErrorLastLoggedMs[key] = nowMs;
        Serilog.Log.Warning(
            ex,
            "[IndicatorRenderer] Primitive render failed: Indicator={IndicatorId}, Primitive={PrimitiveType}, Primary={PrimarySeries}",
            indicatorId,
            desc.PrimitiveType,
            desc.PrimarySeriesName);
    }

    private static bool IsDescriptorSuppressed(CoreIndicatorSettings setting, SeriesPrimitiveDescriptor desc)
    {
        if (desc.PrimitiveType == SeriesPrimitiveType.StackedArea && desc.StackedSeriesNames is { Count: > 0 } layers)
        {
            for (int k = 0; k < layers.Count; k++)
            {
                if (!IndicatorChartSuppression.IsSuppressed(setting.TypeEnum, layers[k], setting.ParameterObject) &&
                    SeriesRenderStyleCatalog.IsSeriesVisible(setting, layers[k]))
                {
                    return false;
                }
            }
            return true;
        }

        return IndicatorChartSuppression.IsSuppressed(setting.TypeEnum, desc.PrimarySeriesName, setting.ParameterObject);
    }

    private static bool IsDescriptorVisible(CoreIndicatorSettings setting, SeriesPrimitiveDescriptor desc)
    {
        if (desc.PrimitiveType == SeriesPrimitiveType.StackedArea)
        {
            return true;
        }

        if (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, desc.PrimarySeriesName))
        {
            return false;
        }

        bool isPairedMultiSeries = desc.PrimitiveType is SeriesPrimitiveType.BandCloud
            or SeriesPrimitiveType.FloatingBar
            or SeriesPrimitiveType.Waterfall
            || (desc.PrimitiveType == SeriesPrimitiveType.Area && desc.BaselineMode == AreaBaselineMode.ReferenceSeries)
            || (desc.PrimitiveType == SeriesPrimitiveType.Vector && desc.VectorMode == VectorInputMode.PriceSpaceEndpoints);

        if (isPairedMultiSeries && !string.IsNullOrEmpty(desc.SecondarySeriesName))
        {
            return SeriesRenderStyleCatalog.IsSeriesVisible(setting, desc.SecondarySeriesName);
        }

        return true;
    }

    internal void RenderSynthesizedSeries(
        SKCanvas canvas,
        Rect targetRect,
        ChartDataSnapshot snapshot,
        IIndicatorResult result,
        CoreIndicatorSettings setting,
        IChartRenderConfig config,
        ICoordinateTransform transform,
        string seriesName,
        IReadOnlyList<decimal?> values,
        SKColor seriesColor,
        SeriesPrimitiveType primitiveType,
        decimal minVal,
        decimal maxVal,
        int visibleCandles,
        int offset,
        TimeSpan interval,
        float xOffset,
        bool isSubWindowContext)
    {
        if (_disposed || snapshot.Candles == null || snapshot.Candles.Count == 0 || visibleCandles <= 0)
        {
            return;
        }

        var renderer = _primitiveRegistry.Resolve(primitiveType);
        if (renderer == null)
        {
            return;
        }

        float columnWidth = ChartPrimitiveMath.ComputeColumnWidth(
            targetRect,
            snapshot.Candles,
            interval,
            visibleCandles,
            transform,
            config.ChartType.IsIndexBased(),
            config.VisibleStartIndex);
        if (!float.IsFinite(columnWidth) || columnWidth <= 0f)
        {
            return;
        }

        decimal? baselineOverride = NeedsSynthesizedBaseline(primitiveType)
            ? ComputeSynthesizedBaseline(setting, primitiveType, transform.PriceScale, values, visibleCandles, offset)
            : null;
        var lineStyle = ResolveSynthesizedLineStyle(primitiveType, setting.Style);
        var nullPolicy = SeriesRenderStyleCatalog.ResolveNullGapPolicy(setting.TypeEnum);
        var desc = GetOrCreateSynthesizedDescriptor(setting.Id, seriesName, primitiveType, lineStyle, nullPolicy);
        var theme = config.ThemeManager?.CurrentTheme ?? ThemeColors.Light;

        var ctx = new SeriesPrimitiveRenderContext(
            canvas,
            targetRect,
            snapshot,
            result,
            setting,
            desc,
            config,
            transform,
            seriesColor,
            minVal,
            maxVal,
            visibleCandles,
            offset,
            config.VisibleStartIndex,
            snapshot.Candles,
            interval,
            columnWidth,
            xOffset,
            isSubWindowContext,
            theme,
            baselineOverride);

        try
        {
            renderer.Render(in ctx);
        }
        catch (Exception ex)
        {
            LogDescriptorRenderError(setting.Id, desc, ex);
        }
    }

    private static bool NeedsSynthesizedBaseline(SeriesPrimitiveType primitiveType) => primitiveType switch
    {
        SeriesPrimitiveType.Histogram => true,
        SeriesPrimitiveType.PositiveNegativeArea => true,
        SeriesPrimitiveType.MarkerSymbol => true,
        SeriesPrimitiveType.ColorStrip => true,
        SeriesPrimitiveType.Lollipop => true,
        _ => false
    };

    private static CoreLineStyle ResolveSynthesizedLineStyle(SeriesPrimitiveType primitiveType, CoreLineStyle settingStyle)
    {
        if (primitiveType == SeriesPrimitiveType.DashedLine)
        {
            return settingStyle is CoreLineStyle.Dash or CoreLineStyle.Dot or CoreLineStyle.DashDot
                ? settingStyle
                : CoreLineStyle.Dash;
        }

        return CoreLineStyle.Solid;
    }

    private static decimal ComputeSynthesizedBaseline(
        CoreIndicatorSettings setting,
        SeriesPrimitiveType primitiveType,
        PriceScaleType priceScale,
        IReadOnlyList<decimal?>? values,
        int visibleCandles,
        int offset)
    {
        bool isCenterDiverging = primitiveType is SeriesPrimitiveType.PositiveNegativeArea or SeriesPrimitiveType.ColorStrip;
        if (isCenterDiverging && setting.MinValue.HasValue && setting.MaxValue.HasValue)
        {
            decimal fixedCenter = (setting.MinValue.Value + setting.MaxValue.Value) * 0.5m;
            if (priceScale != PriceScaleType.Log || fixedCenter > 0m)
            {
                return fixedCenter;
            }
        }

        decimal minVal = decimal.MaxValue;
        decimal maxVal = decimal.MinValue;
        bool hasVal = false;
        if (values != null)
        {
            for (int i = 0; i < visibleCandles; i++)
            {
                int valIdx = i - offset;
                if (valIdx < 0 || valIdx >= values.Count) continue;
                var v = values[valIdx];
                if (!v.HasValue) continue;
                if (priceScale == PriceScaleType.Log && v.Value <= 0m) continue;
                if (v.Value < minVal) minVal = v.Value;
                if (v.Value > maxVal) maxVal = v.Value;
                hasVal = true;
            }
        }

        if (!hasVal)
        {
            return priceScale == PriceScaleType.Log ? 1m : 0m;
        }

        if (isCenterDiverging)
        {
            if (!setting.IsOverlay &&
                priceScale != PriceScaleType.Log &&
                ((minVal < 0m && maxVal > 0m) || setting.IncludeZeroBaseline))
            {
                return 0m;
            }

            return (minVal + maxVal) * 0.5m;
        }

        if (!setting.IsOverlay &&
            priceScale != PriceScaleType.Log &&
            (minVal <= 0m || setting.IncludeZeroBaseline))
        {
            return 0m;
        }

        if (priceScale == PriceScaleType.Log)
        {
            return minVal * 0.95m;
        }

        decimal span = maxVal - minVal;
        if (span > 0m)
        {
            return minVal - span * 0.05m;
        }

        return minVal > 0m ? minVal * 0.95m : minVal - 1m;
    }

    private SeriesPrimitiveDescriptor GetOrCreateSynthesizedDescriptor(
        string? indicatorId,
        string seriesName,
        SeriesPrimitiveType primitiveType,
        CoreLineStyle lineStyle,
        NullGapPolicy nullPolicy)
    {
        var key = new SynthesizedDescriptorKey(indicatorId ?? string.Empty, seriesName);
        if (_synthesizedDescriptorCache.TryGetValue(key, out var cached) &&
            cached.PrimitiveType == primitiveType &&
            cached.LineStyle == lineStyle &&
            cached.NullPolicy == nullPolicy)
        {
            return cached.Descriptor;
        }

        var descriptor = BuildSynthesizedDescriptor(seriesName, primitiveType, lineStyle, nullPolicy);
        if (cached == null)
        {
            if (_synthesizedDescriptorCache.Count >= MaxSynthesizedDescriptorCacheEntries)
            {
                _synthesizedDescriptorCache.Clear();
            }

            cached = new CachedSynthesizedDescriptor();
            _synthesizedDescriptorCache[key] = cached;
        }

        cached.PrimitiveType = primitiveType;
        cached.LineStyle = lineStyle;
        cached.NullPolicy = nullPolicy;
        cached.Descriptor = descriptor;
        return descriptor;
    }

    private static SeriesPrimitiveDescriptor BuildSynthesizedDescriptor(
        string seriesName,
        SeriesPrimitiveType primitiveType,
        CoreLineStyle lineStyle,
        NullGapPolicy nullPolicy) => primitiveType switch
    {
        SeriesPrimitiveType.Line => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Line,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.DashedLine => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.DashedLine,
            PrimarySeriesName = seriesName,
            LineStyle = lineStyle,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.Dot => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Dot,
            PrimarySeriesName = seriesName,
            MarkerRadiusPx = float.NaN,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.Histogram => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Histogram,
            PrimarySeriesName = seriesName,
            BaselineValue = 0m,
            SegmentWidthRatio = SynthesizedHistogramWidthRatio,
            FillAlpha = SynthesizedOpaqueAlpha
        },
        SeriesPrimitiveType.StepLine => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.StepLine,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            StepMode = StepAlignmentMode.After,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.HorizontalSegment => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.HorizontalSegment,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            SegmentWidthRatio = SynthesizedHorizontalSegmentWidthRatio
        },
        SeriesPrimitiveType.Spline => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Spline,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            SplineTension = SynthesizedSplineTension,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.Area => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Area,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            BaselineMode = AreaBaselineMode.PanelBottom,
            FillAlpha = SynthesizedAreaFillAlpha,
            DrawBorderStroke = true,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.PositiveNegativeArea => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.PositiveNegativeArea,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            BaselineMode = AreaBaselineMode.ScalarValue,
            BaselineValue = 0m,
            FillAlpha = SynthesizedAreaFillAlpha,
            DrawBorderStroke = true,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.MarkerSymbol => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.MarkerSymbol,
            PrimarySeriesName = seriesName,
            BaselineValue = 0m,
            MarkerShape = MarkerSymbolShape.Circle,
            MarkerRadiusPx = SynthesizedMarkerRadiusPx,
            MarkerFilled = true
        },
        SeriesPrimitiveType.ColorStrip => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.ColorStrip,
            PrimarySeriesName = seriesName,
            BaselineValue = 0m,
            StripAnchor = ColorStripAnchor.Bottom,
            StripHeightPx = SynthesizedStripHeightPx,
            StripScaleMode = ColorStripScaleMode.Diverging,
            StripAlpha = SynthesizedStripAlpha
        },
        SeriesPrimitiveType.DynamicStyling => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.DynamicStyling,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            StyleMetric = DynamicStyleMetric.Slope,
            MinStrokeWidthPx = SynthesizedDynamicMinWidthPx,
            MaxStrokeWidthPx = SynthesizedDynamicMaxWidthPx,
            MinDynamicAlpha = SynthesizedAreaFillAlpha,
            MaxDynamicAlpha = SynthesizedOpaqueAlpha,
            NullPolicy = nullPolicy
        },
        SeriesPrimitiveType.Lollipop => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Lollipop,
            PrimarySeriesName = seriesName,
            BaselineValue = 0m,
            MarkerRadiusPx = SynthesizedMarkerRadiusPx,
            MarkerFilled = true,
            LollipopStemWidthPx = SynthesizedLollipopStemWidthPx
        },
        _ => new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Line,
            PrimarySeriesName = seriesName,
            LineStyle = CoreLineStyle.Solid,
            NullPolicy = nullPolicy
        }
    };

    private static int CompareDescriptorOrder(IReadOnlyList<SeriesPrimitiveDescriptor> descriptors, int aIdx, int bIdx)
    {
        var a = descriptors[aIdx];
        var b = descriptors[bIdx];

        int tierCmp = GetZOrderTier(a).CompareTo(GetZOrderTier(b));
        if (tierCmp != 0) return tierCmp;

        int subCmp = GetZOrderSubTier(a).CompareTo(GetZOrderSubTier(b));
        if (subCmp != 0) return subCmp;

        int offsetCmp = a.ZOrderOffset.CompareTo(b.ZOrderOffset);
        if (offsetCmp != 0) return offsetCmp;

        return aIdx.CompareTo(bIdx);
    }

    private static int GetZOrderTier(SeriesPrimitiveDescriptor desc) => desc.PrimitiveType switch
    {
        SeriesPrimitiveType.ColorStrip => 1,
        SeriesPrimitiveType.StackedArea => 2,
        SeriesPrimitiveType.BandCloud => 2,
        SeriesPrimitiveType.Area => 2,
        SeriesPrimitiveType.PositiveNegativeArea => 2,
        SeriesPrimitiveType.Histogram => 3,
        SeriesPrimitiveType.FloatingBar => 3,
        SeriesPrimitiveType.Waterfall => 3,
        SeriesPrimitiveType.StepLine => 4,
        SeriesPrimitiveType.HorizontalSegment => 4,
        SeriesPrimitiveType.Line => 4,
        SeriesPrimitiveType.DashedLine => 4,
        SeriesPrimitiveType.Spline => 4,
        SeriesPrimitiveType.DynamicStyling => 4,
        SeriesPrimitiveType.Lollipop => 5,
        SeriesPrimitiveType.Dot => 5,
        SeriesPrimitiveType.Bubble => 5,
        SeriesPrimitiveType.MarkerSymbol => 5,
        SeriesPrimitiveType.Vector => 5,
        _ => 4
    };

    private static int GetZOrderSubTier(SeriesPrimitiveDescriptor desc) =>
        desc.PrimitiveType == SeriesPrimitiveType.ColorStrip && desc.StripAnchor != ColorStripAnchor.FullPanel ? 1 : 0;

    public void ClearCaches()
    {
        _ichimokuSeriesCache.Clear();
        _localTransforms.Clear();
        _subTransforms.Clear();
        _synthesizedDescriptorCache.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _indexIndicatorRenderer.Dispose();
        _lineRenderer.Dispose();
        _histogramRenderer.Dispose();
        _stepLineRenderer.Dispose();
        _blockChartOverlayRenderer.Dispose();
        _volumeRenderer.Dispose();
        _sarRenderer.Dispose();
        _mssRenderer.Dispose();
        _granvilleRenderer.Dispose();
        _ichimokuRenderer.Dispose();
        _vpRenderer.Dispose();
        _primitiveRegistry.Dispose();
        _subHistPaint.Dispose();
        _subLinePaint.Dispose();
        _subPath.Dispose();
        _descriptorErrorLastLoggedMs?.Clear();
        ClearCaches();
    }
}
