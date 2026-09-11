using System;
using System.Buffers;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Utils;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the Close price series as a filled area chart (Area Chart).
/// Provides LTTB downsampling, zero-allocation path caching, and optional data markers.
/// </summary>
public sealed class AreaChartRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _linePaint;
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _markerPaint;
    private readonly SKPaint _projectionPaint;
    private readonly SKPath _linePath;
    private readonly SKPath _areaPath;
    
    // Shader Cache
    private SKShader? _activeShader;
    private SKColor _lastThemeColor;
    private SKColor _lastBgColor;
    private float _lastTop;
    private float _lastBottom;

    public AreaChartRenderer()
    {
        _linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = LayoutConstants.DefaultStrokeWidth * 2,
            IsAntialias = true
        };

        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _markerPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _projectionPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { ChartTheme.GridDashOn, ChartTheme.GridDashOff }, 0)
        };

        _linePath = new SKPath();
        _areaPath = new SKPath();
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (IAreaChartRenderConfig)baseConfig;
        if (snapshot.Candles.Count == 0) return;
        if (config?.Transform is not ICoordinateTransform transform) return;

        _linePaint.Color = config.AreaChartColor.ToSkColor();
        _markerPaint.Color = config.AreaChartColor.ToSkColor();

        // Calculate Interval for Time-Based Width
        TimeSpan interval = TimeSpan.FromDays(1);
        if (snapshot.Candles.Count > 1)
        {
            interval = snapshot.Candles[1].Timestamp - snapshot.Candles[0].Timestamp;
        }

        // Determine if we are rendering in Index mode
        bool isIndexMode = config.ChartType.IsIndexBased();

        // Jitter-free: width from time interval via transform
        double x0;
        double x1;
        if (isIndexMode)
        {
             x0 = transform.ChartToScreen(new ChartPoint(new DateTime(snapshot.StartIndex), 0)).X;
             x1 = transform.ChartToScreen(new ChartPoint(new DateTime(snapshot.StartIndex + 1), 0)).X;
        }
        else
        {
             x0 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
             x1 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp + interval, 0)).X;
        }

        float candleWidth = (float)(x1 - x0);

        _linePath.Reset();
        _areaPath.Reset();

        int threshold = (int)Math.Max(chartArea.Width * 2, 2);
        int maxPoints = Math.Min(snapshot.Candles.Count, threshold);

        int[] indicesBuffer = ArrayPool<int>.Shared.Rent(maxPoints);
        try
        {
            int pointCount = LttbDownsampler.Downsample(snapshot.Candles, maxPoints, indicesBuffer.AsSpan(0, maxPoints));
            
            bool firstPoint = true;
            float firstX = 0;
            float lastX = 0;
            bool drawFill = candleWidth >= ChartTheme.AreaChartLODThreshold; // LOD: Hide fill if candles are thinner than threshold
            bool drawMarkers = config.ShowAreaMarkers && candleWidth >= ChartTheme.AreaChartMarkerThreshold;

            for (int i = 0; i < pointCount; i++)
            {
                int dataIndex = indicesBuffer[i];
                var candle = snapshot.Candles[dataIndex];
                
                float x;
                if (isIndexMode)
                {
                    int absoluteIndex = snapshot.StartIndex + dataIndex;
                    x = (float)transform.ChartToScreen(new ChartPoint(new DateTime(absoluteIndex), 0)).X + (float)chartArea.Left;
                }
                else
                {
                    x = (float)transform.ChartToScreen(new ChartPoint(candle.Timestamp, 0)).X + (float)chartArea.Left;
                }
                
                float centerX = MathF.Floor(x + candleWidth / 2f) + 0.5f;
                float y = MathF.Floor((float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, candle.Close)).Y + (float)chartArea.Top) + 0.5f;

                if (firstPoint)
                {
                    _linePath.MoveTo(centerX, y);
                    if (drawFill) _areaPath.MoveTo(centerX, y);
                    firstPoint = false;
                    firstX = centerX;
                }
                else
                {
                    _linePath.LineTo(centerX, y);
                    if (drawFill) _areaPath.LineTo(centerX, y);
                }
                lastX = centerX;

                if (drawMarkers)
                {
                    canvas.DrawCircle(centerX, y, _linePaint.StrokeWidth, _markerPaint);
                }
            }

            if (drawFill)
            {
                // 1. Close and Draw Area Path (Fill first for correct Z-order)
                float bottomY = MathF.Floor((float)chartArea.Bottom) + 0.5f;
                _areaPath.LineTo(lastX, bottomY);
                _areaPath.LineTo(firstX, bottomY);
                _areaPath.Close();

                // 2. Dynamic Alpha Scaling based on Background Luminance
                SKColor bgColor = config.ThemeManager.CurrentTheme.ChartBackground.ToSkColor();
                float luminance = (0.2126f * bgColor.Red + 0.7152f * bgColor.Green + 0.0722f * bgColor.Blue) / 255f;
                
                // Scale alpha: Light (L=1.0) -> 100%, Dark (L=0.0) -> ~60%
                float alphaScale = ChartTheme.AreaChartAlphaBaseScale + ChartTheme.AreaChartAlphaLuminanceScale * luminance;
                byte alphaTop = (byte)Math.Clamp(ChartTheme.AreaFillAlphaTop * alphaScale, 0, 255);
                byte alphaBottom = (byte)Math.Clamp(ChartTheme.AreaFillAlphaBottom * alphaScale, 0, 255);

                var areaColor = config.AreaChartColor.ToSkColor();
                // Update Shader Cache if necessary
                if (_activeShader == null || 
                    areaColor != _lastThemeColor || 
                    bgColor != _lastBgColor ||
                    (float)chartArea.Top != _lastTop || 
                    (float)chartArea.Bottom != _lastBottom)
                {
                    _activeShader?.Dispose();
                    _activeShader = SKShader.CreateLinearGradient(
                        new SKPoint(0, (float)chartArea.Top),
                        new SKPoint(0, (float)chartArea.Bottom),
                        new SKColor[] { 
                            areaColor.WithAlpha(alphaTop), 
                            areaColor.WithAlpha(alphaBottom) 
                        },
                        null,
                        SKShaderTileMode.Clamp);
                    
                    _lastThemeColor = areaColor;
                    _lastBgColor = bgColor;
                    _lastTop = (float)chartArea.Top;
                    _lastBottom = (float)chartArea.Bottom;
                }

                _fillPaint.Shader = _activeShader;
                canvas.DrawPath(_areaPath, _fillPaint);
                _fillPaint.Shader = null;
            }

            // 3. Draw Projection Line (Horizontal extension of last price)
            if (lastX < chartArea.Right - 2)
            {
                var areaColorForProj = config.AreaChartColor.ToSkColor();
                var lastCandle = snapshot.Candles[^1];
                float lastY = MathF.Floor((float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, lastCandle.Close)).Y + (float)chartArea.Top) + 0.5f;

                _projectionPaint.Color = areaColorForProj.WithAlpha(ChartTheme.AreaChartProjectionAlpha); // Semi-transparent for projection
                canvas.DrawLine(lastX, lastY, (float)chartArea.Right, lastY, _projectionPaint);
            }

            // 2. Draw Line (On top of Fill)
            canvas.DrawPath(_linePath, _linePaint);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indicesBuffer);
        }
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _fillPaint.Dispose();
        _markerPaint.Dispose();
        _projectionPaint.Dispose();
        _linePath.Dispose();
        _areaPath.Dispose();
        _activeShader?.Dispose();
    }
}
