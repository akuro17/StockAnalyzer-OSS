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
    /// <summary>Highest signal strength per side: signals are +1..+4 (buy) and -1..-4 (sell).</summary>
    private const int SignalStrengthCount = 4;

    /// <summary>Color slots: indices 0..3 are Buy1..Buy4, indices 4..7 are Sell1..Sell4.</summary>
    private const int SignalSlotCount = SignalStrengthCount * 2;

    /// <summary>Default slot colors, built once from <see cref="IndicatorDefaultConstants"/> (same values as before).</summary>
    private static readonly SKColor[] DefaultSlotColors =
    {
        ToSkColor(IndicatorDefaultConstants.GranvilleBuy1Color),
        ToSkColor(IndicatorDefaultConstants.GranvilleBuy2Color),
        ToSkColor(IndicatorDefaultConstants.GranvilleBuy3Color),
        ToSkColor(IndicatorDefaultConstants.GranvilleBuy4Color),
        ToSkColor(IndicatorDefaultConstants.GranvilleSell1Color),
        ToSkColor(IndicatorDefaultConstants.GranvilleSell2Color),
        ToSkColor(IndicatorDefaultConstants.GranvilleSell3Color),
        ToSkColor(IndicatorDefaultConstants.GranvilleSell4Color),
    };

    private static SKColor ToSkColor(IndicatorColor c) => new SKColor(c.R, c.G, c.B, c.A);

    /// <summary>Slot of a signal in -4..-1 and 1..4 (the caller guarantees the range).</summary>
    private static int SlotOf(int signal) => signal > 0 ? signal - 1 : SignalStrengthCount + (-signal - 1);

    /// <summary>Resolves the color slot of a signal; false for 0 and for strengths outside 1..4 (nothing is drawn, as before).</summary>
    internal static bool TrySlotOf(int signal, out int slot)
    {
        if (signal >= 1 && signal <= SignalStrengthCount || signal <= -1 && signal >= -SignalStrengthCount)
        {
            slot = SlotOf(signal);
            return true;
        }

        slot = 0;
        return false;
    }

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

        Span<SKColor> colors = stackalloc SKColor[SignalSlotCount];
        DefaultSlotColors.CopyTo(colors);

        if (setting.SeriesColors != null && setting.SeriesColors.Count > 0)
        {
            foreach (var sc in setting.SeriesColors)
            {
                if (sc.Name == "Buy1") colors[SlotOf(1)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Buy2") colors[SlotOf(2)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Buy3") colors[SlotOf(3)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Buy4") colors[SlotOf(4)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell1") colors[SlotOf(-1)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell2") colors[SlotOf(-2)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell3") colors[SlotOf(-3)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                else if (sc.Name == "Sell4") colors[SlotOf(-4)] = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
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

            if (TrySlotOf(signalVal, out int slot))
            {
                var color = colors[slot];
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
