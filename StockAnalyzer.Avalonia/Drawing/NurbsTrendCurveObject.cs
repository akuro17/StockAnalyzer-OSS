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

        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rentedPts = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        Span<double> weightsSpan = count <= 128
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

        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rentedPts = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        Span<double> weightsSpan = count <= 128
            ? stackalloc double[count]
            : (rentedWeights = ArrayPool<double>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
                weightsSpan[i] = Weights[i];

                if (screenPoints[i].X < minX) minX = screenPoints[i].X;
                if (screenPoints[i].X > maxX) maxX = screenPoints[i].X;
                if (screenPoints[i].Y < minY) minY = screenPoints[i].Y;
                if (screenPoints[i].Y > maxY) maxY = screenPoints[i].Y;
            }

            double effectiveTolerance = tolerance + (Thickness / 2.0);

            // Bounding box pre-check
            if (screenPoint.X < minX - effectiveTolerance || screenPoint.X > maxX + effectiveTolerance ||
                screenPoint.Y < minY - effectiveTolerance || screenPoint.Y > maxY + effectiveTolerance)
            {
                return false;
            }

            // Sample curve and test segment distances
            int knotCount = count + p + 1;
            if (knotCount > 256) return false;

            Span<double> localKnots = stackalloc double[knotCount];
            NurbsMath.GenerateClampedKnotVector(count, p, localKnots);

            int sampleCount = Math.Clamp(count * 20, 40, 128);
            double tStart = localKnots[p];
            double tEnd = localKnots[count];
            double dt = (tEnd - tStart) / (sampleCount - 1);

            SKPoint skPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);
            if (!NurbsMath.TryEvaluate(tStart, p, screenPoints, weightsSpan, localKnots, out var prevSample))
            {
                return false;
            }

            double tolSquared = effectiveTolerance * effectiveTolerance;

            for (int i = 1; i < sampleCount; i++)
            {
                double t = tStart + i * dt;
                if (NurbsMath.TryEvaluate(t, p, screenPoints, weightsSpan, localKnots, out var currSample))
                {
                    if (DistanceSquaredToSegment(skPt, prevSample, currSample) <= tolSquared)
                    {
                        return true;
                    }
                    prevSample = currSample;
                }
            }

            return false;
        }
        finally
        {
            if (rentedPts != null) ArrayPool<SKPoint>.Shared.Return(rentedPts);
            if (rentedWeights != null) ArrayPool<double>.Shared.Return(rentedWeights);
        }
    }

    private static double DistanceSquaredToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float l2 = dx * dx + dy * dy;
        if (l2 <= 1e-12f)
        {
            float px = p.X - a.X;
            float py = p.Y - a.Y;
            return px * px + py * py;
        }

        float t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2;
        t = Math.Clamp(t, 0f, 1f);
        float projX = a.X + t * dx;
        float projY = a.Y + t * dy;
        float dX = p.X - projX;
        float dY = p.Y - projY;
        return dX * dX + dY * dY;
    }
}
