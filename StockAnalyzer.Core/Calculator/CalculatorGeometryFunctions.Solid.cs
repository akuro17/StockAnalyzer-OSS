using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal static partial class CalculatorGeometryFunctions
{
    private static bool TryEvaluateSolid(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            case "cube_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CubeVol(args[0]), position, name);
                return true;

            case "cube_side_from_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CubeSideFromVol(args[0]), position, name);
                return true;

            case "cuboid_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CuboidVol(args[0], args[1], args[2]), position, name);
                return true;

            case "tetra_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TetraVol(args[0], args[1]), position, name);
                return true;

            case "reg_tetra_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RegTetraVol(args[0]), position, name);
                return true;

            case "reg_tetra_side_from_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RegTetraSideFromVol(args[0]), position, name);
                return true;

            case "tri_prism_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TriPrismVol(args[0], args[1]), position, name);
                return true;

            case "tri_prism_h_from_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TriPrismHFromVol(args[0], args[1]), position, name);
                return true;

            case "sq_prism_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SqPrismVol(args[0], args[1]), position, name);
                return true;

            case "sq_prism_h_from_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SqPrismHFromVol(args[0], args[1]), position, name);
                return true;

            case "hex_prism_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.HexPrismVol(args[0], args[1]), position, name);
                return true;

            case "hex_prism_h_from_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.HexPrismHFromVol(args[0], args[1]), position, name);
                return true;

            case "sq_pyramid_vol_bh":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SqPyramidVolBh(args[0], args[1]), position, name);
                return true;

            case "sq_pyramid_vol_be":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SqPyramidVolBe(args[0], args[1]), position, name);
                return true;

            case "sq_frustum_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SqFrustumVol(args[0], args[1], args[2]), position, name);
                return true;

            case "rect_frustum_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RectFrustumVol(args[0], args[1], args[2], args[3], args[4]), position, name);
                return true;

            case "wedge_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.WedgeVol(args[0], args[1], args[2], args[3]), position, name);
                return true;

            case "pyramid_frustum_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.PyramidFrustumVol(args[0], args[1], args[2]), position, name);
                return true;

            case "pyramid_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.PyramidVol(args[0], args[1]), position, name);
                return true;

            case "cyl_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CylVol(args[0], args[1]), position, name);
                return true;

            case "cyl_partial_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CylPartialVol(args[0], args[1], args[2]), position, name);
                return true;

            case "cyl_hollow_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CylHollowVol(args[0], args[1], args[2]), position, name);
                return true;

            case "cyl_oblique_cut_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CylObliqueCutVol(args[0], args[1], args[2]), position, name);
                return true;

            case "ellip_cyl_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipCylVol(args[0], args[1], args[2]), position, name);
                return true;

            case "cone_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.ConeVol(args[0], args[1]), position, name);
                return true;

            case "cone_oblique_cut_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.ConeObliqueCutVol(args[0], args[1], args[2], args[3]), position, name);
                return true;

            case "cone_partial_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.ConePartialVol(args[0], args[1], args[2]), position, name);
                return true;

            case "cone_frustum_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.ConeFrustumVol(args[0], args[1], args[2]), position, name);
                return true;

            case "ellip_cone_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipConeVol(args[0], args[1], args[2]), position, name);
                return true;

            case "ellip_frustum_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipFrustumVol(args[0], args[1], args[2], args[3], args[4]), position, name);
                return true;

            case "sphere_cap_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SphereCapVol(args[0], args[1]), position, name);
                return true;

            case "sphere_zone_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SphereZoneVol(args[0], args[1], args[2]), position, name);
                return true;

            case "segment_rev_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SegmentRevVol(args[0], args[1]), position, name);
                return true;

            case "segment_rev_partial_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SegmentRevPartialVol(args[0], args[1], args[2]), position, name);
                return true;

            case "ellipsoid_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipsoidVol(args[0], args[1], args[2]), position, name);
                return true;

            case "ellipsoid_cap_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipsoidCapVol(args[0], args[1], args[2], args[3]), position, name);
                return true;

            case "spheroid_cap_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SpheroidCapVol(args[0], args[1], args[2]), position, name);
                return true;

            case "platonic_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.PlatonicVol(args[0], args[1]), position, name);
                return true;

            case "nball_vol":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.NBallVol(args[0], args[1]), position, name);
                return true;

            case "sphere_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SphereArea(args[0]), position, name);
                return true;

            case "cyl_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CylArea(args[0], args[1]), position, name);
                return true;

            case "torus_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TorusArea(args[0], args[1]), position, name);
                return true;

            case "cuboid_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CuboidArea(args[0], args[1], args[2]), position, name);
                return true;

            default:
                outcome = default;
                return false;
        }
    }
}
