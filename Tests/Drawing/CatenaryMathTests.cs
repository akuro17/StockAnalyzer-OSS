using System;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class CatenaryMathTests
{
    [Fact]
    public void Solve_HorizontalEndpoints_PassesThroughAllThreePoints()
    {
        // Start (100, 200), End (500, 200), Sag (300, 250) -> Sag delta = +50 (Support / Hanging)
        var s0 = new Point(100, 200);
        var s1 = new Point(500, 200);
        var s2 = new Point(300, 250);

        var result = CatenaryMath.Solve(s0, s1, s2);

        Assert.NotNull(result);
        var p = result.Value;
        Assert.False(p.IsLinear);
        Assert.Equal(1, p.S);

        // Verify pass-through at endpoints
        Assert.True(Math.Abs(p.EvaluateY(100) - 200) < 0.1, $"Y(100) was {p.EvaluateY(100)}, expected 200");
        Assert.True(Math.Abs(p.EvaluateY(500) - 200) < 0.1, $"Y(500) was {p.EvaluateY(500)}, expected 200");

        // Verify pass-through at sag point
        Assert.True(Math.Abs(p.EvaluateY(300) - 250) < 0.1, $"Y(300) was {p.EvaluateY(300)}, expected 250");
    }

    [Fact]
    public void Solve_TiltedEndpoints_PassesThroughAllThreePoints()
    {
        // Start (100, 150), End (400, 300), Sag (250, 320) -> Tilted downward
        var s0 = new Point(100, 150);
        var s1 = new Point(400, 300);
        var s2 = new Point(250, 320);

        var result = CatenaryMath.Solve(s0, s1, s2);

        Assert.NotNull(result);
        var p = result.Value;
        Assert.False(p.IsLinear);
        Assert.Equal(1, p.S);

        Assert.True(Math.Abs(p.EvaluateY(100) - 150) < 0.1, $"Y(100) was {p.EvaluateY(100)}, expected 150");
        Assert.True(Math.Abs(p.EvaluateY(400) - 300) < 0.1, $"Y(400) was {p.EvaluateY(400)}, expected 300");
        Assert.True(Math.Abs(p.EvaluateY(250) - 320) < 0.1, $"Y(250) was {p.EvaluateY(250)}, expected 320");
    }

    [Fact]
    public void Solve_ResistanceArch_UpwardSag()
    {
        // Start (100, 300), End (500, 300), Sag (300, 200) -> Sag delta = -100 (Resistance / Arch)
        var s0 = new Point(100, 300);
        var s1 = new Point(500, 300);
        var s2 = new Point(300, 200);

        var result = CatenaryMath.Solve(s0, s1, s2);

        Assert.NotNull(result);
        var p = result.Value;
        Assert.False(p.IsLinear);
        Assert.Equal(-1, p.S);

        Assert.True(Math.Abs(p.EvaluateY(100) - 300) < 0.1, $"Y(100) was {p.EvaluateY(100)}, expected 300");
        Assert.True(Math.Abs(p.EvaluateY(500) - 300) < 0.1, $"Y(500) was {p.EvaluateY(500)}, expected 300");
        Assert.True(Math.Abs(p.EvaluateY(300) - 200) < 0.1, $"Y(300) was {p.EvaluateY(300)}, expected 200");
    }

    [Fact]
    public void Solve_LinearSagBelowThreshold_ReturnsLinearMode()
    {
        // Sag is collinear with chord: Start (100, 100), End (300, 300), Sag (200, 200.2)
        var s0 = new Point(100, 100);
        var s1 = new Point(300, 300);
        var s2 = new Point(200, 200.2);

        var result = CatenaryMath.Solve(s0, s1, s2);

        Assert.NotNull(result);
        var p = result.Value;
        Assert.True(p.IsLinear);
        Assert.Equal(200.0, p.EvaluateY(200), 2);
    }

    [Fact]
    public void Solve_DegenerateWidth_ReturnsNull()
    {
        // Width < 1e-4 px
        var s0 = new Point(100.0, 100.0);
        var s1 = new Point(100.00001, 200.0);
        var s2 = new Point(100.0, 150.0);

        var result = CatenaryMath.Solve(s0, s1, s2);
        Assert.Null(result);
    }

    [Fact]
    public void Solve_ExtremeSag_ZeroCrashAndClamped()
    {
        // Very deep sag
        var s0 = new Point(100, 100);
        var s1 = new Point(200, 100);
        var s2 = new Point(150, 10000);

        var result = CatenaryMath.Solve(s0, s1, s2);
        Assert.NotNull(result);
        var p = result.Value;
        Assert.False(double.IsNaN(p.A));
        Assert.False(double.IsInfinity(p.A));
        Assert.False(double.IsNaN(p.EvaluateY(150)));
    }

    [Fact]
    public void DistanceToPoint_OnCurve_ReturnsNearZero()
    {
        var s0 = new Point(100, 200);
        var s1 = new Point(500, 200);
        var s2 = new Point(300, 260);

        var result = CatenaryMath.Solve(s0, s1, s2);
        Assert.NotNull(result);
        var p = result.Value;

        double sampleX = 250;
        double sampleY = p.EvaluateY(sampleX);

        double dist = p.DistanceToPoint(sampleX, sampleY);
        Assert.True(dist < 1e-4, $"Distance should be near 0, was {dist}");

        // Off curve point (sampleX, sampleY + 10)
        double offDist = p.DistanceToPoint(sampleX, sampleY + 10);
        Assert.True(offDist > 0 && offDist <= 10.0, $"Normal distance should be close to 10.0, was {offDist}");
    }

    [Fact]
    public void ParabolicFallback_EvaluatesExactThreePoints()
    {
        // Construct a Parabolic fallback CatenaryParams
        double x1 = 100, y1 = 150;
        double x2 = 500, y2 = 250;
        double xm = 300;
        double chordAtMid = 200; // y1 + (y2-y1)/2 = 150 + 50 = 200
        double targetSagY = 280;
        double delta = targetSagY - chordAtMid; // +80

        var p = new CatenaryParams(
            A: 100.0,
            X0: xm,
            Y0: 200.0,
            S: 1,
            X1: x1,
            Y1: y1,
            X2: x2,
            Y2: y2,
            IsLinear: false,
            IsParabolic: true,
            SagDelta: delta
        );

        Assert.True(Math.Abs(p.EvaluateY(100) - 150) < 1e-6, "Parabola at x1 must equal y1");
        Assert.True(Math.Abs(p.EvaluateY(500) - 250) < 1e-6, "Parabola at x2 must equal y2");
        Assert.True(Math.Abs(p.EvaluateY(300) - 280) < 1e-6, "Parabola at xm must equal sag point exactly");
    }
}
