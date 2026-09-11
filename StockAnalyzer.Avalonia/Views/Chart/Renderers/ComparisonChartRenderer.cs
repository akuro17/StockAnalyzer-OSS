using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Utils;
using StockAnalyzer.Avalonia.Common;
using System.Buffers;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the Relative Comparison Chart (multi-symbol percentage-change overlay).
/// </summary>
public sealed class ComparisonChartRenderer : IChartRenderer, IDisposable, IAxisProjectable
{
    private readonly SKPaint _linePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = LayoutConstants.DefaultStrokeWidth * 1.5f,
        IsAntialias = true,
        StrokeJoin = SKStrokeJoin.Round,
        StrokeCap = SKStrokeCap.Round
    };
    private readonly SKPaint _markerPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _baselinePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0) // Pre-allocate dash effect
    };
    private readonly SKPaint _badgePaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _badgeTextPaint = new() 
    { 
        Color = SKColors.White, 
        IsAntialias = true, 
        TextSize = 10f, 
        Typeface = SKTypeface.Default // Use standard typeface instead of hardcoded Arial
    };
    private readonly SKPath _path = new();
    private readonly SKPath _outlierPath = new();

    private readonly List<string> _sortedSymbolsBuffer = new();
    private readonly List<AxisLabelRequest> _axisLabelBuffer = new();

    public ComparisonChartRenderer()
    {
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        if (baseConfig is not IComparisonRenderConfig config) return;
        var comparisonData = config.ComparisonData;
        
        // Safety checks: Ensure data and timeline consistency
        if (comparisonData == null || comparisonData.Series.Count == 0 || comparisonData.Timestamps.Length == 0) return;
        if (config.Transform is not ICoordinateTransform transform) return;

        int candleCount = snapshot.Candles.Count;
        if (candleCount == 0) return;

        // Determine Interval for centering lines on candles
        TimeSpan interval = TimeSpan.FromDays(1);
        if (candleCount > 1)
        {
             interval = snapshot.Candles[1].Timestamp - snapshot.Candles[0].Timestamp;
        }
        
        // Calculate coordinate offsets for centering
        float xStart = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
        float xNext = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp + interval, 0)).X;
        float halfCandleWidth = (xNext - xStart) / 2f;

        // Determine LOD threshold for LTTB
        int threshold = (int)Math.Max(chartArea.Width * 2, 2);
        int maxPoints = Math.Min(candleCount, threshold);

        // ZeroAllocation: Rent buffers from ArrayPool
        int[] indicesBuffer = ArrayPool<int>.Shared.Rent(maxPoints);
        decimal?[] relativeBuffer = ArrayPool<decimal?>.Shared.Rent(candleCount);

        try
        {

            // 0. Draw Reference/Baseline Line (Mode-aware)
            float baselineValue = config.ComparisonMode switch
            {
                ComparisonMode.Performance => 0f,
                ComparisonMode.ZScore => 0f,
                ComparisonMode.Ratio => 1f,
                _ => 0f
            };

            var baselinePoint = transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)baselineValue));
            float baselineY = MathF.Floor((float)baselinePoint.Y + (float)chartArea.Top) + 0.5f;

            _baselinePaint.Color = SKColors.Gray.WithAlpha(80);

            if (baselineY >= chartArea.Top && baselineY <= chartArea.Bottom)
            {
                // On-screen: Draw full horizontal line
                canvas.DrawLine((float)chartArea.Left, baselineY, (float)chartArea.Right, baselineY, _baselinePaint);
            }
            else
            {
                // Off-screen: Draw "Base: X.XX ↑/↓" badge at the edge
                bool isAbove = baselineY < chartArea.Top;
                float edgeY = isAbove ? (float)chartArea.Top + 10f : (float)chartArea.Bottom - 10f;
                string arrow = isAbove ? "↑" : "↓";
                string label = $"Base: {baselineValue:F2} {arrow}";

                var baseColorIndex = config.SeriesColorIndex.GetOrAdd(comparisonData.PrimarySymbol);
                var baseColor = ChartColorPalette.Get(baseColorIndex, config.ThemeManager.CurrentTheme.IsDark);
                
                _badgePaint.Color = baseColor.WithAlpha(120).ToSkColor();
                
                float textWidth = _badgeTextPaint.MeasureText(label);
                float margin = 4f;
                float rectWidth = textWidth + (margin * 2);
                float rectHeight = 16f;
                float rectX = (float)chartArea.Left + 4f;
                float rectY = edgeY - (rectHeight / 2f);

                var rect = new SKRect(rectX, rectY, rectX + rectWidth, rectY + rectHeight);
                canvas.DrawRoundRect(rect, 4f, 4f, _badgePaint);
                canvas.DrawText(label, rectX + margin, rectY + rectHeight - 4f, _badgeTextPaint);
            }

            int symbolIndex = 0;
            _sortedSymbolsBuffer.Clear();
            foreach (var key in comparisonData.Series.Keys)
            {
                _sortedSymbolsBuffer.Add(key);
            }
            _sortedSymbolsBuffer.Sort(StringComparer.Ordinal);

            foreach (var symbol in _sortedSymbolsBuffer)
            {
                // 1. Skip drawing the benchmark symbol (PrimarySymbol) in Ratio or Spread modes.
                // It is already represented by the dashed baseline, and drawing a solid line on top is redundant.
                bool isStaticBase = string.Equals(symbol, comparisonData.PrimarySymbol, StringComparison.OrdinalIgnoreCase) && 
                                    (config.ComparisonMode == ComparisonMode.Ratio || config.ComparisonMode == ComparisonMode.Spread);
                
                if (isStaticBase)
                {
                    // Draw a dashed baseline if this is the static primary symbol in Ratio/Spread mode
                    DrawBaseline(canvas, chartArea, config, comparisonData.PrimarySymbol);
                    continue;
                }

                var series = comparisonData.Series[symbol];
                // 1. Get pre-calculated series from Snapshot (ZeroAllocation & SRP)
                if (snapshot.ComparisonSeries == null || !snapshot.ComparisonSeries.TryGetValue(symbol, out var precalcSeries)) continue;
                if (precalcSeries == null || precalcSeries.Length == 0) continue;

                // Populate local buffer for LTTB
                int validCount = Math.Min(candleCount, precalcSeries.Length);
                for (int i = 0; i < validCount; i++)
                {
                    relativeBuffer[i] = precalcSeries[i];
                }

                // 2. Optimization: Apply LTTB downsampling to preserve extrema while reducing drawing calls
                int pointCount = LttbDownsampler.Downsample(relativeBuffer.AsSpan(0, validCount), maxPoints, indicesBuffer.AsSpan(0, maxPoints));

                // 3. Draw Path
                _path.Reset();
                bool firstPoint = true;
                
                bool isDark = config.ThemeManager.CurrentTheme.IsDark;
                int colorIndex = config.SeriesColorIndex.GetOrAdd(symbol);
                _linePaint.Color = ChartColorPalette.Get(colorIndex, isDark).ToSkColor();

                float lastX = 0;
                float lastY = 0;
                bool hasPoints = false;

                for (int i = 0; i < pointCount; i++)
                {
                    int dataIndex = indicesBuffer[i];
                    var relativeValue = relativeBuffer[dataIndex];
                    if (!relativeValue.HasValue) continue;

                    var candle = snapshot.Candles[dataIndex];
                    float x = MathF.Floor((float)transform.ChartToScreen(new ChartPoint(candle.Timestamp, 0)).X + halfCandleWidth) + 0.5f;
                    
                    // Step 6: Clipping for Z-Score outliers (|z| > 3.5)
                    decimal renderedValue = relativeValue.Value;
                    bool isPositiveOutlier = false;
                    bool isNegativeOutlier = false;
                    if (config.ComparisonMode == ComparisonMode.ZScore)
                    {
                        if (renderedValue > StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit) { renderedValue = StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit; isPositiveOutlier = true; }
                        else if (renderedValue < -StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit) { renderedValue = -StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit; isNegativeOutlier = true; }
                    }

                    float y = MathF.Floor((float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, renderedValue)).Y + (float)chartArea.Top) + 0.5f;

                    // FINITENESS GUARD: Prevent Skia thread crash on NaN/Infinity during transitions
                    if (!float.IsFinite(x) || !float.IsFinite(y)) continue;

                    if (firstPoint)
                    {
                        _path.MoveTo(x, y);
                        firstPoint = false;
                    }
                    else
                    {
                        _path.LineTo(x, y);
                    }

                    if (isPositiveOutlier || isNegativeOutlier)
                    {
                        DrawOutlierIndicator(canvas, x, y, isPositiveOutlier, _linePaint.Color);
                    }

                    lastX = x;
                    lastY = y;
                    hasPoints = true;
                }

                canvas.DrawPath(_path, _linePaint);

                if (hasPoints)
                {
                    _markerPaint.Color = _linePaint.Color;
                    canvas.DrawCircle(lastX, lastY, _linePaint.StrokeWidth * 1.5f, _markerPaint);
                }

                symbolIndex++;
            }
        }
        finally
        {
            // Guaranteed return to pool
            ArrayPool<int>.Shared.Return(indicesBuffer);
            ArrayPool<decimal?>.Shared.Return(relativeBuffer);
        }
    }

    private void DrawOutlierIndicator(SKCanvas canvas, float x, float y, bool isPositive, SKColor color)
    {
        _markerPaint.Color = color;
        float halfSize = 4.0f;
        _outlierPath.Reset();
        if (isPositive)
        {
            _outlierPath.MoveTo(x, y - 2);
            _outlierPath.LineTo(x - halfSize, y + halfSize);
            _outlierPath.LineTo(x + halfSize, y + halfSize);
        }
        else
        {
            _outlierPath.MoveTo(x, y + 2);
            _outlierPath.LineTo(x - halfSize, y - halfSize);
            _outlierPath.LineTo(x + halfSize, y - halfSize);
        }
        _outlierPath.Close();
        canvas.DrawPath(_outlierPath, _markerPaint);
    }

    public IEnumerable<AxisLabelRequest> GetAxisProjections(ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        if (baseConfig is not IComparisonRenderConfig config || snapshot.Candles.Count == 0) yield break;
        var comparisonData = config.ComparisonData;
        if (comparisonData == null || snapshot.ComparisonSeries == null) yield break;

        bool isDark = config.ThemeManager.CurrentTheme.IsDark;
        int candleCount = snapshot.Candles.Count;

        _axisLabelBuffer.Clear();

        foreach (var symbol in comparisonData.Series.Keys)
        {
            // Skip the benchmark symbol label in Ratio or Spread modes to avoid redundancy and label collision at the baseline.
            bool isStaticBase = (symbol == comparisonData.PrimarySymbol) && (config.ComparisonMode == ComparisonMode.Ratio || config.ComparisonMode == ComparisonMode.Spread);
            if (isStaticBase) continue;

            if (!snapshot.ComparisonSeries.TryGetValue(symbol, out var precalcSeries)) continue;
            if (precalcSeries == null || precalcSeries.Length == 0) continue;

            decimal? lastValue = null;
            int validCount = Math.Min(candleCount, precalcSeries.Length);
            for (int i = validCount - 1; i >= 0; i--)
            {
                if (precalcSeries[i].HasValue) { lastValue = precalcSeries[i]; break; }
            }
            if (!lastValue.HasValue) continue;

            int colorIndex = config.SeriesColorIndex.GetOrAdd(symbol);
            SKColor lineColor = ChartColorPalette.Get(colorIndex, isDark).ToSkColor();

            string labelText = config.ShowTickerInsteadOfValue
                ? symbol
                : config.ComparisonMode switch
                {
                    ComparisonMode.Performance => $"{lastValue.Value.ToString(ChartConstants.DefaultRelativePerformanceFormat)}{ChartConstants.DefaultRelativePerformanceSuffix}",
                    ComparisonMode.Ratio => lastValue.Value.ToString(ChartConstants.DefaultRatioFormat),
                    ComparisonMode.ZScore => $"{ChartConstants.ZScorePrefix}{lastValue.Value.ToString(ChartConstants.DefaultZScoreFormat)}",
                    ComparisonMode.Spread => lastValue.Value.ToString(ChartConstants.DefaultSpreadFormat),
                    _ => lastValue.Value.ToString("F2")
                };


            // Step 6: Clip the Y-position value, but keep the original label text
            decimal renderedValue = lastValue.Value;
            if (config.ComparisonMode == ComparisonMode.ZScore)
            {
                if (renderedValue > StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit) renderedValue = StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit;
                else if (renderedValue < -StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit) renderedValue = -StockAnalyzer.Avalonia.Common.ChartConstants.ZScoreClippingLimit;
            }

            _axisLabelBuffer.Add(new AxisLabelRequest(
                Value: renderedValue, // This determines Y-position
                Color: lineColor,
                Label: labelText,     // This shows the actual value (e.g., Z:+5.2)
                Style: AxisLabelStyle.CurrentPrice
            ));
        }

        _axisLabelBuffer.Sort((a, b) => b.Value.CompareTo(a.Value));

        for (int i = 0; i < _axisLabelBuffer.Count; i++)
        {
            yield return _axisLabelBuffer[i];
        }
    }

    private void DrawBaseline(SKCanvas canvas, Rect chartArea, IComparisonRenderConfig config, string primarySymbol)
    {
        if (config.Transform is not ICoordinateTransform transform) return;
        
        // Baseline is always 0.0 (Performance/ZScore/Spread) or 1.0 (Ratio)
        decimal baselineValue = config.ComparisonMode switch
        {
            ComparisonMode.Performance => 0m,
            ComparisonMode.ZScore => 0m,
            ComparisonMode.Ratio => 1m,
            ComparisonMode.Spread => 0m,
            _ => 0m
        };

        // decimal baselineValue = ... (already calculated above)

        var baselinePoint = transform.ChartToScreen(new ChartPoint(DateTime.MinValue, baselineValue));
        float baselineY = MathF.Floor((float)baselinePoint.Y + (float)chartArea.Top) + 0.5f;

        if (baselineY >= chartArea.Top && baselineY <= chartArea.Bottom)
        {
            // Draw horizontal dashed line
            _baselinePaint.Color = SKColors.Gray.WithAlpha(120);
            canvas.DrawLine((float)chartArea.Left, baselineY, (float)chartArea.Right, baselineY, _baselinePaint);
        }
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _markerPaint.Dispose();
        _baselinePaint.Dispose();
        _badgePaint.Dispose();
        _badgeTextPaint.Dispose();
        _path.Dispose();
        _outlierPath.Dispose();
    }
}
