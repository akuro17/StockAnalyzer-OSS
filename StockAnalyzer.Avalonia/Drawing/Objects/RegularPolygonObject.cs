using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Regular polygon: anchor 0 is the centre, anchor 1 is vertex 0. The polygon is built in SCREEN space (radius = pixel distance of the two anchors, first angle = the direction of anchor 1), so it stays
/// regular whatever the horizontal and vertical zoom of the chart is; the other <c>Sides - 1</c> vertices are computed at every render with <see cref="Math.SinCos(double)"/> into a preallocated
/// array (no allocation per frame). The circumscribed circle has the radius <c>R</c>, the inscribed circle <c>R cos(pi / N)</c>. The tool has no headless (Data-tab) value: its geometry only exists in pixels (DX-8 = A).
/// </summary>
public sealed class RegularPolygonObject : IChartObject, IDisposable
{
    /// <summary>The fewest sides.</summary>
    public const int MinSides = 3;

    /// <summary>The most sides (the size of the preallocated vertex array).</summary>
    public const int MaxSides = 24;

    /// <summary>The number of sides a new polygon starts with.</summary>
    public const int DefaultSides = 6;

    private const float CircleStrokeScale = 0.75f;
    private const float CircleDashOn = 4f;
    private const float CircleDashOff = 3f;
    private const byte CircleAlpha = 160;

    private readonly NativeResourceGate _gate = new();
    private readonly SKPoint[] _vertices = new SKPoint[MaxSides];
    private readonly SKPath _path = new();
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Miter };
    private readonly SKPaint _circlePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPathEffect _circleDash = SKPathEffect.CreateDash(new[] { CircleDashOn, CircleDashOff }, 0f);

    private int _sides = DefaultSides;
    private Color _polygonFillColor = DrawingThemeContext.DefaultColor;
    private Color _circumscribedFillColor = DrawingThemeContext.DefaultColor;
    private Color _inscribedFillColor = DrawingThemeContext.DefaultColor;
    private int _polygonFillOpacity = DrawingFillOpacity.DefaultPercent;
    private int _circumscribedFillOpacity = DrawingFillOpacity.DefaultPercent;
    private int _inscribedFillOpacity = DrawingFillOpacity.DefaultPercent;

    public RegularPolygonObject()
    {
        _circlePaint.PathEffect = _circleDash;
    }

    public RegularPolygonObject(ChartPoint center, ChartPoint vertex)
        : this()
    {
        Points.Add(center);
        Points.Add(vertex);
    }

    public string? CustomName { get; set; }

    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;

    public bool IsMoveAxisModeExplicit { get; set; }

    public Guid Id { get; } = Guid.NewGuid();

    public ChartObjectType Type => ChartObjectType.RegularPolygon;

    public List<ChartPoint> Points { get; } = new(2);

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

    /// <summary>Number of sides (3 .. 24).</summary>
    [Category("Parameters")]
    [DisplayName("Sides")]
    [Range(MinSides, MaxSides)]
    [Display(Order = 1)]
    public int Sides
    {
        get => _sides;
        set => _sides = Math.Clamp(value, MinSides, MaxSides);
    }

    /// <summary>Draws the circle through the vertices.</summary>
    [Category("Parameters")]
    [DisplayName("Show Circumscribed Circle")]
    [Display(Order = 2)]
    public bool ShowCircumscribedCircle { get; set; }

    /// <summary>Draws the circle touching the middle of every side.</summary>
    [Category("Parameters")]
    [DisplayName("Show Inscribed Circle")]
    [Display(Order = 3)]
    public bool ShowInscribedCircle { get; set; }

    /// <summary>Fill colour of the polygon area (the default is the default drawing colour, the same as the line colour of a new drawing).</summary>
    [Category("Fill Style")]
    [DisplayName("Polygon Fill Color")]
    [Display(Order = 1)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public Color PolygonFillColor
    {
        get => _polygonFillColor;
        set => _polygonFillColor = DrawingFillOpacity.NormalizeFillColor(value);
    }

    /// <summary>Opacity (0-100 %, default 15) of the polygon fill; 0 draws no fill. The polygon fill does not depend on the circle toggles.</summary>
    [Category("Fill Style")]
    [DisplayName("Polygon Fill Opacity")]
    [Range(0, DrawingFillOpacity.FullPercent)]
    [Display(Order = 2)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public int PolygonFillOpacity
    {
        get => _polygonFillOpacity;
        set => _polygonFillOpacity = Math.Clamp(value, 0, DrawingFillOpacity.FullPercent);
    }

    /// <summary>Fill colour of the circumscribed circle area. Only used while <see cref="ShowCircumscribedCircle"/> is on.</summary>
    [Category("Fill Style")]
    [DisplayName("Circumscribed Fill Color")]
    [Display(Order = 3)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public Color CircumscribedFillColor
    {
        get => _circumscribedFillColor;
        set => _circumscribedFillColor = DrawingFillOpacity.NormalizeFillColor(value);
    }

    /// <summary>Opacity (0-100 %, default 15) of the circumscribed circle fill; 0 draws no fill. Ignored (drawing and hit test) while <see cref="ShowCircumscribedCircle"/> is off.</summary>
    [Category("Fill Style")]
    [DisplayName("Circumscribed Fill Opacity")]
    [Range(0, DrawingFillOpacity.FullPercent)]
    [Display(Order = 4)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public int CircumscribedFillOpacity
    {
        get => _circumscribedFillOpacity;
        set => _circumscribedFillOpacity = Math.Clamp(value, 0, DrawingFillOpacity.FullPercent);
    }

    /// <summary>Fill colour of the inscribed circle area. Only used while <see cref="ShowInscribedCircle"/> is on.</summary>
    [Category("Fill Style")]
    [DisplayName("Inscribed Fill Color")]
    [Display(Order = 5)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public Color InscribedFillColor
    {
        get => _inscribedFillColor;
        set => _inscribedFillColor = DrawingFillOpacity.NormalizeFillColor(value);
    }

    /// <summary>Opacity (0-100 %, default 15) of the inscribed circle fill; 0 draws no fill. Ignored (drawing and hit test) while <see cref="ShowInscribedCircle"/> is off.</summary>
    [Category("Fill Style")]
    [DisplayName("Inscribed Fill Opacity")]
    [Range(0, DrawingFillOpacity.FullPercent)]
    [Display(Order = 6)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public int InscribedFillOpacity
    {
        get => _inscribedFillOpacity;
        set => _inscribedFillOpacity = Math.Clamp(value, 0, DrawingFillOpacity.FullPercent);
    }

    /// <summary>
    /// Writes the vertices of the regular polygon with the centre (<paramref name="centerX"/>, <paramref name="centerY"/>) and vertex 0 at (<paramref name="vertexX"/>, <paramref name="vertexY"/>)
    /// into <paramref name="destination"/> and returns their number: <paramref name="sides"/> (clamped to the supported range), or 0 when the two points do not give a radius
    /// (equal points, a non-finite coordinate). Vertex k lies at the angle <c>phi0 + 2 pi k / N</c>. No allocation.
    /// </summary>
    internal static int ComputeVertices(double centerX, double centerY, double vertexX, double vertexY, int sides, SKPoint[] destination)
    {
        double dx = vertexX - centerX;
        double dy = vertexY - centerY;
        double radius = Math.Sqrt((dx * dx) + (dy * dy));
        if (!double.IsFinite(radius) || !(radius > 0.0) || !double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            return 0;
        }

        int count = Math.Clamp(sides, MinSides, MaxSides);
        double firstAngle = Math.Atan2(dy, dx);
        double step = Math.Tau / count;
        for (int k = 0; k < count; k++)
        {
            (double sin, double cos) = Math.SinCos(firstAngle + (step * k));
            destination[k] = new SKPoint((float)(centerX + (radius * cos)), (float)(centerY + (radius * sin)));
        }

        return count;
    }

    /// <summary>The radius of the inscribed circle of a regular polygon with <paramref name="sides"/> sides and circumscribed radius <paramref name="radius"/>.</summary>
    internal static double InscribedRadius(double radius, int sides) => radius * Math.Cos(Math.PI / Math.Clamp(sides, MinSides, MaxSides));

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || canvas == null || transform == null || Points.Count < 2)
            {
                return;
            }

            global::Avalonia.Point center = transform.ChartToScreen(Points[0]);
            global::Avalonia.Point vertex = transform.ChartToScreen(Points[1]);
            int count = ComputeVertices(center.X, center.Y, vertex.X, vertex.Y, _sides, _vertices);
            SKColor color = SkiaColor;
            if (count > 0)
            {
                _path.Rewind();
                _path.MoveTo(_vertices[0]);
                for (int k = 1; k < count; k++)
                {
                    _path.LineTo(_vertices[k]);
                }

                _path.Close();

                double dx = vertex.X - center.X;
                double dy = vertex.Y - center.Y;
                double radius = Math.Sqrt((dx * dx) + (dy * dy));

                // Areas from the back to the front: circumscribed circle, polygon, inscribed circle (a circle area is only drawn while its toggle is on).
                if (ShowCircumscribedCircle && CircumscribedFillOpacity > 0)
                {
                    _fillPaint.Color = DrawingFillOpacity.Resolve(CircumscribedFillColor, CircumscribedFillOpacity);
                    canvas.DrawCircle((float)center.X, (float)center.Y, (float)radius, _fillPaint);
                }

                if (PolygonFillOpacity > 0)
                {
                    _fillPaint.Color = DrawingFillOpacity.Resolve(PolygonFillColor, PolygonFillOpacity);
                    canvas.DrawPath(_path, _fillPaint);
                }

                if (ShowInscribedCircle && InscribedFillOpacity > 0)
                {
                    _fillPaint.Color = DrawingFillOpacity.Resolve(InscribedFillColor, InscribedFillOpacity);
                    canvas.DrawCircle((float)center.X, (float)center.Y, (float)InscribedRadius(radius, count), _fillPaint);
                }

                _paint.Color = color;
                _paint.StrokeWidth = (float)Thickness;
                canvas.DrawPath(_path, _paint);

                _circlePaint.Color = color.WithAlpha(CircleAlpha);
                _circlePaint.StrokeWidth = (float)Math.Max(1.0, Thickness * CircleStrokeScale);
                if (ShowCircumscribedCircle)
                {
                    canvas.DrawCircle((float)center.X, (float)center.Y, (float)radius, _circlePaint);
                }

                if (ShowInscribedCircle)
                {
                    canvas.DrawCircle((float)center.X, (float)center.Y, (float)InscribedRadius(radius, count), _circlePaint);
                }
            }

            if (IsSelected)
            {
                SelectionHandleRenderer.Draw(canvas, center, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
                SelectionHandleRenderer.Draw(canvas, vertex, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || transform == null || Points.Count < 2)
            {
                return false;
            }

            global::Avalonia.Point center = transform.ChartToScreen(Points[0]);
            global::Avalonia.Point vertex = transform.ChartToScreen(Points[1]);
            int count = ComputeVertices(center.X, center.Y, vertex.X, vertex.Y, _sides, _vertices);
            if (count == 0)
            {
                return false;
            }

            for (int k = 0; k < count; k++)
            {
                SKPoint a = _vertices[k];
                SKPoint b = _vertices[(k + 1) % count];
                if (DistanceToSegment(screenPoint.X, screenPoint.Y, a.X, a.Y, b.X, b.Y) <= tolerance)
                {
                    return true;
                }
            }

            double fromCenter = Math.Sqrt(Math.Pow(screenPoint.X - center.X, 2) + Math.Pow(screenPoint.Y - center.Y, 2));
            double radius = Math.Sqrt(Math.Pow(vertex.X - center.X, 2) + Math.Pow(vertex.Y - center.Y, 2));
            double inscribed = InscribedRadius(radius, count);
            if ((ShowCircumscribedCircle && Math.Abs(fromCenter - radius) <= tolerance)
                || (ShowInscribedCircle && Math.Abs(fromCenter - inscribed) <= tolerance))
            {
                return true;
            }

            // Filled areas: only an area that is drawn (opacity above 0, and its circle shown) takes a click inside it.
            return (PolygonFillOpacity > 0 && IsInsideConvexPolygon(screenPoint.X, screenPoint.Y, count))
                || (ShowCircumscribedCircle && CircumscribedFillOpacity > 0 && fromCenter <= radius)
                || (ShowInscribedCircle && InscribedFillOpacity > 0 && fromCenter <= inscribed);
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

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed())
            {
                return;
            }

            _path.Dispose();
            _paint.Dispose();
            _circlePaint.Dispose();
            _fillPaint.Dispose();
            _circleDash.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>True when the point lies inside (or on) the convex polygon held in the first <paramref name="count"/> vertices: the cross products with every edge share one sign. No allocation.</summary>
    private bool IsInsideConvexPolygon(double px, double py, int count)
    {
        bool hasPositive = false;
        bool hasNegative = false;
        for (int k = 0; k < count; k++)
        {
            SKPoint a = _vertices[k];
            SKPoint b = _vertices[(k + 1) % count];
            double cross = ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
            hasPositive |= cross > 0.0;
            hasNegative |= cross < 0.0;
            if (hasPositive && hasNegative)
            {
                return false;
            }
        }

        return true;
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax;
        double aby = by - ay;
        double lengthSquared = (abx * abx) + (aby * aby);
        double t = lengthSquared > 0.0 ? Math.Clamp((((px - ax) * abx) + ((py - ay) * aby)) / lengthSquared, 0.0, 1.0) : 0.0;
        double dx = px - (ax + (t * abx));
        double dy = py - (ay + (t * aby));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
