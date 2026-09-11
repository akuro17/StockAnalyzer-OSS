using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders Granville's Law signals: 
/// - Main Chart Labels (B1-B4, S1-S4) above/below candles.
/// - Sub-window Heatmap Bar at the bottom of the indicator's panel.
/// </summary>
public sealed class GranvilleLawRenderer
{
    private readonly SKPaint _buyLabelPaint;
    private readonly SKPaint _sellLabelPaint;
    private readonly SKPaint _buyHeatmapPaint;
    private readonly SKPaint _sellHeatmapPaint;

    public GranvilleLawRenderer()
    {
        _buyLabelPaint = new SKPaint
        {
            Color = new SKColor(IndicatorDefaultConstants.GranvilleBuy1Color.R, IndicatorDefaultConstants.GranvilleBuy1Color.G, IndicatorDefaultConstants.GranvilleBuy1Color.B, IndicatorDefaultConstants.GranvilleBuy1Color.A),
            IsAntialias = true,
            TextSize = 12,
            FakeBoldText = true,
            TextAlign = SKTextAlign.Center
        };

        _sellLabelPaint = new SKPaint
        {
            Color = new SKColor(IndicatorDefaultConstants.GranvilleSell1Color.R, IndicatorDefaultConstants.GranvilleSell1Color.G, IndicatorDefaultConstants.GranvilleSell1Color.B, IndicatorDefaultConstants.GranvilleSell1Color.A),
            IsAntialias = true,
            TextSize = 12,
            FakeBoldText = true,
            TextAlign = SKTextAlign.Center
        };

        // Heatmap uses same colors but we might want them semi-transparent,
        // however AppTheme colors usually have some alpha already.
        _buyHeatmapPaint = new SKPaint { Color = new SKColor(IndicatorDefaultConstants.GranvilleBuy1Color.R, IndicatorDefaultConstants.GranvilleBuy1Color.G, IndicatorDefaultConstants.GranvilleBuy1Color.B, IndicatorDefaultConstants.GranvilleBuy1Color.A), IsAntialias = false };
        _sellHeatmapPaint = new SKPaint { Color = new SKColor(IndicatorDefaultConstants.GranvilleSell1Color.R, IndicatorDefaultConstants.GranvilleSell1Color.G, IndicatorDefaultConstants.GranvilleSell1Color.B, IndicatorDefaultConstants.GranvilleSell1Color.A), IsAntialias = false };
    }

    public void Render(
        SKCanvas canvas,
        Rect targetRect,
        CoreIndicatorSettings setting,
        ChartDataSnapshot snapshot,
        ICoordinateTransform transform,
        int visibleCandles,
        TimeSpan interval,
        bool isSubWindowContext = false)
    {
        if (snapshot.IndicatorResults == null || !snapshot.IndicatorResults.TryGetValue(setting.Id, out var result))
        {
            return;
        }

        var param = setting.ParameterObject as CoreGranvilleLawParameter;
        if (param == null) return;

        if (!isSubWindowContext) return;

        var signals = result.GetSeries("Signals");
        if (signals == null) return;

        var colors = new Dictionary<int, SKColor>
        {
            { 1, new SKColor(IndicatorDefaultConstants.GranvilleBuy1Color.R, IndicatorDefaultConstants.GranvilleBuy1Color.G, IndicatorDefaultConstants.GranvilleBuy1Color.B, IndicatorDefaultConstants.GranvilleBuy1Color.A) },
            { 2, new SKColor(IndicatorDefaultConstants.GranvilleBuy2Color.R, IndicatorDefaultConstants.GranvilleBuy2Color.G, IndicatorDefaultConstants.GranvilleBuy2Color.B, IndicatorDefaultConstants.GranvilleBuy2Color.A) },
            { 3, new SKColor(IndicatorDefaultConstants.GranvilleBuy3Color.R, IndicatorDefaultConstants.GranvilleBuy3Color.G, IndicatorDefaultConstants.GranvilleBuy3Color.B, IndicatorDefaultConstants.GranvilleBuy3Color.A) },
            { 4, new SKColor(IndicatorDefaultConstants.GranvilleBuy4Color.R, IndicatorDefaultConstants.GranvilleBuy4Color.G, IndicatorDefaultConstants.GranvilleBuy4Color.B, IndicatorDefaultConstants.GranvilleBuy4Color.A) },
            { -1, new SKColor(IndicatorDefaultConstants.GranvilleSell1Color.R, IndicatorDefaultConstants.GranvilleSell1Color.G, IndicatorDefaultConstants.GranvilleSell1Color.B, IndicatorDefaultConstants.GranvilleSell1Color.A) },
            { -2, new SKColor(IndicatorDefaultConstants.GranvilleSell2Color.R, IndicatorDefaultConstants.GranvilleSell2Color.G, IndicatorDefaultConstants.GranvilleSell2Color.B, IndicatorDefaultConstants.GranvilleSell2Color.A) },
            { -3, new SKColor(IndicatorDefaultConstants.GranvilleSell3Color.R, IndicatorDefaultConstants.GranvilleSell3Color.G, IndicatorDefaultConstants.GranvilleSell3Color.B, IndicatorDefaultConstants.GranvilleSell3Color.A) },
            { -4, new SKColor(IndicatorDefaultConstants.GranvilleSell4Color.R, IndicatorDefaultConstants.GranvilleSell4Color.G, IndicatorDefaultConstants.GranvilleSell4Color.B, IndicatorDefaultConstants.GranvilleSell4Color.A) }
        };
        
        if (setting.SeriesColors != null && setting.SeriesColors.Count > 0)
        {
            foreach (var sc in setting.SeriesColors)
            {
                if (sc.Name == "Buy1") colors[1] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Buy2") colors[2] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Buy3") colors[3] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Buy4") colors[4] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell1") colors[-1] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell2") colors[-2] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell3") colors[-3] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell4") colors[-4] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
            }
        }

        decimal minVal = setting.MinValue ?? IndicatorDefaultConstants.GranvilleHistogramMin;
        decimal maxVal = setting.MaxValue ?? IndicatorDefaultConstants.GranvilleHistogramMax;
        decimal range = maxVal - minVal;
        if (range == 0) range = 1m;

        float zeroY = (float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, 0m)).Y + (float)targetRect.Top;

        float candleWidth = 1f;
        if (snapshot.Candles.Count > 1)
        {
            float x0 = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
            float x1 = (float)transform.ChartToScreen(new ChartPoint(snapshot.Candles[1].Timestamp, 0)).X;
            candleWidth = System.Math.Max(1f, x1 - x0);
        }
        
        // Add 0.5f to ensure no 1px gaps between adjacent heatmap bars
        float dx = candleWidth + 0.5f;

        for (int i = 0; i < visibleCandles; i++)
        {
            // Both Candles and indicator series (buySignals/sellSignals) are sliced in ChartDataSnapshot.
            // Therefore, both should be accessed using 'i' (0-indexed).
            if (i >= signals.Count || i >= snapshot.Candles.Count) break;

            var candle = snapshot.Candles[i];
            var sig = signals[i];
            int signalVal = sig.HasValue ? (int)sig.Value : 0;

            if (signalVal != 0 && colors.TryGetValue(signalVal, out var color))
            {
                float x;
                if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
                {
                    x = (float)transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, snapshot.StartIndex + i)), 0)).X;
                }
                else
                {
                    x = (float)transform.ChartToScreen(new ChartPoint(candle.Timestamp, 0)).X;
                }
                
                // Align exactly with CandleStickRenderer
                float snappedCenterX = (float)Math.Floor(x) + 0.5f;

                _buyHeatmapPaint.Color = color;
                _buyHeatmapPaint.StrokeWidth = dx;
                _buyHeatmapPaint.Style = SKPaintStyle.Stroke;
                
                float y1 = (float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)signalVal)).Y + (float)targetRect.Top;
                canvas.DrawLine(snappedCenterX, zeroY, snappedCenterX, y1, _buyHeatmapPaint);
            }
        }
    }
}
