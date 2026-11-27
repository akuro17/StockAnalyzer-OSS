using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// A smooth curve through any number of anchors (DSL expansion: PchipCurve, CubicSplineCurve). Placed like the polyline: every click adds an anchor, the last anchor follows the pointer, a double
/// click or Enter finishes. <see cref="InterpolationCurveValues"/> samples the curve in bar space (it needs the candles, so the object is recalculated by
/// <see cref="DeferredComputationRecalculator"/>, which is cheap and therefore also runs while an anchor is dragged). Until a result exists for the CURRENT anchors the object draws the straight
/// polyline through them. Computed state is private (persistence reads every public settable property).
/// </summary>
public abstract class InterpolationCurveObjectBase : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    private readonly NativeResourceGate _gate = new();
    private readonly SKPath _path = new();
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };

    private InterpolationCurve _curve = InterpolationCurve.Empty;
    private long _computedSignature;
    private bool _isComputed;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;

    protected InterpolationCurveObjectBase()
    {
    }

    protected InterpolationCurveObjectBase(IEnumerable<ChartPoint> points)
    {
        Points.AddRange(points);
    }

    public string? CustomName { get; set; }

    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;

    public bool IsMoveAxisModeExplicit { get; set; }

    public Guid Id { get; } = Guid.NewGuid();

    public abstract ChartObjectType Type { get; }

    public List<ChartPoint> Points { get; } = new();

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;

    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;

    public bool IsSelected { get; set; }

    public bool IsVisible { get; set; } = true;

    public bool IsLocked { get; set; }

    public int PanelIndex { get; set; } = -1;

    public int ZIndex { get; set; }

    public int Priority { get; set; }

    public int AnchorPointIndex { get; set; }

    public SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);

    /// <summary>The interpolation of this tool.</summary>
    protected abstract InterpolationCurveKind Kind { get; }

    /// <summary>
    /// How the anchors are connected: <see cref="InterpolationCurveMode.TimeSeries"/> (a function of time, anchors sorted by time; the value of a file that has none) or
    /// <see cref="InterpolationCurveMode.Path"/> (the placement order, loops allowed; what a newly placed curve starts with, set by the tool behaviour).
    /// </summary>
    [Category("Parameters")]
    [DisplayName("Curve Mode")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public InterpolationCurveMode CurveMode { get; set; } = InterpolationCurveMode.TimeSeries;

    /// <summary>The cached curve (tests read it; it is not persisted).</summary>
    internal InterpolationCurve Curve
    {
        get
        {
            lock (_gate.SyncRoot)
            {
                return _curve;
            }
        }
    }

    /// <summary>Adds an anchor (the placement click of a variable-point tool).</summary>
    public void AddPoint(ChartPoint point) => Points.Add(point);

    /// <summary>Recalculates the curve for the current anchors from <paramref name="candles"/> (oldest first).</summary>
    public void Recalculate(IReadOnlyList<CoreCandleData> candles)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed)
            {
                return;
            }

            var anchors = new List<DrawingAnchor>(Points.Count);
            foreach (ChartPoint point in Points)
            {
                anchors.Add(point.ToAnchor());
            }

            _curve = InterpolationCurveValues.Calculate(candles, anchors, Kind, CurveMode);
            _computedSignature = Signature();
            _isComputed = true;
            _hasCachedValues = false;
        }
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || canvas == null || transform == null || Points.Count < 2)
            {
                return;
            }

            SKColor color = SkiaColor;
            _linePaint.Color = color;
            _linePaint.StrokeWidth = (float)Thickness;
            _path.Rewind();

            bool smooth = _isComputed && _computedSignature == Signature() && _curve.IsOk;
            if (smooth)
            {
                bool started = false;
                for (int i = 0; i < _curve.Count; i++)
                {
                    global::Avalonia.Point p = transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i]));
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                    {
                        continue;
                    }

                    if (started)
                    {
                        _path.LineTo((float)p.X, (float)p.Y);
                    }
                    else
                    {
                        _path.MoveTo((float)p.X, (float)p.Y);
                        started = true;
                    }
                }
            }
            else
            {
                // No result for the current anchors (placement, a drag before the recalculation, a load before the data): the straight polyline through the anchors.
                for (int i = 0; i < Points.Count; i++)
                {
                    global::Avalonia.Point p = transform.ChartToScreen(Points[i]);
                    if (i == 0)
                    {
                        _path.MoveTo((float)p.X, (float)p.Y);
                    }
                    else
                    {
                        _path.LineTo((float)p.X, (float)p.Y);
                    }
                }
            }

            canvas.DrawPath(_path, _linePaint);

            if (IsSelected)
            {
                for (int i = 0; i < Points.Count; i++)
                {
                    SelectionHandleRenderer.Draw(canvas, transform.ChartToScreen(Points[i]), AnchorPointIndex == i ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
                }
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (_gate.IsDisposed || !IsVisible || transform == null || Points.Count < 2)
        {
            return false;
        }

        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed)
            {
                return false;
            }

            bool smooth = _isComputed && _computedSignature == Signature() && _curve.IsOk;
            int count = smooth ? _curve.Count : Points.Count;
            SKPoint target = new((float)screenPoint.X, (float)screenPoint.Y);
            SKPoint previous = default;
            for (int i = 0; i < count; i++)
            {
                global::Avalonia.Point p = smooth
                    ? transform.ChartToScreen(new ChartPoint(_curve.Times[i], _curve.Prices[i]))
                    : transform.ChartToScreen(Points[i]);
                var current = new SKPoint((float)p.X, (float)p.Y);
                if (i > 0 && BezierSplineMath.DistancePointToSegment(target, previous, current) <= tolerance)
                {
                    return true;
                }

                previous = current;
            }

            return false;
        }
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            ChartPoint p = Points[i];
            Points[i] = new ChartPoint(p.Time.Add(timeDelta), p.Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !_isComputed || _computedSignature != Signature())
            {
                return Array.Empty<DrawingCalculatedValue>();
            }

            var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
            if (!_hasCachedValues || _cachedValuesColor != color)
            {
                _cachedValues = InterpolationCurveValues.Calculate(_curve, color);
                _cachedValuesColor = color;
                _hasCachedValues = true;
            }

            return _cachedValues;
        }
    }

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed())
            {
                return;
            }

            _path.Dispose();
            _linePaint.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A number that changes when the anchors change (count, times, prices); no allocation.</summary>
    private long Signature()
    {
        unchecked
        {
            long hash = Points.Count;
            hash = (hash * 1000003L) ^ (long)CurveMode;
            for (int i = 0; i < Points.Count; i++)
            {
                hash = (hash * 1000003L) ^ Points[i].Time.Ticks;
                hash = (hash * 1000003L) ^ Points[i].Price.GetHashCode();
            }

            return hash;
        }
    }
}

/// <summary>A PCHIP (monotone cubic Hermite) curve through the anchors.</summary>
public sealed class PchipCurveObject : InterpolationCurveObjectBase
{
    public PchipCurveObject()
    {
    }

    public PchipCurveObject(List<ChartPoint> points)
        : base(points)
    {
    }

    public override ChartObjectType Type => ChartObjectType.PchipCurve;

    protected override InterpolationCurveKind Kind => InterpolationCurveKind.Pchip;
}

/// <summary>A natural cubic spline through the anchors.</summary>
public sealed class CubicSplineCurveObject : InterpolationCurveObjectBase
{
    public CubicSplineCurveObject()
    {
    }

    public CubicSplineCurveObject(List<ChartPoint> points)
        : base(points)
    {
    }

    public override ChartObjectType Type => ChartObjectType.CubicSplineCurve;

    protected override InterpolationCurveKind Kind => InterpolationCurveKind.CubicSpline;
}
