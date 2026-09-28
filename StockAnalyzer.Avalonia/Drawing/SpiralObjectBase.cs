namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

/// <summary>
/// Shared two-point screen-space spiral object behavior.
/// </summary>
public abstract class SpiralObjectBase : RelativeGeometricRenderer
{
    private double _maxTurns = SpiralMath.DefaultMaxTurns;
    private SpiralDirection _direction = SpiralDirection.Clockwise;

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
    [Range(SpiralMath.MinimumTurns, SpiralMath.MaximumTurns)]
    [Display(Order = 20)]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public double MaxTurns
    {
        get => _maxTurns;
        set
        {
            if (value < SpiralMath.MinimumTurns || value > SpiralMath.MaximumTurns || !double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _maxTurns = value;
        }
    }

    protected SpiralObjectBase() { }

    protected SpiralObjectBase(ChartPoint center, ChartPoint start) : this()
    {
        Points.Add(center);
        Points.Add(start);
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;
        var center = transform.ChartToScreen(Points[0]);
        var start = transform.ChartToScreen(Points[1]);
        BuildPath(new SKPoint((float)center.X, (float)center.Y), new SKPoint((float)start.X, (float)start.Y));
        canvas.DrawPath(_cachedPath, _cachedPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;
        var center = transform.ChartToScreen(Points[0]);
        var start = transform.ChartToScreen(Points[1]);
        var centerPoint = new SKPoint((float)center.X, (float)center.Y);
        var startPoint = new SKPoint((float)start.X, (float)start.Y);
        var target = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);
        if (BezierSplineMath.DistancePointToSegment(target, centerPoint, centerPoint) <= tolerance * 2.0) return true;
        if (BezierSplineMath.DistancePointToSegment(target, startPoint, startPoint) <= tolerance * 2.0) return true;
        return HitTestPath(target, centerPoint, startPoint, tolerance);
    }

    protected abstract void BuildPath(SKPoint center, SKPoint start);
    protected abstract bool HitTestPath(SKPoint target, SKPoint center, SKPoint start, double tolerance);
}
