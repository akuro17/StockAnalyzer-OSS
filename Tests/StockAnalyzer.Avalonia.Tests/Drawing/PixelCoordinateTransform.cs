using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Test transform with one pixel per unit: the chart point (Epoch + x days, price y) maps to the screen point (x, y), so a test can
/// state geometry directly in pixels. With <c>logarithmicPrice: true</c> the price axis is logarithmic instead:
/// screen y = <see cref="CanvasHeight"/> - <see cref="PixelsPerLogUnit"/> * ln(price) (a larger price is higher on the screen).
/// </summary>
internal sealed class PixelCoordinateTransform : ICoordinateTransform
{
    private static readonly DateTime Epoch = new(2025, 1, 1);
    private const double PixelsPerLogUnit = 100.0;

    private readonly bool _logarithmicPrice;

    public PixelCoordinateTransform(bool logarithmicPrice = false) => _logarithmicPrice = logarithmicPrice;

    public double CanvasWidth => 800;
    public double CanvasHeight => 600;
    public Rect ScreenRect => new(0, 0, 800, 600);
    public double ViewportX => 0;
    public double ViewportWidth => 800;
    public double ScaleX => 1.0;
    public PriceScaleType PriceScale => _logarithmicPrice ? PriceScaleType.Log : PriceScaleType.Linear;
    public TransformMetadata Metadata => new(false, true, ChartType.Line);
    public IReadOnlyList<DateTime>? TimeMap => null;

    public static ChartPoint Chart(double x, double y) => new(Epoch.AddDays(x), (decimal)y);

    public Point ChartToScreen(ChartPoint chartPoint) => new((chartPoint.Time - Epoch).TotalDays, GetYFromPrice(chartPoint.Price));
    public ChartPoint ScreenToChart(Point screenPoint) => new(Epoch.AddDays(screenPoint.X), _logarithmicPrice
        ? (decimal)Math.Exp((CanvasHeight - screenPoint.Y) / PixelsPerLogUnit)
        : (decimal)screenPoint.Y);
    public Point NumericToScreen(double x, double y) => new(x, y);
    public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
    public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
    public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
    public double GetXFromIndex(double index) => index;
    public double GetYFromPrice(decimal price) => _logarithmicPrice ? CanvasHeight - PixelsPerLogUnit * Math.Log((double)price) : (double)price;
}
