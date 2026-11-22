using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for DSL v6 Euclidean, solid, and analytic geometry functions.
/// </summary>
internal static partial class CalculatorGeometryFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        ref DslExecutionBudget budget,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            case "tri_area_equilateral":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TriAreaEquilateral(args[0]), position, name);
                return true;

            case "tri_area_bh":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TriAreaBh(args[0], args[1]), position, name);
                return true;

            case "tri_area_sas":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TriAreaSas(args[0], args[1], args[2]), position, name);
                return true;

            case "tri_area_asa":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TriAreaAsa(args[0], args[1], args[2]), position, name);
                return true;

            case "square_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SquareArea(args[0]), position, name);
                return true;

            case "rect_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RectArea(args[0], args[1]), position, name);
                return true;

            case "trapezoid_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.TrapezoidArea(args[0], args[1], args[2]), position, name);
                return true;

            case "rhombus_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RhombusArea(args[0], args[1]), position, name);
                return true;

            case "parallelogram_area_bh":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.ParallelogramAreaBh(args[0], args[1]), position, name);
                return true;

            case "parallelogram_area_sas":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.ParallelogramAreaSas(args[0], args[1], args[2]), position, name);
                return true;

            case "cyclic_quad_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CyclicQuadArea(args[0], args[1], args[2], args[3]), position, name);
                return true;

            case "quad_area_bretschneider":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.QuadAreaBretschneider(args[0], args[1], args[2], args[3], args[4]), position, name);
                return true;

            case "reg_poly_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RegPolyArea(args[0], args[1]), position, name);
                return true;

            case "reg_poly_side_from_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RegPolySideFromArea(args[0], args[1]), position, name);
                return true;

            case "circle_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CircleArea(args[0]), position, name);
                return true;

            case "circle_radius_from_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.CircleRadiusFromArea(args[0]), position, name);
                return true;

            case "sector_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SectorArea(args[0], args[1]), position, name);
                return true;

            case "segment_area_angle":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SegmentAreaAngle(args[0], args[1]), position, name);
                return true;

            case "segment_area_rh":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SegmentAreaRh(args[0], args[1]), position, name);
                return true;

            case "segment_area_ch":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.SegmentAreaCh(args[0], args[1]), position, name);
                return true;

            case "ellipse_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipseArea(args[0], args[1]), position, name);
                return true;

            case "ellipse_sector_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipseSectorArea(args[0], args[1], args[2], args[3]), position, name);
                return true;

            case "ellipse_segment_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.EllipseSegmentArea(args[0], args[1], args[2]), position, name);
                return true;

            case "parabola_segment_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.ParabolaSegmentArea(args[0], args[1]), position, name);
                return true;

            case "hyperbola_segment_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.HyperbolaSegmentArea(args[0], args[1], args[2]), position, name);
                return true;

            case "hyperbola_sector_area":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.HyperbolaSectorArea(args[0], args[1], args[2]), position, name);
                return true;

            case "reg_poly_inradius":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RegPolyInradius(args[0], args[1]), position, name);
                return true;

            case "reg_poly_circumradius":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RegPolyCircumradius(args[0], args[1]), position, name);
                return true;

            case "rect_circumradius":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslGeometryMath.RectCircumradius(args[0], args[1]), position, name);
                return true;

            case "tri_area_pts":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslGeometryMath.TriAreaPts(args[0], args[1], args[2], args[3], args[4], args[5]),
                    position,
                    name);
                return true;

            default:
                return TryEvaluateSolid(name, position, args, out outcome);
        }
    }

    public static bool TryEvaluateValue(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        int outLen = name switch
        {
            "tri_equilateral" => 5,
            "tri_right_isosceles" => 4,
            "tri_right" => 6,
            "tri_isosceles" => 5,
            "tri_scalene" => 7,
            "trapezoid_4sides" => 2,
            "trapezoid_3sides_h" => 2,
            "circle_inscribed_reg_poly" => 3,
            "circle_circumscribed_reg_poly" => 3,
            "rt_tri_from_angle_base_deg" => 2,
            "rt_tri_from_angle_height_deg" => 2,
            "rt_tri_from_angle_hyp_deg" => 2,
            "rt_tri_from_base_height_deg" => 2,
            "rt_tri_from_base_hyp_deg" => 2,
            "rt_tri_from_height_hyp_deg" => 2,
            "tri_angles_sss_deg" => 3,
            _ => 0,
        };

        if (outLen == 0)
        {
            return TryEvaluateValueAnalytic(name, position, args, ref arena, out outcome);
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsArray)
            {
                outcome = DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects scalar arguments.", name);
                return true;
            }
        }

        int savedCursor = arena.Cursor;
        if (!arena.TryAllocate(outLen, out DslArrayHandle handle, out Span<decimal> dst))
        {
            outcome = CalculatorMatrixFunctions.FailMath(MathErrorCode.BudgetExceeded, position, name);
            return true;
        }

        MathErrorCode err = name switch
        {
            "tri_equilateral" => DslGeometryMath.TriEquilateral(args[0].Scalar, dst),
            "tri_right_isosceles" => DslGeometryMath.TriRightIsosceles(args[0].Scalar, dst),
            "tri_right" => DslGeometryMath.TriRight(args[0].Scalar, args[1].Scalar, dst),
            "tri_isosceles" => DslGeometryMath.TriIsosceles(args[0].Scalar, args[1].Scalar, dst),
            "tri_scalene" => DslGeometryMath.TriScalene(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
            "trapezoid_4sides" => DslGeometryMath.Trapezoid4Sides(args[0].Scalar, args[1].Scalar, args[2].Scalar, args[3].Scalar, dst),
            "trapezoid_3sides_h" => DslGeometryMath.Trapezoid3SidesH(args[0].Scalar, args[1].Scalar, args[2].Scalar, args[3].Scalar, dst),
            "circle_inscribed_reg_poly" => DslGeometryMath.CircleInscribedRegPoly(args[0].Scalar, args[1].Scalar, dst),
            "circle_circumscribed_reg_poly" => DslGeometryMath.CircleCircumscribedRegPoly(args[0].Scalar, args[1].Scalar, dst),
            "rt_tri_from_angle_base_deg" => DslGeometryMath.RtTriFromAngleBaseDeg(args[0].Scalar, args[1].Scalar, dst),
            "rt_tri_from_angle_height_deg" => DslGeometryMath.RtTriFromAngleHeightDeg(args[0].Scalar, args[1].Scalar, dst),
            "rt_tri_from_angle_hyp_deg" => DslGeometryMath.RtTriFromAngleHypDeg(args[0].Scalar, args[1].Scalar, dst),
            "rt_tri_from_base_height_deg" => DslGeometryMath.RtTriFromBaseHeightDeg(args[0].Scalar, args[1].Scalar, dst),
            "rt_tri_from_base_hyp_deg" => DslGeometryMath.RtTriFromBaseHypDeg(args[0].Scalar, args[1].Scalar, dst),
            "rt_tri_from_height_hyp_deg" => DslGeometryMath.RtTriFromHeightHypDeg(args[0].Scalar, args[1].Scalar, dst),
            "tri_angles_sss_deg" => DslGeometryMath.TriAnglesSssDeg(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
            _ => MathErrorCode.DomainError,
        };

        if (err != MathErrorCode.None)
        {
            arena.RestoreCursor(savedCursor);
            outcome = CalculatorMatrixFunctions.FailMath(err, position, name);
            return true;
        }

        outcome = DslEvalOutcome.OkArray(handle);
        return true;
    }
}
