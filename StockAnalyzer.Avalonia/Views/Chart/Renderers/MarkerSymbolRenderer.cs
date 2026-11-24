using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders discrete geometric markers (Circle, TriangleUp, TriangleDown, Square, Cross, Plus, Diamond)
/// at indicator data points with equal-area diamond scaling and fill/stroke support.
/// </summary>
internal sealed class MarkerSymbolRenderer : ISeriesPrimitiveRenderer
{
    private const float TriangleScaleFactor = 1.2f;

    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _path = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        if (values == null || values.Count == 0 || context.VisibleCandles <= 0) return;

        float baseR = float.IsFinite(desc.MarkerRadiusPx) ? Math.Clamp(desc.MarkerRadiusPx, 1.0f, 32.0f) : 4.0f;
        float r = MathF.Max(1.0f, MathF.Min(baseR, context.ColumnWidth * ChartPrimitiveMath.DotSpacingMarginFactor));
        float yOff = float.IsFinite(desc.MarkerYOffsetPx) ? desc.MarkerYOffsetPx : 0.0f;
        float strokeW = MathF.Max(1.0f, (float)context.Setting.Thickness);
        _strokePaint.StrokeWidth = strokeW;

        float leftClip = (float)context.ChartArea.Left;
        float rightClip = (float)context.ChartArea.Right;
        float topClip = (float)context.ChartArea.Top;
        float botClip = (float)context.ChartArea.Bottom;

        bool twoColors = context.Setting.UseUpDownColors;
        decimal effectiveBaseline = context.EffectiveBaseline;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;

        bool useFill = desc.MarkerFilled &&
                       desc.MarkerShape != MarkerSymbolShape.Cross &&
                       desc.MarkerShape != MarkerSymbolShape.Plus;
        SKPaint paint = useFill ? _fillPaint : _strokePaint;

        float rDiamond = r * ChartPrimitiveMath.DotDiamondScaleFactor;
        float rTri = r * TriangleScaleFactor;
        float triHalfW = rTri * ChartPrimitiveMath.Cos30;
        float triHalfH = rTri * 0.5f;
        float shapeScale = desc.MarkerShape switch
        {
            MarkerSymbolShape.Diamond => ChartPrimitiveMath.DotDiamondScaleFactor,
            MarkerSymbolShape.TriangleUp or MarkerSymbolShape.TriangleDown => TriangleScaleFactor,
            _ => 1.0f
        };
        float cullRadius = r * shapeScale + (useFill ? 0.0f : strokeW * 0.5f);

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= values.Count) continue;

            decimal? val = values[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(val, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float rawY))
            {
                continue;
            }

            float cx = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(cx)) continue;

            float cy = rawY + yOff;
            if (cx + cullRadius < leftClip || cx - cullRadius > rightClip || cy + cullRadius < topClip || cy - cullRadius > botClip)
            {
                continue;
            }

            paint.Color = twoColors
                ? (val!.Value >= effectiveBaseline ? upColor : downColor)
                : singleColor;

            switch (desc.MarkerShape)
            {
                case MarkerSymbolShape.Square:
                    context.Canvas.DrawRect(cx - r, cy - r, r * 2f, r * 2f, paint);
                    break;

                case MarkerSymbolShape.Diamond:
                    _path.Rewind();
                    _path.MoveTo(cx, cy - rDiamond);
                    _path.LineTo(cx + rDiamond, cy);
                    _path.LineTo(cx, cy + rDiamond);
                    _path.LineTo(cx - rDiamond, cy);
                    _path.Close();
                    context.Canvas.DrawPath(_path, paint);
                    break;

                case MarkerSymbolShape.TriangleUp:
                    _path.Rewind();
                    _path.MoveTo(cx, cy - rTri);
                    _path.LineTo(cx - triHalfW, cy + triHalfH);
                    _path.LineTo(cx + triHalfW, cy + triHalfH);
                    _path.Close();
                    context.Canvas.DrawPath(_path, paint);
                    break;

                case MarkerSymbolShape.TriangleDown:
                    _path.Rewind();
                    _path.MoveTo(cx, cy + rTri);
                    _path.LineTo(cx - triHalfW, cy - triHalfH);
                    _path.LineTo(cx + triHalfW, cy - triHalfH);
                    _path.Close();
                    context.Canvas.DrawPath(_path, paint);
                    break;

                case MarkerSymbolShape.Cross:
                    _path.Rewind();
                    _path.MoveTo(cx - r, cy - r);
                    _path.LineTo(cx + r, cy + r);
                    _path.MoveTo(cx - r, cy + r);
                    _path.LineTo(cx + r, cy - r);
                    context.Canvas.DrawPath(_path, paint);
                    break;

                case MarkerSymbolShape.Plus:
                    float sx = ChartPrimitiveMath.SnapPixel(cx);
                    float sy = ChartPrimitiveMath.SnapPixel(cy);
                    _path.Rewind();
                    _path.MoveTo(sx - r, sy);
                    _path.LineTo(sx + r, sy);
                    _path.MoveTo(sx, sy - r);
                    _path.LineTo(sx, sy + r);
                    context.Canvas.DrawPath(_path, paint);
                    break;

                default: // MarkerSymbolShape.Circle
                    context.Canvas.DrawCircle(cx, cy, r, paint);
                    break;
            }
        }
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _path.Dispose();
    }
}
