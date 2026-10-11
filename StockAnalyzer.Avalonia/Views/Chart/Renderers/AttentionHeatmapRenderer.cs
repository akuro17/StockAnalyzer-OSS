using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Draws the attention of the newest prediction as full-height vertical bands behind the grid and the candles (T24b). The value is never a price: the Y extent is the
/// whole chart area and no price transform is involved. Bands are recomputed only when the cache key changes, and rendering does no model, file or aggregation work
/// and no managed allocation (the arrays are preallocated to <see cref="AttentionContract.MaxWindow"/>).
/// </summary>
public sealed class AttentionHeatmapRenderer : IDisposable
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKRect[] _rectangles = new SKRect[AttentionContract.MaxWindow];
    private readonly float[] _levels = new float[AttentionContract.MaxWindow];
    private CacheKey _key;
    private bool _hasKey;
    private int _count;

    private readonly record struct CacheKey(AttentionSnapshot Attention, string Symbol, string Timeframe, int Start, int End,
        double FirstX, double LastX, double ScaleX, Rect Area, SKMatrix Matrix);

    public void Render(SKCanvas canvas, Rect area, ChartDataSnapshot snapshot, ICoordinateTransform transform, ThemeColors theme)
    {
        var attention = snapshot.Attention;
        if (attention is null || snapshot.Candles.Count == 0 || !snapshot.ChartType.IsTimeBased()
            || !attention.Key.Symbol.Equals(snapshot.Symbol, StringComparison.Ordinal)
            || !attention.Key.Timeframe.Equals(snapshot.Timeframe, StringComparison.OrdinalIgnoreCase)) return;
        int bars = attention.Timestamps.Length;
        if (bars < AttentionContract.MinWindow || bars > _rectangles.Length || attention.Relative.Length != bars) return;

        var key = new CacheKey(attention, snapshot.Symbol, snapshot.Timeframe, snapshot.StartIndex, snapshot.EndIndex,
            transform.GetXFromTime(snapshot.Candles[0].Timestamp), transform.GetXFromTime(snapshot.Candles[^1].Timestamp),
            transform.ScaleX, area, canvas.TotalMatrix);
        if (!_hasKey || key != _key)
        {
            Prepare(attention, area, transform);
            _key = key;
            _hasKey = true;
        }

        if (_count == 0) return;
        var heat = theme.AttentionHeat;
        canvas.Save();
        canvas.ClipRect(new SKRect((float)area.Left, (float)area.Top, (float)area.Right, (float)area.Bottom));
        for (int i = 0; i < _count; i++)
        {
            // opacity = base opacity x w / max(w): the base alpha is the most attended bar (S13)
            byte alpha = (byte)Math.Clamp(MathF.Round(heat.A * _levels[i]), 0f, 255f);
            if (alpha == 0) continue;
            _paint.Color = new SKColor(heat.R, heat.G, heat.B, alpha);
            canvas.DrawRect(_rectangles[i], _paint);
        }

        canvas.Restore();
    }

    /// <summary>Band i spans from the midpoint to its previous neighbour to the midpoint to its next neighbour; the two ends use half a bar on their open side.</summary>
    private void Prepare(AttentionSnapshot attention, Rect area, ICoordinateTransform transform)
    {
        _count = 0;
        int bars = attention.Timestamps.Length;
        double previous = double.NaN, current = transform.GetXFromTime(attention.Timestamps[0]) + area.Left;
        for (int i = 0; i < bars; i++)
        {
            double next = i + 1 < bars ? transform.GetXFromTime(attention.Timestamps[i + 1]) + area.Left : double.NaN;
            double left = double.IsNaN(previous) ? double.NaN : (previous + current) / 2.0;
            double right = double.IsNaN(next) ? double.NaN : (current + next) / 2.0;
            if (double.IsNaN(left)) left = current - (right - current);
            if (double.IsNaN(right)) right = current + (current - left);
            float level = attention.Relative[i];
            if (double.IsFinite(left) && double.IsFinite(right) && right > left && float.IsFinite(level) && level > 0f)
            {
                _rectangles[_count] = new SKRect((float)left, (float)area.Top, (float)right, (float)area.Bottom);
                _levels[_count++] = Math.Min(level, 1f);
            }

            previous = current;
            current = next;
        }
    }

    public void Dispose() => _paint.Dispose();
}
