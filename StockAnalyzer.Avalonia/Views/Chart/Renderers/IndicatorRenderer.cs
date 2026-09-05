using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders indicators (SMA, EMA, etc.) on the chart.
/// </summary>
public sealed class IndicatorRenderer : IChartRenderer, IDisposable
{
    private readonly LineIndicatorRenderer _lineRenderer = new();
    private readonly HistogramRenderer _histogramRenderer = new();
    private readonly StepLineRenderer _stepLineRenderer = new();
    private readonly VolumeIndicatorRenderer _volumeRenderer = new();
    private readonly ParabolicSarRenderer _sarRenderer = new();
    private readonly IchimokuRenderer _ichimokuRenderer = new();
    private readonly MarketStructureShiftRenderer _mssRenderer = new();
    private readonly GranvilleLawRenderer _granvilleRenderer = new();
    private readonly VolumeProfileRenderer _vpRenderer = new();
    private readonly IndexIndicatorRenderer _indexIndicatorRenderer = new(); // Generalized from RenkoIndicatorRenderer
    private readonly Dictionary<string, IReadOnlyList<decimal?>> _ichimokuSeriesCache = new();
    private readonly Dictionary<string, GenericCoordinateTransform> _localTransforms = new();
    private readonly Dictionary<string, SubWindowCoordinateTransform> _subTransforms = new();
    // Reused across RenderIndicator() calls to avoid per-frame SKPaint/SKPath allocations
    // in the sub-window histogram/line branch (SA_RENDERING_PERFORMANCE.md ZeroAllocation rule).
    private readonly SKPaint _subHistPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKPaint _subLinePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _subPath = new();

    public IndicatorRenderer()
    {
    }

    /// <summary>
    /// Non-capturing replacement for <c>seriesColors.FirstOrDefault(c => c.TargetSeries.Contains(seriesName))</c>
    /// to avoid a per-call closure allocation and LINQ enumeration inside the render loop.
    /// </summary>
    private static SeriesColorConfig? FindSeriesColor(ObservableCollection<SeriesColorConfig>? seriesColors, string seriesName)
    {
        if (seriesColors == null) return null;
        for (int i = 0; i < seriesColors.Count; i++)
        {
            if (seriesColors[i].TargetSeries.Contains(seriesName)) return seriesColors[i];
        }
        return null;
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
        if (!setting.IsEnabled) return;

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

                    foreach (var seriesName in result.SeriesNames)
                    {
                        if (PanelValueRangeCalculator.IsSignalSeries(seriesName)) continue;

                        // IFFT Instantaneous Phase: exclude Phase (Main, 0-360 deg) and the
                        // degree/bar-scaled diagnostic series (PhaseDelta/LocalPeriod/
                        // PhaseStability) from the auto-scale range so none of them drown out
                        // SineWave/LeadSine (-1..1).
                        if (setting.TypeEnum == IndicatorType.IFFTInstantaneousPhase &&
                            (seriesName == IndicatorResult.MainSeriesName || seriesName == "PhaseDelta" ||
                             seriesName == "LocalPeriod" || seriesName == "PhaseStability"))
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
                 var values = result.MainValues;
                 _volumeRenderer.Render(canvas, targetRect, values, setting, minVal, maxVal, 
                    snapshot.VisibleCandleCount > 0 ? snapshot.VisibleCandleCount : snapshot.Candles!.Count, 
                    setting.Offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform);
                 return;
            }

            // CHECK for Volume Profile using TypeEnum and CustomData
            if (setting.TypeEnum == IndicatorType.VolumeProfile)
            {
                 // Try to get CustomData from the stored result if available
                 if (result.CustomData is List<Core.Analysis.VolumeBin> profile)
                 {
                     // Get Side from parameters if possible
                     bool isRight = true;
                     if (setting.ParameterObject is CoreVolumeProfileParameter vpParam)
                     {
                         isRight = vpParam.Side == DisplaySide.Right || vpParam.Side == DisplaySide.Both; // "Both" logic might need dual render or different handling.
                         // Renderer supports "Right" or "Left". 
                         // If "Both", maybe we render right? Or maybe we need to update Renderer to support Both.
                         // For now, let's support Left/Right.
                         if (vpParam.Side == DisplaySide.Left) isRight = false;
                     }
                     
                     if (transform == null) return;
                     _vpRenderer.Render(canvas, targetRect, profile, minVal, maxVal, transform, isRight, config, setting);
                     
                     // If Both, render Left as well?
                     if (setting.ParameterObject is CoreVolumeProfileParameter vpParam2 && vpParam2.Side == DisplaySide.Both)
                     {
                          _vpRenderer.Render(canvas, targetRect, profile, minVal, maxVal, transform, false, config, setting);
                     }

                     // Note: We continue to allow standard rendering below for the POC line (if it is returned as a series)
                     
                     // UPDATE: User requested ONLY histogram. Return here to skip standard line rendering.
                     return;
                 }
            }

            // CHECK for Parabolic SAR
            if (setting.TypeEnum == IndicatorType.ParabolicSAR)
            {
                 var values = result.MainValues; // SAR values
                 var trend = result.GetSeries("IsUpTrend"); // Trend values (1 or 0)
                 
                  _sarRenderer.Render(canvas, targetRect, values, trend, setting, minVal, maxVal, 
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

            // Generalized rendering for Index-based charts (Renko, P&F, Kagi, etc.)
            // CRITICAL: Only route OVERLAY indicators to IndexIndicatorRenderer.
            // Sub-window indicators (RSI, MACD, etc.) must use the standard rendering path
            // with SubWindowCoordinateTransform for proper Y-axis scaling.
            if (snapshot.ChartType.IsIndexBased() && !isSubWindowContext)
            {
                // Iterate through all series for multi-series indicators (e.g. Bollinger Bands)
                var seriesNames = result.SeriesNamesList;
                for (int i = 0; i < seriesNames.Count; i++)
                {
                    var seriesValues = result.GetSeries(seriesNames[i]);
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

                foreach (var seriesName in result.SeriesNames)
                {
                    if (PanelValueRangeCalculator.IsSignalSeries(seriesName))
                        continue;

                    // IFFT Instantaneous Phase: don't draw Phase (Main) or the diagnostic series
                    // (PhaseDelta/LocalPeriod/PhaseStability) here either, for consistency with
                    // the standard sub-panel path.
                    if (setting.TypeEnum == IndicatorType.IFFTInstantaneousPhase &&
                        (seriesName == IndicatorResult.MainSeriesName || seriesName == "PhaseDelta" ||
                         seriesName == "LocalPeriod" || seriesName == "PhaseStability"))
                        continue;

                    var values = result.GetSeries(seriesName);
                    if (values == null || values.Count == 0) continue;

                    int count = Math.Min(values.Count, snapshot.Candles!.Count);

                    // Determine series color
                    SKColor seriesColor;
                    var colorConfig = FindSeriesColor(setting.SeriesColors, seriesName);
                    seriesColor = colorConfig != null
                        ? colorConfig.Color.ToSkColor()
                        : setting.Color.ToSkColor();

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
            bool isIchimoku = result.HasSeries("SenkouSpanA") && result.HasSeries("SenkouSpanB");
            if (isIchimoku)
            {
                // Force Offset 0 for Ichimoku (Data is already shifted by Logic)
                offset = 0;
                
                _ichimokuSeriesCache.Clear();
                foreach (var name in result.SeriesNames)
                    _ichimokuSeriesCache[name] = result.GetSeries(name);
                
                _ichimokuRenderer.Render(canvas, targetRect, _ichimokuSeriesCache, setting, minVal, maxVal, visibleCandles, offset, config.VisibleStartIndex, snapshot.Candles!, interval, transform, config);
                return;
            }

            foreach (var seriesName in result.SeriesNames)
            {
                // Filter Signal Series (not drawn as continuous lines by LineIndicatorRenderer)
                if (PanelValueRangeCalculator.IsSignalSeries(seriesName))
                    continue;

                // Fix: Skip rendering Granville Law signals on the main chart to avoid pollution
                if (!isSubWindowContext && setting.TypeEnum == IndicatorType.GranvilleLaw && seriesName == "Signals")
                    continue;

                // IFFT Instantaneous Phase: never draw Phase (Main, 0-360 deg) or the
                // degree/bar-scaled diagnostic series (PhaseDelta/LocalPeriod/PhaseStability) —
                // their scales would dominate the panel's Y axis and hide SineWave/LeadSine
                // (-1..1). The values are still computed and served (e.g. for the screener).
                if (setting.TypeEnum == IndicatorType.IFFTInstantaneousPhase &&
                    (seriesName == IndicatorResult.MainSeriesName || seriesName == "PhaseDelta" ||
                     seriesName == "LocalPeriod" || seriesName == "PhaseStability"))
                    continue;

                var values = result.GetSeries(seriesName);
                if (values == null || values.Count == 0) continue;


                // Determine Series Color
                SKColor seriesColor;
                var colorConfig = FindSeriesColor(setting.SeriesColors, seriesName);
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
            // Fallback / Legacy (Single Series)
            if (values == null || values.Count == 0) return;


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
                     foreach(var v in values)
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

    public void ClearCaches()
    {
        _ichimokuSeriesCache.Clear();
        _localTransforms.Clear();
        _subTransforms.Clear();
    }

    public void Dispose()
    {
        _indexIndicatorRenderer.Dispose();
        _subHistPaint.Dispose();
        _subLinePaint.Dispose();
        _subPath.Dispose();
        ClearCaches();
    }
}
