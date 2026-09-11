using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// SkiaSharp-based custom draw operation for chart rendering.
/// Extracted from ChartBaseControl inner class for maintainability.
/// Implements ICustomDrawOperation for Avalonia rendering pipeline.
/// </summary>
internal sealed class ChartDrawOperation : ICustomDrawOperation
{
    private readonly global::Avalonia.Rect _bounds;
    private readonly ChartDataSnapshot _snapshot;
    private readonly Renderers.IChartRenderer _mainRenderer;
    private readonly Renderers.ChartRenderPipeline _pipeline;
    
    private readonly Renderers.RulerRenderer _rulerRenderer;
    private readonly global::Avalonia.Point _mousePosition;
    private readonly bool _isCrosshairVisible;
    private readonly global::Avalonia.Rect _controlBounds;
    private readonly Renderers.IChartRenderConfig _renderConfig;
    private readonly Drawing.IChartObject? _currentDrawingObject;
    private readonly Drawing.ChartObjectManager _objectManager;
    private readonly Drawing.ICoordinateTransform _coordinateTransform;
    private readonly global::Avalonia.Point? _snapPoint;
    private readonly ChartType _chartType;
    private readonly Renderers.ChartLayoutContext _layout;

    public ChartDrawOperation(
        global::Avalonia.Rect bounds,  
        ChartDataSnapshot snapshot,
        ChartType chartType,
        Renderers.IChartRenderer mainRenderer,
        Renderers.ChartRenderPipeline pipeline,
        global::Avalonia.Point mousePosition, 
        bool isCrosshairVisible, 
        global::Avalonia.Rect controlBounds,
        Renderers.RulerRenderer rulerRenderer,

        Drawing.ChartObjectManager objectManager,
        Drawing.ICoordinateTransform coordinateTransform,
        Renderers.IChartRenderConfig renderConfig,
        Renderers.ChartLayoutContext layout,
        global::Avalonia.Point? snapPoint = null,
        Drawing.IChartObject? currentDrawingObject = null) 
    {
        _bounds = bounds;
        _snapshot = snapshot;
        _chartType = chartType;
        _mainRenderer = mainRenderer;
        _pipeline = pipeline;
        _layout = layout;
        
        _rulerRenderer = rulerRenderer; 
        
        _objectManager = objectManager;
        _coordinateTransform = coordinateTransform;
        _snapPoint = snapPoint;
        _mousePosition = mousePosition;
        _isCrosshairVisible = isCrosshairVisible;
        _controlBounds = controlBounds;
        _renderConfig = renderConfig;
        _currentDrawingObject = currentDrawingObject;
    }
    
    public global::Avalonia.Rect Bounds => _bounds;

    public void Dispose()
    {
        // No disposal needed here. 
    }
    
    public bool Equals(ICustomDrawOperation? other)
        // Rendering depends on mutable pipeline, object-manager, transform, and layout state.
        // Reusing a previous custom operation can retain Skia pixels from an earlier pointer frame.
        => false;

    public override bool Equals(object? obj) => Equals(obj as ICustomDrawOperation);

    public override int GetHashCode()
    {
        return System.HashCode.Combine(_bounds, _snapshot, _mousePosition, _isCrosshairVisible);
    }

    public bool HitTest(global::Avalonia.Point p) => _bounds.Contains(p);

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;

        if (feature == null)
        {
            return;
        }

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();

        _pipeline.Execute(
            canvas,
            _layout,
            _snapshot,
            _mainRenderer,
            _renderConfig,
            _chartType,
            _coordinateTransform,
            _objectManager,
            _rulerRenderer,
            _mousePosition,
            _isCrosshairVisible,
            _controlBounds,
            _snapPoint,
            _currentDrawingObject);

        canvas.Restore();
    }
}
