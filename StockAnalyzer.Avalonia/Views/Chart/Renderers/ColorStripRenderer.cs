using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders a time-series color/heatmap strip anchored to Bottom, Top, or FullPanel
/// using seam-free floor bounds and <see cref="OklabColor"/> / <see cref="HeatmapColorMapper"/> SSoT.
/// </summary>
internal sealed class ColorStripRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        int n = context.VisibleCandles;
        if (values == null || values.Count == 0 || n <= 0 || desc.StripAlpha == 0) return;

        float panelHeight = MathF.Max(4.0f, (float)context.ChartArea.Height);
        float stripH = float.IsFinite(desc.StripHeightPx) ? Math.Clamp(desc.StripHeightPx, 4.0f, panelHeight) : 12.0f;

        float top;
        float bottom;
        switch (desc.StripAnchor)
        {
            case ColorStripAnchor.Top:
                top = (float)context.ChartArea.Top;
                bottom = top + stripH;
                break;

            case ColorStripAnchor.FullPanel:
                top = (float)context.ChartArea.Top;
                bottom = (float)context.ChartArea.Bottom;
                break;

            default: // ColorStripAnchor.Bottom
                bottom = (float)context.ChartArea.Bottom;
                top = bottom - stripH;
                break;
        }

        // Resolve domain bounds for Sequential / Diverging normalization (fixed domain skips Pass 1 scan)
        decimal vMin = decimal.MaxValue;
        decimal vMax = decimal.MinValue;
        if (desc.StripScaleMode != ColorStripScaleMode.DiscreteState)
        {
            if (ChartPrimitiveMath.TryGetFixedScaleDomain(desc, out decimal domMin, out decimal domMax))
            {
                vMin = domMin;
                vMax = domMax;
            }
            else
            {
                bool hasAny = false;
                for (int i = 0; i < n; i++)
                {
                    int valIdx = i - context.Offset;
                    if (valIdx < 0 || valIdx >= values.Count) continue;
                    decimal? v = values[valIdx];
                    if (!v.HasValue) continue;
                    if (v.Value < vMin) vMin = v.Value;
                    if (v.Value > vMax) vMax = v.Value;
                    hasAny = true;
                }
                if (!hasAny) return;
            }
        }

        IndicatorColor neutralColor = ChartPrimitiveMath.FindSeriesColor(context.Setting.SeriesColors, "Neutral", matchByNameFallback: true)?.Color
            ?? context.Theme.Neutral;
        IndicatorColor upPeakColor = ChartPrimitiveMath.FindSeriesColor(context.Setting.SeriesColors, "Up", matchByNameFallback: true)?.Color
            ?? (context.Setting.UseUpDownColors && context.Setting.UpColor.A > 0 ? context.Setting.UpColor : context.Theme.Bullish);
        IndicatorColor downPeakColor = ChartPrimitiveMath.FindSeriesColor(context.Setting.SeriesColors, "Down", matchByNameFallback: true)?.Color
            ?? (context.Setting.UseUpDownColors && context.Setting.DownColor.A > 0 ? context.Setting.DownColor : context.Theme.Bearish);

        IndicatorColor seqStartColor = ChartPrimitiveMath.FindSeriesColor(context.Setting.SeriesColors, "Start", matchByNameFallback: true)?.Color
            ?? context.Theme.ChartBackground;
        IndicatorColor seqPeakColor = context.Setting.Color;
        OklabColor seqStartLab = OklabColor.FromIndicatorColor(seqStartColor);
        OklabColor seqPeakLab = OklabColor.FromIndicatorColor(seqPeakColor);

        decimal effectiveBaseline = context.EffectiveBaseline;
        decimal seqRange = vMax - vMin;
        decimal divMaxAbs = Math.Max(Math.Abs(vMax - effectiveBaseline), Math.Abs(vMin - effectiveBaseline));

        for (int i = 0; i < n; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= values.Count) continue;
            decimal? valOpt = values[valIdx];
            if (!valOpt.HasValue) continue;

            float xCurr = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(xCurr)) continue;

            float xPrev = i > 0
                ? ChartPrimitiveMath.ComputeBarCenterX(
                    i - 1, context.ColumnWidth, context.ChartArea, context.StartIndex,
                    context.Candles, context.Interval, context.Transform,
                    context.Snapshot, context.Config, context.XOffset)
                : xCurr - context.ColumnWidth;
            if (!float.IsFinite(xPrev)) xPrev = xCurr - context.ColumnWidth;

            float xNext = i + 1 < n
                ? ChartPrimitiveMath.ComputeBarCenterX(
                    i + 1, context.ColumnWidth, context.ChartArea, context.StartIndex,
                    context.Candles, context.Interval, context.Transform,
                    context.Snapshot, context.Config, context.XOffset)
                : xCurr + context.ColumnWidth;
            if (!float.IsFinite(xNext)) xNext = xCurr + context.ColumnWidth;

            float left = MathF.Floor((xPrev + xCurr) * 0.5f);
            float right = MathF.Floor((xCurr + xNext) * 0.5f);
            if (right <= left) right = left + 1.0f;

            IndicatorColor cellColor;
            decimal v = valOpt.Value;
            switch (desc.StripScaleMode)
            {
                case ColorStripScaleMode.Sequential:
                    double tSeq = seqRange > 0m
                        ? Math.Clamp((double)(v - vMin) / (double)seqRange, 0.0, 1.0)
                        : 0.5;
                    cellColor = OklabColor.Lerp(seqStartLab, seqPeakLab, tSeq).ToIndicatorColorInGamut(desc.StripAlpha);
                    break;

                case ColorStripScaleMode.DiscreteState:
                    double roundedState = Math.Round((double)v, MidpointRounding.AwayFromZero);
                    if (!double.IsFinite(roundedState)) continue;
                    long clampedState = (long)Math.Clamp(roundedState, -1_000_000_000.0, 1_000_000_000.0);
                    long absState = clampedState < 0 ? -clampedState : clampedState;
                    var seriesColors = context.Setting.SeriesColors;
                    cellColor = seriesColors != null && seriesColors.Count > 0
                        ? seriesColors[(int)(absState % seriesColors.Count)].Color
                        : ChartColorPalette.Get((int)(absState % ChartColorPalette.PaletteSize), context.Theme.IsDark);
                    cellColor = cellColor.WithAlpha(desc.StripAlpha);
                    break;

                default: // ColorStripScaleMode.Diverging
                    if (divMaxAbs <= 0m)
                    {
                        cellColor = neutralColor.WithAlpha(desc.StripAlpha);
                    }
                    else
                    {
                        double rSigned = Math.Clamp((double)(v - effectiveBaseline) / (double)divMaxAbs, -1.0, 1.0);
                        var sample = HeatmapColorMapper.Classify(rSigned, 1.0, desc.StripQuantizationSteps);
                        cellColor = sample.Kind switch
                        {
                            HeatmapColorKind.Up => HeatmapColorRamp.InterpolateStep(neutralColor, upPeakColor, sample.Factor),
                            HeatmapColorKind.Down => HeatmapColorRamp.InterpolateStep(neutralColor, downPeakColor, sample.Factor),
                            _ => neutralColor
                        };
                        cellColor = cellColor.WithAlpha(desc.StripAlpha);
                    }
                    break;
            }

            _paint.Color = cellColor.ToSkColor();
            context.Canvas.DrawRect(new SKRect(left, top, right, bottom), _paint);
        }
    }

    public void Dispose()
    {
        _paint.Dispose();
    }
}
