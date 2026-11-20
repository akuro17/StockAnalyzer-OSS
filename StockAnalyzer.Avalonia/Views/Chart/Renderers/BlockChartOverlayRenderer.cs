using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Draws one block chart overlay (Renko, Point and Figure, Kagi or Three Line Break on a time-series chart), bottom to top:
/// the Update dots (Renko / Point and Figure only), the Reversal dots, then the Close step line.
/// The step line has the geometry of <see cref="StepLineRenderer"/> (horizontal to the bar, then vertical); the segment that arrives at bar i is drawn in the up colour when
/// IsUpTrend[i] is at or above <see cref="IndicatorDefaultConstants.BlockChartOverlayUpTrendThreshold"/>, in the down colour otherwise (when <see cref="CoreIndicatorSettings.UseUpDownColors"/>
/// is on; off = the single <see cref="CoreIndicatorSettings.Color"/>). A null value (before the first block, or the single bar of an invalid input bar) breaks the line, so no segment ever
/// spans a gap. Every non-null Reversal / Update value gets one filled dot.
/// The renderer is only called for a time-series chart: on a non-time-series main chart the overlays are neither drawn nor part of the price axis (see <c>IndicatorRenderer</c>).
/// </summary>
public sealed class BlockChartOverlayRenderer : IDisposable
{
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _dotPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPath _path = new();

    public void Dispose()
    {
        _linePaint.Dispose();
        _dotPaint.Dispose();
        _path.Dispose();
    }

    /// <param name="close">The Main (Close) series.</param>
    /// <param name="isUpTrend">The IsUpTrend helper series (null = single colour).</param>
    /// <param name="reversal">The Reversal series (null = none).</param>
    /// <param name="update">The Update series (null for Kagi and Three Line Break, which have none).</param>
    /// <param name="reversalColor">Colour of the Reversal dots.</param>
    /// <param name="updateColor">Colour of the Update dots.</param>
    public void Render(SKCanvas canvas, Rect chartArea,
        IReadOnlyList<decimal?> close, IReadOnlyList<decimal?>? isUpTrend, IReadOnlyList<decimal?>? reversal, IReadOnlyList<decimal?>? update,
        CoreIndicatorSettings setting, SKColor reversalColor, SKColor updateColor,
        int visibleCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData> candles, TimeSpan interval, ICoordinateTransform? transform)
    {
        if (transform == null || candles == null || candles.Count == 0 || visibleCandles <= 0) return;

        float candleWidth = ColumnWidth(chartArea, candles, interval, visibleCandles, transform);
        float radius = DotRadius(setting.DotSize);

        // Z order: Update dots, Reversal dots, Close line.
        DrawDots(canvas, chartArea, update, updateColor, radius, candleWidth, visibleCandles, offset, startIndex, candles, interval, transform);
        DrawDots(canvas, chartArea, reversal, reversalColor, radius, candleWidth, visibleCandles, offset, startIndex, candles, interval, transform);
        DrawStepLine(canvas, chartArea, close, isUpTrend, setting, candleWidth, visibleCandles, offset, startIndex, candles, interval, transform);
    }

    private void DrawDots(SKCanvas canvas, Rect chartArea, IReadOnlyList<decimal?>? values, SKColor color, float radius, float candleWidth,
        int visibleCandles, int offset, int startIndex, IReadOnlyList<CoreCandleData> candles, TimeSpan interval, ICoordinateTransform transform)
    {
        if (values == null || values.Count == 0) return;

        _dotPaint.Color = color;
        for (int i = 0; i < visibleCandles; i++)
        {
            int valueIndex = i - offset;
            if (valueIndex < 0 || valueIndex >= values.Count) continue;

            decimal? value = values[valueIndex];
            if (!value.HasValue) continue;

            canvas.DrawCircle(ColumnCenterX(i, candleWidth, chartArea, startIndex, candles, interval, transform), PriceY(value.Value, chartArea, transform), radius, _dotPaint);
        }
    }

    private void DrawStepLine(SKCanvas canvas, Rect chartArea, IReadOnlyList<decimal?> close, IReadOnlyList<decimal?>? isUpTrend, CoreIndicatorSettings setting,
        float candleWidth, int visibleCandles, int offset, int startIndex, IReadOnlyList<CoreCandleData> candles, TimeSpan interval, ICoordinateTransform transform)
    {
        if (close == null || close.Count == 0) return;

        bool twoColors = setting.UseUpDownColors;
        SKColor upColor = setting.UpColor.ToSkColor();
        SKColor downColor = setting.DownColor.ToSkColor();
        SKColor singleColor = setting.Color.ToSkColor();

        _linePaint.StrokeWidth = (float)setting.Thickness;
        _path.Reset();

        bool hasPrevious = false;
        float prevX = 0f, prevY = 0f;
        SKColor pathColor = singleColor;
        bool pathHasSegment = false;

        for (int i = 0; i < visibleCandles; i++)
        {
            int valueIndex = i - offset;
            if (valueIndex < 0 || valueIndex >= close.Count) continue;

            decimal? value = close[valueIndex];
            if (!value.HasValue)
            {
                // A gap (also the single null bar of an invalid input bar) ends the line: the next value starts a new one.
                Flush(canvas, ref pathHasSegment);
                hasPrevious = false;
                continue;
            }

            float x = ColumnCenterX(i, candleWidth, chartArea, startIndex, candles, interval, transform);
            float y = PriceY(value.Value, chartArea, transform);

            if (hasPrevious)
            {
                SKColor segmentColor = twoColors ? SegmentColor(isUpTrend, valueIndex, upColor, downColor, singleColor) : singleColor;
                if (pathHasSegment && segmentColor != pathColor)
                {
                    Flush(canvas, ref pathHasSegment);
                }

                if (!pathHasSegment)
                {
                    _path.MoveTo(prevX, prevY);
                    pathColor = segmentColor;
                    pathHasSegment = true;
                }

                _path.LineTo(x, prevY);   // horizontal to this bar ...
                _path.LineTo(x, y);       // ... then vertical to its level
            }

            hasPrevious = true;
            prevX = x;
            prevY = y;
        }

        Flush(canvas, ref pathHasSegment);

        void Flush(SKCanvas target, ref bool hasSegment)
        {
            if (!hasSegment) return;
            _linePaint.Color = pathColor;
            target.DrawPath(_path, _linePaint);
            _path.Reset();
            hasSegment = false;
        }
    }

    /// <summary>Radius of a dot from the setting (a diameter), kept inside the range of the setting so that a hand-edited or non-finite value cannot give an invisible or huge dot.</summary>
    internal static float DotRadius(double dotSize)
    {
        double diameter = double.IsFinite(dotSize) ? dotSize : IndicatorDefaultConstants.BlockChartOverlayDefaultDotSize;
        diameter = Math.Clamp(diameter, IndicatorDefaultConstants.BlockChartOverlayMinDotSize, IndicatorDefaultConstants.BlockChartOverlayMaxDotSize);
        return (float)(diameter / 2.0);
    }

    /// <summary>The colour of the segment that arrives at the bar: IsUpTrend at or above the threshold = up. A missing flag falls back to the single colour.</summary>
    private static SKColor SegmentColor(IReadOnlyList<decimal?>? isUpTrend, int valueIndex, SKColor up, SKColor down, SKColor fallback)
    {
        if (isUpTrend == null || valueIndex >= isUpTrend.Count) return fallback;
        decimal? flag = isUpTrend[valueIndex];
        if (!flag.HasValue) return fallback;
        return flag.Value >= IndicatorDefaultConstants.BlockChartOverlayUpTrendThreshold ? up : down;
    }

    /// <summary>Width of one bar column in pixels (the distance of the first bar and the same instant one interval later, like the other indicator renderers).</summary>
    internal static float ColumnWidth(Rect chartArea, IReadOnlyList<CoreCandleData> candles, TimeSpan interval, int visibleCandles, ICoordinateTransform transform)
    {
        if (candles.Count > 0)
        {
            double x0 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0)).X;
            double x1 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp + interval, 0)).X;
            return (float)(x1 - x0);
        }

        return visibleCandles > 0 ? (float)(chartArea.Width / visibleCandles) : 0f;
    }

    /// <summary>
    /// The one X helper of this renderer: the screen X of the centre of bar i of the visible slice. A bar past the last candle (future projection) is extrapolated by whole intervals.
    /// The price coordinate is converted by <see cref="PriceY"/> only; screen and data coordinates are never mixed.
    /// </summary>
    internal static float ColumnCenterX(int i, float candleWidth, Rect chartArea, int startIndex, IReadOnlyList<CoreCandleData> candles, TimeSpan interval, ICoordinateTransform transform)
    {
        double x;
        if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
        {
            x = transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, startIndex + i)), 0)).X;
        }
        else
        {
            DateTime t = i < candles.Count
                ? candles[i].Timestamp
                : candles[candles.Count - 1].Timestamp.AddTicks(interval.Ticks * (i - (candles.Count - 1)));
            x = transform.ChartToScreen(new ChartPoint(t, 0)).X;
        }

        return (float)x + candleWidth / 2f;
    }

    internal static float PriceY(decimal price, Rect chartArea, ICoordinateTransform transform)
        => (float)(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, price)).Y + chartArea.Top);
}
