using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslGeometryMath
{
    private const double Sqrt5 = 2.2360679774997896964;
    private const double ObliqueConeSlopeEpsilon = 1e-12;
    private const double SegmentRevLengthTolerance = 1e-12;
    private const int MinNBallDim = 1;
    private const int MaxNBallDim = 1000;

    // ---- 4.6 3D Volume, Inverse Dimensions & Surface Area Formulas ----

    public static MathResult<decimal> CubeVol(decimal a)
    {
        if (a <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(a * a * a)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> CubeSideFromVol(decimal v)
        => v <= 0m ? MathResult<decimal>.Fail(MathErrorCode.DomainError) : DslElementaryMath.Cbrt(v);

    public static MathResult<decimal> CuboidVol(decimal a, decimal b, decimal c)
    {
        if (a <= 0m || b <= 0m || c <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(a * b * c)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> TetraVol(decimal baseArea, decimal h)
        => PyramidVol(baseArea, h);

    public static MathResult<decimal> RegTetraVol(decimal a)
    {
        if (a <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        return DslElementaryMath.NarrowDouble((Sqrt2 / 12.0) * da * da * da);
    }

    public static MathResult<decimal> RegTetraSideFromVol(decimal v)
        => v <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(Math.Cbrt((12.0 * (double)v) / Sqrt2));

    public static MathResult<decimal> TriPrismVol(decimal a, decimal h)
        => a <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble((Sqrt3 * 0.25) * (double)a * (double)a * (double)h);

    public static MathResult<decimal> TriPrismHFromVol(decimal a, decimal v)
    {
        if (a <= 0m || v <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        return DslElementaryMath.NarrowDouble((4.0 * (double)v) / (Sqrt3 * da * da));
    }

    public static MathResult<decimal> SqPrismVol(decimal a, decimal h)
        => a <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : CuboidVol(a, a, h);

    public static MathResult<decimal> SqPrismHFromVol(decimal a, decimal v)
    {
        if (a <= 0m || v <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(v / checked(a * a))));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> HexPrismVol(decimal a, decimal h)
        => a <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble((1.5 * Sqrt3) * (double)a * (double)a * (double)h);

    public static MathResult<decimal> HexPrismHFromVol(decimal a, decimal v)
    {
        if (a <= 0m || v <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        return DslElementaryMath.NarrowDouble((2.0 * (double)v) / (3.0 * Sqrt3 * da * da));
    }

    public static MathResult<decimal> SqPyramidVolBh(decimal a, decimal h)
    {
        if (a <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(a * a * h) / 3m));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> SqPyramidVolBe(decimal a, decimal lateralEdge)
    {
        if (a <= 0m || lateralEdge <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double de = (double)lateralEdge;
        double halfDiag = da / Sqrt2;
        if (de <= halfDiag)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double h = Math.Sqrt((de - halfDiag) * (de + halfDiag));
        if (!(h > 0.0))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble((da * da * h) / 3.0);
    }

    public static MathResult<decimal> SqFrustumVol(decimal a, decimal b, decimal h)
    {
        if (a <= 0m || b <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal quad = checked(checked(a * a) + checked(a * b) + checked(b * b));
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(h * quad) / 3m));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> RectFrustumVol(decimal a1, decimal b1, decimal a2, decimal b2, decimal h)
    {
        if (a1 <= 0m || b1 <= 0m || a2 <= 0m || b2 <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal inner = checked(checked(a1 * b1) + checked(a2 * b2) + checked(checked(a1 + a2) * checked(b1 + b2)));
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(h * inner) / 6m));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> WedgeVol(decimal a, decimal b, decimal c, decimal h)
    {
        if (a <= 0m || b <= 0m || c < 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal factor = checked(checked(2m * a) + c);
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(checked(b * h) * factor) / 6m));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> PyramidFrustumVol(decimal s1, decimal s2, decimal h)
    {
        if (s1 <= 0m || s2 <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double ds1 = (double)s1;
        double ds2 = (double)s2;
        return DslElementaryMath.NarrowDouble(((double)h / 3.0) * (ds1 + ds2 + Math.Sqrt(ds1 * ds2)));
    }

    public static MathResult<decimal> PyramidVol(decimal baseArea, decimal h)
    {
        if (baseArea <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(baseArea * h) / 3m));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> CylVol(decimal r, decimal h)
        => r <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(Math.PI * (double)r * (double)r * (double)h);

    public static MathResult<decimal> CylPartialVol(decimal r, decimal h, decimal fillDepth)
    {
        if (r <= 0m || h <= 0m || fillDepth <= 0m || (fillDepth / 2m) > r)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(ComputeSegmentAreaRhDouble((double)r, (double)fillDepth) * (double)h);
    }

    public static MathResult<decimal> CylHollowVol(decimal rOuter, decimal rInner, decimal h)
    {
        if (rInner <= 0m || rOuter <= rInner || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double rOut = (double)rOuter;
        double rIn = (double)rInner;
        return DslElementaryMath.NarrowDouble(Math.PI * (rOut - rIn) * (rOut + rIn) * (double)h);
    }

    public static MathResult<decimal> CylObliqueCutVol(decimal r, decimal h1, decimal h2)
        => r <= 0m || h1 <= 0m || h2 <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(Math.PI * (double)r * (double)r * (0.5 * ((double)h1 + (double)h2)));

    public static MathResult<decimal> EllipCylVol(decimal a, decimal b, decimal h)
        => a <= 0m || b <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(Math.PI * (double)a * (double)b * (double)h);

    public static MathResult<decimal> ConeVol(decimal r, decimal h)
        => r <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble((Math.PI / 3.0) * (double)r * (double)r * (double)h);

    public static MathResult<decimal> ConeObliqueCutVol(decimal r, decimal h, decimal cutAngleRad, decimal cutHeight)
    {
        if (r <= 0m || h <= 0m || cutHeight <= 0m || cutHeight >= h)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double absAlpha = Math.Abs((double)cutAngleRad);
        if (!double.IsFinite(absAlpha) || absAlpha >= MathConstants.HalfPi)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dr = (double)r;
        double dh = (double)h;
        double z0 = (double)cutHeight;
        double k = dr / dh;
        double m = Math.Tan(absAlpha);
        double km = k * m;
        if (!double.IsFinite(km) || km >= 1.0 - ObliqueConeSlopeEpsilon || (z0 / (1.0 - km)) > dh)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double ecc = Math.Pow((1.0 - km) * (1.0 + km), 1.5);
        double term = (z0 * z0 * z0) / (dh * dh * ecc);
        double vol = (Math.PI * dr * dr / 3.0) * (dh - term);
        return DslElementaryMath.NarrowDouble(vol);
    }

    public static MathResult<decimal> ConePartialVol(decimal r, decimal h, decimal cutOffset)
    {
        if (r <= 0m || h <= 0m || cutOffset <= 0m || cutOffset >= r)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dr = (double)r;
        double dh = (double)h;
        double d = (double)cutOffset;
        double s = Math.Sqrt(Math.Max(0.0, (dr - d) * (dr + d)));
        double term1 = dr * dr * Math.Acos(Math.Clamp(d / dr, -1.0, 1.0));
        double term2 = 2.0 * d * s;
        double term3 = ((d * d * d) / dr) * Math.Log((dr + s) / d);
        return DslElementaryMath.NarrowDouble((dh / 3.0) * (term1 - term2 + term3));
    }

    public static MathResult<decimal> ConeFrustumVol(decimal r1, decimal r2, decimal h)
    {
        if (r1 <= 0m || r2 <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double d1 = (double)r1;
        double d2 = (double)r2;
        return DslElementaryMath.NarrowDouble((Math.PI * (double)h / 3.0) * ((d1 * d1) + (d1 * d2) + (d2 * d2)));
    }

    public static MathResult<decimal> EllipConeVol(decimal a, decimal b, decimal h)
        => a <= 0m || b <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble((Math.PI / 3.0) * (double)a * (double)b * (double)h);

    public static MathResult<decimal> EllipFrustumVol(decimal a1, decimal b1, decimal a2, decimal b2, decimal h)
    {
        if (a1 <= 0m || b1 <= 0m || a2 <= 0m || b2 <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da1 = (double)a1;
        double db1 = (double)b1;
        double da2 = (double)a2;
        double db2 = (double)b2;
        double vol = (Math.PI * (double)h / 6.0) * ((da1 * db1) + (da2 * db2) + ((da1 + da2) * (db1 + db2)));
        return DslElementaryMath.NarrowDouble(vol);
    }

    public static MathResult<decimal> SphereCapVol(decimal r, decimal h)
    {
        if (r <= 0m || h <= 0m || (h / 2m) > r)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dr = (double)r;
        double dh = (double)h;
        return DslElementaryMath.NarrowDouble((Math.PI * dh * dh / 3.0) * ((3.0 * dr) - dh));
    }

    public static MathResult<decimal> SphereZoneVol(decimal r1, decimal r2, decimal h)
    {
        if (r1 < 0m || r2 < 0m || (r1 == 0m && r2 == 0m) || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double d1 = (double)r1;
        double d2 = (double)r2;
        double dh = (double)h;
        return DslElementaryMath.NarrowDouble((Math.PI * dh / 6.0) * ((3.0 * d1 * d1) + (3.0 * d2 * d2) + (dh * dh)));
    }

    public static MathResult<decimal> SegmentRevVol(decimal r, decimal h)
    {
        if (r <= 0m || h <= 0m || h > r)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dr = (double)r;
        double dh = (double)h;
        double halfChord = Math.Sqrt(Math.Max(0.0, ((2.0 * dr) - dh) * dh));
        return DslElementaryMath.NarrowDouble(ComputeSegmentRevSliceDouble(dr, dh, halfChord));
    }

    public static MathResult<decimal> SegmentRevPartialVol(decimal r, decimal h, decimal cutLen)
    {
        if (r <= 0m || h <= 0m || h > r || cutLen <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dr = (double)r;
        double dh = (double)h;
        double maxL = Math.Sqrt(Math.Max(0.0, ((2.0 * dr) - dh) * dh));
        double l = (double)cutLen;
        if (l > maxL + SegmentRevLengthTolerance)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(ComputeSegmentRevSliceDouble(dr, dh, Math.Min(l, maxL)));
    }

    public static MathResult<decimal> EllipsoidVol(decimal a, decimal b, decimal c)
        => a <= 0m || b <= 0m || c <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble((4.0 / 3.0) * Math.PI * (double)a * (double)b * (double)c);

    public static MathResult<decimal> EllipsoidCapVol(decimal a, decimal b, decimal c, decimal h)
    {
        if (a <= 0m || b <= 0m || c <= 0m || h <= 0m || (h / 2m) > c)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        double dc = (double)c;
        double dh = (double)h;
        double vol = ((Math.PI * da * db * dh * dh) / (3.0 * dc * dc)) * ((3.0 * dc) - dh);
        return DslElementaryMath.NarrowDouble(vol);
    }

    public static MathResult<decimal> SpheroidCapVol(decimal a, decimal c, decimal h)
        => EllipsoidCapVol(a, a, c, h);

    public static MathResult<decimal> PlatonicVol(decimal facesDec, decimal a)
    {
        if (a <= 0m || !DslMathValidation.TryReadInt32InRange(facesDec, 4, 20, out int faces))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double a3 = da * da * da;
        double vol = faces switch
        {
            4 => (Sqrt2 / 12.0) * a3,
            6 => a3,
            8 => (Sqrt2 / 3.0) * a3,
            12 => ((15.0 + (7.0 * Sqrt5)) / 4.0) * a3,
            20 => ((5.0 * (3.0 + Sqrt5)) / 12.0) * a3,
            _ => double.NaN,
        };

        return double.IsNaN(vol)
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(vol);
    }

    public static MathResult<decimal> NBallVol(decimal nDec, decimal r)
    {
        if (!DslMathValidation.TryReadInt32InRange(nDec, MinNBallDim, MaxNBallDim, out int n) || r <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double halfN = 0.5 * n;
        double logVol = (halfN * Math.Log(Math.PI)) - DslElementaryMath.LogGammaPositive(halfN + 1.0) + (n * Math.Log((double)r));
        return DslElementaryMath.NarrowDouble(Math.Exp(logVol));
    }

    public static MathResult<decimal> SphereArea(decimal r)
        => r <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(4.0 * Math.PI * (double)r * (double)r);

    public static MathResult<decimal> CylArea(decimal r, decimal h)
        => r <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(MathConstants.TwoPi * (double)r * ((double)r + (double)h));

    public static MathResult<decimal> TorusArea(decimal majorR, decimal minorR)
        => minorR <= 0m || majorR <= 0m || majorR < minorR
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(4.0 * Math.PI * Math.PI * (double)majorR * (double)minorR);

    public static MathResult<decimal> CuboidArea(decimal a, decimal b, decimal c)
    {
        if (a <= 0m || b <= 0m || c <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal area = checked(2m * checked(checked(a * b) + checked(b * c) + checked(c * a)));
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(area));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- Shared Solid Helpers ----

    private static double ComputeSegmentRevSliceDouble(double dr, double dh, double halfLen)
    {
        double l = Math.Min(halfLen, dr);
        double d = dr - dh;
        double radial = Math.Sqrt(Math.Max(0.0, (dr - l) * (dr + l)));
        double arc = (l * radial) + (dr * dr * Math.Asin(Math.Clamp(l / dr, -1.0, 1.0)));
        return MathConstants.TwoPi * ((((dr * dr) + (d * d)) * l) - ((l * l * l) / 3.0) - (d * arc));
    }
}
