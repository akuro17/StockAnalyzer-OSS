using System;
using Avalonia;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Encapsulates higher-level state information used to synchronize rendering pipelines.
/// </summary>
public record struct TransformMetadata(bool IsSubWindowVisible, bool IsMainWindowVisible, ChartType BaseChartType);

/// <summary>
/// Coordinate transform interface (chart coords ↔ screen coords)
/// </summary>
public interface ICoordinateTransform
{
    /// <summary>Convert chart coordinate to screen coordinate</summary>
    global::Avalonia.Point ChartToScreen(ChartPoint chartPoint);

    /// <summary>Convert screen coordinate to chart coordinate</summary>
    ChartPoint ScreenToChart(global::Avalonia.Point screenPoint);

    /// <summary>Convert raw numeric (X, Y) to screen coordinate (for non-time or XY charts)</summary>
    global::Avalonia.Point NumericToScreen(double x, double y);

    /// <summary>Convert screen coordinate to raw numeric (X, Y)</summary>
    (double x, double y) ScreenToNumeric(global::Avalonia.Point screenPoint);

    /// <summary>Update coordinate range (supports canvas resize)</summary>
    void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null);

    /// <summary>Injects a timestamp array for gapless/ordinal chronological mappings</summary>
    void SetTimeMap(System.Collections.Generic.IReadOnlyList<DateTime> timeMap);

    /// <summary>Retrieves the active timestamp array if configured</summary>
    System.Collections.Generic.IReadOnlyList<DateTime>? TimeMap { get; }

    /// <summary>Canvas width in pixels</summary>
    double CanvasWidth { get; }

    /// <summary>Canvas height in pixels</summary>
    double CanvasHeight { get; }

    /// <summary>Bounding rectangle of the screen area</summary>
    global::Avalonia.Rect ScreenRect { get; }

    /// <summary>Viewport X-offset (pixels)</summary>
    double ViewportX { get; }

    /// <summary>Viewport Width (pixels)</summary>
    double ViewportWidth { get; }

    /// <summary>X-Axis Scaling factor (pixels per unit)</summary>
    double ScaleX { get; }

    /// <summary>Directly converts Index to screen X coordinate (Mathematical Formula)</summary>
    double GetXFromIndex(double index);

    /// <summary>Directly converts Price to screen Y coordinate (Mathematical Formula)</summary>
    double GetYFromPrice(decimal price);

    PriceScaleType PriceScale { get; }

    /// <summary>Metadata for layout synchronization</summary>
    TransformMetadata Metadata { get; }
}

/// <summary>
/// Linear coordinate transform implementation
/// IMPROVEMENT v1.1: Added PixelPerPrice getter for precision HitTest calculation
/// </summary>
public class LinearCoordinateTransform : ICoordinateTransform
{
    private DateTime _minTime;
    private DateTime _maxTime;
    private decimal _minPrice;
    private decimal _maxPrice;

    /// <summary>Canvas width (pixels)</summary>
    public double CanvasWidth { get; private set; }

    /// <summary>Canvas height (pixels)</summary>
    public double CanvasHeight { get; private set; }

    private double _pixelPerMillisecond;  // pixels per millisecond (time axis)
    private double _pixelPerPrice;        // pixels per price unit (price axis)

    /// <summary>Pixels per price unit (read-only, for HitTest precision)</summary>
    public double PixelPerPrice => _pixelPerPrice;

    /// <summary>X-Axis Scaling factor (pixels per millisecond)</summary>
    public double ScaleX => _pixelPerMillisecond;

    /// <summary>Bounding rectangle of the screen area</summary>
    public global::Avalonia.Rect ScreenRect => new global::Avalonia.Rect(0, 0, CanvasWidth, CanvasHeight);

    public double ViewportX => 0;
    public double ViewportY => 0;
    public double ViewportWidth => CanvasWidth;
    public double ViewportHeight => CanvasHeight;
    public PriceScaleType PriceScale => PriceScaleType.Linear;
    public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);

    /// <summary>Constructor</summary>
    public LinearCoordinateTransform(
        DateTime minTime, DateTime maxTime,
        decimal minPrice, decimal maxPrice,
        double canvasWidth, double canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
            throw new ArgumentException("Canvas dimensions must be positive.");
            
        _minTime = minTime;
        _maxTime = maxTime;
        _minPrice = minPrice;
        _maxPrice = maxPrice;

        // Validation / Correction
        if (_minTime >= _maxTime) _maxTime = _minTime.AddMinutes(1);
        if (_minPrice >= _maxPrice) _maxPrice = _minPrice + 1m;
        
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;

        RecalculateScalingCoefficients();
    }

    /// <summary>Ignored for Linear Transform</summary>
    public void SetTimeMap(System.Collections.Generic.IReadOnlyList<DateTime> timeMap) { }

    /// <summary>Always null for Linear Transform</summary>
    public System.Collections.Generic.IReadOnlyList<DateTime>? TimeMap => null;

    /// <summary>Directly converts Index (ms) to screen X coordinate</summary>
    public double GetXFromIndex(double index)
    {
        return index * _pixelPerMillisecond;
    }

    /// <summary>Directly converts Price to screen Y coordinate</summary>
    public double GetYFromPrice(decimal price)
    {
        return CanvasHeight - (double)(price - _minPrice) * _pixelPerPrice;
    }

    /// <summary>Convert chart coordinate to screen coordinate</summary>
    public global::Avalonia.Point ChartToScreen(ChartPoint chartPoint)
    {
        double x = GetXFromIndex((chartPoint.Time - _minTime).TotalMilliseconds);
        double y = GetYFromPrice(chartPoint.Price);
        return new global::Avalonia.Point(x, y);
    }

    /// <summary>Convert screen coordinate to chart coordinate (IMPROVEMENT: decimal precision)</summary>
    public ChartPoint ScreenToChart(global::Avalonia.Point screenPoint)
    {
        double msFromStart = screenPoint.X / _pixelPerMillisecond;
        DateTime time = _minTime.AddMilliseconds(msFromStart);

        // IMPROVEMENT v1.1: Use decimal precision for price calculation
        decimal priceRange = _maxPrice - _minPrice;
        decimal pricePerPixel = priceRange / (decimal)CanvasHeight;
        decimal priceOffset = (decimal)(CanvasHeight - screenPoint.Y) * pricePerPixel;
        decimal price = _minPrice + priceOffset;

        return new ChartPoint(time, price);
    }

    /// <summary>Convert raw numeric (X, Y) to screen coordinate</summary>
    public global::Avalonia.Point NumericToScreen(double x, double y)
    {
        // For LinearCoordinateTransform (Time-based), X is treated as milliseconds from MinTime
        double screenX = GetXFromIndex(x);
        double screenY = GetYFromPrice((decimal)y);
        return new global::Avalonia.Point(screenX, screenY);
    }

    /// <summary>Convert screen coordinate to raw numeric (X, Y)</summary>
    public (double x, double y) ScreenToNumeric(global::Avalonia.Point screenPoint)
    {
        double msFromStart = screenPoint.X / _pixelPerMillisecond;
        double price = (double)_minPrice + (CanvasHeight - screenPoint.Y) / _pixelPerPrice;
        return (msFromStart, price);
    }

    /// <summary>Update coordinate range (supports canvas resize)</summary>
    public void UpdateRange(
        DateTime minTime, DateTime maxTime,
        decimal minPrice, decimal maxPrice,
        double? newCanvasWidth = null,
        double? newCanvasHeight = null)
    {
        // Removed aggressive validation throwing to prevent UI crashes on edge cases
        _minTime = minTime;
        _maxTime = maxTime;
        _minPrice = minPrice;
        _maxPrice = maxPrice;

        // Auto-correct invalid ranges
        if (_minTime >= _maxTime) _maxTime = _minTime.AddMinutes(1);
        if (_minPrice >= _maxPrice) _maxPrice = _minPrice + 1m;

        if (newCanvasWidth.HasValue && newCanvasWidth.Value > 0)
            CanvasWidth = newCanvasWidth.Value;

        if (newCanvasHeight.HasValue && newCanvasHeight.Value > 0)
            CanvasHeight = newCanvasHeight.Value;

        RecalculateScalingCoefficients();
    }

    /// <summary>Recalculate scaling coefficients (internal helper)</summary>
    private void RecalculateScalingCoefficients()
    {
        double timeRangeMs = (_maxTime - _minTime).TotalMilliseconds;
        decimal priceRange = _maxPrice - _minPrice;

        _pixelPerMillisecond = CanvasWidth / timeRangeMs;
        _pixelPerPrice = CanvasHeight / (double)priceRange;

        // Zero-scale division protection
        if (double.IsNaN(_pixelPerMillisecond) || double.IsInfinity(_pixelPerMillisecond) || _pixelPerMillisecond <= 0.0)
        {
            _pixelPerMillisecond = 1.0;
        }
        if (double.IsNaN(_pixelPerPrice) || double.IsInfinity(_pixelPerPrice) || _pixelPerPrice <= 0.0)
        {
            _pixelPerPrice = 1.0;
        }
    }
}
