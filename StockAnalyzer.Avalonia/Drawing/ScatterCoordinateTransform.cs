using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Core.Models;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Coordinate transform implementation for 2D scatter plots.
/// Maps between Data Space (X, Y) and Screen Space (Sx, Sy) with independent Linear and Log10 scaling.
/// Guarantees zero allocation, reversibility within 1e-6 error, and non-throwing singularity protection.
/// </summary>
public sealed class ScatterCoordinateTransform : ICoordinateTransform
{
    private const double Epsilon = 1e-12;
    private const double MinPositiveValue = 1e-12;

    public PriceScaleType XScale { get; set; } = PriceScaleType.Linear;
    public PriceScaleType YScale { get; set; } = PriceScaleType.Linear;

    public PriceScaleType PriceScale => YScale;

    public bool CanUseLogX => MaxX > 0.0;
    public bool CanUseLogY => MaxY > 0.0;
    public bool CanUseLogScale => CanUseLogX && CanUseLogY;

    public double MinX { get; private set; }
    public double MaxX { get; private set; }
    public double MinY { get; private set; }
    public double MaxY { get; private set; }

    public double ViewportLeft { get; private set; }
    public double ViewportTop { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }

    public double CanvasWidth { get; private set; }
    public double CanvasHeight { get; private set; }

    public Rect ScreenRect => new Rect(0, 0, CanvasWidth, CanvasHeight);
    public Rect ViewportRect => new Rect(ViewportLeft, ViewportTop, ViewportWidth, ViewportHeight);

    public double ViewportX => ViewportLeft;
    public double ScaleX => ViewportWidth > 0 && Math.Abs(MaxX - MinX) > Epsilon ? ViewportWidth / (MaxX - MinX) : 1.0;

    /// <summary>
    /// Metadata contract: uses ReverseWatch as a surrogate chart type for 2D scatter coordinates under ICoordinateTransform.
    /// </summary>
    public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.ReverseWatch);
    public IReadOnlyList<DateTime>? TimeMap => null;

    public ScatterCoordinateTransform(
        double minX = 0.0,
        double maxX = 1.0,
        double minY = 0.0,
        double maxY = 1.0,
        double viewportLeft = 0.0,
        double viewportTop = 0.0,
        double viewportWidth = 100.0,
        double viewportHeight = 100.0,
        double canvasWidth = 100.0,
        double canvasHeight = 100.0,
        PriceScaleType xScale = PriceScaleType.Linear,
        PriceScaleType yScale = PriceScaleType.Linear)
    {
        XScale = xScale;
        YScale = yScale;
        SetRanges(minX, maxX, minY, maxY, viewportLeft, viewportTop, viewportWidth, viewportHeight, canvasWidth, canvasHeight);
    }

    public void SetRanges(
        double minX, double maxX,
        double minY, double maxY,
        double viewportLeft, double viewportTop,
        double viewportWidth, double viewportHeight,
        double canvasWidth, double canvasHeight)
    {
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
        ViewportLeft = viewportLeft;
        ViewportTop = viewportTop;
        ViewportWidth = Math.Max(0.0, viewportWidth);
        ViewportHeight = Math.Max(0.0, viewportHeight);
        CanvasWidth = Math.Max(0.0, canvasWidth);
        CanvasHeight = Math.Max(0.0, canvasHeight);
    }

    public void UpdateDataRange(double minX, double maxX, double minY, double maxY)
    {
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
    }

    public void UpdateViewport(double left, double top, double width, double height, double canvasWidth, double canvasHeight)
    {
        ViewportLeft = left;
        ViewportTop = top;
        ViewportWidth = Math.Max(0.0, width);
        ViewportHeight = Math.Max(0.0, height);
        CanvasWidth = Math.Max(0.0, canvasWidth);
        CanvasHeight = Math.Max(0.0, canvasHeight);
    }

    /// <summary>
    /// Creates an independent snapshot of the transform for off-thread render operations.
    /// </summary>
    public ScatterCoordinateTransform Clone()
    {
        return new ScatterCoordinateTransform(
            MinX, MaxX, MinY, MaxY,
            ViewportLeft, ViewportTop, ViewportWidth, ViewportHeight,
            CanvasWidth, CanvasHeight,
            XScale, YScale);
    }

    public double XToScreen(double x)
    {
        if (!double.IsFinite(x)) return double.NaN;
        if (ViewportWidth <= 0.0) return ViewportLeft;

        if (XScale == PriceScaleType.Log)
        {
            if (x <= 0.0 || MaxX <= 0.0) return double.NaN;

            double safeMinX = MinX > 0.0 ? MinX : MinPositiveValue;
            double safeMaxX = MaxX;
            double logMin = Math.Log10(safeMinX);
            double logMax = Math.Log10(safeMaxX);
            double logRange = logMax - logMin;

            if (Math.Abs(logRange) <= Epsilon)
            {
                return ViewportLeft + (0.5 * ViewportWidth);
            }

            double logX = Math.Log10(x);
            double fraction = (logX - logMin) / logRange;
            return ViewportLeft + (fraction * ViewportWidth);
        }
        else
        {
            double range = MaxX - MinX;
            if (Math.Abs(range) <= Epsilon)
            {
                return ViewportLeft + (0.5 * ViewportWidth);
            }
            double fraction = (x - MinX) / range;
            return ViewportLeft + (fraction * ViewportWidth);
        }
    }

    public double YToScreen(double y)
    {
        if (!double.IsFinite(y)) return double.NaN;
        if (ViewportHeight <= 0.0) return ViewportTop;

        if (YScale == PriceScaleType.Log)
        {
            if (y <= 0.0 || MaxY <= 0.0) return double.NaN;

            double safeMinY = MinY > 0.0 ? MinY : MinPositiveValue;
            double safeMaxY = MaxY;
            double logMin = Math.Log10(safeMinY);
            double logMax = Math.Log10(safeMaxY);
            double logRange = logMax - logMin;

            if (Math.Abs(logRange) <= Epsilon)
            {
                return ViewportTop + (0.5 * ViewportHeight);
            }

            double logY = Math.Log10(y);
            double fraction = (logY - logMin) / logRange;
            return ViewportTop + ViewportHeight - (fraction * ViewportHeight);
        }
        else
        {
            double range = MaxY - MinY;
            if (Math.Abs(range) <= Epsilon)
            {
                return ViewportTop + (0.5 * ViewportHeight);
            }
            double fraction = (y - MinY) / range;
            return ViewportTop + ViewportHeight - (fraction * ViewportHeight);
        }
    }

    public double ScreenToX(double screenX)
    {
        if (!double.IsFinite(screenX)) return double.NaN;
        if (ViewportWidth <= 0.0) return MinX;

        double fraction = (screenX - ViewportLeft) / ViewportWidth;

        if (XScale == PriceScaleType.Log)
        {
            if (MaxX <= 0.0) return double.NaN;

            double safeMinX = MinX > 0.0 ? MinX : MinPositiveValue;
            double safeMaxX = MaxX;
            double logMin = Math.Log10(safeMinX);
            double logMax = Math.Log10(safeMaxX);
            double logRange = logMax - logMin;

            if (Math.Abs(logRange) <= Epsilon)
            {
                return safeMinX;
            }

            double logX = logMin + (fraction * logRange);
            return Math.Pow(10.0, logX);
        }
        else
        {
            return MinX + (fraction * (MaxX - MinX));
        }
    }

    public double ScreenToY(double screenY)
    {
        if (!double.IsFinite(screenY)) return double.NaN;
        if (ViewportHeight <= 0.0) return MinY;

        double fraction = (ViewportTop + ViewportHeight - screenY) / ViewportHeight;

        if (YScale == PriceScaleType.Log)
        {
            if (MaxY <= 0.0) return double.NaN;

            double safeMinY = MinY > 0.0 ? MinY : MinPositiveValue;
            double safeMaxY = MaxY;
            double logMin = Math.Log10(safeMinY);
            double logMax = Math.Log10(safeMaxY);
            double logRange = logMax - logMin;

            if (Math.Abs(logRange) <= Epsilon)
            {
                return safeMinY;
            }

            double logY = logMin + (fraction * logRange);
            return Math.Pow(10.0, logY);
        }
        else
        {
            return MinY + (fraction * (MaxY - MinY));
        }
    }

    public Point NumericToScreen(double x, double y)
    {
        return new Point(XToScreen(x), YToScreen(y));
    }

    public (double x, double y) ScreenToNumeric(Point screenPoint)
    {
        return (ScreenToX(screenPoint.X), ScreenToY(screenPoint.Y));
    }

    public Point ChartToScreen(ChartPoint chartPoint)
    {
        return NumericToScreen((double)chartPoint.Time.Ticks, (double)chartPoint.Price);
    }

    public ChartPoint ScreenToChart(Point screenPoint)
    {
        var (x, y) = ScreenToNumeric(screenPoint);
        long ticks = (long)Math.Clamp(x, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        var time = new DateTime(ticks, DateTimeKind.Utc);
        decimal price = (decimal)Math.Clamp(y, (double)decimal.MinValue, (double)decimal.MaxValue);
        return new ChartPoint(time, price);
    }

    public double GetXFromIndex(double index) => XToScreen(index);
    public double GetYFromPrice(decimal price) => YToScreen((double)price);
    public double GetXFromTime(DateTime time) => XToScreen((double)time.Ticks);

    public void UpdateRange(
        DateTime minTime, DateTime maxTime,
        decimal minPrice, decimal maxPrice,
        double? newCanvasWidth = null,
        double? newCanvasHeight = null)
    {
        UpdateDataRange((double)minTime.Ticks, (double)maxTime.Ticks, (double)minPrice, (double)maxPrice);
        if (newCanvasWidth.HasValue && newCanvasWidth.Value > 0) CanvasWidth = newCanvasWidth.Value;
        if (newCanvasHeight.HasValue && newCanvasHeight.Value > 0) CanvasHeight = newCanvasHeight.Value;
    }

    public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
}
