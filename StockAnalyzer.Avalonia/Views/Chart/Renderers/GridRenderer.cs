using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders grid lines (horizontal for price, vertical for time) on the chart.
/// </summary>
public sealed class GridRenderer : IDisposable
{
    private readonly SKPaint _gridPaint;
    private readonly SKPaint _zScoreZeroPaint;
    private readonly SKPaint _zScoreThresholdPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPathEffect _dashEffect;

    public GridRenderer()
    {
        // Dash pattern: 2px line, 2px space
        _dashEffect = SKPathEffect.CreateDash(new float[] { ChartTheme.GridDashOn, ChartTheme.GridDashOff }, 0);
        
        _gridPaint = new SKPaint
        {
            StrokeWidth = 1,
            IsAntialias = false, // Crisp lines
            Style = SKPaintStyle.Stroke,
            PathEffect = _dashEffect
        };

        _zScoreZeroPaint = new SKPaint
        {
            StrokeWidth = ChartTheme.ZScoreZeroWidth,
            IsAntialias = true, // Thicker lines benefit from AA
            Style = SKPaintStyle.Stroke
            // Solid line (no PathEffect)
        };

        var thresholdDash = SKPathEffect.CreateDash(new float[] { ChartTheme.ZScoreThresholdDashOn, ChartTheme.ZScoreThresholdDashOff }, 0);
        _zScoreThresholdPaint = new SKPaint
        {
            StrokeWidth = 1,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            PathEffect = thresholdDash
        };

        _textPaint = new SKPaint
        {
            TextSize = ChartTheme.GridLabelTextSize,
            IsAntialias = true,
            TextAlign = SKTextAlign.Left
        };
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        if (snapshot.Candles.Count == 0) return;

        _gridPaint.Color = config.ThemeManager.CurrentTheme.GridLine.ToSkColor();
        
        var gridColor = config.ThemeManager.CurrentTheme.GridLine.ToSkColor();
        _zScoreZeroPaint.Color = gridColor.WithAlpha(ChartTheme.ZScoreZeroAlpha);
        _zScoreThresholdPaint.Color = gridColor.WithAlpha(ChartTheme.ZScoreThresholdAlpha);

        DrawHorizontalNetwork(canvas, chartArea, snapshot, config);
        DrawVerticalNetwork(canvas, chartArea, snapshot, config);
    }

    private void DrawHorizontalNetwork(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        // Calculate nice step for price
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
            // For P&F, rigidly lock the grid to exactly the predefined BoxSize.
            // Do NOT use CalculateNiceStep or ChartTheme.HorizontalGridLineTarget.
            step = snapshot.MaxBrickSize;
            
            // For extreme zoom-out, we optionally skip lines to prevent the screen turning solid grid
            decimal roughStep = range / ChartTheme.HorizontalGridLineTarget;
            if (roughStep > step * 2) 
            {
               decimal multiplier = Math.Max(1m, Math.Round(roughStep / step));
               step = step * multiplier;
            }

            // Snap the first line directly to a BoxSize multiple
            firstLine = Math.Ceiling(minPrice / snapshot.MaxBrickSize) * snapshot.MaxBrickSize;
        }
        else
        {
            decimal roughStep = range / ChartTheme.HorizontalGridLineTarget;
            step = CalculateNiceStep(roughStep);
            firstLine = Math.Ceiling(minPrice / step) * step;
        }

        _textPaint.Color = config.ThemeManager.CurrentTheme.AxisText.ToSkColor();

        if (config.Transform == null) return;
        var t = config.Transform;

        for (decimal price = firstLine; price <= maxPrice; price += step)
        {
            // Skip drawing standard grid for Z-Score priority lines to avoid double rendering
            if (config is IComparisonRenderConfig { ComparisonMode: ComparisonMode.ZScore })
            {
                if (price == 0m || Math.Abs(price) == 1m || Math.Abs(price) == 2m)
                    continue;
            }

            float y = MathF.Floor((float)t.ChartToScreen(new StockAnalyzer.Avalonia.Drawing.ChartPoint(DateTime.MinValue, price)).Y + (float)chartArea.Top) + 0.5f;
            
            // Draw horizontal line
            canvas.DrawLine((float)chartArea.Left, y, (float)chartArea.Right, y, _gridPaint);

            // Draw Value
            // string label = FormatValue(price, step);
            // canvas.DrawText(label, (float)chartArea.Right + 5, y + 4, _textPaint);
        }

        // --- Z-Score Specialized Grid Priority Layer ---
        if (config is IComparisonRenderConfig { ComparisonMode: ComparisonMode.ZScore })
        {
            var priorities = new decimal[] { -2m, -1m, 1m, 2m, 0m };
            foreach (var val in priorities)
            {
                if (val < minPrice || val > maxPrice) continue;

                float y = MathF.Floor((float)t.ChartToScreen(new StockAnalyzer.Avalonia.Drawing.ChartPoint(DateTime.MinValue, val)).Y + (float)chartArea.Top) + 0.5f;
                var paint = val == 0m ? _zScoreZeroPaint : _zScoreThresholdPaint;
                
                canvas.DrawLine((float)chartArea.Left, y, (float)chartArea.Right, y, paint);
            }
        }
    }

    private void DrawVerticalNetwork(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        int count = snapshot.Candles.Count;
        if (count == 0) return;

        if (config.Transform == null) return;
        var t = config.Transform;

        float pixelsPerGrid = ChartTheme.VerticalGridPixelInterval;

        // Specialized precise vertical grid for P&F (Index-based, 1:1 Aspect Ratio)
        if (config.ChartType == ChartType.PointAndFigure)
        {
            // Find logic coordinate boundaries for the current screen width
            // This ensures vertical lines move accurately with panning/zooming.
            // ScreenToChart expects local coordinates relative to the chart area
            var startPt = t.ScreenToChart(new global::Avalonia.Point(0, 0));
            var endPt = t.ScreenToChart(new global::Avalonia.Point(chartArea.Width, 0));

            // Ticks = Index
            int startIdx = (int)Math.Max(0, (int)Math.Floor((double)startPt.Time.Ticks));
            int endIdx = (int)Math.Min(count - 1, (int)Math.Ceiling((double)endPt.Time.Ticks));
            
            if (startIdx >= count) return;

            // Figure out a reasonable interval based on screen density
            int visibleColumns = endIdx - startIdx + 1;
            int stepsCount = Math.Max(2, (int)(chartArea.Width / pixelsPerGrid));
            int interval = Math.Max(1, visibleColumns / stepsCount);

            for (int i = startIdx; i <= endIdx; i += interval)
            {
                // Retrieve precise X coordinate from Transform (matches Symbol rendering X exactly)
                float x = MathF.Floor((float)t.ChartToScreen(new ChartPoint(new DateTime(i), 0)).X + (float)chartArea.Left) + 0.5f;

                canvas.DrawLine(x, (float)chartArea.Top, x, (float)chartArea.Bottom, _gridPaint);
            }
        }
        else if (config.ChartType == ChartType.ReverseWatch)
        {
            var startPt = t.ScreenToNumeric(new global::Avalonia.Point(0, 0));
            var endPt = t.ScreenToNumeric(new global::Avalonia.Point(chartArea.Width, 0));
            
            decimal minVol = (decimal)Math.Min(startPt.x, endPt.x);
            decimal maxVol = (decimal)Math.Max(startPt.x, endPt.x);
            
            if (maxVol > minVol)
            {
                decimal volRange = maxVol - minVol;
                int stepsCount = Math.Max(2, (int)(chartArea.Width / pixelsPerGrid));
                decimal roughStep = volRange / stepsCount;
                decimal niceStep = CalculateNiceStep(roughStep);
                if (niceStep > 0)
                {
                    decimal startVol = Math.Floor(minVol / niceStep) * niceStep;
                    
                    for (decimal v = startVol; v <= maxVol; v += niceStep)
                    {
                        if (v < minVol) continue;
                        float x = MathF.Floor((float)t.NumericToScreen((double)v, 0).X + (float)chartArea.Left) + 0.5f;
                        canvas.DrawLine(x, (float)chartArea.Top, x, (float)chartArea.Bottom, _gridPaint);
                    }
                }
            }
        }
        else
        {
            // Standard calculation for time-based charts using exact transform mapping
            var startPt = t.ScreenToChart(new global::Avalonia.Point(0, 0));
            var endPt = t.ScreenToChart(new global::Avalonia.Point(chartArea.Width, 0));
            
            // Find time bounds
            DateTime startTime = startPt.Time;
            DateTime endTime = endPt.Time;
            
            if (startTime >= endTime) return;
            
            TimeSpan visibleDuration = endTime - startTime;
            
            // Determine logical step (must match AxisRenderer!)
            TimeSpan step;
            if (visibleDuration.TotalDays > 365 * 3) step = TimeSpan.FromDays(365);
            else if (visibleDuration.TotalDays > 180) step = TimeSpan.FromDays(30);
            else if (visibleDuration.TotalDays > 30) step = TimeSpan.FromDays(7);
            else if (visibleDuration.TotalDays > 3) step = TimeSpan.FromDays(1);
            else step = TimeSpan.FromHours(visibleDuration.TotalHours > 24 ? 4 : 1);

            DateTime curr = startTime;
            // Snap to nearest boundary
            if (step.TotalDays == 365) curr = new DateTime(curr.Year, 1, 1);
            else if (step.TotalDays == 30) curr = new DateTime(curr.Year, curr.Month, 1);
            else if (step.TotalDays == 7) { while (curr.DayOfWeek != DayOfWeek.Monday) curr = curr.AddDays(-1); curr = curr.Date; }
            else if (step.TotalDays == 1) curr = curr.Date;
            else curr = new DateTime(curr.Year, curr.Month, curr.Day, curr.Hour, 0, 0);

            while (curr <= endTime)
            {
                if (curr >= startTime)
                {
                    float x = MathF.Floor((float)t.ChartToScreen(new ChartPoint(curr, 0)).X + (float)chartArea.Left) + 0.5f;
                    if (x >= (float)chartArea.Left && x <= (float)chartArea.Right)
                    {
                        canvas.DrawLine(x, (float)chartArea.Top, x, (float)chartArea.Bottom, _gridPaint);
                    }
                }
                
                if (step.TotalDays == 365) curr = curr.AddYears(1);
                else if (step.TotalDays == 30) curr = curr.AddMonths(1);
                else curr = curr.Add(step);
            }
        }

    }

    private decimal CalculateNiceStep(decimal roughStep)
    {
        if (roughStep == 0) return 1;

        // Find magnitude
        double mag = Math.Floor(Math.Log10((double)roughStep));
        decimal powerOf10 = (decimal)Math.Pow(10, mag);

        // Normalized step (0.1 to 1.0)
        decimal normalized = roughStep / powerOf10;

        decimal niceStep;
        if (normalized < 1.5m) niceStep = 1m;
        else if (normalized < 3m) niceStep = 2m; // 2 or 2.5
        else if (normalized < 7m) niceStep = 5m;
        else niceStep = 10m;

        return niceStep * powerOf10;
    }

    private string FormatValue(decimal value, decimal step)
    {
        // Simple heuristic: if step is integer, format as F0. If step < 1, format usually F2 or dynamic based on mag.
        if (step >= 1 && step % 1 == 0) return value.ToString("F0");
        if (step >= 0.1m) return value.ToString("F1");
        return value.ToString("F2");
    }

    /// <summary>
    /// Renders grid lines for a sub-window panel with custom value range.
    /// </summary>
    public void RenderPanel(SKCanvas canvas, Rect panelArea, decimal minVal, decimal priceRange, int totalCandles, IChartRenderConfig config, bool isFixedRange = false)
    {
        if (totalCandles == 0 || priceRange <= 0) return;

        _gridPaint.Color = config.ThemeManager.CurrentTheme.GridLine.ToSkColor();

        // Draw Vertical Grid (same logic as main chart)
        DrawVerticalNetworkForPanel(canvas, panelArea, totalCandles);

        // Draw Horizontal Grid based on panel's value range
        DrawHorizontalNetworkForPanel(canvas, panelArea, minVal, priceRange, isFixedRange, config);
    }

    private void DrawVerticalNetworkForPanel(SKCanvas canvas, Rect panelArea, int totalCandles)
    {
        if (totalCandles == 0) return;

        float pixelsPerGrid = ChartTheme.VerticalGridPixelInterval;
        int stepsCount = Math.Max(2, (int)(panelArea.Width / pixelsPerGrid));
        int interval = Math.Max(1, totalCandles / stepsCount);

        for (int i = 0; i < totalCandles; i += interval)
        {
            float candleWidth = (float)(panelArea.Width / totalCandles);
            float x = (float)panelArea.Left + i * candleWidth;

            float centerX = MathF.Floor(x + candleWidth / 2f) + 0.5f;

            canvas.DrawLine(centerX, (float)panelArea.Top, centerX, (float)panelArea.Bottom, _gridPaint);
        }
    }

    private void DrawHorizontalNetworkForPanel(SKCanvas canvas, Rect panelArea, decimal minVal, decimal priceRange, bool isFixedRange, IChartRenderConfig config)
    {
        if (priceRange <= 0) return;

        decimal maxVal = minVal + priceRange;
        
        _textPaint.Color = config.ThemeManager.CurrentTheme.AxisText.ToSkColor();

        if (isFixedRange)
        {
              // Draw strictly at Min and Max
              DrawGridLine(canvas, panelArea, minVal, minVal, priceRange, _textPaint);
              DrawGridLine(canvas, panelArea, maxVal, minVal, priceRange, _textPaint);
             
              // Detect Granville Law range specifically (-4 to 4) and draw -2, 0, 2 instead of a single middle line
              if (minVal == -4m && priceRange == 8m)
              {
                  DrawGridLine(canvas, panelArea, -2m, minVal, priceRange, _textPaint);
                  DrawGridLine(canvas, panelArea, 0m, minVal, priceRange, _textPaint);
                  DrawGridLine(canvas, panelArea, 2m, minVal, priceRange, _textPaint);
              }
              // If fixed range spans across zero (e.g. Correlation [-1, 1]), draw the 0 baseline
              else if (minVal < 0m && maxVal > 0m)
              {
                  DrawGridLine(canvas, panelArea, 0m, minVal, priceRange, _textPaint);
              }
              // Add a middle line if range is large enough (e.g. 0-100 -> 50) and not Granville Law
              else if (priceRange >= ChartTheme.FixedRangeMiddleLineThreshold)
              {
                  decimal mid = minVal + priceRange / 2m;
                  DrawGridLine(canvas, panelArea, mid, minVal, priceRange, _textPaint);
              }
        }
        else
        {
            decimal roughStep = priceRange / ChartTheme.PanelHorizontalGridLineTarget; // Fewer lines for smaller panels
            decimal step = CalculateNiceStep(roughStep);

            decimal firstLine = Math.Ceiling(minVal / step) * step;

            for (decimal val = firstLine; val <= maxVal; val += step)
            {
                DrawGridLine(canvas, panelArea, val, minVal, priceRange, _textPaint);
            }
        }
    }

    private void DrawGridLine(SKCanvas canvas, Rect panelArea, decimal value, decimal minVal, decimal priceRange, SKPaint textPaint)
    {
        float chartHeight = (float)panelArea.Height;
        const double PaddingY = 5.0; // Synchronized with GenericCoordinateTransform.PaddingY
        double availableHeight = chartHeight - (PaddingY * 2);
        if (availableHeight <= 0) availableHeight = 1;
        double scaleY = availableHeight / (double)priceRange;
        double offsetY = chartHeight - PaddingY;
        
        float y = MathF.Floor((float)(offsetY - (double)(value - minVal) * scaleY + panelArea.Top)) + 0.5f;
        
        // Clamp Y to avoid drawing outside? PriceToY handles it relative to Rect.
        // If value is slightly outside due to precision, it might be clipped.
        
        canvas.DrawLine((float)panelArea.Left, y, (float)panelArea.Right, y, _gridPaint);
        
        // Draw Label
        // Format based on range magnitude?
        string label = value.ToString(priceRange >= ChartTheme.FixedRangeMiddleLineThreshold ? "F0" : "F2");
        canvas.DrawText(label, (float)panelArea.Right + ChartTheme.GridLabelOffsetX, y + ChartTheme.GridLabelOffsetY, textPaint);
    }

    public void Dispose()
    {
        _gridPaint.Dispose();
        _zScoreZeroPaint.Dispose();
        _zScoreThresholdPaint.Dispose();
        _textPaint.Dispose();
        _dashEffect.Dispose();
    }
}
