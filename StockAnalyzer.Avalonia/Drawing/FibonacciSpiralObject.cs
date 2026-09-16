using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using global::Avalonia;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;

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
    public const double DefaultMaxTurns = DefaultQuadrants / 4.0;
    public const double MaximumTurns = BezierSplineMath.MaxSpiralQuadrants / 4.0;

    private double _maxTurns = DefaultMaxTurns;
    private SpiralDirection _direction = SpiralDirection.Clockwise;

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

    [Category("Geometry")]
    [DisplayName("Rotation Direction")]
    [Display(Order = 10)]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public SpiralDirection Direction
    {
        get => _direction;
        set
        {
            if (!Enum.IsDefined(typeof(SpiralDirection), value)) throw new ArgumentOutOfRangeException(nameof(value));
            _direction = value;
        }
    }

    [Category("Geometry")]
    [DisplayName("Maximum Turns")]
    [Range(1.0, MaximumTurns)]
    [Display(Order = 20)]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public double MaxTurns
    {
        get => _maxTurns;
        set
        {
            if (value < 1.0 || value > MaximumTurns || !double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _maxTurns = value;
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]); // Center
        var p2 = transform.ChartToScreen(Points[1]); // Start

        SKPoint center = new SKPoint((float)p1.X, (float)p1.Y);
        SKPoint start = new SKPoint((float)p2.X, (float)p2.Y);

        BezierSplineMath.BuildLogarithmicSpiralPath(_cachedPath, center, start, GetQuadrantCount(), DefaultMaxRadius, Direction);
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
        return BezierSplineMath.HitTestLogarithmicSpiral(skScreenPt, center, start, tolerance, GetQuadrantCount(), DefaultMaxRadius, Direction);
    }

    private int GetQuadrantCount() => (int)Math.Round(MaxTurns * 4.0, MidpointRounding.AwayFromZero);
}
