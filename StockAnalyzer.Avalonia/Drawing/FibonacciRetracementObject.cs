using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

public class FibonacciRetracementObject : RelativeGeometricRenderer, IDrawingCalculatedValuesProvider
{
    public override ChartObjectType Type => ChartObjectType.FibonacciRetracement;

    private static readonly float[] DefaultLevels = { 0f, 0.236f, 0.382f, 0.5f, 0.618f, 0.786f, 1.0f };
    private static readonly float[] ExtensionLevels = { -3.236f, -1.618f, -0.618f, -0.272f, 0f, 0.236f, 0.382f, 0.5f, 0.618f, 0.786f, 1.0f, 1.272f, 1.618f, 2.618f, 4.236f };
    private static readonly int[] FibSequence = { 0, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233 };

    public bool ShowExtensions { get; set; } = false;
    public FibonacciRetracementExtendMode ExtendMode { get; set; } = FibonacciRetracementExtendMode.Default;

    public IReadOnlyList<float> Levels => ShowExtensions ? ExtensionLevels : DefaultLevels;

    private readonly SKPaint _dashPaint;
    private readonly SKPaint _textPaint;

    public FibonacciRetracementObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);

        _dashPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true
        };
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Draw Trend Line
        _dashPaint.Color = SkiaColor.WithAlpha(128);
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, _dashPaint);

        float yDiff = (float)p1.Y - (float)p2.Y;
        float baseStartX = (float)p1.X;
        float baseEndX = (float)p2.X;
        float minX = Math.Min(baseStartX, baseEndX);
        float maxX = Math.Max(baseStartX, baseEndX);

        var bounds = canvas.LocalClipBounds;
        float chartLeft = bounds.Left;
        float chartRight = bounds.Right;
        float chartTop = bounds.Top;
        float chartBottom = bounds.Bottom;

        // Determine horizontal line span based on ExtendMode
        float lineX1 = minX;
        float lineX2 = maxX;

        switch (ExtendMode)
        {
            case FibonacciRetracementExtendMode.ExtendRight:
            case FibonacciRetracementExtendMode.CyclicLines:
            case FibonacciRetracementExtendMode.FibTimeZone:
                lineX1 = minX;
                lineX2 = Math.Max(maxX, chartRight);
                break;
            case FibonacciRetracementExtendMode.ExtendBoth:
                lineX1 = Math.Min(minX, chartLeft);
                lineX2 = Math.Max(maxX, chartRight);
                break;
            case FibonacciRetracementExtendMode.Default:
            default:
                lineX1 = minX;
                lineX2 = maxX;
                break;
        }

        _textPaint.Color = SkiaColor;
        _textPaint.TextSize = DrawingThemeContext.DrawingFontSize;

        // Draw Vertical Lines for CyclicLines or FibTimeZone
        if (ExtendMode == FibonacciRetracementExtendMode.CyclicLines || ExtendMode == FibonacciRetracementExtendMode.FibTimeZone)
        {
            float intervalX = Math.Abs(baseEndX - baseStartX);
            if (intervalX >= 5f)
            {
                float startX = (float)p1.X;
                float dir = baseEndX >= baseStartX ? 1f : -1f;

                if (ExtendMode == FibonacciRetracementExtendMode.CyclicLines)
                {
                    float x = startX;
                    int count = 0;
                    while (count < 1000)
                    {
                        if (x >= chartLeft && x <= chartRight)
                        {
                            canvas.DrawLine(x, chartTop, x, chartBottom, _dashPaint);
                            if (count > 0)
                            {
                                canvas.DrawText($"{count}", x + 2, chartBottom - 5, _textPaint);
                            }
                        }
                        else if ((dir > 0 && x > chartRight) || (dir < 0 && x < chartLeft))
                        {
                            break;
                        }
                        x += intervalX * dir;
                        count++;
                    }
                }
                else if (ExtendMode == FibonacciRetracementExtendMode.FibTimeZone)
                {
                    foreach (var fib in FibSequence)
                    {
                        float x = startX + (intervalX * fib * dir);
                        if (x >= chartLeft && x <= chartRight)
                        {
                            canvas.DrawLine(x, chartTop, x, chartBottom, _dashPaint);
                            canvas.DrawText($"{fib}", x + 2, chartBottom - 5, _textPaint);
                        }
                        else if ((dir > 0 && x > chartRight) || (dir < 0 && x < chartLeft))
                        {
                            break;
                        }
                    }
                }
            }
        }

        // Draw Horizontal Levels
        var levels = Levels;
        foreach (var level in levels)
        {
            float y = (float)p2.Y + (yDiff * level);

            canvas.DrawLine(lineX1, y, lineX2, y, _cachedPaint);

            // Draw Text (Legacy Layout: Right side of line, Top: %, Bottom: Value)
            float textX = lineX2 + 5;
            if (lineX2 >= chartRight && lineX1 < lineX2)
            {
                textX = Math.Min(chartRight - 65, maxX + 5);
                if (textX < lineX1) textX = lineX1 + 5;
            }

            float textY_Percent = y - 3;
            float textY_Value = y + 10;

            string textPercent = $"{level:P1}";
            string textValue = $"{GetPrice(y, transform):F2}";

            canvas.DrawText(textPercent, textX, textY_Percent, _textPaint);
            canvas.DrawText(textValue, textX, textY_Value, _textPaint);
        }
    }

    private static decimal GetPrice(float y, ICoordinateTransform transform)
    {
        return transform.ScreenToChart(new global::Avalonia.Point(0, y)).Price;
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        float baseStartX = (float)p1.X;
        float baseEndX = (float)p2.X;
        float minX = Math.Min(baseStartX, baseEndX);
        float maxX = Math.Max(baseStartX, baseEndX);

        float lineX1 = minX;
        float lineX2 = maxX;

        float canvasWidth = (float)transform.CanvasWidth;
        if (canvasWidth <= 0)
        {
            canvasWidth = 10000;
        }

        switch (ExtendMode)
        {
            case FibonacciRetracementExtendMode.ExtendRight:
            case FibonacciRetracementExtendMode.CyclicLines:
            case FibonacciRetracementExtendMode.FibTimeZone:
                lineX1 = minX;
                lineX2 = Math.Max(maxX, canvasWidth);
                break;
            case FibonacciRetracementExtendMode.ExtendBoth:
                lineX1 = 0;
                lineX2 = Math.Max(maxX, canvasWidth);
                break;
            case FibonacciRetracementExtendMode.Default:
            default:
                lineX1 = minX;
                lineX2 = maxX;
                break;
        }

        // Check levels
        float yDiff = (float)p1.Y - (float)p2.Y;
        var levels = Levels;

        if (screenPoint.X >= lineX1 - tolerance && screenPoint.X <= lineX2 + tolerance)
        {
            foreach (var level in levels)
            {
                float y = (float)p2.Y + (yDiff * level);
                if (Math.Abs(screenPoint.Y - y) <= tolerance) return true;
            }
        }

        // Check vertical lines if CyclicLines or FibTimeZone
        if (ExtendMode == FibonacciRetracementExtendMode.CyclicLines || ExtendMode == FibonacciRetracementExtendMode.FibTimeZone)
        {
            float intervalX = Math.Abs(baseEndX - baseStartX);
            if (intervalX >= 5f)
            {
                float startX = (float)p1.X;
                float dir = baseEndX >= baseStartX ? 1f : -1f;

                if (ExtendMode == FibonacciRetracementExtendMode.CyclicLines)
                {
                    float diff = ((float)screenPoint.X - startX) * dir;
                    if (diff >= -tolerance)
                    {
                        double k = Math.Round(diff / intervalX);
                        float nearestX = startX + (float)k * intervalX * dir;
                        if (Math.Abs(screenPoint.X - nearestX) <= tolerance) return true;
                    }
                }
                else if (ExtendMode == FibonacciRetracementExtendMode.FibTimeZone)
                {
                    foreach (var fib in FibSequence)
                    {
                        float x = startX + (intervalX * fib * dir);
                        if (Math.Abs(screenPoint.X - x) <= tolerance) return true;
                    }
                }
            }
        }

        return false;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < 2) return Array.Empty<DrawingCalculatedValue>();

        var p1 = Points[0];
        var p2 = Points[1];
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        decimal range = Math.Abs(p1.Price - p2.Price);
        decimal yDiffPrice = p1.Price - p2.Price;

        var levels = Levels;
        var values = new List<DrawingCalculatedValue>(levels.Count + 2)
        {
            new DrawingCalculatedValue("Range", "Range", range, $"{range:F3}", IndicatorColor.Gray)
        };

        foreach (var level in levels)
        {
            decimal levelPrice = p2.Price + yDiffPrice * (decimal)level;
            string label = $"Fib {level:P1}";
            string key = $"Fib_{level:F3}";
            values.Add(new DrawingCalculatedValue(key, label, levelPrice, $"{levelPrice:F3}", color));
        }

        if (currentPrice.HasValue)
        {
            decimal minDiff = decimal.MaxValue;
            string nearestLabel = string.Empty;
            decimal nearestPrice = 0m;
            foreach (var level in levels)
            {
                decimal levelPrice = p2.Price + yDiffPrice * (decimal)level;
                decimal diff = Math.Abs(currentPrice.Value - levelPrice);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    nearestLabel = $"{level:P1}";
                    nearestPrice = levelPrice;
                }
            }
            decimal priceDelta = currentPrice.Value - nearestPrice;
            string deltaSign = priceDelta >= 0 ? "+" : "";
            values.Add(new DrawingCalculatedValue("NearestLevel", "Nearest Level", nearestPrice, $"{nearestLabel} ({deltaSign}{priceDelta:F3})", IndicatorColor.FromRgb(255, 215, 0)));
        }

        return values;
    }

    public override void Dispose()
    {
        _dashPaint.Dispose();
        _textPaint.Dispose();
        base.Dispose();
    }
}
