using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Avalonia.Drawing;

public class TriangleObject : IChartObject, IDisposable
{
    private const int AngleLabelTenthsPerDegree = 10;
    private const int MaximumInteriorAngleTenths = 180 * AngleLabelTenthsPerDegree;
    private static readonly string[] InteriorAngleLabels = CreateInteriorAngleLabels();

    private readonly SKPaint _strokePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true
    };

    private readonly SKPaint _reflectedStrokePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(
            [ChartConstants.TriangleReflectionDashInterval, ChartConstants.TriangleReflectionDashInterval],
            0f)
    };

    private readonly SKPaint _angleTextPaint = new()
    {
        IsAntialias = true,
        TextAlign = SKTextAlign.Center
    };

    private readonly SKPaint _centerConstructionPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(
            [ChartConstants.TriangleCenterConstructionDashInterval, ChartConstants.TriangleCenterConstructionDashInterval],
            0f)
    };

    private readonly SKPaint _centerPointPaint = new()
    {
        Style = SKPaintStyle.Fill,
        IsAntialias = true
    };

    private readonly SKPath _trianglePath = new();
    private readonly SKPath _reflectedTrianglePath = new();
    private bool _disposed;

    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Triangle;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _strokePaint.Dispose();
        _reflectedStrokePaint.Dispose();
        _angleTextPaint.Dispose();
        _centerConstructionPaint.Dispose();
        _centerPointPaint.Dispose();
        _trianglePath.Dispose();
        _reflectedTrianglePath.Dispose();
    }

    [Category("Analysis")]
    [DisplayName("Show Interior Angles")]
    [Display(Order = 10)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowInteriorAngles { get; set; } = false;

    [Category("Analysis")]
    [DisplayName("Show Reflected Triangle")]
    [Display(Order = 20)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowReflectedTriangle { get; set; } = false;

    [Category("Analysis")]
    [DisplayName("Show Centroid")]
    [Display(Order = 30)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowCentroid { get; set; } = false;

    [Category("Analysis")]
    [DisplayName("Show Circumcenter")]
    [Display(Order = 40)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowCircumcenter { get; set; } = false;

    [Category("Analysis")]
    [DisplayName("Show Incenter")]
    [Display(Order = 50)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowIncenter { get; set; } = false;

    [Category("Analysis")]
    [DisplayName("Show Orthocenter")]
    [Display(Order = 60)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowOrthocenter { get; set; } = false;

    [Category("Analysis")]
    [DisplayName("Show Excenters")]
    [Display(Order = 70)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowExcenters { get; set; } = false;

    public TriangleObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
    {
        Points.Add(p1);
        Points.Add(p2);
        Points.Add(p3);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Points.Count < 3) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        var p3 = transform.ChartToScreen(Points[2]);

        _strokePaint.Color = SkiaColor;
        _strokePaint.StrokeWidth = (float)Thickness;
        _trianglePath.Rewind();
        _trianglePath.MoveTo((float)p1.X, (float)p1.Y);
        _trianglePath.LineTo((float)p2.X, (float)p2.Y);
        _trianglePath.LineTo((float)p3.X, (float)p3.Y);
        _trianglePath.Close();
        canvas.DrawPath(_trianglePath, _strokePaint);

        if ((ShowCentroid || ShowCircumcenter || ShowIncenter || ShowOrthocenter || ShowExcenters) &&
            TryGetCenterGeometry(p1, p2, p3, out var centers))
        {
            _centerConstructionPaint.Color = SkiaColor;
            _centerConstructionPaint.StrokeWidth = (float)Thickness;
            _centerPointPaint.Color = SkiaColor;

            if (ShowCentroid)
            {
                DrawCentroid(canvas, p1, p2, p3, centers.Centroid);
            }

            if (ShowCircumcenter)
            {
                DrawCircumcenter(canvas, p1, p2, p3, centers.Circumcenter, centers.Circumradius);
            }

            if (ShowIncenter)
            {
                DrawIncenter(canvas, p1, p2, p3, centers.Incenter, centers.Inradius);
            }

            if (ShowOrthocenter)
            {
                DrawOrthocenter(canvas, p1, p2, p3, centers.Orthocenter);
            }

            if (ShowExcenters)
            {
                DrawExcenters(canvas, centers);
            }
        }

        if (ShowReflectedTriangle && TryGetReflectedVertex(p1, p2, p3, AnchorPointIndex, out var reflectedVertex, out var baseStart, out var baseEnd))
        {
            _reflectedStrokePaint.Color = SkiaColor;
            _reflectedStrokePaint.StrokeWidth = (float)Thickness;
            _reflectedTrianglePath.Rewind();
            _reflectedTrianglePath.MoveTo((float)baseStart.X, (float)baseStart.Y);
            _reflectedTrianglePath.LineTo((float)reflectedVertex.X, (float)reflectedVertex.Y);
            _reflectedTrianglePath.LineTo((float)baseEnd.X, (float)baseEnd.Y);
            _reflectedTrianglePath.Close();
            canvas.DrawPath(_reflectedTrianglePath, _reflectedStrokePaint);
        }

        if (ShowInteriorAngles)
        {
            _angleTextPaint.Color = DrawingThemeContext.MainTextSkColor;
            _angleTextPaint.TextSize = DrawingThemeContext.DetailFontSize;
            DrawInteriorAngle(canvas, p1, p2, p3);
            DrawInteriorAngle(canvas, p2, p1, p3);
            DrawInteriorAngle(canvas, p3, p1, p2);
        }

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, p3, AnchorPointIndex == 2 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 3) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        var p3 = transform.ChartToScreen(Points[2]);

        // Check distance to any of the 3 segments
        if (DistancePointToSegment(screenPoint, p1, p2) <= tolerance) return true;
        if (DistancePointToSegment(screenPoint, p2, p3) <= tolerance) return true;
        if (DistancePointToSegment(screenPoint, p3, p1) <= tolerance) return true;

        return false;
    }

    private static double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = (v.X - w.X) * (v.X - w.X) + (v.Y - w.Y) * (v.Y - w.Y);
        if (l2 == 0) return Math.Sqrt((p.X - v.X) * (p.X - v.X) + (p.Y - v.Y) * (p.Y - v.Y));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        global::Avalonia.Point projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt((p.X - projection.X) * (p.X - projection.X) + (p.Y - projection.Y) * (p.Y - projection.Y));
    }

    internal static double GetInteriorAngleDegrees(global::Avalonia.Point vertex, global::Avalonia.Point firstNeighbor, global::Avalonia.Point secondNeighbor)
    {
        double firstX = firstNeighbor.X - vertex.X;
        double firstY = firstNeighbor.Y - vertex.Y;
        double secondX = secondNeighbor.X - vertex.X;
        double secondY = secondNeighbor.Y - vertex.Y;
        double firstLengthSquared = firstX * firstX + firstY * firstY;
        double secondLengthSquared = secondX * secondX + secondY * secondY;
        if (firstLengthSquared <= ChartConstants.TriangleMinimumSquaredEdgeLength ||
            secondLengthSquared <= ChartConstants.TriangleMinimumSquaredEdgeLength)
        {
            return double.NaN;
        }

        double cosine = (firstX * secondX + firstY * secondY) /
                        Math.Sqrt(firstLengthSquared * secondLengthSquared);
        return Math.Acos(Math.Clamp(cosine, -1.0, 1.0)) * MathConstants.RadToDeg;
    }

    internal static bool TryGetReflectedVertex(
        global::Avalonia.Point p1,
        global::Avalonia.Point p2,
        global::Avalonia.Point p3,
        int anchorPointIndex,
        out global::Avalonia.Point reflectedVertex,
        out global::Avalonia.Point baseStart,
        out global::Avalonia.Point baseEnd)
    {
        global::Avalonia.Point anchor;
        switch (anchorPointIndex)
        {
            case 1:
                anchor = p2;
                baseStart = p1;
                baseEnd = p3;
                break;
            case 2:
                anchor = p3;
                baseStart = p1;
                baseEnd = p2;
                break;
            default:
                anchor = p1;
                baseStart = p2;
                baseEnd = p3;
                break;
        }

        double baseX = baseEnd.X - baseStart.X;
        double baseY = baseEnd.Y - baseStart.Y;
        double baseLengthSquared = baseX * baseX + baseY * baseY;
        if (baseLengthSquared <= ChartConstants.TriangleMinimumSquaredEdgeLength)
        {
            reflectedVertex = default;
            return false;
        }

        double projectionScale = ((anchor.X - baseStart.X) * baseX + (anchor.Y - baseStart.Y) * baseY) /
                                 baseLengthSquared;
        double projectionX = baseStart.X + projectionScale * baseX;
        double projectionY = baseStart.Y + projectionScale * baseY;
        reflectedVertex = new global::Avalonia.Point(
            2.0 * projectionX - anchor.X,
            2.0 * projectionY - anchor.Y);
        return true;
    }

    internal static bool TryGetCenterGeometry(
        global::Avalonia.Point a,
        global::Avalonia.Point b,
        global::Avalonia.Point c,
        out TriangleCenterGeometry geometry)
    {
        geometry = default;
        if (!IsFinitePoint(a) || !IsFinitePoint(b) || !IsFinitePoint(c)) return false;

        double sideASquared = SquaredDistance(b, c);
        double sideBSquared = SquaredDistance(c, a);
        double sideCSquared = SquaredDistance(a, b);
        if (sideASquared <= ChartConstants.TriangleMinimumSquaredEdgeLength ||
            sideBSquared <= ChartConstants.TriangleMinimumSquaredEdgeLength ||
            sideCSquared <= ChartConstants.TriangleMinimumSquaredEdgeLength)
        {
            return false;
        }

        double twiceSignedArea = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        double twiceArea = Math.Abs(twiceSignedArea);
        if (!double.IsFinite(twiceArea) || twiceArea <= ChartConstants.TriangleMinimumTwiceArea) return false;

        double sideA = Math.Sqrt(sideASquared);
        double sideB = Math.Sqrt(sideBSquared);
        double sideC = Math.Sqrt(sideCSquared);
        double perimeter = sideA + sideB + sideC;
        double excenterDenominatorA = -sideA + sideB + sideC;
        double excenterDenominatorB = sideA - sideB + sideC;
        double excenterDenominatorC = sideA + sideB - sideC;
        if (!double.IsFinite(perimeter) ||
            perimeter <= ChartConstants.TriangleMinimumCenterDenominator ||
            excenterDenominatorA <= ChartConstants.TriangleMinimumCenterDenominator ||
            excenterDenominatorB <= ChartConstants.TriangleMinimumCenterDenominator ||
            excenterDenominatorC <= ChartConstants.TriangleMinimumCenterDenominator)
        {
            return false;
        }

        var centroid = new global::Avalonia.Point(
            (a.X + b.X + c.X) / 3.0,
            (a.Y + b.Y + c.Y) / 3.0);

        double circumcenterDenominator = 2.0 * twiceSignedArea;
        double aSquaredMagnitude = a.X * a.X + a.Y * a.Y;
        double bSquaredMagnitude = b.X * b.X + b.Y * b.Y;
        double cSquaredMagnitude = c.X * c.X + c.Y * c.Y;
        var circumcenter = new global::Avalonia.Point(
            (aSquaredMagnitude * (b.Y - c.Y) +
             bSquaredMagnitude * (c.Y - a.Y) +
             cSquaredMagnitude * (a.Y - b.Y)) / circumcenterDenominator,
            (aSquaredMagnitude * (c.X - b.X) +
             bSquaredMagnitude * (a.X - c.X) +
             cSquaredMagnitude * (b.X - a.X)) / circumcenterDenominator);
        double circumradius = Distance(circumcenter, a);

        var incenter = new global::Avalonia.Point(
            (sideA * a.X + sideB * b.X + sideC * c.X) / perimeter,
            (sideA * a.Y + sideB * b.Y + sideC * c.Y) / perimeter);
        double inradius = twiceArea / perimeter;

        var orthocenter = new global::Avalonia.Point(
            a.X + b.X + c.X - 2.0 * circumcenter.X,
            a.Y + b.Y + c.Y - 2.0 * circumcenter.Y);

        var excenterA = new global::Avalonia.Point(
            (-sideA * a.X + sideB * b.X + sideC * c.X) / excenterDenominatorA,
            (-sideA * a.Y + sideB * b.Y + sideC * c.Y) / excenterDenominatorA);
        var excenterB = new global::Avalonia.Point(
            (sideA * a.X - sideB * b.X + sideC * c.X) / excenterDenominatorB,
            (sideA * a.Y - sideB * b.Y + sideC * c.Y) / excenterDenominatorB);
        var excenterC = new global::Avalonia.Point(
            (sideA * a.X + sideB * b.X - sideC * c.X) / excenterDenominatorC,
            (sideA * a.Y + sideB * b.Y - sideC * c.Y) / excenterDenominatorC);
        double exradiusA = twiceArea / excenterDenominatorA;
        double exradiusB = twiceArea / excenterDenominatorB;
        double exradiusC = twiceArea / excenterDenominatorC;

        if (!IsFinitePoint(centroid) ||
            !IsFinitePoint(circumcenter) || !IsFinitePositive(circumradius) ||
            !IsFinitePoint(incenter) || !IsFinitePositive(inradius) ||
            !IsFinitePoint(orthocenter) ||
            !IsFinitePoint(excenterA) || !IsFinitePositive(exradiusA) ||
            !IsFinitePoint(excenterB) || !IsFinitePositive(exradiusB) ||
            !IsFinitePoint(excenterC) || !IsFinitePositive(exradiusC))
        {
            return false;
        }

        geometry = new TriangleCenterGeometry(
            centroid,
            circumcenter,
            circumradius,
            incenter,
            inradius,
            orthocenter,
            excenterA,
            exradiusA,
            excenterB,
            exradiusB,
            excenterC,
            exradiusC);
        return true;
    }

    private void DrawCentroid(
        SKCanvas canvas,
        global::Avalonia.Point a,
        global::Avalonia.Point b,
        global::Avalonia.Point c,
        global::Avalonia.Point centroid)
    {
        DrawConstructionLine(canvas, a, Midpoint(b, c));
        DrawConstructionLine(canvas, b, Midpoint(c, a));
        DrawConstructionLine(canvas, c, Midpoint(a, b));
        DrawCenterPoint(canvas, centroid);
    }

    private void DrawCircumcenter(
        SKCanvas canvas,
        global::Avalonia.Point a,
        global::Avalonia.Point b,
        global::Avalonia.Point c,
        global::Avalonia.Point circumcenter,
        double circumradius)
    {
        DrawSolidCircle(canvas, circumcenter, circumradius);
        DrawPerpendicularBisector(canvas, a, b, circumcenter, circumradius);
        DrawPerpendicularBisector(canvas, b, c, circumcenter, circumradius);
        DrawPerpendicularBisector(canvas, c, a, circumcenter, circumradius);
        DrawCenterPoint(canvas, circumcenter);
    }

    private void DrawIncenter(
        SKCanvas canvas,
        global::Avalonia.Point a,
        global::Avalonia.Point b,
        global::Avalonia.Point c,
        global::Avalonia.Point incenter,
        double inradius)
    {
        DrawSolidCircle(canvas, incenter, inradius);
        DrawInternalAngleBisector(canvas, a, b, c, inradius);
        DrawInternalAngleBisector(canvas, b, c, a, inradius);
        DrawInternalAngleBisector(canvas, c, a, b, inradius);
        DrawCenterPoint(canvas, incenter);
    }

    private void DrawOrthocenter(
        SKCanvas canvas,
        global::Avalonia.Point a,
        global::Avalonia.Point b,
        global::Avalonia.Point c,
        global::Avalonia.Point orthocenter)
    {
        DrawOrthocenterDiameterCircle(canvas, a, orthocenter);
        DrawOrthocenterDiameterCircle(canvas, b, orthocenter);
        DrawOrthocenterDiameterCircle(canvas, c, orthocenter);
        DrawAltitude(canvas, a, b, c, orthocenter);
        DrawAltitude(canvas, b, c, a, orthocenter);
        DrawAltitude(canvas, c, a, b, orthocenter);

        DrawCenterPoint(canvas, orthocenter);
    }

    private void DrawOrthocenterDiameterCircle(
        SKCanvas canvas,
        global::Avalonia.Point vertex,
        global::Avalonia.Point orthocenter)
    {
        DrawSolidCircle(canvas, Midpoint(vertex, orthocenter), Distance(vertex, orthocenter) / 2.0);
    }

    private void DrawExcenters(SKCanvas canvas, TriangleCenterGeometry centers)
    {
        DrawSolidCircle(canvas, centers.ExcenterA, centers.ExradiusA);
        DrawSolidCircle(canvas, centers.ExcenterB, centers.ExradiusB);
        DrawSolidCircle(canvas, centers.ExcenterC, centers.ExradiusC);
        DrawConstructionLine(canvas, centers.ExcenterB, centers.ExcenterC);
        DrawConstructionLine(canvas, centers.ExcenterA, centers.ExcenterC);
        DrawConstructionLine(canvas, centers.ExcenterA, centers.ExcenterB);
        DrawCenterPoint(canvas, centers.ExcenterA);
        DrawCenterPoint(canvas, centers.ExcenterB);
        DrawCenterPoint(canvas, centers.ExcenterC);
    }

    private void DrawPerpendicularBisector(
        SKCanvas canvas,
        global::Avalonia.Point sideStart,
        global::Avalonia.Point sideEnd,
        global::Avalonia.Point circumcenter,
        double circumradius)
    {
        double sideX = sideEnd.X - sideStart.X;
        double sideY = sideEnd.Y - sideStart.Y;
        double sideLength = Math.Sqrt(sideX * sideX + sideY * sideY);
        if (!IsFinitePositive(sideLength)) return;

        double extent = circumradius * ChartConstants.TriangleCircumBisectorRadiusMultiplier;
        double unitX = -sideY / sideLength;
        double unitY = sideX / sideLength;
        DrawConstructionLine(
            canvas,
            new global::Avalonia.Point(circumcenter.X - unitX * extent, circumcenter.Y - unitY * extent),
            new global::Avalonia.Point(circumcenter.X + unitX * extent, circumcenter.Y + unitY * extent));
    }

    private void DrawInternalAngleBisector(
        SKCanvas canvas,
        global::Avalonia.Point vertex,
        global::Avalonia.Point sideStart,
        global::Avalonia.Point sideEnd,
        double extension)
    {
        double distanceToStart = Distance(vertex, sideStart);
        double distanceToEnd = Distance(vertex, sideEnd);
        double denominator = distanceToStart + distanceToEnd;
        if (!IsFinitePositive(denominator)) return;

        var oppositePoint = new global::Avalonia.Point(
            (distanceToEnd * sideStart.X + distanceToStart * sideEnd.X) / denominator,
            (distanceToEnd * sideStart.Y + distanceToStart * sideEnd.Y) / denominator);
        double directionX = oppositePoint.X - vertex.X;
        double directionY = oppositePoint.Y - vertex.Y;
        double directionLength = Math.Sqrt(directionX * directionX + directionY * directionY);
        if (!IsFinitePositive(directionLength)) return;

        var extendedPoint = new global::Avalonia.Point(
            oppositePoint.X + directionX / directionLength * extension,
            oppositePoint.Y + directionY / directionLength * extension);
        DrawConstructionLine(canvas, vertex, extendedPoint);
    }

    private void DrawAltitude(
        SKCanvas canvas,
        global::Avalonia.Point vertex,
        global::Avalonia.Point sideStart,
        global::Avalonia.Point sideEnd,
        global::Avalonia.Point orthocenter)
    {
        double sideX = sideEnd.X - sideStart.X;
        double sideY = sideEnd.Y - sideStart.Y;
        double sideLengthSquared = sideX * sideX + sideY * sideY;
        if (sideLengthSquared <= ChartConstants.TriangleMinimumSquaredEdgeLength) return;

        double projection = ((vertex.X - sideStart.X) * sideX + (vertex.Y - sideStart.Y) * sideY) /
                            sideLengthSquared;
        var foot = new global::Avalonia.Point(
            sideStart.X + projection * sideX,
            sideStart.Y + projection * sideY);
        double altitudeX = foot.X - vertex.X;
        double altitudeY = foot.Y - vertex.Y;
        double altitudeLengthSquared = altitudeX * altitudeX + altitudeY * altitudeY;
        if (altitudeLengthSquared <= ChartConstants.TriangleMinimumSquaredEdgeLength) return;

        double orthocenterProjection = ((orthocenter.X - vertex.X) * altitudeX +
                                         (orthocenter.Y - vertex.Y) * altitudeY) /
                                        altitudeLengthSquared;
        double startScale = Math.Min(0.0, Math.Min(1.0, orthocenterProjection));
        double endScale = Math.Max(0.0, Math.Max(1.0, orthocenterProjection));
        DrawConstructionLine(
            canvas,
            new global::Avalonia.Point(vertex.X + startScale * altitudeX, vertex.Y + startScale * altitudeY),
            new global::Avalonia.Point(vertex.X + endScale * altitudeX, vertex.Y + endScale * altitudeY));
    }

    private void DrawConstructionLine(SKCanvas canvas, global::Avalonia.Point start, global::Avalonia.Point end)
    {
        if (!CanDrawPoint(start) || !CanDrawPoint(end)) return;
        canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, _centerConstructionPaint);
    }

    private void DrawSolidCircle(SKCanvas canvas, global::Avalonia.Point center, double radius)
    {
        if (!CanDrawPoint(center) || !CanDrawValue(radius) || radius <= 0.0) return;
        canvas.DrawCircle((float)center.X, (float)center.Y, (float)radius, _strokePaint);
    }

    private void DrawCenterPoint(SKCanvas canvas, global::Avalonia.Point center)
    {
        if (!CanDrawPoint(center)) return;
        canvas.DrawCircle(
            (float)center.X,
            (float)center.Y,
            ChartConstants.TriangleCenterMarkerRadius,
            _centerPointPaint);
    }

    private static global::Avalonia.Point Midpoint(global::Avalonia.Point first, global::Avalonia.Point second)
        => new((first.X + second.X) / 2.0, (first.Y + second.Y) / 2.0);

    private static double SquaredDistance(global::Avalonia.Point first, global::Avalonia.Point second)
    {
        double x = second.X - first.X;
        double y = second.Y - first.Y;
        return x * x + y * y;
    }

    private static double Distance(global::Avalonia.Point first, global::Avalonia.Point second)
        => Math.Sqrt(SquaredDistance(first, second));

    private static bool IsFinitePoint(global::Avalonia.Point point)
        => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0.0;

    private static bool CanDrawPoint(global::Avalonia.Point point)
        => CanDrawValue(point.X) && CanDrawValue(point.Y);

    private static bool CanDrawValue(double value)
        => double.IsFinite(value) && value >= -float.MaxValue && value <= float.MaxValue;

    private static string[] CreateInteriorAngleLabels()
    {
        var labels = new string[MaximumInteriorAngleTenths + 1];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = (i / (double)AngleLabelTenthsPerDegree).ToString("F1", CultureInfo.InvariantCulture) + "°";
        }
        return labels;
    }

    private void DrawInteriorAngle(
        SKCanvas canvas,
        global::Avalonia.Point vertex,
        global::Avalonia.Point firstNeighbor,
        global::Avalonia.Point secondNeighbor)
    {
        double angleDegrees = GetInteriorAngleDegrees(vertex, firstNeighbor, secondNeighbor);
        if (double.IsNaN(angleDegrees)) return;

        double centroidX = (vertex.X + firstNeighbor.X + secondNeighbor.X) / 3.0;
        double centroidY = (vertex.Y + firstNeighbor.Y + secondNeighbor.Y) / 3.0;
        double directionX = centroidX - vertex.X;
        double directionY = centroidY - vertex.Y;
        double directionLength = Math.Sqrt(directionX * directionX + directionY * directionY);
        if (directionLength <= Math.Sqrt(ChartConstants.TriangleMinimumSquaredEdgeLength)) return;

        float textX = (float)(vertex.X + directionX / directionLength * ChartConstants.TriangleAngleLabelOffset);
        float textY = (float)(vertex.Y + directionY / directionLength * ChartConstants.TriangleAngleLabelOffset -
                              (_angleTextPaint.FontMetrics.Ascent + _angleTextPaint.FontMetrics.Descent) / 2.0f);
        int angleTenths = Math.Clamp(
            (int)Math.Round(angleDegrees * AngleLabelTenthsPerDegree, MidpointRounding.AwayFromZero),
            0,
            MaximumInteriorAngleTenths);
        canvas.DrawText(InteriorAngleLabels[angleTenths], textX, textY, _angleTextPaint);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    internal readonly record struct TriangleCenterGeometry(
        global::Avalonia.Point Centroid,
        global::Avalonia.Point Circumcenter,
        double Circumradius,
        global::Avalonia.Point Incenter,
        double Inradius,
        global::Avalonia.Point Orthocenter,
        global::Avalonia.Point ExcenterA,
        double ExradiusA,
        global::Avalonia.Point ExcenterB,
        double ExradiusB,
        global::Avalonia.Point ExcenterC,
        double ExradiusC);
}
