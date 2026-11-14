using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Cross line: one anchor with a horizontal line through its price and a vertical line through its time,
/// both spanning the whole drawing area. Style properties and defaults are those of <see cref="HorizontalLineObject"/>.
/// </summary>
public class CrossLineObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.CrossLine;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;

    [System.ComponentModel.Category("Style")]
    [System.ComponentModel.DisplayName("Stroke Style")]
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

    public CrossLineObject(ChartPoint point)
    {
        Points.Add(point);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

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

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || Points.Count == 0) return;

            var pt = transform.ChartToScreen(Points[0]);
            var clip = transform.ScreenRect;
            float x = (float)pt.X;
            float y = (float)pt.Y;

            _paint.Color = SkiaColor;
            _paint.StrokeWidth = (float)Thickness;
            _paint.PathEffect = StrokeStyle.ToSkPathEffect();

            canvas.DrawLine((float)clip.Left, y, (float)clip.Right, y, _paint);
            canvas.DrawLine(x, (float)clip.Top, x, (float)clip.Bottom, _paint);

            if (IsSelected)
            {
                SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(pt.X, pt.Y), AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0) return false;

        var pt = transform.ChartToScreen(Points[0]);
        return Math.Abs(screenPoint.Y - pt.Y) <= tolerance || Math.Abs(screenPoint.X - pt.X) <= tolerance;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            Points[0] = new ChartPoint(Points[0].Time.Add(timeDelta), Points[0].Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        return CrossLineValues.Calculate(Points[0].ToAnchor(), timestamp, color);
    }
}
