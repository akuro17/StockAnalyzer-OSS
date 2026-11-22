using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslGeometryMath
{
    private const double AnalyticDegenerateEps = 1e-12;
    private const int MaxPolygonPointCount = 256;

    // ---- 4.8 2D Analytic Geometry & Coordinate Transforms ----

    public static MathErrorCode Line2D2Pts(decimal x1, decimal y1, decimal x2, decimal y2, scoped Span<decimal> result)
    {
        if ((x1 == x2 && y1 == y2) || result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        try
        {
            decimal a = checked(y1 - y2);
            decimal b = checked(x2 - x1);
            decimal c = checked(checked(x1 * y2) - checked(x2 * y1));
            result[0] = DslElementaryMath.NormalizeZero(a);
            result[1] = DslElementaryMath.NormalizeZero(b);
            result[2] = DslElementaryMath.NormalizeZero(c);
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    public static MathErrorCode Line2DIntercepts(decimal xInt, decimal yInt, scoped Span<decimal> result)
    {
        if (xInt == 0m || yInt == 0m || result.Length < 4)
        {
            return MathErrorCode.DomainError;
        }

        try
        {
            decimal c = checked(-checked(xInt * yInt));
            decimal slope = checked(-yInt / xInt);
            result[0] = DslElementaryMath.NormalizeZero(yInt);
            result[1] = DslElementaryMath.NormalizeZero(xInt);
            result[2] = DslElementaryMath.NormalizeZero(c);
            result[3] = DslElementaryMath.NormalizeZero(slope);
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    public static MathErrorCode Line2DIntersect(
        decimal a1,
        decimal b1,
        decimal c1,
        decimal a2,
        decimal b2,
        decimal c2,
        scoped Span<decimal> result)
    {
        if (result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathErrorCode err = DslLinearAlgebraMath.SolveLinearSystem2x2(a1, b1, a2, b2, -c1, -c2, out decimal x, out decimal y);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        result[0] = DslElementaryMath.NormalizeZero(x);
        result[1] = DslElementaryMath.NormalizeZero(y);
        return MathErrorCode.None;
    }

    public static MathResult<decimal> TriAreaPts(
        decimal x1,
        decimal y1,
        decimal x2,
        decimal y2,
        decimal x3,
        decimal y3)
    {
        try
        {
            decimal cross = checked(
                checked(x1 * checked(y2 - y3)) +
                checked(x2 * checked(y3 - y1)) +
                checked(x3 * checked(y1 - y2)));
            decimal area = checked(0.5m * Math.Abs(cross));
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(area));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> PolyAreaPts(scoped ReadOnlySpan<decimal> xs, scoped ReadOnlySpan<decimal> ys)
    {
        if (xs.Length != ys.Length)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (xs.Length < 3 || xs.Length > MaxPolygonPointCount)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<DslPoint2D> pts = stackalloc DslPoint2D[xs.Length];
        for (int i = 0; i < xs.Length; i++)
        {
            pts[i] = new DslPoint2D(xs[i], ys[i]);
        }

        return DslElementaryMath.PolygonArea(pts);
    }

    public static MathErrorCode Rot2D(decimal x, decimal y, decimal angleRad, bool inverseAxisRotation, scoped Span<decimal> result)
    {
        if (Math.Abs(angleRad) > DslElementaryMath.RadianTrigRangeLimit || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        (double s, double c) = Math.SinCos((double)angleRad);
        double dx = (double)x;
        double dy = (double)y;
        double rx = inverseAxisRotation ? (dx * c) + (dy * s) : (dx * c) - (dy * s);
        double ry = inverseAxisRotation ? -(dx * s) + (dy * c) : (dx * s) + (dy * c);

        return TryWriteDouble(result, 0, rx) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, ry);
    }

    public static MathErrorCode CartToPolar(decimal x, decimal y, scoped Span<decimal> result)
    {
        if (result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> rRes = DslElementaryMath.Hypot(x, y);
        if (!rRes.IsOk)
        {
            return rRes.Error;
        }

        MathResult<decimal> thetaRes = (x == 0m && y == 0m)
            ? MathResult<decimal>.Ok(0m)
            : DslElementaryMath.Atan2(y, x);
        if (!thetaRes.IsOk)
        {
            return thetaRes.Error;
        }

        result[0] = rRes.Value;
        result[1] = thetaRes.Value;
        return MathErrorCode.None;
    }

    public static MathErrorCode PolarToCart(decimal r, decimal thetaRad, scoped Span<decimal> result)
    {
        if (r < 0m || Math.Abs(thetaRad) > DslElementaryMath.RadianTrigRangeLimit || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        (double s, double c) = Math.SinCos((double)thetaRad);
        double dr = (double)r;
        return TryWriteDouble(result, 0, dr * c) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, dr * s);
    }

    // ---- 4.9 3D Analytic Geometry & Coordinate Transforms ----

    public static MathResult<double> Dist3DLineLine(
        scoped ReadOnlySpan<double> p1,
        scoped ReadOnlySpan<double> d1,
        scoped ReadOnlySpan<double> p2,
        scoped ReadOnlySpan<double> d2)
    {
        if (p1.Length != 3 || d1.Length != 3 || p2.Length != 3 || d2.Length != 3)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        MathResult<double> n1Res = DslLinearAlgebraMath.VecNorm(d1);
        if (!n1Res.IsOk)
        {
            return n1Res;
        }

        MathResult<double> n2Res = DslLinearAlgebraMath.VecNorm(d2);
        if (!n2Res.IsOk)
        {
            return n2Res;
        }

        double normD1 = n1Res.Value;
        double normD2 = n2Res.Value;
        if (normD1 <= AnalyticDegenerateEps || normD2 <= AnalyticDegenerateEps)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        Span<double> delta = stackalloc double[3];
        Subtract3D(p2, p1, delta);

        Span<double> crossN = stackalloc double[3];
        MathErrorCode crossErr = DslLinearAlgebraMath.Cross3D(d1, d2, crossN);
        if (crossErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(crossErr);
        }

        MathResult<double> normNRes = DslLinearAlgebraMath.VecNorm(crossN);
        if (!normNRes.IsOk)
        {
            return normNRes;
        }

        double normN = normNRes.Value;
        if (normN > AnalyticDegenerateEps * normD1 * normD2)
        {
            MathResult<double> dotRes = DslLinearAlgebraMath.VecDot(delta, crossN);
            if (!dotRes.IsOk)
            {
                return dotRes;
            }

            double dist = Math.Abs(dotRes.Value) / normN;
            return double.IsFinite(dist)
                ? MathResult<double>.Ok(dist == 0.0 ? 0.0 : dist)
                : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        Span<double> parallelCross = stackalloc double[3];
        MathErrorCode pErr = DslLinearAlgebraMath.Cross3D(delta, d1, parallelCross);
        if (pErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(pErr);
        }

        MathResult<double> normParRes = DslLinearAlgebraMath.VecNorm(parallelCross);
        if (!normParRes.IsOk)
        {
            return normParRes;
        }

        double pDist = normParRes.Value / normD1;
        return double.IsFinite(pDist)
            ? MathResult<double>.Ok(pDist == 0.0 ? 0.0 : pDist)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathErrorCode Plane3D3Pts(
        scoped ReadOnlySpan<double> p1,
        scoped ReadOnlySpan<double> p2,
        scoped ReadOnlySpan<double> p3,
        scoped Span<double> planeOut)
    {
        if (p1.Length != 3 || p2.Length != 3 || p3.Length != 3 || planeOut.Length < 4)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<double> u = stackalloc double[3];
        Span<double> v = stackalloc double[3];
        Subtract3D(p2, p1, u);
        Subtract3D(p3, p1, v);

        MathResult<double> nuRes = DslLinearAlgebraMath.VecNorm(u);
        if (!nuRes.IsOk)
        {
            return nuRes.Error;
        }

        MathResult<double> nvRes = DslLinearAlgebraMath.VecNorm(v);
        if (!nvRes.IsOk)
        {
            return nvRes.Error;
        }

        double normU = nuRes.Value;
        double normV = nvRes.Value;
        double scaleU = MaxAbsCoord3D(p1, p2);
        double scaleV = MaxAbsCoord3D(p1, p3);
        if (normU <= AnalyticDegenerateEps * scaleU || normV <= AnalyticDegenerateEps * scaleV)
        {
            return MathErrorCode.SingularMatrix;
        }

        Span<double> normal = stackalloc double[3];
        MathErrorCode crossErr = DslLinearAlgebraMath.Cross3D(u, v, normal);
        if (crossErr != MathErrorCode.None)
        {
            return crossErr;
        }

        MathResult<double> normRes = DslLinearAlgebraMath.VecNorm(normal);
        if (!normRes.IsOk)
        {
            return normRes.Error;
        }

        double norm = normRes.Value;
        if (norm <= AnalyticDegenerateEps * normU * normV)
        {
            return MathErrorCode.SingularMatrix;
        }

        double nx = normal[0] / norm;
        double ny = normal[1] / norm;
        double nz = normal[2] / norm;
        double d = -((nx * p1[0]) + (ny * p1[1]) + (nz * p1[2]));
        if (!double.IsFinite(nx) || !double.IsFinite(ny) || !double.IsFinite(nz) || !double.IsFinite(d))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        planeOut[0] = nx == 0.0 ? 0.0 : nx;
        planeOut[1] = ny == 0.0 ? 0.0 : ny;
        planeOut[2] = nz == 0.0 ? 0.0 : nz;
        planeOut[3] = d == 0.0 ? 0.0 : d;
        return MathErrorCode.None;
    }

    public static MathResult<double> TetraVolPts(
        scoped ReadOnlySpan<double> p1,
        scoped ReadOnlySpan<double> p2,
        scoped ReadOnlySpan<double> p3,
        scoped ReadOnlySpan<double> p4)
    {
        if (p1.Length != 3 || p2.Length != 3 || p3.Length != 3 || p4.Length != 3)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        Span<double> u = stackalloc double[3];
        Span<double> v = stackalloc double[3];
        Span<double> w = stackalloc double[3];
        Subtract3D(p2, p1, u);
        Subtract3D(p3, p1, v);
        Subtract3D(p4, p1, w);

        Span<double> cross = stackalloc double[3];
        MathErrorCode crossErr = DslLinearAlgebraMath.Cross3D(v, w, cross);
        if (crossErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(crossErr);
        }

        MathResult<double> dotRes = DslLinearAlgebraMath.VecDot(u, cross);
        if (!dotRes.IsOk)
        {
            return dotRes;
        }

        double vol = Math.Abs(dotRes.Value) / 6.0;
        return double.IsFinite(vol)
            ? MathResult<double>.Ok(vol == 0.0 ? 0.0 : vol)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<double> Dist3DPtPlane(
        scoped ReadOnlySpan<double> pt,
        scoped ReadOnlySpan<double> plane)
    {
        if (pt.Length != 3 || plane.Length != 4)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        MathResult<double> normRes = DslLinearAlgebraMath.VecNorm(plane.Slice(0, 3));
        if (!normRes.IsOk)
        {
            return normRes;
        }

        double norm = normRes.Value;
        if (norm <= AnalyticDegenerateEps)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double signedNum = (plane[0] * pt[0]) + (plane[1] * pt[1]) + (plane[2] * pt[2]) + plane[3];
        double dist = Math.Abs(signedNum) / norm;
        return double.IsFinite(dist)
            ? MathResult<double>.Ok(dist == 0.0 ? 0.0 : dist)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathErrorCode CartToSph(decimal x, decimal y, decimal z, scoped Span<decimal> result)
    {
        if (result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        if (x == 0m && y == 0m && z == 0m)
        {
            result[0] = 0m;
            result[1] = 0m;
            result[2] = 0m;
            return MathErrorCode.None;
        }

        Span<double> vec = [(double)x, (double)y, (double)z];
        MathResult<double> normRes = DslLinearAlgebraMath.VecNorm(vec);
        if (!normRes.IsOk)
        {
            return normRes.Error;
        }

        double r = normRes.Value;
        double theta = Math.Acos(Math.Clamp(vec[2] / r, -1.0, 1.0));
        double phi = (x == 0m && y == 0m) ? 0.0 : Math.Atan2(vec[1], vec[0]);

        return TryWriteDouble(result, 0, r) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, theta) is var e1 && e1 != MathErrorCode.None ? e1 :
               TryWriteDouble(result, 2, phi);
    }

    public static MathErrorCode CartToCyl(decimal x, decimal y, decimal z, scoped Span<decimal> result)
    {
        if (result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> rhoRes = DslElementaryMath.Hypot(x, y);
        if (!rhoRes.IsOk)
        {
            return rhoRes.Error;
        }

        MathResult<decimal> phiRes = (x == 0m && y == 0m)
            ? MathResult<decimal>.Ok(0m)
            : DslElementaryMath.Atan2(y, x);
        if (!phiRes.IsOk)
        {
            return phiRes.Error;
        }

        result[0] = rhoRes.Value;
        result[1] = phiRes.Value;
        result[2] = DslElementaryMath.NormalizeZero(z);
        return MathErrorCode.None;
    }

    public static MathErrorCode SphToCart(decimal r, decimal thetaRad, decimal phiRad, scoped Span<decimal> result)
    {
        double theta = (double)thetaRad;
        if (r < 0m || !double.IsFinite(theta) || theta < 0.0 || theta > Math.PI || Math.Abs(phiRad) > DslElementaryMath.RadianTrigRangeLimit || result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        double dr = (double)r;
        (double sinT, double cosT) = Math.SinCos(theta);
        (double sinP, double cosP) = Math.SinCos((double)phiRad);
        double rho = dr * sinT;

        return TryWriteDouble(result, 0, rho * cosP) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, rho * sinP) is var e1 && e1 != MathErrorCode.None ? e1 :
               TryWriteDouble(result, 2, dr * cosT);
    }

    public static MathErrorCode SphToCyl(decimal r, decimal thetaRad, decimal phiRad, scoped Span<decimal> result)
    {
        double theta = (double)thetaRad;
        if (r < 0m || !double.IsFinite(theta) || theta < 0.0 || theta > Math.PI || Math.Abs(phiRad) > DslElementaryMath.RadianTrigRangeLimit || result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        double dr = (double)r;
        (double sinT, double cosT) = Math.SinCos(theta);
        result[1] = DslElementaryMath.NormalizeZero(phiRad);

        return TryWriteDouble(result, 0, dr * sinT) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 2, dr * cosT);
    }

    public static MathErrorCode CylToCart(decimal rho, decimal phiRad, decimal z, scoped Span<decimal> result)
    {
        if (rho < 0m || Math.Abs(phiRad) > DslElementaryMath.RadianTrigRangeLimit || result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        (double sinP, double cosP) = Math.SinCos((double)phiRad);
        double drho = (double)rho;
        result[2] = DslElementaryMath.NormalizeZero(z);

        return TryWriteDouble(result, 0, drho * cosP) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, drho * sinP);
    }

    public static MathErrorCode CylToSph(decimal rho, decimal phiRad, decimal z, scoped Span<decimal> result)
    {
        if (rho < 0m || Math.Abs(phiRad) > DslElementaryMath.RadianTrigRangeLimit || result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        double drho = (double)rho;
        double dz = (double)z;
        double r = DslElementaryMath.HypotDouble(drho, dz);
        if (!double.IsFinite(r))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        double theta = r == 0.0 ? 0.0 : Math.Acos(Math.Clamp(dz / r, -1.0, 1.0));
        result[2] = DslElementaryMath.NormalizeZero(phiRad);

        return TryWriteDouble(result, 0, r) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, theta);
    }

    public static MathErrorCode Rot3DPt(
        scoped ReadOnlySpan<double> point,
        scoped ReadOnlySpan<double> axis,
        double angleRad,
        scoped Span<double> rotatedOut)
    {
        if (point.Length != 3 || axis.Length != 3 || rotatedOut.Length < 3)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<double> rot = stackalloc double[9];
        MathErrorCode err = DslLinearAlgebraMath.MatRot3D(axis, angleRad, rot);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        for (int i = 0; i < 3; i++)
        {
            int row = i * 3;
            double val = (rot[row] * point[0]) + (rot[row + 1] * point[1]) + (rot[row + 2] * point[2]);
            if (!double.IsFinite(val))
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            rotatedOut[i] = val == 0.0 ? 0.0 : val;
        }

        return MathErrorCode.None;
    }

    private static void Subtract3D(scoped ReadOnlySpan<double> a, scoped ReadOnlySpan<double> b, scoped Span<double> dst)
    {
        dst[0] = a[0] - b[0];
        dst[1] = a[1] - b[1];
        dst[2] = a[2] - b[2];
    }

    private static double MaxAbsCoord3D(scoped ReadOnlySpan<double> a, scoped ReadOnlySpan<double> b)
    {
        double m = Math.Max(Math.Abs(a[0]), Math.Max(Math.Abs(a[1]), Math.Abs(a[2])));
        return Math.Max(m, Math.Max(Math.Abs(b[0]), Math.Max(Math.Abs(b[1]), Math.Abs(b[2]))));
    }
}
