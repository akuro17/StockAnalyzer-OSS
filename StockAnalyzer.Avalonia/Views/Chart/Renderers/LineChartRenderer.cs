using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Utils;
using System.Buffers;
namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the Close price series as a continuous line (Line Chart).
/// </summary>
public sealed class LineChartRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _linePaint;
    private readonly SKPaint _markerPaint;
    private readonly SKPath _path;

    public LineChartRenderer()
    {
        _linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = LayoutConstants.DefaultStrokeWidth * 2,
            IsAntialias = true
        };
        _markerPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        _path = new SKPath();
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (ILineChartRenderConfig)baseConfig;
        if (snapshot.Candles.Count == 0) return;
        if (config?.Transform is not ICoordinateTransform transform) return;
        
        _linePaint.StrokeWidth = (float)config.DefaultDrawingThickness * 2;
        _linePaint.Color = config.LineChartColor.ToSkColor();

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

        _path.Reset();

        int threshold = (int)Math.Max(chartArea.Width * 2, 2);
        int maxPoints = Math.Min(snapshot.Candles.Count, threshold);

        int[] indicesBuffer = ArrayPool<int>.Shared.Rent(maxPoints);
        try
        {
            int pointCount = LttbDownsampler.Downsample(snapshot.Candles, maxPoints, indicesBuffer.AsSpan(0, maxPoints));
            bool firstPoint = true;
            bool drawMarkers = config.ShowLineMarkers && candleWidth >= 5.0f;
            if (drawMarkers) _markerPaint.Color = config.LineChartColor.ToSkColor();

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

                // Center of candle
                float centerX = x + candleWidth / 2f;
                float y = (float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, candle.Close)).Y + (float)chartArea.Top;

                if (firstPoint)
                {
                    _path.MoveTo(centerX, y);
                    firstPoint = false;
                }
                else
                {
                    _path.LineTo(centerX, y);
                }

                if (drawMarkers)
                {
                    canvas.DrawCircle(centerX, y, _linePaint.StrokeWidth, _markerPaint);
                }
            }

            canvas.DrawPath(_path, _linePaint);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indicesBuffer);
        }
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _markerPaint.Dispose();
        _path.Dispose();
    }
}

