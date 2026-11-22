#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6Geometry3DTests
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

    [Fact]
    public void PolyhedraPrismsPyramidsAndFrustums_MatchVolumesAndInverses()
    {
        Assert.Equal(27m, EvalScalar("cube_vol(3)"));
        Assert.Equal(3m, EvalScalar("cube_side_from_vol(27)"));
        Assert.Equal(24m, EvalScalar("cuboid_vol(2, 3, 4)"));
        Assert.Equal(52m, EvalScalar("cuboid_area(2, 3, 4)"));
        Assert.Equal(10m, EvalScalar("tetra_vol(6, 5)"));
        Assert.Equal(10m, EvalScalar("pyramid_vol(6, 5)"));

        decimal regTetVol = EvalScalar("reg_tetra_vol(4)");
        Assert.InRange(Math.Abs((double)regTetVol - ((Math.Sqrt(2.0) / 12.0) * 64.0)), 0.0, 1e-12);
        Assert.InRange(Math.Abs(EvalScalar($"reg_tetra_side_from_vol({regTetVol})") - 4m), 0m, 1e-11m);

        decimal triPrismV = EvalScalar("tri_prism_vol(2, 5)");
        Assert.InRange(Math.Abs(EvalScalar($"tri_prism_h_from_vol(2, {triPrismV})") - 5m), 0m, 1e-11m);

        Assert.Equal(45m, EvalScalar("sq_prism_vol(3, 5)"));
        Assert.Equal(5m, EvalScalar("sq_prism_h_from_vol(3, 45)"));

        decimal hexV = EvalScalar("hex_prism_vol(2, 4)");
        Assert.InRange(Math.Abs(EvalScalar($"hex_prism_h_from_vol(2, {hexV})") - 4m), 0m, 1e-11m);

        Assert.Equal(12m, EvalScalar("sq_pyramid_vol_bh(3, 4)"));
        // Square side a=2 => half-diagonal = sqrt(2); edge = sqrt(11) => h = 3 => vol = 4*3/3 = 4
        decimal e = (decimal)Math.Sqrt(11.0);
        Assert.InRange(Math.Abs(EvalScalar($"sq_pyramid_vol_be(2, {e})") - 4m), 0m, 1e-10m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sq_pyramid_vol_be(2, 1.4)"));

        Assert.Equal(39m, EvalScalar("sq_frustum_vol(2, 5, 3)"));
        // Degenerate square frustum == rect frustum
        Assert.Equal(EvalScalar("sq_frustum_vol(2, 5, 3)"), EvalScalar("rect_frustum_vol(2, 2, 5, 5, 3)"));
        Assert.Equal(EvalScalar("sq_frustum_vol(2, 5, 3)"), EvalScalar("pyramid_frustum_vol(4, 25, 3)"));

        // Rectangular pyramid limit wedge(a, b, 0, h) = (1/3)*a*b*h, triangular prism limit wedge(a, b, a, h) = 0.5*a*b*h
        Assert.Equal(20m, EvalScalar("wedge_vol(3, 5, 0, 4)"));
        Assert.Equal(30m, EvalScalar("wedge_vol(3, 5, 3, 4)"));
    }

    [Fact]
    public void CylindersConesSpheresAndPartialCuts_HoldGeometricIdentities()
    {
        decimal cylFull = EvalScalar("cyl_vol(3, 10)");
        Assert.InRange(Math.Abs((double)cylFull - (90.0 * Math.PI)), 0.0, 1e-11);
        Assert.InRange(Math.Abs(EvalScalar("cyl_partial_vol(3, 10, 3)") - (cylFull / 2m)), 0m, 1e-11m);
        Assert.InRange(Math.Abs((double)EvalScalar("cyl_hollow_vol(5, 3, 2)") - (32.0 * Math.PI)), 0.0, 1e-11);
        Assert.InRange(Math.Abs(EvalScalar("cyl_oblique_cut_vol(3, 8, 12)") - cylFull), 0m, 1e-11m);
        Assert.InRange(Math.Abs(EvalScalar("ellip_cyl_vol(3, 3, 10)") - cylFull), 0m, 1e-11m);

        decimal coneV = EvalScalar("cone_vol(3, 6)");
        Assert.InRange(Math.Abs((double)coneV - (18.0 * Math.PI)), 0.0, 1e-11);

        // Horizontal cut (cutAngleRad = 0) at apex distance z0 = 3 (half height) removes 1/8 of cone -> leaves 7/8
        decimal horizCut = EvalScalar("cone_oblique_cut_vol(3, 6, 0, 3)");
        Assert.InRange(Math.Abs(horizCut - (coneV * 0.875m)), 0m, 1e-11m);
        // Oblique cut exceeding lateral intersection fails
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("cone_oblique_cut_vol(3, 6, 1.2, 5)"));

        decimal conePartial = EvalScalar("cone_partial_vol(4, 6, 2)");
        Assert.True(conePartial > 0m && conePartial < EvalScalar("cone_vol(4, 6)") / 2m);

        Assert.InRange(Math.Abs(EvalScalar("cone_frustum_vol(2, 2, 5)") - EvalScalar("cyl_vol(2, 5)")), 0m, 1e-11m);
        Assert.InRange(Math.Abs(EvalScalar("ellip_cone_vol(3, 3, 6)") - coneV), 0m, 1e-11m);
        Assert.InRange(Math.Abs(EvalScalar("ellip_frustum_vol(2, 2, 4, 4, 3)") - EvalScalar("cone_frustum_vol(2, 4, 3)")), 0m, 1e-11m);

        // Hemisphere & Sphere zones
        decimal sphereV = EvalScalar("ellipsoid_vol(3, 3, 3)");
        Assert.InRange(Math.Abs(EvalScalar("sphere_cap_vol(3, 6)") - sphereV), 0m, 1e-11m);
        Assert.InRange(Math.Abs(EvalScalar("sphere_zone_vol(3, 0, 3)") - (sphereV / 2m)), 0m, 1e-11m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("sphere_zone_vol(0, 0, 6)"));
    }

    [Fact]
    public void SegmentRevEllipsoidPlatonicNBallAndSurfaceAreas_MatchIdentities()
    {
        // Revolved semicircle (r=3, h=3) equals sphere volume
        decimal sphereV = EvalScalar("ellipsoid_vol(3, 3, 3)");
        Assert.InRange(Math.Abs(EvalScalar("segment_rev_vol(3, 3)") - sphereV), 0m, 1e-11m);
        Assert.InRange(Math.Abs(EvalScalar("segment_rev_partial_vol(3, 3, 3)") - sphereV), 0m, 1e-11m);
        Assert.True(EvalScalar("segment_rev_partial_vol(5, 2, 2)") < EvalScalar("segment_rev_vol(5, 2)"));

        Assert.InRange(Math.Abs(EvalScalar("ellipsoid_cap_vol(2, 3, 4, 8)") - EvalScalar("ellipsoid_vol(2, 3, 4)")), 0m, 1e-11m);
        Assert.InRange(Math.Abs(EvalScalar("spheroid_cap_vol(2, 4, 5)") - EvalScalar("ellipsoid_cap_vol(2, 2, 4, 5)")), 0m, 1e-11m);

        Assert.InRange(Math.Abs(EvalScalar("platonic_vol(4, 3)") - EvalScalar("reg_tetra_vol(3)")), 0m, 1e-12m);
        Assert.Equal(27m, EvalScalar("platonic_vol(6, 3)"));
        Assert.True(EvalScalar("platonic_vol(20, 1)") > 2.18m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("platonic_vol(10, 1)"));

        // n-ball unit radius: n=1 -> 2, n=2 -> pi, n=3 -> 4*pi/3, n=4 -> pi^2 / 2
        Assert.InRange(Math.Abs(EvalScalar("nball_vol(1, 1)") - 2m), 0m, 1e-11m);
        Assert.InRange(Math.Abs((double)EvalScalar("nball_vol(2, 1)") - Math.PI), 0.0, 1e-11);
        Assert.InRange(Math.Abs((double)EvalScalar("nball_vol(3, 1)") - ((4.0 / 3.0) * Math.PI)), 0.0, 1e-11);
        Assert.InRange(Math.Abs((double)EvalScalar("nball_vol(4, 1)") - (0.5 * Math.PI * Math.PI)), 0.0, 1e-11);

        // Surface areas
        Assert.InRange(Math.Abs((double)EvalScalar("sphere_area(3)") - (36.0 * Math.PI)), 0.0, 1e-11);
        Assert.InRange(Math.Abs((double)EvalScalar("cyl_area(2, 5)") - (28.0 * Math.PI)), 0.0, 1e-11);
        Assert.InRange(Math.Abs((double)EvalScalar("torus_area(5, 2)") - (40.0 * Math.PI * Math.PI)), 0.0, 1e-10);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("torus_area(2, 5)"));
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
    public void Analytic3DGeometryCoordinatesAndRotations_MatchExactIdentities()
    {
        // Skew lines along x-axis (at z=0) and y-axis (at z=5) -> distance = 5
        Assert.Equal(5m, EvalScalar("dist3d_line_line([0, 0, 0], [1, 0, 0], [0, 0, 5], [0, 1, 0])"));
        // Parallel lines along z-axis separated by (3, 4, 0) -> distance = 5
        Assert.Equal(5m, EvalScalar("dist3d_line_line([0, 0, 0], [0, 0, 1], [3, 4, 2], [0, 0, 2])"));

        // Plane through (1,0,0), (0,1,0), (0,0,1): normalized normal (1,1,1)/sqrt(3), d = -1/sqrt(3)
        IReadOnlyList<decimal> plane = EvalArray("plane3d_3pts([1, 0, 0], [0, 1, 0], [0, 0, 1])");
        Assert.Equal(4, plane.Count);
        double invSqrt3 = 1.0 / Math.Sqrt(3.0);
        Assert.InRange(Math.Abs((double)plane[0] - invSqrt3), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)plane[3] + invSqrt3), 0.0, 1e-12);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, EvalBatchError("plane3d_3pts([0, 0, 0], [1, 1, 1], [2, 2, 2])"));

        // Distance from origin to z = 4 plane (0, 0, 1, -4) == 4
        Assert.Equal(4m, EvalScalar("dist3d_pt_plane([0, 0, 0], [0, 0, 1, -4])"));
        // Tetrahedron of unit corner axes -> volume = 1/6
        Assert.InRange(Math.Abs(EvalScalar("tetra_vol_pts([0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1])") - (1m / 6m)), 0m, 1e-12m);

        // Coordinate conversion round-trips: Cart -> Sph -> Cyl -> Cart
        IReadOnlyList<decimal> sph = EvalArray("cart_to_sph(1, 2, 2)");
        Assert.Equal(3m, sph[0]);
        IReadOnlyList<decimal> cyl = EvalArray($"sph_to_cyl({sph[0]}, {sph[1]}, {sph[2]})");
        IReadOnlyList<decimal> cart = EvalArray($"cyl_to_cart({cyl[0]}, {cyl[1]}, {cyl[2]})");
        Assert.InRange(Math.Abs(cart[0] - 1m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(cart[1] - 2m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(cart[2] - 2m), 0m, 1e-11m);

        IReadOnlyList<decimal> sphBack = EvalArray($"cyl_to_sph({cyl[0]}, {cyl[1]}, {cyl[2]})");
        IReadOnlyList<decimal> cart2 = EvalArray($"sph_to_cart({sphBack[0]}, {sphBack[1]}, {sphBack[2]})");
        Assert.InRange(Math.Abs(cart2[0] - 1m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(cart2[1] - 2m), 0m, 1e-11m);
        Assert.InRange(Math.Abs(cart2[2] - 2m), 0m, 1e-11m);

        IReadOnlyList<decimal> cylDirect = EvalArray("cart_to_cyl(3, 4, 7)");
        Assert.Equal(5m, cylDirect[0]);
        Assert.Equal(7m, cylDirect[2]);
        Assert.Equal([0m, 0m, 0m], EvalArray("cart_to_sph(0, 0, 0)"));

        // Direct wire mat_rot3d & rot3d_pt around z-axis by pi/2: [1, 0, 0] -> [0, 1, 0]
        IReadOnlyList<decimal> rotMat = EvalArray("mat_rot3d([0, 0, 2], pi / 2)");
        Assert.Equal(9, rotMat.Count);
        Assert.InRange(Math.Abs(rotMat[0]), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rotMat[1] + 1m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rotMat[3] - 1m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rotMat[8] - 1m), 0m, 1e-12m);

        IReadOnlyList<decimal> rotPt = EvalArray("rot3d_pt([1, 0, 5], [0, 0, 1], pi / 2)");
        Assert.InRange(Math.Abs(rotPt[0]), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rotPt[1] - 1m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rotPt[2] - 5m), 0m, 1e-12m);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchError("mat_rot3d([0, 0, 0], 1)"));

        // Scale invariance on plane3d_3pts (both 1e-8 and 1e8 scales)
        IReadOnlyList<decimal> planeSmall = EvalArray("plane3d_3pts([0, 0, 0], [1e-8, 0, 0], [0, 1e-8, 0])");
        Assert.Equal([0m, 0m, 1m, 0m], planeSmall);
        IReadOnlyList<decimal> planeLarge = EvalArray("plane3d_3pts([0, 0, 0], [1e8, 0, 0], [0, 1e8, 0])");
        Assert.Equal([0m, 0m, 1m, 0m], planeLarge);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, EvalBatchError("plane3d_3pts([1e8, 0, 0], [2e8, 0, 0], [3e8, 0, 0])"));

        // cone_oblique_cut_vol open-interval boundary (7=B): cutHeight == h returns DomainError
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("cone_oblique_cut_vol(3, 6, 0, 6)"));

        // SO(3) group invariants for general axis mat_rot3d: R^T R == I, det(R) == 1, ||R v|| == ||v||
        IReadOnlyList<decimal> rMat = EvalArray("mat_rot3d([1, 2, 3], 0.7)");
        decimal r00 = rMat[0], r01 = rMat[1], r02 = rMat[2];
        decimal r10 = rMat[3], r11 = rMat[4], r12 = rMat[5];
        decimal r20 = rMat[6], r21 = rMat[7], r22 = rMat[8];
        decimal rtR00 = (r00 * r00) + (r10 * r10) + (r20 * r20);
        decimal rtR01 = (r00 * r01) + (r10 * r11) + (r20 * r21);
        decimal detR = (r00 * ((r11 * r22) - (r12 * r21)))
                     - (r01 * ((r10 * r22) - (r12 * r20)))
                     + (r02 * ((r10 * r21) - (r11 * r20)));
        Assert.InRange(Math.Abs(rtR00 - 1m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(rtR01), 0m, 1e-12m);
        Assert.InRange(Math.Abs(detR - 1m), 0m, 1e-12m);

        IReadOnlyList<decimal> vRot = EvalArray("rot3d_pt([3, -4, 5], [1, 2, 3], 0.7)");
        decimal normSq = (vRot[0] * vRot[0]) + (vRot[1] * vRot[1]) + (vRot[2] * vRot[2]);
        Assert.InRange(Math.Abs(normSq - 50m), 0m, 1e-11m);
    }
}
