namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Collections.Generic;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

/// <summary>
/// NURBS Conic Arc drawing object using 3-point quadratic rational B-spline (0 &lt; w1 &lt; 1).
/// Uses 3 anchor points: Points[0] = Start, Points[1] = Bulge/curvature control, Points[2] = End.
/// Unlike NurbsParabolaObject (w1 = 1.0, open/unbounded) and NurbsHyperbolaObject (w1 &gt; 1.0,
/// open/unbounded), this produces a closed elliptical/circular arc segment bounded between the
/// start and end points. Default color matches Arrow tool (Colors.Green).
/// </summary>
public class NurbsConicArcObject : RelativeGeometricRenderer, INurbsWeightedCurveObject
{
    public override ChartObjectType Type => ChartObjectType.NurbsConicArc;

    private double _weight = NurbsConicFactory.InvSqrt2;
    public double Weight
    {
        get => _weight;
        set => _weight = Math.Clamp(value, 0.01, 0.99);
    }

    public double WeightRangeMin => 0.01;
    public double WeightRangeMax => 0.99;
    public string WeightLabelKey => "Setting_Nurbs_ConicArcWeight";
    public double WeightIncrement => 0.01;
    public string WeightFormatString => "0.00";

    public NurbsConicArcObject() : base()
    {
        Color = Colors.Green;
    }

    public NurbsConicArcObject(ChartPoint p0, ChartPoint p1, ChartPoint p2) : this()
    {
        Points.Add(p0);
        Points.Add(p1);
        Points.Add(p2);
    }

    public NurbsConicArcObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 3) return;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);

        Span<SKPoint> pts = stackalloc SKPoint[3]
        {
            new SKPoint((float)s0.X, (float)s0.Y),
            new SKPoint((float)s1.X, (float)s1.Y),
            new SKPoint((float)s2.X, (float)s2.Y)
        };

        _cachedPath.Rewind();
        NurbsConicFactory.BuildConicArcPath(_cachedPath, pts, Weight);

        _cachedPaint.StrokeJoin = SKStrokeJoin.Round;
        _cachedPaint.StrokeCap = SKStrokeCap.Round;
        canvas.DrawPath(_cachedPath, _cachedPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 3) return false;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);

        Span<SKPoint> pts = stackalloc SKPoint[3]
        {
            new SKPoint((float)s0.X, (float)s0.Y),
            new SKPoint((float)s1.X, (float)s1.Y),
            new SKPoint((float)s2.X, (float)s2.Y)
        };

        Span<double> weights = stackalloc double[3] { 1.0, Weight, 1.0 };
        double effectiveTolerance = tolerance + (Thickness / 2.0);
        SKPoint skPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);

        return NurbsMath.HitTestNurbsCurve(skPt, 2, pts, weights, effectiveTolerance, NurbsMath.ThreePointHitTestSampleCount);
    }
}
