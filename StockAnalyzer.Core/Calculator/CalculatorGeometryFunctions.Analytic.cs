using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal static partial class CalculatorGeometryFunctions
{
    private static bool TryEvaluateValueAnalytic(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "poly_area_pts":
                outcome = EvaluatePolyAreaPts(name, position, args, in arena);
                return true;

            case "dist3d_line_line":
                outcome = EvaluateDist3DLineLine(name, position, args, in arena);
                return true;

            case "plane3d_3pts":
                outcome = EvaluatePlane3D3Pts(name, position, args, ref arena);
                return true;

            case "tetra_vol_pts":
                outcome = EvaluateTetraVolPts(name, position, args, in arena);
                return true;

            case "dist3d_pt_plane":
                outcome = EvaluateDist3DPtPlane(name, position, args, in arena);
                return true;

            case "mat_rot3d":
                outcome = EvaluateMatRot3D(name, position, args, ref arena);
                return true;

            case "rot3d_pt":
                outcome = EvaluateRot3DPt(name, position, args, ref arena);
                return true;

            default:
                break;
        }

        int outLen = name switch
        {
            "line2d_2pts" => 3,
            "line2d_intercepts" => 4,
            "line2d_intersect" => 2,
            "rot2d_pt" => 2,
            "rot2d_axis" => 2,
            "cart_to_polar" => 2,
            "polar_to_cart" => 2,
            "cart_to_sph" => 3,
            "cart_to_cyl" => 3,
            "sph_to_cart" => 3,
            "sph_to_cyl" => 3,
            "cyl_to_cart" => 3,
            "cyl_to_sph" => 3,
            _ => 0,
        };

        if (outLen == 0)
        {
            outcome = default;
            return false;
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
            "line2d_2pts" => DslGeometryMath.Line2D2Pts(args[0].Scalar, args[1].Scalar, args[2].Scalar, args[3].Scalar, dst),
            "line2d_intercepts" => DslGeometryMath.Line2DIntercepts(args[0].Scalar, args[1].Scalar, dst),
            "line2d_intersect" => DslGeometryMath.Line2DIntersect(args[0].Scalar, args[1].Scalar, args[2].Scalar, args[3].Scalar, args[4].Scalar, args[5].Scalar, dst),
            "rot2d_pt" => DslGeometryMath.Rot2D(args[0].Scalar, args[1].Scalar, args[2].Scalar, inverseAxisRotation: false, dst),
            "rot2d_axis" => DslGeometryMath.Rot2D(args[0].Scalar, args[1].Scalar, args[2].Scalar, inverseAxisRotation: true, dst),
            "cart_to_polar" => DslGeometryMath.CartToPolar(args[0].Scalar, args[1].Scalar, dst),
            "polar_to_cart" => DslGeometryMath.PolarToCart(args[0].Scalar, args[1].Scalar, dst),
            "cart_to_sph" => DslGeometryMath.CartToSph(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
            "cart_to_cyl" => DslGeometryMath.CartToCyl(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
            "sph_to_cart" => DslGeometryMath.SphToCart(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
            "sph_to_cyl" => DslGeometryMath.SphToCyl(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
            "cyl_to_cart" => DslGeometryMath.CylToCart(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
            "cyl_to_sph" => DslGeometryMath.CylToSph(args[0].Scalar, args[1].Scalar, args[2].Scalar, dst),
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

    private static DslEvalOutcome EvaluatePolyAreaPts(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects (xs, ys) array arguments.", name);
        }

        ReadOnlySpan<decimal> xs = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> ys = arena.GetSlice(args[1].Array);
        MathResult<decimal> res = DslGeometryMath.PolyAreaPts(xs, ys);
        return CalculatorArrayFunctions.ToEvalOutcome(CalculatorElementaryFunctions.FromMathResult(res, position, name));
    }

    private static bool TryReadFixedVectorDouble(
        DslValue arg,
        int expectedLength,
        scoped Span<double> dst,
        in DslArrayArena arena,
        int position,
        string name,
        out DslEvalOutcome errorOutcome)
    {
        if (!arg.IsArray)
        {
            errorOutcome = DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects array coordinate arguments.", name);
            return false;
        }

        ReadOnlySpan<decimal> slice = arena.GetSlice(arg.Array);
        if (slice.Length != expectedLength)
        {
            errorOutcome = CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, position, name);
            return false;
        }

        CalculatorMatrixFunctions.CopyToDouble(slice, dst);
        errorOutcome = default;
        return true;
    }

    private static DslEvalOutcome EvaluateDist3DLineLine(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        Span<double> p1 = stackalloc double[3];
        Span<double> d1 = stackalloc double[3];
        Span<double> p2 = stackalloc double[3];
        Span<double> d2 = stackalloc double[3];

        if (!TryReadFixedVectorDouble(args[0], 3, p1, in arena, position, name, out DslEvalOutcome err) ||
            !TryReadFixedVectorDouble(args[1], 3, d1, in arena, position, name, out err) ||
            !TryReadFixedVectorDouble(args[2], 3, p2, in arena, position, name, out err) ||
            !TryReadFixedVectorDouble(args[3], 3, d2, in arena, position, name, out err))
        {
            return err;
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(DslGeometryMath.Dist3DLineLine(p1, d1, p2, d2), position, name);
    }

    private static DslEvalOutcome EvaluatePlane3D3Pts(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        Span<double> p1 = stackalloc double[3];
        Span<double> p2 = stackalloc double[3];
        Span<double> p3 = stackalloc double[3];
        if (!TryReadFixedVectorDouble(args[0], 3, p1, in arena, position, name, out DslEvalOutcome err) ||
            !TryReadFixedVectorDouble(args[1], 3, p2, in arena, position, name, out err) ||
            !TryReadFixedVectorDouble(args[2], 3, p3, in arena, position, name, out err))
        {
            return err;
        }

        Span<double> plane = stackalloc double[4];
        MathErrorCode mErr = DslGeometryMath.Plane3D3Pts(p1, p2, p3, plane);
        return mErr == MathErrorCode.None
            ? CalculatorMatrixFunctions.NarrowArrayResult(plane, position, name, ref arena)
            : CalculatorMatrixFunctions.FailMath(mErr, position, name);
    }

    private static DslEvalOutcome EvaluateTetraVolPts(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        Span<double> p1 = stackalloc double[3];
        Span<double> p2 = stackalloc double[3];
        Span<double> p3 = stackalloc double[3];
        Span<double> p4 = stackalloc double[3];
        if (!TryReadFixedVectorDouble(args[0], 3, p1, in arena, position, name, out DslEvalOutcome err) ||
            !TryReadFixedVectorDouble(args[1], 3, p2, in arena, position, name, out err) ||
            !TryReadFixedVectorDouble(args[2], 3, p3, in arena, position, name, out err) ||
            !TryReadFixedVectorDouble(args[3], 3, p4, in arena, position, name, out err))
        {
            return err;
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(DslGeometryMath.TetraVolPts(p1, p2, p3, p4), position, name);
    }

    private static DslEvalOutcome EvaluateDist3DPtPlane(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        in DslArrayArena arena)
    {
        Span<double> pt = stackalloc double[3];
        Span<double> plane = stackalloc double[4];
        if (!TryReadFixedVectorDouble(args[0], 3, pt, in arena, position, name, out DslEvalOutcome err) ||
            !TryReadFixedVectorDouble(args[1], 4, plane, in arena, position, name, out err))
        {
            return err;
        }

        return CalculatorMatrixFunctions.NarrowScalarResult(DslGeometryMath.Dist3DPtPlane(pt, plane), position, name);
    }

    private static DslEvalOutcome EvaluateMatRot3D(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        Span<double> axis = stackalloc double[3];
        if (!TryReadFixedVectorDouble(args[0], 3, axis, in arena, position, name, out DslEvalOutcome err))
        {
            return err;
        }

        if (args[1].IsArray || Math.Abs(args[1].Scalar) > DslElementaryMath.RadianTrigRangeLimit)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> mat = stackalloc double[9];
        MathErrorCode mErr = DslLinearAlgebraMath.MatRot3D(axis, (double)args[1].Scalar, mat);
        return mErr == MathErrorCode.None
            ? CalculatorMatrixFunctions.NarrowArrayResult(mat, position, name, ref arena)
            : CalculatorMatrixFunctions.FailMath(mErr, position, name);
    }

    private static DslEvalOutcome EvaluateRot3DPt(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslArrayArena arena)
    {
        Span<double> pt = stackalloc double[3];
        Span<double> axis = stackalloc double[3];
        if (!TryReadFixedVectorDouble(args[0], 3, pt, in arena, position, name, out DslEvalOutcome err) ||
            !TryReadFixedVectorDouble(args[1], 3, axis, in arena, position, name, out err))
        {
            return err;
        }

        if (args[2].IsArray || Math.Abs(args[2].Scalar) > DslElementaryMath.RadianTrigRangeLimit)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DomainError, position, name);
        }

        Span<double> rotated = stackalloc double[3];
        MathErrorCode mErr = DslGeometryMath.Rot3DPt(pt, axis, (double)args[2].Scalar, rotated);
        return mErr == MathErrorCode.None
            ? CalculatorMatrixFunctions.NarrowArrayResult(rotated, position, name, ref arena)
            : CalculatorMatrixFunctions.FailMath(mErr, position, name);
    }
}
