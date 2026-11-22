#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6Geometry2DTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static decimal EvalScalar(string expr)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Value);
        return outcome.Value!.Value;
    }

    private static CalculatorErrorCode EvalError(string expr)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, CalculatorLimits.Default);
        Assert.NotNull(outcome.Error);
        return outcome.Error!.Code;
    }

    private static IReadOnlyList<decimal> EvalArray(string expr)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            CalculatorLimits.Default);
        Assert.True(results[0].IsOk);
        Assert.True(results[0].IsArray);
        return results[0].ArrayValues!;
    }

    private static CalculatorErrorCode EvalBatchError(string expr)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            CalculatorLimits.Default);
        Assert.False(results[0].IsOk);
        return results[0].Error!.Code;
    }

    [Fact]
    public void TriangleSolvers_ComputeExactMetricsAndFrozenArrayLayouts()
    {
        // tri_equilateral(2): area=sqrt(3), perimeter=6, height=sqrt(3), inradius=sqrt(3)/3, circumradius=2*sqrt(3)/3
        IReadOnlyList<decimal> eq = EvalArray("tri_equilateral(2)");
        Assert.Equal(5, eq.Count);
        Assert.InRange(Math.Abs((double)eq[0] - Math.Sqrt(3.0)), 0.0, 1e-12);
        Assert.Equal(6m, eq[1]);
        Assert.InRange(Math.Abs((double)eq[2] - Math.Sqrt(3.0)), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)eq[4] - (2.0 * (double)eq[3])), 0.0, 1e-12);

        // tri_right_isosceles(1): [sqrt(2), 0.5, 2+sqrt(2), sqrt(2)/2]
        IReadOnlyList<decimal> ri = EvalArray("tri_right_isosceles(1)");
        Assert.Equal(4, ri.Count);
        Assert.InRange(Math.Abs((double)ri[0] - Math.Sqrt(2.0)), 0.0, 1e-12);
        Assert.Equal(0.5m, ri[1]);
        Assert.InRange(Math.Abs((double)ri[3] - (Math.Sqrt(2.0) / 2.0)), 0.0, 1e-12);

        // tri_right(3, 4): [5, 6, 12, atan2(3,4)*deg, atan2(4,3)*deg, 2.4]
        IReadOnlyList<decimal> rt = EvalArray("tri_right(3, 4)");
        Assert.Equal(6, rt.Count);
        Assert.Equal(5m, rt[0]);
        Assert.Equal(6m, rt[1]);
        Assert.Equal(12m, rt[2]);
        Assert.InRange(Math.Abs((rt[3] + rt[4]) - 90m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rt[5] - 2.4m), 0m, 1e-12m);

        // tri_isosceles(5, 6): height=4, area=12, perimeter=16, apex + 2*base == 180
        IReadOnlyList<decimal> iso = EvalArray("tri_isosceles(5, 6)");
        Assert.Equal(5, iso.Count);
        Assert.Equal(12m, iso[0]);
        Assert.Equal(16m, iso[1]);
        Assert.Equal(4m, iso[2]);
        Assert.InRange(Math.Abs((iso[3] + (2m * iso[4])) - 180m), 0m, 1e-12m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchError("tri_isosceles(3, 6)"));

        // tri_scalene(3, 4, 5): area=6, perimeter=12, C=90, inradius=1, circumradius=2.5
        IReadOnlyList<decimal> sc = EvalArray("tri_scalene(3, 4, 5)");
        Assert.Equal(7, sc.Count);
        Assert.Equal(6m, sc[0]);
        Assert.Equal(12m, sc[1]);
        Assert.InRange(Math.Abs(sc[4] - 90m), 0m, 1e-11m);
        Assert.Equal(1m, sc[5]);
        Assert.Equal(2.5m, sc[6]);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchError("tri_scalene(1, 2, 3)"));
    }

    [Fact]
    public void Planar2DAreasAndInverseSolvers_HoldIdentities()
    {
        Assert.InRange(Math.Abs((double)EvalScalar("tri_area_equilateral(2)") - Math.Sqrt(3.0)), 0.0, 1e-12);
        Assert.Equal(10m, EvalScalar("tri_area_bh(4, 5)"));
        Assert.InRange(Math.Abs(EvalScalar("tri_area_sas(3, 4, pi / 2)") - 6m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(EvalScalar("tri_area_asa(2, pi / 3, pi / 3)") - (decimal)Math.Sqrt(3.0)), 0m, 1e-11m);

        Assert.Equal(25m, EvalScalar("square_area(5)"));
        Assert.Equal(12m, EvalScalar("rect_area(3, 4)"));
        Assert.Equal(18m, EvalScalar("trapezoid_area(4, 8, 3)"));
        Assert.Equal(15m, EvalScalar("rhombus_area(5, 6)"));
        Assert.Equal(20m, EvalScalar("parallelogram_area_bh(4, 5)"));
        Assert.InRange(Math.Abs(EvalScalar("parallelogram_area_sas(4, 5, pi / 2)") - 20m), 0m, 1e-12m);
        Assert.Equal(24m, EvalScalar("parabola_segment_area(6, 6)"));

        // Isosceles trapezoid bases 4 and 10, legs 5 and 5 -> height = 4, area = 28
        IReadOnlyList<decimal> trap4 = EvalArray("trapezoid_4sides(4, 10, 5, 5)");
        Assert.Equal(2, trap4.Count);
        Assert.InRange(Math.Abs(trap4[0] - 4m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(trap4[1] - 28m), 0m, 1e-12m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchError("trapezoid_4sides(5, 5, 3, 3)"));

        IReadOnlyList<decimal> trap3h = EvalArray("trapezoid_3sides_h(4, 5, 5, 4)");
        Assert.Equal(2, trap3h.Count);
        Assert.InRange(Math.Abs(trap3h[0] - 10m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(trap3h[1] - 28m), 0m, 1e-12m);

        // Rectangle (3, 4, 3, 4) cyclic & Bretschneider at opposite sum == pi
        Assert.InRange(Math.Abs(EvalScalar("cyclic_quad_area(3, 4, 3, 4)") - 12m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(EvalScalar("quad_area_bretschneider(3, 4, 3, 4, pi)") - 12m), 0m, 1e-12m);

        // Regular polygon & circle area inverse round-trips
        decimal sqPolyArea = EvalScalar("reg_poly_area(4, 5)");
        Assert.InRange(Math.Abs(sqPolyArea - 25m), 0m, 1e-11m);
        decimal recoveredSide = EvalScalar($"reg_poly_side_from_area(4, {sqPolyArea})");
        Assert.InRange(Math.Abs(recoveredSide - 5m), 0m, 1e-11m);

        decimal circArea = EvalScalar("circle_area(3)");
        Assert.InRange(Math.Abs((double)circArea - (9.0 * Math.PI)), 0.0, 1e-12);
        Assert.InRange(Math.Abs(EvalScalar($"circle_radius_from_area({circArea})") - 3m), 0m, 1e-11m);

        Assert.InRange(Math.Abs(EvalScalar("sector_area(2, pi)") - (decimal)(2.0 * Math.PI)), 0m, 1e-12m);
        Assert.InRange(Math.Abs(EvalScalar("segment_area_angle(2, pi)") - (decimal)(2.0 * Math.PI)), 0m, 1e-12m);
        Assert.InRange(Math.Abs(EvalScalar("segment_area_rh(2, 2)") - (decimal)(2.0 * Math.PI)), 0m, 1e-12m);
        Assert.InRange(Math.Abs(EvalScalar("segment_area_ch(4, 2)") - (decimal)(2.0 * Math.PI)), 0m, 1e-12m);

        // Ellipse area + full 2*pi sector wraparound continuity + half segment + hyperbola
        decimal ellArea = EvalScalar("ellipse_area(5, 3)");
        Assert.InRange(Math.Abs((double)ellArea - (15.0 * Math.PI)), 0.0, 1e-12);
        decimal fullSector = EvalScalar("ellipse_sector_area(5, 3, 0, 2 * pi)");
        Assert.InRange(Math.Abs(fullSector - ellArea), 0m, 1e-10m);
        decimal halfEllSeg = EvalScalar("ellipse_segment_area(5, 3, 5)");
        Assert.InRange(Math.Abs(halfEllSeg - (ellArea / 2m)), 0m, 1e-10m);

        decimal hypSeg = EvalScalar("hyperbola_segment_area(1, 2, 2)");
        decimal hypSec = EvalScalar("hyperbola_sector_area(1, 2, 2)");
        double expectedSec = Math.Acosh(2.0);
        double expectedSeg = 2.0 * ((2.0 * Math.Sqrt(3.0)) - expectedSec);
        Assert.InRange(Math.Abs((double)hypSec - expectedSec), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)hypSeg - expectedSeg), 0.0, 1e-11);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("hyperbola_sector_area(2, 1, 2)"));
    }

    [Fact]
    public void InscribedCircumscribedAndRightTriangleDegreeSolvers_MatchExactValues()
    {
        Assert.InRange(Math.Abs(EvalScalar("reg_poly_inradius(4, 6)") - 3m), 0m, 1e-12m);
        Assert.InRange(Math.Abs((double)EvalScalar("reg_poly_circumradius(6, 5)") - 5.0), 0.0, 1e-12);
        Assert.Equal(2.5m, EvalScalar("rect_circumradius(3, 4)"));

        // Square inscribed in R = 1: side = sqrt(2), perimeter = 4*sqrt(2), area = 2
        IReadOnlyList<decimal> ins = EvalArray("circle_inscribed_reg_poly(4, 1)");
        Assert.Equal(3, ins.Count);
        Assert.InRange(Math.Abs((double)ins[0] - Math.Sqrt(2.0)), 0.0, 1e-12);
        Assert.InRange(Math.Abs(ins[2] - 2m), 0m, 1e-12m);

        // Square circumscribed around r = 3: side = 6, perimeter = 24, area = 36
        IReadOnlyList<decimal> circ = EvalArray("circle_circumscribed_reg_poly(4, 3)");
        Assert.Equal(3, circ.Count);
        Assert.InRange(Math.Abs(circ[0] - 6m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(circ[1] - 24m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(circ[2] - 36m), 0m, 1e-11m);

        // Right triangle degree solvers at exact 60°, 45°, 30° and 3-4-5
        IReadOnlyList<decimal> ab = EvalArray("rt_tri_from_angle_base_deg(60, 5)");
        Assert.Equal(10m, ab[0]);
        Assert.InRange(Math.Abs((double)ab[1] - (5.0 * Math.Sqrt(3.0))), 0.0, 1e-12);

        IReadOnlyList<decimal> ah = EvalArray("rt_tri_from_angle_height_deg(30, 5)");
        Assert.InRange(Math.Abs((double)ah[0] - (5.0 * Math.Sqrt(3.0))), 0.0, 1e-12);
        Assert.Equal(10m, ah[1]);

        IReadOnlyList<decimal> ac = EvalArray("rt_tri_from_angle_hyp_deg(30, 10)");
        Assert.InRange(Math.Abs((double)ac[0] - (5.0 * Math.Sqrt(3.0))), 0.0, 1e-12);
        Assert.Equal(5m, ac[1]);

        IReadOnlyList<decimal> bh = EvalArray("rt_tri_from_base_height_deg(3, 4)");
        Assert.Equal(5m, bh[1]);
        Assert.InRange(Math.Abs(bh[0] - EvalArray("rt_tri_from_base_height_deg(4, 4)")[0]), 5m, 15m);
        Assert.Equal(45m, EvalArray("rt_tri_from_base_height_deg(4, 4)")[0]);

        IReadOnlyList<decimal> bc = EvalArray("rt_tri_from_base_hyp_deg(1, 2)");
        Assert.Equal(60m, bc[0]);
        Assert.InRange(Math.Abs((double)bc[1] - Math.Sqrt(3.0)), 0.0, 1e-12);

        IReadOnlyList<decimal> hc = EvalArray("rt_tri_from_height_hyp_deg(1, 2)");
        Assert.Equal(30m, hc[0]);
        Assert.InRange(Math.Abs((double)hc[1] - Math.Sqrt(3.0)), 0.0, 1e-12);

        IReadOnlyList<decimal> eqAngles = EvalArray("tri_angles_sss_deg(5, 5, 5)");
        Assert.Equal(3, eqAngles.Count);
        Assert.InRange(Math.Abs(eqAngles[0] - 60m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(eqAngles[1] - 60m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(eqAngles[2] - 60m), 0m, 1e-11m);
    }

    [Fact]
    public void Analytic2DGeometryAndTransforms_HoldIdentitiesAndPolygonWire()
    {
        // line through (0, 2) and (4, 0): 2x + 4y - 8 = 0
        IReadOnlyList<decimal> line2 = EvalArray("line2d_2pts(0, 2, 4, 0)");
        Assert.Equal([2m, 4m, -8m], line2);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchError("line2d_2pts(1, 1, 1, 1)"));

        // intercepts x=4, y=2 -> A=2, B=4, C=-8, slope=-0.5
        IReadOnlyList<decimal> lineInt = EvalArray("line2d_intercepts(4, 2)");
        Assert.Equal([2m, 4m, -8m, -0.5m], lineInt);

        // intersection of x + y - 3 = 0 and x - y - 1 = 0 -> (2, 1)
        IReadOnlyList<decimal> hit = EvalArray("line2d_intersect(1, 1, -3, 1, -1, -1)");
        Assert.Equal([2m, 1m], hit);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, EvalBatchError("line2d_intersect(1, 1, -1, 2, 2, -5)"));

        Assert.Equal(6m, EvalScalar("tri_area_pts(0, 0, 3, 0, 0, 4)"));
        Assert.Equal(0m, EvalScalar("tri_area_pts(0, 0, 1, 1, 2, 2)"));

        // Direct wire poly_area_pts(xs, ys) for unit square & 3-4-5 triangle
        Assert.Equal(12m, EvalScalar("poly_area_pts([0, 4, 4, 0], [0, 0, 3, 3])"));
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalError("poly_area_pts([0, 1, 2], [0, 1])"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("poly_area_pts([0, 1], [0, 1])"));

        // 2D rotation & inverse axis rotation by pi/2
        IReadOnlyList<decimal> rPt = EvalArray("rot2d_pt(1, 0, pi / 2)");
        Assert.InRange(Math.Abs(rPt[0]), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rPt[1] - 1m), 0m, 1e-12m);

        IReadOnlyList<decimal> rAx = EvalArray("rot2d_axis(0, 1, pi / 2)");
        Assert.InRange(Math.Abs(rAx[0] - 1m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rAx[1]), 0m, 1e-12m);

        // Polar round-trip
        IReadOnlyList<decimal> pol = EvalArray("cart_to_polar(3, 4)");
        Assert.Equal(5m, pol[0]);
        IReadOnlyList<decimal> back = EvalArray($"polar_to_cart({pol[0]}, {pol[1]})");
        Assert.InRange(Math.Abs(back[0] - 3m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(back[1] - 4m), 0m, 1e-11m);
        Assert.Equal([0m, 0m], EvalArray("cart_to_polar(0, 0)"));
    }
}
