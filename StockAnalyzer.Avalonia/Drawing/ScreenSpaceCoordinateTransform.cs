using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Core.Models;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Coordinate transform implementation for viewport-anchored screen-space layers.
/// Directly maps normalized unit fractions (Ux, Uy) in [0.0m, 1.0m]^2 to screen pixels
/// (Round(Ux * CanvasWidth), Round(Uy * CanvasHeight)) without chart pan/zoom scaling.
/// Guarantees zero allocation on hot paths and strict decimal/UTC domain invariants.
/// </summary>
public sealed class ScreenSpaceCoordinateTransform : ICoordinateTransform
{
    /// <summary>
    /// Base reference epoch used to map normalized Ux in [0.0, 1.0] to a UTC DateTime without tick drift.
    /// </summary>
    public static readonly DateTime NormalizedTimeBase = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Span of ticks representing the full [0.0, 1.0] range across the normalized time axis (1 day).
    /// </summary>
    public static readonly long NormalizedTimeSpanTicks = TimeSpan.FromDays(1).Ticks;

    public double CanvasWidth { get; private set; }
    public double CanvasHeight { get; private set; }

    public Rect ScreenRect => new Rect(0, 0, CanvasWidth, CanvasHeight);

    public double ViewportX => 0;
    public double ViewportWidth => CanvasWidth;

    public double ScaleX => CanvasWidth > 0 ? CanvasWidth : 1.0;

    public PriceScaleType PriceScale => PriceScaleType.Linear;

    public TransformMetadata Metadata { get; set; }

    public IReadOnlyList<DateTime>? TimeMap => null;

    public ScreenSpaceCoordinateTransform(double canvasWidth, double canvasHeight)
    {
        CanvasWidth = Math.Max(0.0, canvasWidth);
        CanvasHeight = Math.Max(0.0, canvasHeight);
    }

    public Point ChartToScreen(ChartPoint chartPoint)
    {
        double fractionX = (double)(chartPoint.Time - NormalizedTimeBase).Ticks / NormalizedTimeSpanTicks;
        double ux = Math.Clamp(fractionX, 0.0, 1.0);
        double uy = Math.Clamp((double)chartPoint.Price, 0.0, 1.0);

        double screenX = Math.Round(ux * CanvasWidth);
        double screenY = Math.Round(uy * CanvasHeight);

        return new Point(screenX, screenY);
    }

    public ChartPoint ScreenToChart(Point screenPoint)
    {
        double ux = CanvasWidth > 0 ? Math.Clamp(screenPoint.X / CanvasWidth, 0.0, 1.0) : 0.0;
        double uy = CanvasHeight > 0 ? Math.Clamp(screenPoint.Y / CanvasHeight, 0.0, 1.0) : 0.0;

        long ticks = (long)Math.Round(ux * NormalizedTimeSpanTicks);
        var time = DateTime.SpecifyKind(NormalizedTimeBase.AddTicks(ticks), DateTimeKind.Utc);
        decimal price = (decimal)uy;

        return new ChartPoint(time, price);
    }

    public Point NumericToScreen(double x, double y)
    {
        double screenX = Math.Round(Math.Clamp(x, 0.0, 1.0) * CanvasWidth);
        double screenY = Math.Round(Math.Clamp(y, 0.0, 1.0) * CanvasHeight);
        return new Point(screenX, screenY);
    }

    public (double x, double y) ScreenToNumeric(Point screenPoint)
    {
        double x = CanvasWidth > 0 ? Math.Clamp(screenPoint.X / CanvasWidth, 0.0, 1.0) : 0.0;
        double y = CanvasHeight > 0 ? Math.Clamp(screenPoint.Y / CanvasHeight, 0.0, 1.0) : 0.0;
        return (x, y);
    }

    public double GetXFromTime(DateTime time)
    {
        double fractionX = (double)(time - NormalizedTimeBase).Ticks / NormalizedTimeSpanTicks;
        double ux = Math.Clamp(fractionX, 0.0, 1.0);
        return Math.Round(ux * CanvasWidth);
    }

    public double GetXFromIndex(double index)
    {
        return Math.Round(Math.Clamp(index, 0.0, 1.0) * CanvasWidth);
    }

    public double GetYFromPrice(decimal price)
    {
        double uy = Math.Clamp((double)price, 0.0, 1.0);
        return Math.Round(uy * CanvasHeight);
    }

    public void UpdateRange(
        DateTime minTime,
        DateTime maxTime,
        decimal minPrice,
        decimal maxPrice,
        double? newCanvasWidth = null,
        double? newCanvasHeight = null)
    {
        if (newCanvasWidth.HasValue && newCanvasWidth.Value >= 0)
        {
            CanvasWidth = newCanvasWidth.Value;
        }

        if (newCanvasHeight.HasValue && newCanvasHeight.Value >= 0)
        {
            CanvasHeight = newCanvasHeight.Value;
        }
    }

    public void SetTimeMap(IReadOnlyList<DateTime> timeMap)
    {
        // Screen space does not utilize gapless time mapping
    }

    /// <summary>
    /// Converts normalized unit fractions (0.0m to 1.0m) directly to a screen pixel point.
    /// </summary>
    public Point NormalizedToScreen(decimal ux, decimal uy)
    {
        double x = Math.Round((double)Math.Clamp(ux, 0.0m, 1.0m) * CanvasWidth);
        double y = Math.Round((double)Math.Clamp(uy, 0.0m, 1.0m) * CanvasHeight);
        return new Point(x, y);
    }

    /// <summary>
    /// Converts a screen pixel point back to normalized unit fractions (0.0m to 1.0m).
    /// </summary>
    public (decimal ux, decimal uy) ScreenToNormalized(Point screenPoint)
    {
        decimal ux = CanvasWidth > 0 ? Math.Clamp((decimal)screenPoint.X / (decimal)CanvasWidth, 0.0m, 1.0m) : 0.0m;
        decimal uy = CanvasHeight > 0 ? Math.Clamp((decimal)screenPoint.Y / (decimal)CanvasHeight, 0.0m, 1.0m) : 0.0m;
        return (ux, uy);
    }
}
