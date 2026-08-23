namespace StockAnalyzer.Tests.Drawing;

using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

public class NurbsMathTests
{
    [Fact]
    public void GenerateClampedKnotVector_InvalidLength_ThrowsArgumentException()
    {
        double[] destination = new double[5]; // Expected 3 + 2 + 1 + 1 = 7 for 3 pts degree 2
        Assert.Throws<ArgumentException>(() => NurbsMath.GenerateClampedKnotVector(3, 2, destination));
    }

    [Fact]
    public void GenerateClampedKnotVector_ValidLength_GeneratesCorrectValues()
    {
        int count = 4;
        int degree = 3;
        int knotCount = count + degree + 1; // 4 + 3 + 1 = 8
        Span<double> knots = stackalloc double[knotCount];

        NurbsMath.GenerateClampedKnotVector(count, degree, knots);

        // Clamped: first degree+1 (4) are 0, last degree+1 (4) are 1
        Assert.Equal(0.0, knots[0]);
        Assert.Equal(0.0, knots[1]);
        Assert.Equal(0.0, knots[2]);
        Assert.Equal(0.0, knots[3]);

        Assert.Equal(1.0, knots[4]);
        Assert.Equal(1.0, knots[5]);
        Assert.Equal(1.0, knots[6]);
        Assert.Equal(1.0, knots[7]);
    }

    [Fact]
    public void Evaluate_ExactCircle_SampledRadiusErrorWithinEpsilon()
    {
        // UT-01: 9-point quadratic rational B-spline exact circle representation
        double r = 100.0;
        SKPoint[] controlPoints =
        [
            new SKPoint((float)r, 0f),
            new SKPoint((float)r, (float)r),
            new SKPoint(0f, (float)r),
            new SKPoint((float)-r, (float)r),
            new SKPoint((float)-r, 0f),
            new SKPoint((float)-r, (float)-r),
            new SKPoint(0f, (float)-r),
            new SKPoint((float)r, (float)-r),
            new SKPoint((float)r, 0f)
        ];

        double invSqrt2 = 1.0 / Math.Sqrt(2.0);
        double[] weights = [1.0, invSqrt2, 1.0, invSqrt2, 1.0, invSqrt2, 1.0, invSqrt2, 1.0];
        double[] knots = [0.0, 0.0, 0.0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1.0, 1.0, 1.0];

        int sampleCount = 361;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / (sampleCount - 1);
            bool success = NurbsMath.TryEvaluate(t, 2, controlPoints, weights, knots, out var pt);
            Assert.True(success, $"Failed to evaluate at t = {t}");

            double evaluatedRadius = Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y);
            double error = Math.Abs(evaluatedRadius - r);
            Assert.True(error <= 1e-4, $"Radius error {error} exceeded 1e-4 at t = {t} (evaluated pt: {pt})");
        }
    }

    [Fact]
    public void Evaluate_SpecificWeight_DeterministicValues()
    {
        // UT-02: Deterministic test with P0=(0,0), P1=(1,2), P2=(3,0), p=2, t=0.5
        SKPoint[] controlPoints =
        [
            new SKPoint(0f, 0f),
            new SKPoint(1f, 2f),
            new SKPoint(3f, 0f)
        ];

        double[] uniformKnots = [0.0, 0.0, 0.0, 1.0, 1.0, 1.0];

        // 1. Standard weights [1, 1, 1] -> Y = 1.0, X = 1.25
        double[] weightsUniform = [1.0, 1.0, 1.0];
        bool okUniform = NurbsMath.TryEvaluate(0.5, 2, controlPoints, weightsUniform, uniformKnots, out var ptUniform);
        Assert.True(okUniform);
        Assert.Equal(1.25f, ptUniform.X, 4);
        Assert.Equal(1.0f, ptUniform.Y, 4);

        // 2. High weight w1 = 100 -> Y = 200/101 ~= 1.980198, X = 101.5/101 ~= 1.00495
        double[] weightsHigh = [1.0, 100.0, 1.0];
        bool okHigh = NurbsMath.TryEvaluate(0.5, 2, controlPoints, weightsHigh, uniformKnots, out var ptHigh);
        Assert.True(okHigh);
        Assert.InRange(ptHigh.Y, 1.979f, 1.981f);
        Assert.InRange(ptHigh.X, 1.004f, 1.006f);
    }

    [Fact]
    public void Evaluate_ClampedEndpoints_ExactMatch()
    {
        // UT-03: Clamped knots must evaluate exactly to P0 at t=0 and Pn at t=1
        SKPoint[] controlPoints =
        [
            new SKPoint(12.5f, 34.2f),
            new SKPoint(50.0f, 80.0f),
            new SKPoint(100.0f, 20.0f),
            new SKPoint(150.0f, 90.0f)
        ];
        double[] weights = [1.0, 2.5, 0.5, 1.0];
        Span<double> knots = stackalloc double[4 + 3 + 1]; // 8
        NurbsMath.GenerateClampedKnotVector(4, 3, knots);

        bool okStart = NurbsMath.TryEvaluate(0.0, 3, controlPoints, weights, knots, out var ptStart);
        Assert.True(okStart);
        Assert.Equal(controlPoints[0].X, ptStart.X, 4);
        Assert.Equal(controlPoints[0].Y, ptStart.Y, 4);

        bool okEnd = NurbsMath.TryEvaluate(1.0, 3, controlPoints, weights, knots, out var ptEnd);
        Assert.True(okEnd);
        Assert.Equal(controlPoints[3].X, ptEnd.X, 4);
        Assert.Equal(controlPoints[3].Y, ptEnd.Y, 4);
    }

    [Fact]
    public void Evaluate_DegenerateInputs_ReturnsFalse()
    {
        // UT-04: Degenerate inputs safely return false without throwing
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(10, 10)];
        double[] w = [1.0, 1.0];
        double[] k = [0.0, 0.0, 1.0, 1.0];

        // 1. Point count < 2
        Assert.False(NurbsMath.TryEvaluate(0.5, 1, ReadOnlySpan<SKPoint>.Empty, ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty, out _));
        Assert.False(NurbsMath.TryEvaluate(0.5, 1, pts[..1], w[..1], k, out _));

        // 2. Mismatched weights length
        Assert.False(NurbsMath.TryEvaluate(0.5, 1, pts, [1.0], k, out _));

        // 3. Mismatched knots length
        Assert.False(NurbsMath.TryEvaluate(0.5, 1, pts, w, [0.0, 1.0], out _));

        // 4. NaN / Infinity in inputs
        SKPoint[] ptsWithNaN = [new SKPoint(float.NaN, 0), new SKPoint(10, 10)];
        Assert.False(NurbsMath.TryEvaluate(0.5, 1, ptsWithNaN, w, k, out _));

        double[] wWithInf = [double.PositiveInfinity, 1.0];
        Assert.False(NurbsMath.TryEvaluate(0.5, 1, pts, wWithInf, k, out _));

        double[] kWithNaN = [0.0, double.NaN, 1.0, 1.0];
        Assert.False(NurbsMath.TryEvaluate(0.5, 1, pts, w, kWithNaN, out _));

        Assert.False(NurbsMath.TryEvaluate(double.NaN, 1, pts, w, k, out _));
    }

    [Fact]
    public void Evaluate_DegreeDegradation_Works()
    {
        // UT-06: Requesting degree 5 with 3 control points safely degrades to degree 2
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(50, 100), new SKPoint(100, 0)];
        double[] w = [1.0, 1.0, 1.0];
        Span<double> knots = stackalloc double[3 + 2 + 1]; // 6
        NurbsMath.GenerateClampedKnotVector(3, 2, knots);

        bool success = NurbsMath.TryEvaluate(0.5, 5, pts, w, knots, out var pt);
        Assert.True(success);
        Assert.Equal(50.0f, pt.X, 3);
        Assert.Equal(50.0f, pt.Y, 3);
    }

    [Fact]
    public void FindSpan_BinarySearch_AccurateSpanIndices()
    {
        // 7 control points (n=6), degree p=3, knots length = 11
        double[] knots = [0, 0, 0, 0, 0.25, 0.5, 0.75, 1.0, 1.0, 1.0, 1.0];
        int p = 3;
        int n = 6;

        // t = 0.0 -> span = 3 (first active span [u_3, u_4) = [0, 0.25))
        Assert.Equal(3, NurbsMath.FindSpan(0.0, p, n, knots));
        Assert.Equal(3, NurbsMath.FindSpan(0.1, p, n, knots));

        // t = 0.3 -> span = 4 ([u_4, u_5) = [0.25, 0.5))
        Assert.Equal(4, NurbsMath.FindSpan(0.3, p, n, knots));

        // t = 0.6 -> span = 5 ([u_5, u_6) = [0.5, 0.75))
        Assert.Equal(5, NurbsMath.FindSpan(0.6, p, n, knots));

        // t = 0.8 -> span = 6 ([u_6, u_7) = [0.75, 1.0))
        Assert.Equal(6, NurbsMath.FindSpan(0.8, p, n, knots));

        // t = 1.0 -> span = n = 6 (endpoint)
        Assert.Equal(6, NurbsMath.FindSpan(1.0, p, n, knots));
    }

    [Fact]
    public void GenerateChordLengthKnotVector_CalculatesNonDecreasingKnotVector()
    {
        SKPoint[] pts =
        [
            new SKPoint(0, 0),
            new SKPoint(10, 0),   // dist = 10
            new SKPoint(30, 0),   // dist = 20
            new SKPoint(70, 0),   // dist = 40
            new SKPoint(100, 0)   // dist = 30  -> total = 100
        ];
        int degree = 2;
        int count = pts.Length; // 5
        int knotLength = count + degree + 1; // 8
        Span<double> knots = stackalloc double[knotLength];

        NurbsMath.GenerateChordLengthKnotVector(pts, degree, knots);

        // Clamped start and end
        Assert.Equal(0.0, knots[0]);
        Assert.Equal(0.0, knots[1]);
        Assert.Equal(0.0, knots[2]);
        Assert.Equal(1.0, knots[5]);
        Assert.Equal(1.0, knots[6]);
        Assert.Equal(1.0, knots[7]);

        // Monotonic non-decreasing
        for (int i = 1; i < knotLength; i++)
        {
            Assert.True(knots[i] >= knots[i - 1]);
        }

        // Internal knots must reflect non-uniform spacing (10, 20, 40, 30)
        Assert.True(knots[3] > 0.0 && knots[3] < 1.0);
        Assert.True(knots[4] > knots[3] && knots[4] < 1.0);
    }

    [Fact]
    public void BuildNurbsPath_ZeroManagedHeapAllocation()
    {
        // UT-05: Path construction in the hot path must have 0 heap allocations
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(20, 40), new SKPoint(60, 80), new SKPoint(100, 0)];
        double[] w = [1.0, 1.5, 0.8, 1.0];

        // Warm-up JIT and SkiaSharp internal caches
        for (int i = 0; i < 5; i++)
        {
            path.Rewind();
            NurbsMath.BuildNurbsPath(path, 3, pts, w);
        }

        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            path.Rewind();
            NurbsMath.BuildNurbsPath(path, 3, pts, w);
        }
        long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, bytesAfter - bytesBefore);
    }

    [Fact]
    public void BuildNurbsPath_PopulatesPathSegments()
    {
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(10, 20), new SKPoint(50, 100), new SKPoint(90, 30)];
        double[] w = [1.0, 2.0, 1.0];

        NurbsMath.BuildNurbsPath(path, 2, pts, w);

        Assert.False(path.IsEmpty);
        Assert.True(path.PointCount > 2);
        Assert.True(path.Bounds.Width > 0);
        Assert.True(path.Bounds.Height > 0);
    }
}
