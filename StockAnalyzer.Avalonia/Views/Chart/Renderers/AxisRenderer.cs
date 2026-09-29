using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders axes labels (Price on Y-Right, Date on X-Bottom).
/// </summary>
public sealed class AxisRenderer : IDisposable
{
    private readonly SKPaint _textPaint;
    private readonly SKPaint _axisLinePaint;
    private readonly SKPaint _highlightTextPaint;

    private readonly System.Collections.Generic.Dictionary<(decimal price, int decimals, bool isPercent), string> _priceLabelCache = new();
    private readonly System.Collections.Generic.Dictionary<decimal, string> _volumeLabelCache = new();
    private readonly System.Collections.Generic.Dictionary<(long, int), string> _dateLabelCache = new();
    private readonly List<DateTime> _labelDates = new();

    private string GetDateLabel(DateTime date, int formatType)
    {
        var key = (date.Ticks, formatType);
        if (_dateLabelCache.TryGetValue(key, out var label)) return label;
        if (_dateLabelCache.Count > 1000) _dateLabelCache.Clear();

        label = formatType switch
        {
            0 => date.ToString("yyyy"),
            1 => date.ToString("MM"),
            2 => date.ToString("dd"),
            4 => date.ToString("yy/MM"),
            5 => date.ToString("MM/dd"),
            6 => date.ToString("MMM", CultureInfo.InvariantCulture),
            7 => date.ToString("MMM dd", CultureInfo.InvariantCulture),
            8 => date.ToString("yyyy MMM", CultureInfo.InvariantCulture),
            _ => date.ToString("HH:mm")
        };
        _dateLabelCache[key] = label;
        return label;
    }

    private string GetPriceLabel(decimal price, int decimalPlaces = 2, bool isPercentage = false, bool forceSign = false, string? prefix = null)
    {
        var key = (price, decimalPlaces, isPercentage);
        if (!forceSign && prefix == null && _priceLabelCache.TryGetValue(key, out var label)) return label;
        
        if (isPercentage)
        {
            // Percentage mode often benefits from fixed precision, but we use decimalPlaces for flexibility
            string format = decimalPlaces switch {
                0 => "+0;-0;0",
                1 => "+0.0;-0.0;0.0",
                _ => "+0.00;-0.00;0.00"
            };
            label = price.ToString(format, CultureInfo.InvariantCulture) + "%";
            if (!forceSign && prefix == null) _priceLabelCache[key] = label;
            return label;
        }

        if (forceSign)
        {
            string format = "+0." + new string('0', decimalPlaces) + ";-0." + new string('0', decimalPlaces) + ";0." + new string('0', decimalPlaces);
            label = (prefix ?? "") + price.ToString(format, CultureInfo.InvariantCulture);
            return label;
        }

        if (_priceLabelCache.Count > 1000) _priceLabelCache.Clear();
        
        // Dynamic format based on calculated decimal places (F0, F1, F2...)
        label = (prefix ?? "") + price.ToString("F" + decimalPlaces, CultureInfo.InvariantCulture);
        if (prefix == null) _priceLabelCache[key] = label;
        return label;
    }

    private string GetVolumeLabel(decimal v)
    {
        if (_volumeLabelCache.TryGetValue(v, out var label)) return label;
        if (_volumeLabelCache.Count > 1000) _volumeLabelCache.Clear();
        
        if (v >= 1_000_000_000m)
            label = (v / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        else if (v >= 1_000_000m)
            label = (v / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        else if (v >= 1_000m)
            label = (v / 1_000m).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        else
            label = v.ToString("0.##", CultureInfo.InvariantCulture);
            
        _volumeLabelCache[v] = label;
        return label;
    }

    public AxisRenderer()
    {
        _textPaint = new SKPaint
        {
            TextSize = 12,
            IsAntialias = true,
            TextAlign = SKTextAlign.Left
        };
        
        _highlightTextPaint = new SKPaint
        {
            TextSize = 12,
            IsAntialias = true,
            TextAlign = SKTextAlign.Left,
            Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold)
        };

        _axisLinePaint = new SKPaint
        {
            StrokeWidth = 1,
            IsAntialias = false
        };
    }

    private void UpdateTheme(IChartRenderConfig config)
    {
        var theme = config.ThemeManager.CurrentTheme;
        _textPaint.Color = theme.AxisText.ToSkColor();
        _highlightTextPaint.Color = theme.AxisText.ToSkColor();
        _axisLinePaint.Color = theme.GridLine.ToSkColor();
    }

    public void Render(SKCanvas canvas, ChartLayoutContext layout, ChartDataSnapshot snapshot, IChartRenderConfig config, IReadOnlyList<AxisLabelRenderer.ResolvedLabel>? occlusions = null)
    {
        if (snapshot.Candles.Count == 0) return;

        UpdateTheme(config);

        DrawPriceAxis(canvas, layout, snapshot, config, occlusions);
        DrawBorderLines(canvas, layout);
        DrawDateAxis(canvas, layout, snapshot, config);
    }

    /// <summary>
    /// Draws border lines (right edge + bottom edge) for chart area, volume area, and panels.
    /// Date text labels are intentionally omitted.
    /// </summary>
    private void DrawBorderLines(SKCanvas canvas, ChartLayoutContext layout)
    {
        var chartArea = layout.ChartArea;
        
        // Chart area bottom border (separator between chart and volume/panels)
        float bottomY = MathF.Floor((float)chartArea.Bottom) + 0.5f;
        canvas.DrawLine((float)chartArea.Left, bottomY, (float)chartArea.Right, bottomY, _axisLinePaint);

        // Volume area borders (right + bottom)
        if (layout.VolumeArea.Height > 0)
        {
            var vol = layout.VolumeArea;
            float volRight = MathF.Floor((float)vol.Right) + 0.5f;
            float volBottom = MathF.Floor((float)vol.Bottom) + 0.5f;
            // Right border
            canvas.DrawLine(volRight, (float)vol.Top, volRight, volBottom, _axisLinePaint);
            // Bottom border
            canvas.DrawLine((float)vol.Left, volBottom, volRight, volBottom, _axisLinePaint);
        }

        // Panel borders
        foreach (var panel in layout.PanelAreas)
        {
            float pRight = MathF.Floor((float)panel.Right) + 0.5f;
            float pBottom = MathF.Floor((float)panel.Bottom) + 0.5f;
            canvas.DrawLine(pRight, (float)panel.Top, pRight, pBottom, _axisLinePaint);
            canvas.DrawLine((float)panel.Left, pBottom, pRight, pBottom, _axisLinePaint);
        }
    }

    private void DrawPriceAxis(SKCanvas canvas, ChartLayoutContext layout, ChartDataSnapshot snapshot, IChartRenderConfig config, IReadOnlyList<AxisLabelRenderer.ResolvedLabel>? occlusions)
    {
        var chartArea = layout.ChartArea;
        // Draw vertical axis line on the right
        float rightX = MathF.Floor((float)chartArea.Right) + 0.5f;
        canvas.DrawLine(rightX, (float)chartArea.Top, rightX, (float)chartArea.Bottom, _axisLinePaint);

        // Uses same logic as GridRenderer for alignment
        decimal range = snapshot.PriceRange;
        decimal minPrice = snapshot.MinPrice;
        decimal maxPrice = snapshot.MaxPrice;

        if (config.ChartType == ChartType.ReverseWatch && config is IReverseWatchRenderConfig rwConfig && rwConfig.ReverseWatchData != null)
        {
            var b = rwConfig.ReverseWatchData.Bounds;
            minPrice = b.MinPrice;
            maxPrice = b.MaxPrice;
            range = maxPrice - minPrice;
        }

        if (range <= 0) return;

        decimal step;
        decimal firstLine;

        if (config.ChartType == ChartType.PointAndFigure && snapshot.MaxBrickSize > 0)
        {
            // For P&F, enforce strict BoxSize steps for Y-axis labels 
            step = snapshot.MaxBrickSize;
            
            // For extreme zoom-out, skip labels to avoid overlapping text
            decimal roughStep = range / 8m;
            if (roughStep > step * 2)
            {
                decimal multiplier = Math.Max(1m, Math.Round(roughStep / step));
                step = step * multiplier;
            }

            // Snap the first label exactly to a BoxSize multiple
            firstLine = Math.Ceiling(minPrice / snapshot.MaxBrickSize) * snapshot.MaxBrickSize;
        }
        else
        {
            // Must use the exact same step logic as GridRenderer to ensure perfect alignment
            decimal roughStep = range / ChartTheme.HorizontalGridLineTarget;
            step = CalculateNiceStep(roughStep);
            firstLine = Math.Ceiling(minPrice / step) * step;
        }

        var t = config.Transform;
        if (t == null) return;

        // CRITICAL PROTECTION: Rapid period changes can lead to extremely small 'step' values.
        // Add a safety iteration cap and finiteness check to prevent Render Thread hangs/crashes.
        int safetyCounter = 0;
        for (decimal price = firstLine; price <= maxPrice && safetyCounter++ < StockAnalyzer.Avalonia.Common.ChartConstants.MaxRenderIterationLimit; price += step)
        {
            float y = MathF.Floor((float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, price)).Y + (float)chartArea.Top) + 0.5f;
            
            // Bounds check to prevent drawing labels in sub-window areas
            if (!float.IsFinite(y)) continue;

            bool isPercentage = false;
            if (config.ChartType == ChartType.RelativePerformance && config is IComparisonRenderConfig comparisonConfig)
            {
                isPercentage = comparisonConfig.ComparisonMode == ComparisonMode.Performance;
            }
            else if (t.PriceScale == PriceScaleType.Percent)
            {
                isPercentage = true;
            }

            int decimalPlaces = isPercentage ? 2 : GetDecimalPlaces(step);
            bool forceSign = false;
            string? prefix = null;

            if (config.ChartType == ChartType.RelativePerformance && config is IComparisonRenderConfig relConfig)
            {
                if (relConfig.ComparisonMode == ComparisonMode.Ratio)
                {
                    decimalPlaces = Math.Max(decimalPlaces, 4);
                }
                else if (relConfig.ComparisonMode == ComparisonMode.ZScore)
                {
                    decimalPlaces = 2;
                    forceSign = true;
                    prefix = StockAnalyzer.Avalonia.Common.ChartConstants.ZScorePrefix;
                }
                else if (relConfig.ComparisonMode == ComparisonMode.Spread)
                {
                    decimalPlaces = 2;
                    forceSign = true;
                }
            }

            decimal displayPrice = price;
            if (t.PriceScale == PriceScaleType.Percent && config.ChartType != ChartType.RelativePerformance && snapshot.Candles.Count > 0)
            {
                decimal referencePrice = snapshot.Candles[0].Close;
                if (referencePrice != 0)
                {
                    displayPrice = (price - referencePrice) / referencePrice * 100m;
                }
            }

            string label = GetPriceLabel(displayPrice, decimalPlaces, isPercentage, forceSign, prefix);
            // Adjust y to center text vertically
            float textY = y + _textPaint.TextSize / 2f - 2f;
            
            // Skip drawing if this position is occluded by a floating label
            if (IsOccluded(y, occlusions)) continue;

            // Step 4.2: Boundary Protection
            // If the label is too close to the Top or Bottom edge, skip it to avoid clipping with panel boundaries
            const float edgeMargin = 8f;
            if (y < (float)chartArea.Top + edgeMargin || y > (float)chartArea.Bottom - edgeMargin) continue;

            canvas.DrawText(label, rightX + 5, textY, _textPaint);

            // Standard axis ticks are now excluded from debug log to focus on synchronization (PR-39-16).
        }
    }

    private bool IsOccluded(float y, IReadOnlyList<AxisLabelRenderer.ResolvedLabel>? occlusions)
    {
        if (occlusions == null || occlusions.Count == 0) return false;

        // Simplified check: If the tick Y is within any floating label's box, it's occluded
        for (int i = 0; i < occlusions.Count; i++)
        {
            var occ = occlusions[i];
            // Occ.Y is top, height is fixed. 
            // Increase buffer to prevent "uncomfortable proximity" (WebAI recommendation)
            const float buffer = ChartTheme.AxisLabelOcclusionBuffer;
            if (y >= occ.Y - buffer && y <= occ.Y + ChartTheme.AxisLabelHeight + buffer)
            {
                return true;
            }
        }
        return false;
    }

    private int GetDecimalPlaces(decimal step)
    {
        if (step <= 0) return 2;
        // The scale of a decimal (number of digits after the decimal point)
        int scale = (decimal.GetBits(step)[3] >> 16) & 0x7F;
        return Math.Clamp(scale, 0, 6); 
    }

    private int FindClosestIndex(IReadOnlyList<CoreCandleData> candles, DateTime targetTime)
    {
        if (candles == null || candles.Count == 0) return 0;
        
        int low = 0;
        int high = candles.Count - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int comparison = candles[mid].Timestamp.CompareTo(targetTime);
            if (comparison == 0) return mid;
            if (comparison < 0) low = mid + 1;
            else high = mid - 1;
        }
        
        // Target not perfectly found; return the lower boundary (the most recent past candle)
        return Math.Max(0, Math.Min(candles.Count - 1, high));
    }

    private void DrawDateAxis(SKCanvas canvas, ChartLayoutContext layout, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        var chartArea = layout.ChartArea;
        
        // Calculate the absolute bottom Y coordinate for the Date labels
        float axisBottomY = (float)chartArea.Bottom;
        if (layout.VolumeArea.Height > 0)
        {
            axisBottomY = Math.Max(axisBottomY, (float)layout.VolumeArea.Bottom);
        }
        for (int i = 0; i < layout.PanelAreas.Count; i++)
        {
            axisBottomY = Math.Max(axisBottomY, (float)layout.PanelAreas[i].Bottom);
        }
        
        axisBottomY = MathF.Floor(axisBottomY) + 0.5f;

        // Draw horizontal axis line at the exact absolute bottom
        canvas.DrawLine((float)chartArea.Left, axisBottomY, (float)chartArea.Right, axisBottomY, _axisLinePaint);

        var transform = config.Transform;
        if (transform == null) return;

        if (config.ChartType == ChartType.ReverseWatch)
        {
            var startPt = transform.ScreenToNumeric(new global::Avalonia.Point(0, 0));
            var endPt = transform.ScreenToNumeric(new global::Avalonia.Point(chartArea.Width, 0));
            
            decimal minVol = (decimal)Math.Min(startPt.x, endPt.x);
            decimal maxVol = (decimal)Math.Max(startPt.x, endPt.x);
            
            if (maxVol > minVol)
            {
                float rwPixelsPerGrid = 120f;
                int rwStepsCount = Math.Max(2, (int)(chartArea.Width / rwPixelsPerGrid));
                decimal roughStep = (maxVol - minVol) / rwStepsCount;
                decimal niceStep = CalculateHybridStep(maxVol - minVol, (float)chartArea.Width);
                
                if (niceStep > 0)
                {
                    decimal startVol = Math.Floor(minVol / niceStep) * niceStep;
                    for (decimal v = startVol; v <= maxVol; v += niceStep)
                    {
                        if (v < minVol) continue;
                        float x = MathF.Floor((float)transform.NumericToScreen((double)v, 0).X + (float)chartArea.Left) + 0.5f;
                        
                        // Global standard volume formatting (K, M, B) cached
                        string label = GetVolumeLabel(v);
                                       
                        float textWidth = _textPaint.MeasureText(label);
                        canvas.DrawText(label, x - textWidth / 2f, axisBottomY + 15, _textPaint);
                    }
                }
            }
            return;
        }

        int count = snapshot.Candles.Count;
        if (count == 0) return;

        bool isIndexMode = config.ChartType.IsIndexBased();
        
        // Find visible time boundaries using ScreenToChart
        DateTime startTime;
        DateTime endTime;
        
        if (isIndexMode)
        {
            // For pure index mode, ScreenToChart returns ticks as the raw index.
            double rawStartIndex = transform.ScreenToChart(new global::Avalonia.Point(chartArea.Left, 0)).Time.Ticks;
            double rawEndIndex = transform.ScreenToChart(new global::Avalonia.Point(chartArea.Right, 0)).Time.Ticks;
            
            int sIdx = Math.Clamp((int)Math.Floor(rawStartIndex), 0, snapshot.Candles.Count - 1);
            int eIdx = Math.Clamp((int)Math.Ceiling(rawEndIndex), 0, snapshot.Candles.Count - 1);
            
            startTime = snapshot.Candles[sIdx].Timestamp;
            endTime = snapshot.Candles[eIdx].Timestamp;
        }
        else
        {
            startTime = transform.ScreenToChart(new global::Avalonia.Point(chartArea.Left, 0)).Time;
            endTime = transform.ScreenToChart(new global::Avalonia.Point(chartArea.Right, 0)).Time;
        }

        if (startTime >= endTime) return;

        TimeSpan visibleDuration = endTime - startTime;
        
        // Define standard time intervals for adaptive selection
        var intervals = new (TimeSpan span, bool isMajor)[] 
        {
            (TimeSpan.FromDays(365), true),     // 1 Year
            (TimeSpan.FromDays(90), false),    // 1 Quarter
            (TimeSpan.FromDays(30), true),     // 1 Month
            (TimeSpan.FromDays(7), false),     // 1 Week
            (TimeSpan.FromDays(1), true),      // 1 Day
            (TimeSpan.FromHours(4), false),    // 4 Hours
            (TimeSpan.FromHours(1), true),     // 1 Hour
            (TimeSpan.FromMinutes(15), false), // 15 Mins
            (TimeSpan.FromMinutes(5), false),  // 5 Mins
            (TimeSpan.FromMinutes(1), true)    // 1 Min
        };

        TimeSpan selectedStep = intervals[0].span;
        float targetPixelWidth = 100f; // Target spacing between labels
        int maxLabels = (int)(chartArea.Width / targetPixelWidth);

        // Find the smallest interval that results in <= maxLabels
        foreach (var interval in intervals)
        {
            double labelCount = (double)visibleDuration.Ticks / (double)interval.span.Ticks;
            if (labelCount <= maxLabels)
            {
                selectedStep = interval.span;
            }
            else
            {
                break;
            }
        }

        _labelDates.Clear();
        DateTime curr = startTime;

        // Align 'curr' to the selected step boundary
        if (selectedStep.TotalDays >= 365)
            curr = new DateTime(startTime.Year, 1, 1);
        else if (selectedStep.TotalDays >= 30)
            curr = new DateTime(startTime.Year, startTime.Month, 1);
        else if (selectedStep.TotalDays >= 7)
        {
            curr = startTime.Date;
            while (curr.DayOfWeek != DayOfWeek.Monday) curr = curr.AddDays(-1);
        }
        else if (selectedStep.TotalDays >= 1)
            curr = startTime.Date;
        else
        {
            long ticks = startTime.Ticks;
            curr = new DateTime(ticks - (ticks % selectedStep.Ticks));
        }

        while (curr <= endTime)
        {
            if (curr >= startTime) _labelDates.Add(curr);
            
            if (selectedStep.TotalDays >= 365) curr = curr.AddYears(1);
            else if (selectedStep.TotalDays >= 90) curr = curr.AddMonths(3);
            else if (selectedStep.TotalDays >= 30) curr = curr.AddMonths(1);
            else curr = curr.Add(selectedStep);
        }

        // State tracking for context transitions
        int lastYear = -1;
        int lastMonth = -1;
        int lastDay = -1;
        float lastDrawnRightEdge = -1000f;

        bool stepIsYear = selectedStep.TotalDays >= 365;
        bool stepIsMonth = selectedStep.TotalDays >= 30 && !stepIsYear;
        bool stepIsWeek = selectedStep.TotalDays >= 7 && !stepIsMonth && !stepIsYear;
        bool stepIsDay = selectedStep.TotalDays >= 1 && !stepIsWeek && !stepIsMonth && !stepIsYear;
        bool stepIsHour = selectedStep.TotalHours >= 1 && !stepIsDay;
        
        foreach (var date in _labelDates)
        {
            float x;
            if (isIndexMode)
            {
                int closestIdx = FindClosestIndex(snapshot.Candles, date);
                double x0 = transform.ChartToScreen(new ChartPoint(new DateTime(closestIdx), 0)).X;
                double x1 = transform.ChartToScreen(new ChartPoint(new DateTime(closestIdx + 1), 0)).X;
                x = (float)x0 + (float)chartArea.Left + (float)(x1 - x0) / 2f;
            }
            else
            {
                x = MathF.Floor((float)transform.ChartToScreen(new ChartPoint(date, 0)).X + (float)chartArea.Left) + 0.5f;
            }

            if (x < (float)chartArea.Left || x > (float)chartArea.Right)
            {
                // Internal state must still be updated even if not drawn to handle transitions correctly
                lastYear = date.Year;
                lastMonth = date.Month;
                lastDay = date.Day;
                continue;
            }

            string label = string.Empty;
            SKPaint paint = _textPaint;

            bool isYearChange = lastYear != -1 && date.Year != lastYear;
            bool isMonthChange = lastMonth != -1 && date.Month != lastMonth;
            bool isDayChange = lastDay != -1 && date.Day != lastDay;

            // Context-aware formatting hierarchy
            if (isYearChange || (lastYear == -1 && !stepIsHour))
            {
                label = GetDateLabel(date, 0); // "yyyy"
                paint = _highlightTextPaint;
            }
            else if (isMonthChange || (lastMonth == -1 && stepIsMonth))
            {
                if (stepIsDay || stepIsWeek || stepIsHour)
                {
                    label = GetDateLabel(date, isYearChange ? 0 : 6); // "yyyy" or "MMM"
                    paint = _highlightTextPaint;
                }
                else
                {
                    label = GetDateLabel(date, 1); // "MM"
                }
            }
            else if (isDayChange || (lastDay == -1 && stepIsDay))
            {
                if (stepIsHour)
                {
                    label = GetDateLabel(date, 7); // "MMM dd"
                    paint = _highlightTextPaint;
                }
                else
                {
                    label = GetDateLabel(date, 2); // "dd"
                }
            }
            else
            {
                // Normal repetitive labels
                if (stepIsYear) label = GetDateLabel(date, 0);
                else if (stepIsMonth) label = GetDateLabel(date, 1);
                else if (stepIsDay || stepIsWeek) label = GetDateLabel(date, 2);
                else label = GetDateLabel(date, 3); // "HH:mm"
            }
            
            lastYear = date.Year;
            lastMonth = date.Month;
            lastDay = date.Day;

            float textWidth = paint.MeasureText(label);
            float drawX = x - textWidth / 2f;
            
            // Prevent text overlapping horizontally
            if (drawX < lastDrawnRightEdge + 15f) continue;
            
            // Ensure label doesn't bleed out of horizontal bounds
            if (drawX < (float)chartArea.Left) drawX = (float)chartArea.Left;
            if (drawX + textWidth > (float)chartArea.Right) drawX = (float)chartArea.Right - textWidth;

            canvas.DrawText(label, drawX, axisBottomY + 15, paint);
            lastDrawnRightEdge = drawX + textWidth;
        }
    }

    private decimal CalculateHybridStep(decimal range, float sizePx)
    {
        if (range <= 0 || sizePx <= 0) return 1m;

        // Goal: Target 80px spacing (comfortable for mobile and desktop)
        // Constraints: Stay between ~45px and ~160px for visual stability
        double targetSpacing = 80.0;
        double pxPerUnit = (double)sizePx / (double)range;
        decimal targetStep = (decimal)(targetSpacing / pxPerUnit);

        // Normalize step to find "Nice" multiplier candidates: 1, 2, 2.5, 5, 10
        double mag = Math.Floor(Math.Log10((double)targetStep));
        decimal powerOf10 = (decimal)Math.Pow(10, mag);
        decimal normalized = targetStep / powerOf10;

        decimal niceStep;
        if (normalized < 1.5m) niceStep = 1m;
        else if (normalized < 2.25m) niceStep = 2m;
        else if (normalized < 3.75m) niceStep = 2.5m; 
        else if (normalized < 7.5m) niceStep = 5m;
        else niceStep = 10m;

        decimal finalStep = niceStep * powerOf10;

        // Final density check: avoid clutter during sudden volatility
        double actualSpacing = (double)finalStep * pxPerUnit;
        if (actualSpacing < 45.0) 
        {
            if (niceStep == 1m) finalStep = 2m * powerOf10;
            else if (niceStep == 2m) finalStep = 2.5m * powerOf10;
            else if (niceStep == 2.5m) finalStep = 5m * powerOf10;
            else if (niceStep == 5m) finalStep = 10m * powerOf10;
        }

        return finalStep;
    }

    /// <summary>
    /// Identical to GridRenderer.CalculateNiceStep — guarantees grid/label alignment.
    /// </summary>
    private decimal CalculateNiceStep(decimal roughStep)
    {
        if (roughStep == 0) return 1;

        double mag = Math.Floor(Math.Log10((double)roughStep));
        decimal powerOf10 = (decimal)Math.Pow(10, mag);

        decimal normalized = roughStep / powerOf10;

        decimal niceStep;
        if (normalized < 1.5m) niceStep = 1m;
        else if (normalized < 3m) niceStep = 2m;
        else if (normalized < 7m) niceStep = 5m;
        else niceStep = 10m;

        return niceStep * powerOf10;
    }

    public void Dispose()
    {
        _textPaint.Dispose();
        _highlightTextPaint.Dispose();
        _axisLinePaint.Dispose();
    }
}
