using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

public class HorizontalLineObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.HorizontalLine;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;

    [Category("Style")]
    [DisplayName("Stroke Style")]
    [ParameterTag(DrawingParameterTags.Common)]
    public DrawingStrokeStyle StrokeStyle { get; set; } = DrawingStrokeStyle.Solid;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;
    public int PanelIndex { get; set; } = -1;

    private readonly SKPaint _paint = new SKPaint
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true
    };

    public HorizontalLineObject(ChartPoint point)
    {
        Points.Add(point);
    }

    // A removed object is disposed at once while a frame that still holds it may render it on another thread (see NativeResourceGate).
    private readonly NativeResourceGate _gate = new NativeResourceGate();

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed()) return;
            _paint.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || Points.Count == 0) return;

            var pt = transform.ChartToScreen(Points[0]);
            float y = (float)pt.Y;

            _paint.Color = SkiaColor;
            _paint.StrokeWidth = (float)Thickness;
            _paint.PathEffect = StrokeStyle.ToSkPathEffect();

            // Use CanvasWidth from the transform to represent the actual chart drawing area
            // to prevent the line from extending into the Y-axis margin.
            float width = (float)transform.CanvasWidth;
            canvas.DrawLine(0, y, width, y, _paint);

            if (IsSelected)
            {
                SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(pt.X, y), AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0) return false;
        var pt = transform.ChartToScreen(Points[0]);
        return Math.Abs(screenPoint.Y - pt.Y) <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            // Horizontal line only cares about Price translation
            Points[0] = new ChartPoint(Points[0].Time, Points[0].Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        return HorizontalLineValues.Calculate(Points[0].ToAnchor(), color);
    }
}
