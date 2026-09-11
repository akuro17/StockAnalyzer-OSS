using System;
using Avalonia;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// A composite coordinate transform used for sub-windows (panels).
/// It maps X coordinates using the main chart's transform (to perfectly align timeline, gapless axes, etc.),
/// while mapping Y coordinates using a local panel-specific transform (for scaled indicator values).
/// </summary>
public class SubWindowCoordinateTransform : ICoordinateTransform
{
    private ICoordinateTransform _mainTransform;
    public GenericCoordinateTransform YTransform { get; private set; }

    public SubWindowCoordinateTransform(ICoordinateTransform mainTransform, GenericCoordinateTransform yTransform)
    {
        _mainTransform = mainTransform;
        YTransform = yTransform;
    }

    public void UpdateTransforms(ICoordinateTransform mainTransform, GenericCoordinateTransform yTransform)
    {
        _mainTransform = mainTransform;
        YTransform = yTransform;
    }

    public global::Avalonia.Point ChartToScreen(ChartPoint chartPoint)
    {
        // X comes from the main chart (already handles Mode, Padding, TimeMap, etc.)
        double x = _mainTransform.ChartToScreen(new ChartPoint(chartPoint.Time, 0)).X;
        
        // Y comes from the local panel transform
        double y = YTransform.ChartToScreen(new ChartPoint(DateTime.MinValue, chartPoint.Price)).Y;
        
        return new global::Avalonia.Point(x, y);
    }

    public ChartPoint ScreenToChart(global::Avalonia.Point screenPoint)
    {
        var mainPoint = _mainTransform.ScreenToChart(new global::Avalonia.Point(screenPoint.X, 0));
        var yPoint = YTransform.ScreenToChart(new global::Avalonia.Point(0, screenPoint.Y));
        
        return new ChartPoint(mainPoint.Time, yPoint.Price);
    }

    public global::Avalonia.Point NumericToScreen(double x, double y)
    {
        double screenX = _mainTransform.NumericToScreen(x, 0).X;
        double screenY = YTransform.NumericToScreen(0, y).Y;
        return new global::Avalonia.Point(screenX, screenY);
    }

    public (double x, double y) ScreenToNumeric(global::Avalonia.Point screenPoint)
    {
        double x = _mainTransform.ScreenToNumeric(new global::Avalonia.Point(screenPoint.X, 0)).x;
        double y = YTransform.ScreenToNumeric(new global::Avalonia.Point(0, screenPoint.Y)).y;
        return (x, y);
    }

    public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null)
    {
        YTransform.UpdateRange(DateTime.MinValue, DateTime.MaxValue, minPrice, maxPrice, null, newCanvasHeight);
    }

    public void SetTimeMap(System.Collections.Generic.IReadOnlyList<DateTime> timeMap)
    {
    }

    public System.Collections.Generic.IReadOnlyList<DateTime>? TimeMap => _mainTransform.TimeMap;

    public double CanvasWidth => _mainTransform.CanvasWidth;
    
    public double CanvasHeight => YTransform.CanvasHeight;
    
    public global::Avalonia.Rect ScreenRect => new global::Avalonia.Rect(0, 0, CanvasWidth, CanvasHeight);
    
    public double ViewportX => _mainTransform.ViewportX;
    
    public double ViewportWidth => _mainTransform.ViewportWidth;

    public double ScaleX => _mainTransform.ScaleX;

    public double GetXFromIndex(double index) => _mainTransform.GetXFromIndex(index);

    public double GetYFromPrice(decimal price) => YTransform.GetYFromPrice(price);

    public PriceScaleType PriceScale => YTransform.PriceScale;

    public TransformMetadata Metadata => _mainTransform.Metadata;
}
