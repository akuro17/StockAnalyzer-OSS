using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>Reusable chart bands; render performs no artifact/model work or managed allocation.</summary>
public sealed class TrainingPeriodRenderer : IDisposable
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKRect[] _rectangles = new SKRect[ModelAnalysisContract.RegimeCount];
    private readonly int[] _kinds = new int[ModelAnalysisContract.RegimeCount];
    private CacheKey _key;
    private bool _hasKey;
    private int _count;
    private readonly record struct CacheKey(ModelAnalysisSnapshot Analysis, string Symbol, string Timeframe,
        int Start, int End, double FirstX, double LastX, double ScaleX, Rect Area, SKMatrix Matrix,
        ThemeColors Theme, PriceScaleType Scale);

    public void Render(SKCanvas canvas, Rect area, ChartDataSnapshot snapshot, ICoordinateTransform transform, ThemeColors theme)
    {
        var analysis = snapshot.ModelAnalysis;
        if (analysis is null || snapshot.Candles.Count == 0 || snapshot.ChartType == ChartType.ReverseWatch
            || !analysis.Analysis.Timeframe.Equals(snapshot.Timeframe, StringComparison.OrdinalIgnoreCase)) return;
        var key = new CacheKey(analysis, snapshot.Symbol, snapshot.Timeframe, snapshot.StartIndex, snapshot.EndIndex,
            transform.GetXFromTime(snapshot.Candles[0].Timestamp), transform.GetXFromTime(snapshot.Candles[^1].Timestamp),
            transform.ScaleX, area, canvas.TotalMatrix, theme, snapshot.PriceScale);
        if (!_hasKey || key != _key)
        {
            _count = 0;
            var periods = analysis.Analysis.Periods;
            for (int i = 0; i < periods.Length && _count < _rectangles.Length; i++)
            {
                var period = periods[i];
                if (period.Symbol != snapshot.Symbol) continue;
                double left = transform.GetXFromTime(period.AnchorStart) + area.Left;
                double right = transform.GetXFromTime(period.AnchorEnd) + area.Left;
                if (!double.IsFinite(left) || !double.IsFinite(right) || right <= left) continue;
                _rectangles[_count] = new SKRect((float)left, (float)area.Top, (float)right, (float)area.Bottom);
                _kinds[_count++] = period.Kind == "train" ? 0 : period.Kind == "validation" ? 1 : 2;
            }
            _key = key; _hasKey = true;
        }
        canvas.Save();
        canvas.ClipRect(new SKRect((float)area.Left, (float)area.Top, (float)area.Right, (float)area.Bottom));
        for (int i = 0; i < _count; i++)
        {
            var color = _kinds[i] == 0 ? theme.TrainingPeriodTrain : _kinds[i] == 1 ? theme.TrainingPeriodValidation : theme.TrainingPeriodOos;
            _paint.Color = new SKColor(color.R, color.G, color.B, color.A);
            canvas.DrawRect(_rectangles[i], _paint);
        }
        canvas.Restore();
    }

    public void Dispose() => _paint.Dispose();
}
