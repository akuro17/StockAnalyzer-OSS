using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders Ichimoku Cloud and Lines.
/// </summary>
public class IchimokuRenderer : IDisposable
{
    private readonly LineIndicatorRenderer _lineRenderer = new();
    private readonly SKPaint _cloudPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPath _cloudPath = new SKPath();
    private readonly SKPath _cloudPath2 = new SKPath();

    public void Dispose()
    {
        _lineRenderer.Dispose();
        _cloudPaint.Dispose();
        _cloudPath.Dispose();
        _cloudPath2.Dispose();
    }

    public void Render(SKCanvas canvas, Rect chartArea,
        Dictionary<string, IReadOnlyList<decimal?>> series,
        CoreIndicatorSettings setting,
        decimal minVal, decimal maxVal,
        int visibleCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData> candles,
        TimeSpan interval,
        ICoordinateTransform? transform,
        IChartRenderConfig config)
    {
        Render(
            canvas, chartArea, series, setting, minVal, maxVal,
            visibleCandles, offset, startIndex, candles, interval, transform, config,
            overrideHost: null, snapshot: null, result: null, xOffset: 0f, isSubWindowContext: false);
    }

    internal void Render(SKCanvas canvas, Rect chartArea,
        Dictionary<string, IReadOnlyList<decimal?>> series,
        CoreIndicatorSettings setting,
        decimal minVal, decimal maxVal,
        int visibleCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData> candles,
        TimeSpan interval,
        ICoordinateTransform? transform,
        IChartRenderConfig config,
        IndicatorRenderer? overrideHost,
        ChartDataSnapshot? snapshot,
        IIndicatorResult? result,
        float xOffset,
        bool isSubWindowContext)
    {
        // Force Offset 0 for Ichimoku (Data is already shifted by Logic)
        offset = 0;

        decimal range = maxVal - minVal;
        if (range == 0) range = 1;

        // Render Cloud (Senkou Span A vs B) only when both span lines are visible
        if (SeriesRenderStyleCatalog.IsSeriesVisible(setting, CoreIchimokuIndicator.SenkouSpanASeriesName) &&
            SeriesRenderStyleCatalog.IsSeriesVisible(setting, CoreIchimokuIndicator.SenkouSpanBSeriesName) &&
            series.TryGetValue(CoreIchimokuIndicator.SenkouSpanASeriesName, out var spanA) &&
            series.TryGetValue(CoreIchimokuIndicator.SenkouSpanBSeriesName, out var spanB))
        {
            RenderCloud(canvas, chartArea, minVal, range, spanA, spanB, setting, visibleCandles, offset, startIndex, candles, interval, transform);
        }

        if (result != null)
        {
            var names = result.SeriesNamesList;
            for (int i = 0; i < names.Count; i++)
            {
                var seriesName = names[i];
                if (series.TryGetValue(seriesName, out var values) && values != null)
                {
                    RenderIchimokuSeries(
                        canvas, chartArea, seriesName, values, setting, minVal, maxVal,
                        visibleCandles, offset, candles, interval, transform, config,
                        overrideHost, snapshot, result, xOffset, isSubWindowContext);
                }
            }
        }
        else
        {
            foreach (var kvp in series)
            {
                if (kvp.Value == null) continue;
                RenderIchimokuSeries(
                    canvas, chartArea, kvp.Key, kvp.Value, setting, minVal, maxVal,
                    visibleCandles, offset, candles, interval, transform, config,
                    overrideHost, snapshot, result: null, xOffset, isSubWindowContext);
            }
        }
    }

    private void RenderIchimokuSeries(
        SKCanvas canvas,
        Rect chartArea,
        string seriesName,
        IReadOnlyList<decimal?> values,
        CoreIndicatorSettings setting,
        decimal minVal,
        decimal maxVal,
        int visibleCandles,
        int offset,
        IReadOnlyList<CoreCandleData> candles,
        TimeSpan interval,
        ICoordinateTransform? transform,
        IChartRenderConfig config,
        IndicatorRenderer? overrideHost,
        ChartDataSnapshot? snapshot,
        IIndicatorResult? result,
        float xOffset,
        bool isSubWindowContext)
    {
        if (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, seriesName))
        {
            return;
        }

        // Determine Series Color
        SKColor seriesColor = SKColors.Gray;
        bool colorFound = false;

        // 1. Try SeriesColors config via SSoT alias-aware catalog without allocation
        var colorConfig = SeriesRenderStyleCatalog.FindSeriesConfig(setting, seriesName);
        if (colorConfig != null)
        {
            seriesColor = colorConfig.Color.ToSkColor();
            colorFound = true;
        }

        // Fallback logic from IndicatorRenderer
        if (!colorFound)
        {
            seriesColor = setting.Color.ToSkColor();
        }

        // FIX: If color is transparent (Alpha=0), force default colors for Ichimoku
        if (seriesColor.Alpha == 0)
        {
            var theme = config.ThemeManager.CurrentTheme;
            var canonicalName = SeriesRenderStyleCatalog.NormalizeSeriesName(setting.TypeEnum, seriesName);
            if (string.Equals(canonicalName, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase))
            {
                seriesColor = theme.IchimokuTenkan.ToSkColor();
            }
            else if (string.Equals(seriesName, CoreIchimokuIndicator.KijunSenSeriesName, StringComparison.OrdinalIgnoreCase))
            {
                seriesColor = theme.IchimokuKijun.ToSkColor();
            }
            else if (string.Equals(seriesName, CoreIchimokuIndicator.ChikouSpanSeriesName, StringComparison.OrdinalIgnoreCase))
            {
                seriesColor = theme.IchimokuChikou.ToSkColor();
            }
            else if (string.Equals(seriesName, CoreIchimokuIndicator.SenkouSpanASeriesName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(seriesName, CoreIchimokuIndicator.SenkouSpanBSeriesName, StringComparison.OrdinalIgnoreCase))
            {
                seriesColor = theme.IchimokuSenkou.ToSkColor();
            }
        }

        var explicitPrimitive = SeriesRenderStyleCatalog.ResolveExplicitPrimitive(setting, seriesName);
        if (explicitPrimitive.HasValue && overrideHost != null && snapshot != null && result != null && transform != null)
        {
            overrideHost.RenderSynthesizedSeries(
                canvas, chartArea, snapshot, result, setting, config, transform,
                seriesName, values, seriesColor, explicitPrimitive.Value,
                minVal, maxVal, visibleCandles, offset, interval, xOffset, isSubWindowContext);
            return;
        }

        _lineRenderer.Render(canvas, chartArea, values, setting, minVal, maxVal, visibleCandles, offset, config.VisibleStartIndex, candles, interval, transform, seriesColor);
    }

    private void RenderCloud(SKCanvas canvas, Rect chartArea, decimal minVal, decimal range,
        IReadOnlyList<decimal?> spanA, IReadOnlyList<decimal?> spanB,
        CoreIndicatorSettings setting, int totalCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData> candles, TimeSpan interval, ICoordinateTransform? transform)
    {
        if (spanA == null || spanB == null) return;

        byte alpha = 50;

        var baseUp = setting.UpColor.A == 0 ? new StockAnalyzer.Core.Models.IndicatorColor(255, 0, 255, 0) : setting.UpColor; // Default Green
        var baseDown = setting.DownColor.A == 0 ? new StockAnalyzer.Core.Models.IndicatorColor(255, 255, 0, 0) : setting.DownColor; // Default Red

        var upColor = baseUp.ToSkColor().WithAlpha(alpha);
        var downColor = baseDown.ToSkColor().WithAlpha(alpha);


        float candleWidth;
        if (transform != null)
        {
            double x0 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0)).X;
            double x1 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp + interval, 0)).X;
            candleWidth = (float)(x1 - x0);
        }
        else
        {
            candleWidth = (float)(chartArea.Width / totalCandles);
        }

        for (int i = 0; i < totalCandles; i++)
        {
            int idx1 = i - offset;
            int idx2 = i + 1 - offset;

            if (idx1 < 0 || idx1 >= spanA.Count || idx1 >= spanB.Count) continue;
            if (idx2 >= spanA.Count || idx2 >= spanB.Count) continue;

            decimal? a1 = spanA[idx1];
            decimal? b1 = spanB[idx1];
            decimal? a2 = spanA[idx2];
            decimal? b2 = spanB[idx2];

            if (!a1.HasValue || !b1.HasValue || !a2.HasValue || !b2.HasValue) continue;

            float x1, x2;
            if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
            {
                x1 = (float)transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, startIndex + i)), 0)).X;
                x2 = (float)transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, startIndex + i + 1)), 0)).X;
            }
            else if (transform != null)
            {
                var t1 = GetCandleTime(i, candles, interval);
                var t2 = GetCandleTime(i + 1, candles, interval);
                x1 = (float)transform.ChartToScreen(new ChartPoint(t1, 0)).X;
                x2 = (float)transform.ChartToScreen(new ChartPoint(t2, 0)).X;
            }
            else
            {
                x1 = (float)chartArea.Left + i * candleWidth;
                x2 = (float)chartArea.Left + (i + 1) * candleWidth;
            }

            float cx1 = x1 + candleWidth / 2f;
            float cx2 = x2 + candleWidth / 2f;

            float ya1 = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, a1.Value)).Y ?? 0) + (double)chartArea.Top);
            float yb1 = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, b1.Value)).Y ?? 0) + (double)chartArea.Top);
            float ya2 = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, a2.Value)).Y ?? 0) + (double)chartArea.Top);
            float yb2 = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, b2.Value)).Y ?? 0) + (double)chartArea.Top);

            bool aTop1 = a1.Value >= b1.Value;
            bool aTop2 = a2.Value >= b2.Value;

            if (aTop1 == aTop2)
            {
                _cloudPath.Reset();
                _cloudPath.MoveTo(cx1, ya1);
                _cloudPath.LineTo(cx2, ya2);
                _cloudPath.LineTo(cx2, yb2);
                _cloudPath.LineTo(cx1, yb1);
                _cloudPath.Close();

                _cloudPaint.Color = aTop1 ? upColor : downColor;
                canvas.DrawPath(_cloudPath, _cloudPaint);
            }
            else
            {
                double diff1 = (double)(a1.Value - b1.Value);
                double diff2 = (double)(a2.Value - b2.Value);
                double denominator = diff1 - diff2;
                if (Math.Abs(denominator) < 1e-9) continue;

                double t = diff1 / denominator;

                float cxMid = cx1 + (float)((cx2 - cx1) * t);
                float yaMid = ya1 + (float)((ya2 - ya1) * t);

                _cloudPath.Reset();
                _cloudPath.MoveTo(cx1, ya1);
                _cloudPath.LineTo(cxMid, yaMid);
                _cloudPath.LineTo(cx1, yb1);
                _cloudPath.Close();
                _cloudPaint.Color = aTop1 ? upColor : downColor;
                canvas.DrawPath(_cloudPath, _cloudPaint);

                _cloudPath2.Reset();
                _cloudPath2.MoveTo(cxMid, yaMid);
                _cloudPath2.LineTo(cx2, ya2);
                _cloudPath2.LineTo(cx2, yb2);
                _cloudPath2.Close();
                _cloudPaint.Color = aTop2 ? upColor : downColor;
                canvas.DrawPath(_cloudPath2, _cloudPaint);
            }
        }
    }

    private DateTime GetCandleTime(int index, IReadOnlyList<CoreCandleData> candles, TimeSpan interval)
    {
        if (candles.Count == 0) return DateTime.MinValue;
        if (index < candles.Count) return candles[index].Timestamp;
        var lastCandle = candles[candles.Count - 1];
        int diff = index - (candles.Count - 1);
        return lastCandle.Timestamp.AddTicks(interval.Ticks * diff);
    }
}
