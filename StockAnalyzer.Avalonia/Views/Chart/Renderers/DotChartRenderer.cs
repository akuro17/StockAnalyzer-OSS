using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the specified price series as discrete geometric dot markers (Dot Chart).
/// Follows ZeroAllocation principles with pre-cached paints and reusable paths.
/// </summary>
public sealed class DotChartRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _upPaint;
    private readonly SKPaint _downPaint;
    private readonly SKPaint _naturalPaint;
    private readonly SKPath _diamondPath;

    public DotChartRenderer()
    {
        _upPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _downPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _naturalPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _diamondPath = new SKPath();
    }

    /// <summary>
    /// Spacing margin factor to avoid adjacent dot collision (derived from diameter &lt; w with 10% safety margin: (1 - 0.10) / 2 = 0.45).
    /// </summary>
    private const float SpacingMarginFactor = 0.45f;

    /// <summary>
    /// Minimum effective rendering radius, aligned with the system/UI minimum DotBaseRadius (0.5).
    /// </summary>
    private const float MinEffectiveRadius = 0.5f;

    /// <summary>
    /// Scaling factor for Diamond shape (sqrt(pi / 2) ~ 1.2533) to equalize visual area (perceptual weight) with Circle.
    /// Area(Diamond) = 2 * (r * 1.2533)^2 = 3.1416 * r^2 = Area(Circle).
    /// </summary>
    private const float DiamondScaleFactor = 1.2533f;

    /// <summary>
    /// Computes the effective rendering radius given the base radius and candle width.
    /// Clamps between MinEffectiveRadius (0.5) and baseRadius, scaling by SpacingMarginFactor (0.45).
    /// Internal for unit testing.
    /// </summary>
    internal static float CalculateEffectiveRadius(float baseRadius, float candleWidth)
    {
        return Math.Max(MinEffectiveRadius, Math.Min(baseRadius, candleWidth * SpacingMarginFactor));
    }

    /// <summary>
    /// Computes the scaled radius for Diamond shape to equalize visual area with Circle.
    /// Internal for unit testing.
    /// </summary>
    internal static float CalculateDiamondRadius(float effectiveRadius)
    {
        return effectiveRadius * DiamondScaleFactor;
    }

    /// <summary>
    /// Evaluates the direction / polarity for dot marker color.
    /// Returns 1 for Up, -1 for Down, 0 for Neutral.
    /// Internal for unit testing.
    /// </summary>
    internal static int EvaluateDirection(decimal currentPrice, decimal? previousPrice)
    {
        if (!previousPrice.HasValue) return 0;
        if (currentPrice > previousPrice.Value) return 1;
        if (currentPrice < previousPrice.Value) return -1;
        return 0;
    }

    /// <summary>
    /// Checks if the price type belongs to the Heikin-Ashi family.
    /// </summary>
    private static bool IsHeikinAshiType(PriceType type) =>
        type is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        if (baseConfig is not IDotChartRenderConfig config) return;
        if (snapshot.Candles == null || snapshot.Candles.Count == 0) return;
        if (config.Transform is not ICoordinateTransform transform) return;

        _upPaint.Color = config.DotUpColor.ToSkColor();
        _downPaint.Color = config.DotDownColor.ToSkColor();
        _naturalPaint.Color = config.DotNaturalColor.ToSkColor();

        // Calculate Interval for Time-Based Width
        TimeSpan interval = TimeSpan.FromDays(1);
        if (snapshot.Candles.Count > 1)
        {
            interval = snapshot.Candles[1].Timestamp - snapshot.Candles[0].Timestamp;
        }

        bool isIndexMode = config.ChartType.IsIndexBased();

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
        float baseRadius = (float)config.DotBaseRadius;
        float effectiveRadius = CalculateEffectiveRadius(baseRadius, candleWidth);
        float diameter = effectiveRadius * 2f;
        float diamondR = CalculateDiamondRadius(effectiveRadius);

        float chartLeft = (float)chartArea.Left;
        float chartTop = (float)chartArea.Top;
        float chartRight = (float)chartArea.Right;
        float chartBottom = (float)chartArea.Bottom;

        decimal? prevPrice = null;
        decimal? prevClose = null;
        decimal? prevHaOpen = null;
        decimal? prevHaClose = null;

        bool isHeikinAshi = IsHeikinAshiType(config.DotPriceType);

        // Scrolled viewport continuity: initialize prevPrice from preceding candle if available
        if (snapshot.StartIndex > 0 && snapshot.AllCandles != null && snapshot.StartIndex - 1 < snapshot.AllCandles.Count)
        {
            if (isHeikinAshi)
            {
                // Warm up Heikin-Ashi recursive open/close state from up to 50 bars back for convergence
                int warmupStart = Math.Max(0, snapshot.StartIndex - 50);
                var initialCandle = snapshot.AllCandles[warmupStart];
                decimal hOpen = (initialCandle.Open + initialCandle.Close) / 2.0m;
                decimal hClose = (initialCandle.Open + initialCandle.High + initialCandle.Low + initialCandle.Close) / 4.0m;

                for (int k = warmupStart + 1; k < snapshot.StartIndex; k++)
                {
                    var c = snapshot.AllCandles[k];
                    hClose = (c.Open + c.High + c.Low + c.Close) / 4.0m;
                    hOpen = (hOpen + hClose) / 2.0m;
                }
                prevHaOpen = hOpen;
                prevHaClose = hClose;
            }

            var precedingCandle = snapshot.AllCandles[snapshot.StartIndex - 1];
            prevPrice = PriceDataHelper.ExtractPrice(precedingCandle, config.DotPriceType, null, prevHaOpen, prevHaClose);
            prevClose = precedingCandle.Close;
        }

        for (int i = 0; i < snapshot.Candles.Count; i++)
        {
            var candle = snapshot.Candles[i];

            decimal price = PriceDataHelper.ExtractPrice(candle, config.DotPriceType, prevClose, prevHaOpen, prevHaClose);

            // Log scale boundary guard: non-positive prices cannot be projected onto log axis
            if (snapshot.PriceScale == PriceScaleType.Log && price <= 0m)
            {
                prevPrice = price;
                prevClose = candle.Close;
                if (isHeikinAshi)
                {
                    decimal haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                    decimal haOpen = prevHaOpen.HasValue && prevHaClose.HasValue
                        ? (prevHaOpen.Value + prevHaClose.Value) / 2.0m
                        : (candle.Open + candle.Close) / 2.0m;
                    prevHaOpen = haOpen;
                    prevHaClose = haClose;
                }
                continue;
            }

            // Determine Color (Predicate Logic: Price-Change-Based with Neutral on true series start)
            SKPaint paint;
            if (prevPrice.HasValue)
            {
                if (price > prevPrice.Value)
                    paint = _upPaint;
                else if (price < prevPrice.Value)
                    paint = _downPaint;
                else
                    paint = _naturalPaint;
            }
            else
            {
                // True dataset start (StartIndex == 0, i == 0): momentum is undefined, assign neutral color
                paint = _naturalPaint;
            }

            // Update state for next candle
            prevPrice = price;
            prevClose = candle.Close;

            if (isHeikinAshi)
            {
                decimal haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                decimal haOpen = prevHaOpen.HasValue && prevHaClose.HasValue
                    ? (prevHaOpen.Value + prevHaClose.Value) / 2.0m
                    : (candle.Open + candle.Close) / 2.0m;
                prevHaOpen = haOpen;
                prevHaClose = haClose;
            }

            float x;
            if (isIndexMode)
            {
                int absoluteIndex = snapshot.StartIndex + i;
                x = (float)transform.ChartToScreen(new ChartPoint(new DateTime(absoluteIndex), 0)).X + chartLeft;
            }
            else
            {
                x = (float)transform.ChartToScreen(new ChartPoint(candle.Timestamp, 0)).X + chartLeft;
            }

            float centerX = x + candleWidth / 2f;

            // Viewport boundary check: skip drawing if dot is completely outside the horizontal chart area
            if (centerX + effectiveRadius < chartLeft || centerX - effectiveRadius > chartRight)
            {
                continue;
            }

            float centerY = (float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, price)).Y + chartTop;

            // Viewport boundary check: skip drawing if dot is completely outside the vertical chart area
            if (centerY + effectiveRadius < chartTop || centerY - effectiveRadius > chartBottom)
            {
                continue;
            }

            switch (config.DotShape)
            {
                case DotShapeType.Circle:
                    canvas.DrawCircle(centerX, centerY, effectiveRadius, paint);
                    break;

                case DotShapeType.Square:
                    canvas.DrawRect(centerX - effectiveRadius, centerY - effectiveRadius, diameter, diameter, paint);
                    break;

                case DotShapeType.Diamond:
                    _diamondPath.Rewind();
                    _diamondPath.MoveTo(centerX, centerY - diamondR);
                    _diamondPath.LineTo(centerX + diamondR, centerY);
                    _diamondPath.LineTo(centerX, centerY + diamondR);
                    _diamondPath.LineTo(centerX - diamondR, centerY);
                    _diamondPath.Close();
                    canvas.DrawPath(_diamondPath, paint);
                    break;

                default:
                    canvas.DrawCircle(centerX, centerY, effectiveRadius, paint);
                    break;
            }
        }
    }

    public void Dispose()
    {
        _upPaint.Dispose();
        _downPaint.Dispose();
        _naturalPaint.Dispose();
        _diamondPath.Dispose();
    }
}
