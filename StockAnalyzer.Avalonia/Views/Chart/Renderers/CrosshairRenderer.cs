using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis; // Added for ReverseWatchCurveData
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the crosshair overlay (mouse-tracking cursor lines and axis labels).
/// </summary>
public sealed class CrosshairRenderer : IDisposable
{
    private readonly SKPaint _linePaint;
    private readonly SKPaint _labelBackgroundPaint;
    private readonly SKPaint _labelTextPaint;
    private readonly SKPaint _markerPaint;
    private readonly SKFont _labelFont;

    // Use centralized constants
    private const float LabelPadding = ChartTheme.CrosshairLabelPadding;
    private const float LabelHeight = ChartTheme.CrosshairLabelHeight;

    private readonly SKPaint _labelBorderPaint;
    
    // String caching to avoid per-frame allocations in mouse-tracking hot paths
    private decimal _lastPrice;
    private string _lastPriceText = string.Empty;
    private PriceScaleType? _lastPriceScale;
    
    private decimal _lastVol;
    private string _lastVolText = string.Empty;
    
    private decimal _lastRwPrice;
    private string _lastRwPriceText = string.Empty;
    
    private decimal _lastPanelValue;
    private string _lastPanelValueText = string.Empty;
    
    private decimal _lastCompValue;
    private ComparisonMode _lastCompMode;
    private string _lastCompValueText = string.Empty;

    public CrosshairRenderer()
    {
        _linePaint = new SKPaint
        {
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
        };

        _labelBackgroundPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill
        };

        _labelBorderPaint = new SKPaint
        {
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _labelTextPaint = new SKPaint
        {
            IsAntialias = true
        };

        _markerPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _labelFont = new SKFont(SKTypeface.Default, 12);
    }
    
    private void UpdateTheme(IChartRenderConfig config)
    {
        var theme = config.ThemeManager.CurrentTheme;
        _linePaint.Color = theme.CrosshairLineAlpha.ToSkColor();
        _linePaint.StrokeWidth = 1.0f;
        _labelBackgroundPaint.Color = theme.ChartBackground.ToSkColor();
        _labelBorderPaint.Color = theme.CrosshairText.ToSkColor();
        _labelTextPaint.Color = theme.CrosshairText.ToSkColor();
    }

    /// <summary>
    /// Renders the crosshair at the specified mouse position.
    /// </summary>
    /// <param name="canvas">The SkiaSharp canvas to draw on.</param>
    /// <param name="chartArea">The chart drawing area.</param>
    /// <param name="volumeArea">The volume drawing area.</param>
    /// <param name="mousePosition">Current mouse position relative to control.</param>
    /// <param name="snapshot">The chart data snapshot.</param>
    /// <param name="controlBounds">The full control bounds.</param>
    public void Render(
        SKCanvas canvas,
        global::Avalonia.Rect chartArea,
        global::Avalonia.Rect volumeArea,
        global::Avalonia.Point mousePosition,
        ChartDataSnapshot snapshot,
        global::Avalonia.Rect controlBounds,
        IChartRenderConfig config)
    {
        if (snapshot.Candles.Count == 0) return;

        UpdateTheme(config);

        float mouseX = (float)mousePosition.X;
        float mouseY = (float)mousePosition.Y;

        // Draw vertical line (full height of chart + volume area)
        float lineTop = (float)chartArea.Y;
        float lineBottom = (float)(volumeArea.Y + volumeArea.Height);
        canvas.DrawLine(MathF.Floor(mouseX) + 0.5f, lineTop, MathF.Floor(mouseX) + 0.5f, lineBottom, _linePaint);

        // Draw horizontal line (only in chart area for price reference)
        if (mouseY >= chartArea.Y && mouseY <= chartArea.Y + chartArea.Height)
        {
            float snappedY = MathF.Floor(mouseY) + 0.5f;
            canvas.DrawLine((float)chartArea.X, snappedY, (float)(chartArea.X + chartArea.Width), snappedY, _linePaint);

            // Draw price label on the right side (Outside chart area on the Y-axis)
            decimal price = YToPrice(mouseY, chartArea, snapshot);
            var scale = config.Transform?.PriceScale ?? PriceScaleType.Linear;
            if (price != _lastPrice || scale != _lastPriceScale || string.IsNullOrEmpty(_lastPriceText))
            {
                _lastPrice = price;
                _lastPriceScale = scale;
                if (scale == PriceScaleType.Percent && snapshot.Candles.Count > 0)
                {
                    decimal referencePrice = snapshot.Candles[0].Close;
                    if (referencePrice != 0)
                    {
                        decimal percentChange = (price - referencePrice) / referencePrice * 100m;
                        _lastPriceText = percentChange.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";
                    }
                    else
                    {
                        _lastPriceText = price.ToString("F2");
                    }
                }
                else
                {
                    _lastPriceText = price.ToString("F2");
                }
            }
            float textWidth = MeasureTextWidth(_lastPriceText);
            float labelWidth = textWidth + LabelPadding * 2;
            
            float labelX = (float)chartArea.Right + 2f;
            DrawLabel(canvas, _lastPriceText, labelX, mouseY - LabelHeight / 2, true, config);
        }

        // Date label at bottom has been removed as per user request.
    }

    /// <summary>
    /// Renders the crosshair for Reverse Watch (XY Plot) charts.
    /// </summary>
    public void RenderReverseWatch(
        SKCanvas canvas,
        global::Avalonia.Rect chartArea,
        global::Avalonia.Point mousePosition,
        ReverseWatchCurveData data,
        IChartRenderConfig config)
    {
        if (data == null) return;

        UpdateTheme(config);

        var bounds = data.Bounds;
        var volumeRange = bounds.MaxVolume - bounds.MinVolume;
        var priceRange = bounds.MaxPrice - bounds.MinPrice;
        if (volumeRange <= 0 || priceRange <= 0) return;

        float mouseX = (float)mousePosition.X;
        float mouseY = (float)mousePosition.Y;
        
        // Ensure mouse is within chart area for drawing lines/labels
        
        // Draw Crosshair Lines
        // Vertical Line
        canvas.DrawLine(MathF.Floor(mouseX) + 0.5f, (float)chartArea.Top, MathF.Floor(mouseX) + 0.5f, (float)chartArea.Bottom, _linePaint);
        // Horizontal Line
        float snappedYXY = MathF.Floor(mouseY) + 0.5f;
        canvas.DrawLine((float)chartArea.Left, snappedYXY, (float)chartArea.Right, snappedYXY, _linePaint);

        // Labels Setup
        // Using existing paints and constants
        
        // X-Axis Label (Volume)
        // Position: Bottom of chart area (or x-axis usage area)
        // Logic from ChartBaseControl:
        float drawX = (float)chartArea.X + 10f; // Margin match
        float drawWidth = (float)chartArea.Width - 20f; // Margin match
        if (drawWidth > 0)
        {
            float ratioX = (mouseX - drawX) / drawWidth;
            decimal volume = bounds.MinVolume + (decimal)ratioX * volumeRange;
            if (volume != _lastVol || string.IsNullOrEmpty(_lastVolText))
            {
                _lastVol = volume;
                _lastVolText = volume.ToString("N0");
            }

            // Draw label at bottom
            float labelY = (float)chartArea.Bottom + 2;
            // Center label on mouseX
            float textWidth = MeasureTextWidth(_lastVolText);
            float labelWidth = textWidth + LabelPadding * 2;
            float labelX = mouseX - labelWidth / 2;

            DrawLabel(canvas, _lastVolText, labelX, labelY, false, config);
        }

        // Y-Axis Label (Price)
        // Position: Right side? Standard Y-axis
        float drawY = (float)chartArea.Y + 10f; // Margin match
        float drawHeight = (float)chartArea.Height - 20f; // Margin match
        if (drawHeight > 0)
        {
            float ratioY = 1.0f - ((mouseY - drawY) / drawHeight);
            decimal price = bounds.MinPrice + (decimal)ratioY * priceRange;
            if (price != _lastRwPrice || string.IsNullOrEmpty(_lastRwPriceText))
            {
                _lastRwPrice = price;
                _lastRwPriceText = price.ToString("N0");
            }

            // Draw label on the right margin (Y-Axis area)
            float textWidth = MeasureTextWidth(_lastRwPriceText);
            float labelWidth = textWidth + LabelPadding * 2;
            float labelX = (float)chartArea.Right + 2f; // Draw just outside the right bound
            float labelY = mouseY - LabelHeight / 2;

            DrawLabel(canvas, _lastRwPriceText, labelX, labelY, true, config);
        }
    }

    private void DrawLabel(SKCanvas canvas, string text, float x, float y, bool isPriceLabel, IChartRenderConfig config)
    {
        if (!config.CrosshairLabelVisible) return;
        float textWidth = MeasureTextWidth(text);
        float labelWidth = textWidth + LabelPadding * 2;

        var rect = new SKRect(x, y, x + labelWidth, y + LabelHeight);
        canvas.DrawRoundRect(rect, 2, 2, _labelBackgroundPaint);
        canvas.DrawRoundRect(rect, 2, 2, _labelBorderPaint); // Draw border

        canvas.DrawText(text, x + LabelPadding, y + LabelHeight - LabelPadding, _labelFont, _labelTextPaint);
    }

    private float MeasureTextWidth(string text)
    {
        return _labelTextPaint.MeasureText(text);
    }

    private static decimal YToPrice(float y, global::Avalonia.Rect chartArea, ChartDataSnapshot snapshot)
    {
        if (snapshot.PriceRange == 0) return snapshot.MinPrice;

        float chartHeight = (float)chartArea.Height;
        float relativeY = y - (float)chartArea.Y;
        decimal normalizedY = 1m - (decimal)relativeY / (decimal)chartHeight;
        return snapshot.MinPrice + normalizedY * snapshot.PriceRange;
    }

    /// <summary>
    /// Renders the crosshair and hover markers for Relative Comparison charts.
    /// </summary>
    public void RenderComparison(
        SKCanvas canvas,
        global::Avalonia.Rect chartArea,
        global::Avalonia.Point mousePosition,
        ChartDataSnapshot snapshot,
        IComparisonRenderConfig config)
    {
        if (snapshot.Candles.Count == 0 || config.ComparisonData == null) return;
        UpdateTheme(config);

        float mouseX = (float)mousePosition.X;
        float mouseY = (float)mousePosition.Y;
        
        if (config.Transform is not ICoordinateTransform transform) return;

        // 1. Draw Vertical Crosshair Line
        canvas.DrawLine(MathF.Floor(mouseX) + 0.5f, (float)chartArea.Top, MathF.Floor(mouseX) + 0.5f, (float)chartArea.Bottom, _linePaint);

        // 2. Draw Horizontal Line and % Label
        if (mouseY >= chartArea.Y && mouseY <= chartArea.Y + chartArea.Height)
        {
            float snappedYComp = MathF.Floor(mouseY) + 0.5f;
            canvas.DrawLine((float)chartArea.X, snappedYComp, (float)(chartArea.X + chartArea.Width), snappedYComp, _linePaint);

            // Price in comparison chart is mode-aware
            decimal price = YToPrice(mouseY, chartArea, snapshot);
            if (price != _lastCompValue || config.ComparisonMode != _lastCompMode || string.IsNullOrEmpty(_lastCompValueText))
            {
                _lastCompValue = price;
                _lastCompMode = config.ComparisonMode;
                _lastCompValueText = config.ComparisonMode switch
                {
                    ComparisonMode.Performance => price.ToString(ChartConstants.DefaultRelativePerformanceFormat) + ChartConstants.DefaultRelativePerformanceSuffix,
                    ComparisonMode.Ratio => price.ToString(ChartConstants.DefaultRatioFormat),
                    ComparisonMode.ZScore => $"{ChartConstants.ZScorePrefix}{price.ToString(ChartConstants.DefaultZScoreFormat)}",
                    ComparisonMode.Spread => price.ToString(ChartConstants.DefaultSpreadFormat),
                    _ => price.ToString("F2")
                };
            }
            float labelX = (float)chartArea.Right + 2f;
            DrawLabel(canvas, _lastCompValueText, labelX, mouseY - LabelHeight / 2, true, config);
        }

        // 3. Identify Data Index at mouseX (accounting for halfCandleWidth offset used by line renderer)
        int candleCount = snapshot.Candles.Count;
        if (candleCount == 0) return;

        // Calculate halfCandleWidth to match ComparisonChartRenderer
        float halfCandleWidth = 0;
        if (candleCount > 1)
        {
            float firstX = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
            float secondX = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[1].Timestamp, 0)).X;
            halfCandleWidth = (secondX - firstX) / 2f;
        }

        // Find the nearest data index by comparing mouseX to actual rendered X positions
        // RATIO-101 partial fix: Ratioモード時は比較基準銘柄（PrimarySymbol）のインデックスに合わせる必要があるため、
        // Snapshot.Candles（主銘柄）ではなく、比較データ側のインデックス解決ロジックに寄せる。
        float chartLeft = (float)chartArea.Left;
        int dataIndex = 0;
        float minDist = float.MaxValue;
        for (int i = 0; i < candleCount; i++)
        {
            float cx = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[i].Timestamp, 0)).X + chartLeft + halfCandleWidth;
            float dist = Math.Abs(cx - mouseX);
            if (dist < minDist) { minDist = dist; dataIndex = i; }
        }

        if (dataIndex < 0 || dataIndex >= candleCount) return;

        // 4. Draw Hover Markers at the EXACT data point position (ZeroAllocation & SRP)
        if (snapshot.ComparisonSeries == null) return;

        int symbolIndex = 0;
        foreach (var kvp in snapshot.ComparisonSeries)
        {
            var symbol = kvp.Key;
            var values = kvp.Value;
            if (dataIndex < values.Length && values[dataIndex].HasValue)
            {
                decimal relativeValue = values[dataIndex]!.Value;
                // Use exact same Y formula as ComparisonChartRenderer.Render
                float markerY = (float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, relativeValue)).Y + (float)chartArea.Top;
                // Use exact same X formula as ComparisonChartRenderer.Render (snap to data point)
                float markerX = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[dataIndex].Timestamp, 0)).X + chartLeft + halfCandleWidth;

                bool isDark = config.ThemeManager.CurrentTheme.IsDark;
                int colorIndex = config.SeriesColorIndex.GetOrAdd(symbol);
                _markerPaint.Color = ChartColorPalette.Get(colorIndex, isDark).ToSkColor();
                
                canvas.DrawCircle(markerX, markerY, 5, _markerPaint);
            }
            symbolIndex++;
        }
    }

    /// <summary>
    /// Renders crosshair for a sub-window panel with custom value range.
    /// </summary>
    public void RenderPanel(
        SKCanvas canvas,
        global::Avalonia.Rect panelArea,
        global::Avalonia.Point mousePosition,
        decimal minVal,
        decimal priceRange,
        string indicatorName,
        IChartRenderConfig config)
    {
        UpdateTheme(config);
        float mouseX = (float)mousePosition.X;
        float mouseY = (float)mousePosition.Y;

        // Always draw vertical line through panels (time sync)
        canvas.DrawLine(MathF.Floor(mouseX) + 0.5f, (float)panelArea.Top, MathF.Floor(mouseX) + 0.5f, (float)panelArea.Bottom, _linePaint);

        // Draw horizontal line and value label only if mouse is within this panel
        if (mouseY >= panelArea.Top && mouseY <= panelArea.Bottom)
        {
            float snappedYPanel = MathF.Floor(mouseY) + 0.5f;
            canvas.DrawLine((float)panelArea.Left, snappedYPanel, (float)panelArea.Right, snappedYPanel, _linePaint);

                // Calculate value at mouse Y position
            if (priceRange > 0)
            {
                float relativeY = mouseY - (float)panelArea.Top;
                decimal normalizedY = 1m - (decimal)relativeY / (decimal)panelArea.Height;
                decimal value = minVal + normalizedY * priceRange;
                if (value != _lastPanelValue || string.IsNullOrEmpty(_lastPanelValueText))
                {
                    _lastPanelValue = value;
                    _lastPanelValueText = value.ToString("F2");
                }
                
                float textWidth = MeasureTextWidth(_lastPanelValueText);
                float labelWidth = textWidth + LabelPadding * 2;
                
                // Draw label outside the right side of panel
                float labelX = (float)panelArea.Right + 2f;
                DrawLabel(canvas, _lastPanelValueText, labelX, mouseY - LabelHeight / 2, true, config);
            }
        }
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _labelBackgroundPaint.Dispose();
        _labelBorderPaint.Dispose();
        _labelTextPaint.Dispose();
        _markerPaint.Dispose();
        _labelFont.Dispose();
    }
}
