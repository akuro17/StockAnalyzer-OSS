using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders scroll-invariant waterfall charts from pre-computed full-history
/// <c>PrimarySeriesName</c> (RunningEnd) and <c>SecondarySeriesName</c> (RunningStart),
/// with optional <see cref="WaterfallBarKind"/> styling and dashed horizontal connector lines.
/// </summary>
internal sealed class WaterfallRenderer : ISeriesPrimitiveRenderer
{
    private const byte ConnectorAlpha = 160;

    private readonly SKPaint _barPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKPaint _connectorPaint;
    private readonly SKPathEffect _connectorDashEffect;

    internal WaterfallRenderer()
    {
        _connectorDashEffect = SKPathEffect.CreateDash(new float[] { ChartTheme.GridDashOn, ChartTheme.GridDashOff }, 0f);
        _connectorPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f,
            IsAntialias = false,
            PathEffect = _connectorDashEffect
        };
    }

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        int n = context.VisibleCandles;
        if (string.IsNullOrEmpty(desc.SecondarySeriesName) || n <= 0) return;

        var endSeries = context.Result.GetSeries(desc.PrimarySeriesName);
        var startSeries = context.Result.GetSeries(desc.SecondarySeriesName);
        if (endSeries == null || startSeries == null || endSeries.Count == 0 || startSeries.Count == 0) return;

        IReadOnlyList<decimal?>? kindSeries = !string.IsNullOrEmpty(desc.TertiarySeriesName)
            ? context.Result.GetSeries(desc.TertiarySeriesName)
            : null;

        float ratio = float.IsFinite(desc.SegmentWidthRatio) ? Math.Clamp(desc.SegmentWidthRatio, 0.1f, 1.0f) : 0.8f;
        float barW = MathF.Max(1.0f, context.ColumnWidth * ratio);
        float halfW = barW * 0.5f;

        SKColor totalColor = context.SeriesColor;
        SKColor upColor = (context.Setting.UseUpDownColors ? context.Setting.UpColor : context.Theme.Bullish).ToSkColor();
        SKColor downColor = (context.Setting.UseUpDownColors ? context.Setting.DownColor : context.Theme.Bearish).ToSkColor();
        SKColor neutralColor = context.Theme.Neutral.ToSkColor();

        // Pass 1 (Z-order: behind bars): horizontal dashed connector lines
        if (desc.WaterfallDrawConnectors && n > 1)
        {
            _connectorPaint.Color = context.Theme.AxisText.WithAlpha(ConnectorAlpha).ToSkColor();

            for (int i = 0; i < n - 1; i++)
            {
                int idx1 = i - context.Offset;
                int idx2 = i + 1 - context.Offset;
                if (idx1 < 0 || idx1 >= endSeries.Count || idx1 >= startSeries.Count) continue;
                if (idx2 < 0 || idx2 >= endSeries.Count || idx2 >= startSeries.Count) continue;

                if (!ChartPrimitiveMath.TryProjectY(endSeries[idx1], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float rawEndY)) continue;
                if (!ChartPrimitiveMath.TryProjectY(startSeries[idx1], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out _) ||
                    !ChartPrimitiveMath.TryProjectY(endSeries[idx2], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out _) ||
                    !ChartPrimitiveMath.TryProjectY(startSeries[idx2], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out _)) continue;

                float cx1 = ChartPrimitiveMath.ComputeBarCenterX(
                    i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                    context.Candles, context.Interval, context.Transform,
                    context.Snapshot, context.Config, context.XOffset);
                float cx2 = ChartPrimitiveMath.ComputeBarCenterX(
                    i + 1, context.ColumnWidth, context.ChartArea, context.StartIndex,
                    context.Candles, context.Interval, context.Transform,
                    context.Snapshot, context.Config, context.XOffset);
                if (!float.IsFinite(cx1) || !float.IsFinite(cx2)) continue;

                float yConn = ChartPrimitiveMath.SnapPixel(rawEndY);
                float xFrom = cx1 + halfW;
                float xTo = cx2 - halfW;
                if (xTo > xFrom)
                {
                    context.Canvas.DrawLine(xFrom, yConn, xTo, yConn, _connectorPaint);
                }
            }
        }

        // Pass 2: floating / subtotal / total waterfall bars
        for (int i = 0; i < n; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= endSeries.Count || valIdx >= startSeries.Count) continue;

            decimal? startOpt = startSeries[valIdx];
            decimal? endOpt = endSeries[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(startOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yStart) ||
                !ChartPrimitiveMath.TryProjectY(endOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yEnd))
            {
                continue;
            }

            float cx = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(cx)) continue;

            WaterfallBarKind kind = WaterfallBarKind.Delta;
            if (kindSeries != null && valIdx < kindSeries.Count && kindSeries[valIdx].HasValue)
            {
                decimal roundedKind = decimal.Round(kindSeries[valIdx]!.Value, 0, MidpointRounding.AwayFromZero);
                if (roundedKind == (decimal)WaterfallBarKind.Subtotal) kind = WaterfallBarKind.Subtotal;
                else if (roundedKind == (decimal)WaterfallBarKind.Total) kind = WaterfallBarKind.Total;
            }

            if (kind != WaterfallBarKind.Delta)
            {
                _barPaint.Color = totalColor;
            }
            else if (endOpt!.Value > startOpt!.Value)
            {
                _barPaint.Color = upColor;
            }
            else if (endOpt.Value < startOpt.Value)
            {
                _barPaint.Color = downColor;
            }
            else
            {
                _barPaint.Color = neutralColor;
            }

            SKRect rect = ChartPrimitiveMath.NormalizeRectWithMinHeight(cx - halfW, barW, yStart, yEnd, 1.0f);
            context.Canvas.DrawRect(rect, _barPaint);
        }
    }

    public void Dispose()
    {
        _barPaint.Dispose();
        _connectorPaint.Dispose();
        _connectorDashEffect.Dispose();
    }
}
