using System;
using System.Linq;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the Header (Symbol, Timeframe, OHLCV) and Indicator Legends.
/// </summary>
public sealed class HeaderOverlayRenderer : IDisposable
{
    private readonly SKPaint _textPaint;
    private readonly SKPaint _labelPaint;
    private readonly SKPaint _backgroundPaint;
    private readonly SKPaint _errorPaint;
    private readonly SKPaint _scorePaint;
    private readonly SKPaint _indicatorColorBoxPaint;

    // Cache for strings to avoid per-frame allocations
    private long _lastOhlcvCandleTicks = -1;
    private string _ohlcvCache = string.Empty;
    private decimal _lastConfluenceScore = -1;
    private string _confluenceCache = string.Empty;
    private readonly System.Collections.Generic.Dictionary<string, (decimal Value, string Name, string Text)> _indicatorCache = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _errorCache = new();

    /// <summary>
    /// Determines if the header should be rendered for the given chart type.
    /// Renko, P&F, Kagi, and ReverseWatch manage their own headers or don't use standard OHLCV.
    /// </summary>
    public static bool ShouldRender(ChartType chartType)
    {
        return StockAnalyzer.Core.Models.ChartTypeExtensions.HasStandardHeader(chartType);
    }

    public HeaderOverlayRenderer()
    {
        _textPaint = new SKPaint
        {
            TextSize = 14,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        _labelPaint = new SKPaint
        {
            TextSize = 14,
            IsAntialias = true
        };

        _backgroundPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill
        };

        _errorPaint = new SKPaint
        {
            Color = SKColors.Red,
            TextSize = 14,
            IsAntialias = true
        };

        _scorePaint = new SKPaint
        {
            TextSize = 14,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        _indicatorColorBoxPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill
        };
    }

    public void Render(SKCanvas canvas, global::Avalonia.Rect chartArea, ChartDataSnapshot snapshot, global::Avalonia.Point mousePosition, bool isMouseOver, IChartRenderConfig config)
    {
        if (snapshot.Candles.Count == 0) return;

        var theme = config.ThemeManager.CurrentTheme;
        _textPaint.Color = theme.AxisText.ToSkColor();
        _labelPaint.Color = theme.AxisText.ToSkColor().WithAlpha(180);
        _backgroundPaint.Color = theme.HeaderBackground.ToSkColor();

        // Determine which candle to show
        int index = -1;
        CoreCandleData? candle = null;

        if (isMouseOver)
        {
            index = (int)Math.Floor((mousePosition.X - chartArea.X) / (chartArea.Width / snapshot.Candles.Count));
            if (index >= 0 && index < snapshot.Candles.Count)
            {
                candle = snapshot.Candles[index];
            }
        }

        // Default to last candle if no mouse hover
        if (candle == null)
        {
            index = snapshot.Candles.Count - 1;
            candle = snapshot.Candles[index];
        }

        // Define Start Position (In Top Margin)
        // User requested:
        // 1. Hide Title "TEST (Daily)"
        // 2. Position OHLC 1 line down (Where title was + 1 line)
        // 3. Font size 14 (Same as title)
        
        // Previous Title Y was (Top - 25). 1 line down (height ~20) is (Top - 5).
        // Let's set it to chartArea.Top - 5 to be close to the grid but legally in margin.
        // Assuming standard margin is 35. Top-5 = 30.
        // Also handling safe fallback for 0-margin charts.
        float y = (float)chartArea.Top - 5; 
        if (y < 20) y = 20; // Ensure minimal visibility if margin is 0

        float x = (float)chartArea.Left + 10;

        // Draw Symbol & Timeframe (Hidden per request)
        // string title = $"{snapshot.Symbol} ({snapshot.Timeframe})";
        // canvas.DrawText(title, x, y, _textPaint);
        
        // Draw OHLCV
        // Cache OHLCV string to avoid per-frame allocation
        if (_lastOhlcvCandleTicks != candle.Timestamp.Ticks)
        {
            _ohlcvCache = $"O: {candle.Open:F2}  H: {candle.High:F2}  L: {candle.Low:F2}  C: {candle.Close:F2}  V: {candle.Volume:N0}";
            
            // Show percentage change for Relative Performance
            if (config.ChartType == ChartType.RelativePerformance && snapshot.Candles.Count > 0)
            {
                decimal basePrice = snapshot.Candles[0].Close;
                if (basePrice != 0)
                {
                    decimal pct = (candle.Close - basePrice) / basePrice * 100m;
                    _ohlcvCache += $"  ({pct:+0.00;-0.00;0.00}%)";
                }
            }
            _lastOhlcvCandleTicks = candle.Timestamp.Ticks;
        }
        
        canvas.DrawText(_ohlcvCache, x, y, _labelPaint);
        
        // Draw Confluence Score (Step UI-2)
        if (snapshot.Confluence.HasValue)
        {
            var conf = snapshot.Confluence.Value;
            float scoreX = x + _labelPaint.MeasureText(_ohlcvCache) + 30; // 30px spacing

            // Use Theme colors for consistency
            string arrow = conf.Score >= 50 ? "▲" : "▼";
            _scorePaint.Color = conf.Score >= 50 ? theme.Bullish.ToSkColor() : theme.Bearish.ToSkColor();
            
            if (_lastConfluenceScore != conf.Score)
            {
                _confluenceCache = $"Confluence: {arrow} {conf.Score:F0}%";
                _lastConfluenceScore = conf.Score;
            }
            canvas.DrawText(_confluenceCache, scoreX, y, _scorePaint);
        }

        // Move down for Indicators
        y += 20;

        // Draw Indicator Legends
        if (snapshot.IndicatorSettings != null && snapshot.IndicatorValues != null)
        {
            foreach (var setting in snapshot.IndicatorSettings)
            {
                if (!setting.IsEnabled) continue;

                // Handle IndicatorResults for errors or multi-series
                string indicatorName = string.IsNullOrEmpty(setting.ShortDisplayName) ? setting.DisplayName : setting.ShortDisplayName;

                if (snapshot.IndicatorResults != null && snapshot.IndicatorResults.TryGetValue(setting.Id, out var result))
                {
                    if (!result.IsSuccessful)
                    {
                        if (!_errorCache.TryGetValue(setting.Id, out var errorText) || !errorText.StartsWith($"⚠️ {indicatorName}"))
                        {
                            errorText = $"⚠️ {indicatorName}: {result.ErrorMessage ?? "Error"}";
                            _errorCache[setting.Id] = errorText;
                        }
                        canvas.DrawText(errorText, x, y, _errorPaint);
                        y += 18;
                        continue;
                    }

                    // For successful results, show the main value if it's not in IndicatorValues
                    if (snapshot.IndicatorValues == null || !snapshot.IndicatorValues.ContainsKey(setting.Id))
                    {
                        var values = result.MainValues;
                        decimal? val = null;
                        if (index >= 0 && index < values.Count)
                        {
                            val = values[index];
                        }
                        
                        if (val.HasValue)
                        {
                            _indicatorColorBoxPaint.Color = setting.Color.ToSkColor();
                            canvas.DrawRect(x, y - 10, 10, 10, _indicatorColorBoxPaint);

                            if (!_indicatorCache.TryGetValue(setting.Id, out var cache) || cache.Value != val.Value || cache.Name != indicatorName)
                            {
                                cache = (val.Value, indicatorName, $"{indicatorName}: {val.Value:F2}");
                                _indicatorCache[setting.Id] = cache;
                            }
                            canvas.DrawText(cache.Text, x + 15, y, _labelPaint);
                            y += 18;
                        }
                        continue;
                    }
                }

                if (snapshot.IndicatorValues == null || !snapshot.IndicatorValues.TryGetValue(setting.Id, out var singleValues)) continue;
                decimal? value = null;
                
                if (index >= 0 && index < singleValues.Count)
                {
                    value = singleValues[index];
                }

                if (value.HasValue)
                {
                    // Draw Color Box
                    _indicatorColorBoxPaint.Color = setting.Color.ToSkColor();
                    canvas.DrawRect(x, y - 10, 10, 10, _indicatorColorBoxPaint);

                    // Draw Name & Value
                    if (!_indicatorCache.TryGetValue(setting.Id, out var cache) || cache.Value != value.Value || cache.Name != indicatorName)
                    {
                        cache = (value.Value, indicatorName, $"{indicatorName}: {value.Value:F2}");
                        _indicatorCache[setting.Id] = cache;
                    }
                    canvas.DrawText(cache.Text, x + 15, y, _labelPaint);

                    // Advance Y
                    y += 18;
                }
            }
        }
    }

    public void Dispose()
    {
        _textPaint.Dispose();
        _labelPaint.Dispose();
        _backgroundPaint.Dispose();
        _errorPaint.Dispose();
        _scorePaint.Dispose();
        _indicatorColorBoxPaint.Dispose();
        _indicatorCache.Clear();
        _errorCache.Clear();
    }
}
