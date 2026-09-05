using System;
using System.Collections.Generic;
using global::Avalonia;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Logarithmic Golden Ratio Spiral drawing object.
/// Inherits RelativeGeometricRenderer for Zero-Allocation rendering and uses 90-degree Cubic Bézier arcs.
/// </summary>
public class FibonacciSpiralObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.FibonacciSpiral;

    public const int DefaultQuadrants = 16;
    public const float DefaultMaxRadius = 10000f;

    public FibonacciSpiralObject()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = DrawingThemeContext.DefaultStrokeThickness;
    }

    public FibonacciSpiralObject(ChartPoint center, ChartPoint startRadius) : this()
    {
        Points.Add(center);
        Points.Add(startRadius);
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]); // Center
        var p2 = transform.ChartToScreen(Points[1]); // Start

        SKPoint center = new SKPoint((float)p1.X, (float)p1.Y);
        SKPoint start = new SKPoint((float)p2.X, (float)p2.Y);

        BezierSplineMath.BuildLogarithmicSpiralPath(_cachedPath, center, start, DefaultQuadrants, DefaultMaxRadius);
        canvas.DrawPath(_cachedPath, _cachedPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        SKPoint center = new SKPoint((float)p1.X, (float)p1.Y);
        SKPoint start = new SKPoint((float)p2.X, (float)p2.Y);
        SKPoint skScreenPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);

        // 1. Hit test on handles (center or start point)
        if (BezierSplineMath.DistancePointToSegment(skScreenPt, center, center) <= tolerance * 2) return true;
        if (BezierSplineMath.DistancePointToSegment(skScreenPt, start, start) <= tolerance * 2) return true;

        // 2. Hit test on the logarithmic spiral Bézier curve
        return BezierSplineMath.HitTestLogarithmicSpiral(skScreenPt, center, start, tolerance, DefaultQuadrants, DefaultMaxRadius);
    }
}
