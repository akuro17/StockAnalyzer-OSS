namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Buffers;
using System.Collections.Generic;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

/// <summary>
/// NURBS Flexible Trend Curve drawing object.
/// Inherits RelativeGeometricRenderer for Zero-Allocation rendering in 60fps hot path.
/// Supports variable control points (N >= 2), degree p (1..5), and per-control-point rational weights.
/// </summary>
public class NurbsTrendCurveObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.NurbsTrendCurve;

    private int _degree = 3;
    public int Degree
    {
        get => _degree;
        set => _degree = Math.Clamp(value, 1, NurbsMath.MaxDegree);
    }

    /// <summary>
    /// Rational weights for each control point (default 1.0).
    /// </summary>
    public List<double> Weights { get; set; } = new();

    public NurbsTrendCurveObject() : base()
    {
        Color = Colors.Green;
    }

    public NurbsTrendCurveObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
            EnsureWeightsCapacity();
        }
    }

    public NurbsTrendCurveObject(List<ChartPoint> points) : this((IEnumerable<ChartPoint>)points)
    {
    }

    public void AddPoint(ChartPoint point, double weight = 1.0)
    {
        Points.Add(point);
        Weights.Add(Math.Clamp(weight, NurbsMath.MinWeight, NurbsMath.MaxWeight));
    }

    public void EnsureWeightsCapacity()
    {
        while (Weights.Count < Points.Count)
        {
            Weights.Add(1.0);
        }
        if (Weights.Count > Points.Count && Points.Count > 0)
        {
            Weights.RemoveRange(Points.Count, Weights.Count - Points.Count);
        }
    }

    public double GetWeight(int index)
    {
        if (index >= 0 && index < Weights.Count) return Weights[index];
        return 1.0;
    }

    public void SetWeight(int index, double weight)
    {
        EnsureWeightsCapacity();
        if (index >= 0 && index < Weights.Count)
        {
            Weights[index] = Math.Clamp(weight, NurbsMath.MinWeight, NurbsMath.MaxWeight);
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        EnsureWeightsCapacity();
        int count = Points.Count;

        SKPoint[]? rentedPts = null;
        double[]? rentedWeights = null;

        Span<SKPoint> screenPoints = count <= DrawingBufferLimits.StackAllocElementThreshold
            ? stackalloc SKPoint[count]
            : (rentedPts = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        Span<double> weightsSpan = count <= DrawingBufferLimits.StackAllocElementThreshold
            ? stackalloc double[count]
            : (rentedWeights = ArrayPool<double>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
                weightsSpan[i] = Weights[i];
            }

            _cachedPaint.StrokeJoin = SKStrokeJoin.Round;
            _cachedPaint.StrokeCap = SKStrokeCap.Round;
            _cachedPath.Rewind();

            NurbsMath.BuildNurbsPath(_cachedPath, Degree, screenPoints, weightsSpan);

            canvas.DrawPath(_cachedPath, _cachedPaint);
        }
        finally
        {
            if (rentedPts != null) ArrayPool<SKPoint>.Shared.Return(rentedPts);
            if (rentedWeights != null) ArrayPool<double>.Shared.Return(rentedWeights);
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        EnsureWeightsCapacity();
        int count = Points.Count;
        int p = Math.Min(Degree, count - 1);
        if (p < 1) return false;

        SKPoint[]? rentedPts = null;
        double[]? rentedWeights = null;

        Span<SKPoint> screenPoints = count <= DrawingBufferLimits.StackAllocElementThreshold
            ? stackalloc SKPoint[count]
            : (rentedPts = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        Span<double> weightsSpan = count <= DrawingBufferLimits.StackAllocElementThreshold
            ? stackalloc double[count]
            : (rentedWeights = ArrayPool<double>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
                weightsSpan[i] = Weights[i];
            }

            double effectiveTolerance = tolerance + (Thickness / 2.0);
            int sampleCount = NurbsMath.ResolveSampleCount(count);
            SKPoint skPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);

            return NurbsMath.HitTestNurbsCurve(skPt, Degree, screenPoints, weightsSpan, effectiveTolerance, sampleCount);
        }
        finally
        {
            if (rentedPts != null) ArrayPool<SKPoint>.Shared.Return(rentedPts);
            if (rentedWeights != null) ArrayPool<double>.Shared.Return(rentedWeights);
        }
    }
}
