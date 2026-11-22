using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation deterministic Euclidean, analytic and solid geometry mathematical kernels
/// for StockAnalyzer DSL v6.
/// </summary>
public static partial class DslGeometryMath
{
    private const int MinRegPolySides = 3;
    private const int MaxRegPolySides = 1_000_000;
    private const double Sqrt2 = 1.4142135623730950488;
    private const double Sqrt3 = 1.7320508075688772935;
    private const double TrapezoidBaseDiffEps = 1e-12;

    // ---- 4.5.1 Triangle Comprehensive Solvers ----

    public static MathErrorCode TriEquilateral(decimal side, scoped Span<decimal> result)
    {
        if (side <= 0m || result.Length < 5)
        {
            return MathErrorCode.DomainError;
        }

        double a = (double)side;
        double area = (Sqrt3 * 0.25) * a * a;
        double height = (Sqrt3 * 0.5) * a;
        double inradius = (Sqrt3 / 6.0) * a;
        double circumradius = (Sqrt3 / 3.0) * a;

        try
        {
            result[1] = DslElementaryMath.NormalizeZero(checked(3m * side));
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        return TryWriteDouble(result, 0, area) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 2, height) is var e2 && e2 != MathErrorCode.None ? e2 :
               TryWriteDouble(result, 3, inradius) is var e3 && e3 != MathErrorCode.None ? e3 :
               TryWriteDouble(result, 4, circumradius);
    }

    public static MathErrorCode TriRightIsosceles(decimal leg, scoped Span<decimal> result)
    {
        if (leg <= 0m || result.Length < 4)
        {
            return MathErrorCode.DomainError;
        }

        double a = (double)leg;
        double hyp = Sqrt2 * a;
        double area = 0.5 * a * a;
        double perim = (2.0 + Sqrt2) * a;
        double alt = 0.5 * hyp;

        return TryWriteDouble(result, 0, hyp) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, area) is var e1 && e1 != MathErrorCode.None ? e1 :
               TryWriteDouble(result, 2, perim) is var e2 && e2 != MathErrorCode.None ? e2 :
               TryWriteDouble(result, 3, alt);
    }

    public static MathErrorCode TriRight(decimal aDec, decimal bDec, scoped Span<decimal> result)
    {
        if (aDec <= 0m || bDec <= 0m || result.Length < 6)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> hypRes = DslElementaryMath.Hypot(aDec, bDec);
        if (!hypRes.IsOk)
        {
            return hypRes.Error;
        }

        double a = (double)aDec;
        double b = (double)bDec;
        double c = (double)hypRes.Value;
        double area = 0.5 * a * b;
        double degA = Math.Atan2(a, b) * MathConstants.RadToDeg;
        double degB = 90.0 - degA;
        double alt = (a * b) / c;

        try
        {
            result[2] = DslElementaryMath.NormalizeZero(checked(aDec + bDec + hypRes.Value));
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        result[0] = hypRes.Value;
        return TryWriteDouble(result, 1, area) is var e1 && e1 != MathErrorCode.None ? e1 :
               TryWriteDouble(result, 3, degA) is var e3 && e3 != MathErrorCode.None ? e3 :
               TryWriteDouble(result, 4, degB) is var e4 && e4 != MathErrorCode.None ? e4 :
               TryWriteDouble(result, 5, alt);
    }

    public static MathErrorCode TriIsosceles(decimal equalSide, decimal baseLen, scoped Span<decimal> result)
    {
        if (equalSide <= 0m || baseLen <= 0m || (baseLen / 2m) >= equalSide || result.Length < 5)
        {
            return MathErrorCode.DomainError;
        }

        double a = (double)equalSide;
        double b = (double)baseLen;
        double halfB = 0.5 * b;
        double h = Math.Sqrt(Math.Max(0.0, (a - halfB) * (a + halfB)));
        if (h <= 0.0)
        {
            return MathErrorCode.DomainError;
        }

        double area = halfB * h;
        double baseDeg = Math.Acos(Math.Clamp(halfB / a, -1.0, 1.0)) * MathConstants.RadToDeg;
        double apexDeg = 180.0 - (2.0 * baseDeg);

        try
        {
            result[1] = DslElementaryMath.NormalizeZero(checked((2m * equalSide) + baseLen));
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        return TryWriteDouble(result, 0, area) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 2, h) is var e2 && e2 != MathErrorCode.None ? e2 :
               TryWriteDouble(result, 3, apexDeg) is var e3 && e3 != MathErrorCode.None ? e3 :
               TryWriteDouble(result, 4, baseDeg);
    }

    public static MathErrorCode TriScalene(decimal a, decimal b, decimal c, scoped Span<decimal> result)
    {
        if (result.Length < 7)
        {
            return MathErrorCode.DomainError;
        }

        MathErrorCode err = DslElementaryMath.TriangleMetricsSss(a, b, c, out decimal area, out decimal inradius, out decimal circumradius);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        try
        {
            result[1] = DslElementaryMath.NormalizeZero(checked(a + b + c));
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        ComputeSssAnglesDeg((double)a, (double)b, (double)c, out double degA, out double degB, out double degC);
        result[0] = area;
        result[5] = inradius;
        result[6] = circumradius;

        return TryWriteDouble(result, 2, degA) is var e2 && e2 != MathErrorCode.None ? e2 :
               TryWriteDouble(result, 3, degB) is var e3 && e3 != MathErrorCode.None ? e3 :
               TryWriteDouble(result, 4, degC);
    }

    // ---- 4.5.2 2D Area & Inverse-Area Formulas ----

    public static MathResult<decimal> TriAreaEquilateral(decimal a)
        => a <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble((Sqrt3 * 0.25) * (double)a * (double)a);

    public static MathResult<decimal> TriAreaBh(decimal b, decimal h)
        => b <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : CheckedDecimalProductHalf(b, h);

    public static MathResult<decimal> TriAreaSas(decimal a, decimal b, decimal angleRad)
    {
        double theta = (double)angleRad;
        if (a <= 0m || b <= 0m || !double.IsFinite(theta) || theta <= 0.0 || theta >= Math.PI)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(0.5 * (double)a * (double)b * Math.Sin(theta));
    }

    public static MathResult<decimal> TriAreaAsa(decimal a, decimal betaRad, decimal gammaRad)
    {
        double beta = (double)betaRad;
        double gamma = (double)gammaRad;
        double sum = beta + gamma;
        if (a <= 0m ||
            !double.IsFinite(beta) ||
            !double.IsFinite(gamma) ||
            beta <= 0.0 ||
            gamma <= 0.0 ||
            sum >= Math.PI)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double denom = 2.0 * Math.Sin(sum);
        if (denom <= 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        return DslElementaryMath.NarrowDouble((da * da * Math.Sin(beta) * Math.Sin(gamma)) / denom);
    }

    public static MathResult<decimal> SquareArea(decimal a)
        => a <= 0m ? MathResult<decimal>.Fail(MathErrorCode.DomainError) : CheckedDecimalProduct(a, a);

    public static MathResult<decimal> RectArea(decimal a, decimal b)
        => a <= 0m || b <= 0m ? MathResult<decimal>.Fail(MathErrorCode.DomainError) : CheckedDecimalProduct(a, b);

    public static MathResult<decimal> TrapezoidArea(decimal a, decimal b, decimal h)
    {
        if (a <= 0m || b <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(0.5m * (a + b) * h)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathErrorCode Trapezoid4Sides(decimal a, decimal b, decimal c, decimal d, scoped Span<decimal> result)
    {
        if (a <= 0m || b <= 0m || c <= 0m || d <= 0m || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        decimal delta = Math.Abs(a - b);
        if ((double)delta <= TrapezoidBaseDiffEps)
        {
            return MathErrorCode.DomainError;
        }

        MathErrorCode triErr = DslElementaryMath.TriangleMetricsSss(delta, c, d, out decimal triArea, out _, out _);
        if (triErr != MathErrorCode.None)
        {
            return triErr;
        }

        double h = (2.0 * (double)triArea) / (double)delta;
        double area = 0.5 * ((double)a + (double)b) * h;

        return TryWriteDouble(result, 0, h) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, area);
    }

    public static MathErrorCode Trapezoid3SidesH(decimal a, decimal c, decimal d, decimal h, scoped Span<decimal> result)
    {
        if (a <= 0m || c <= 0m || d <= 0m || h <= 0m || c < h || d < h || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        double da = (double)a;
        double dc = (double)c;
        double dd = (double)d;
        double dh = (double)h;

        double p1 = Math.Sqrt(Math.Max(0.0, (dc - dh) * (dc + dh)));
        double p2 = Math.Sqrt(Math.Max(0.0, (dd - dh) * (dd + dh)));
        double base2 = da + p1 + p2;
        double area = 0.5 * (da + base2) * dh;

        return TryWriteDouble(result, 0, base2) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, area);
    }

    public static MathResult<decimal> RhombusArea(decimal d1, decimal d2)
        => d1 <= 0m || d2 <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : CheckedDecimalProductHalf(d1, d2);

    public static MathResult<decimal> ParallelogramAreaBh(decimal b, decimal h)
        => b <= 0m || h <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : CheckedDecimalProduct(b, h);

    public static MathResult<decimal> ParallelogramAreaSas(decimal a, decimal b, decimal angleRad)
    {
        double theta = (double)angleRad;
        if (a <= 0m || b <= 0m || !double.IsFinite(theta) || theta <= 0.0 || theta >= Math.PI)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble((double)a * (double)b * Math.Sin(theta));
    }

    public static MathResult<decimal> CyclicQuadArea(decimal a, decimal b, decimal c, decimal d)
    {
        if (!TryValidateQuadSides(a, b, c, d, out double s, out double da, out double db, out double dc, out double dd))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double prod = (s - da) * (s - db) * (s - dc) * (s - dd);
        if (!(prod > 0.0))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(Math.Sqrt(prod));
    }

    public static MathResult<decimal> QuadAreaBretschneider(
        decimal a,
        decimal b,
        decimal c,
        decimal d,
        decimal oppAngleSumRad)
    {
        double theta = (double)oppAngleSumRad;
        if (!TryValidateQuadSides(a, b, c, d, out double s, out double da, out double db, out double dc, out double dd) ||
            !double.IsFinite(theta) ||
            theta <= 0.0 ||
            theta >= MathConstants.TwoPi)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double cosHalf = Math.Cos(0.5 * theta);
        double rad = ((s - da) * (s - db) * (s - dc) * (s - dd)) - (da * db * dc * dd * cosHalf * cosHalf);
        if (!(rad > 0.0))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(Math.Sqrt(rad));
    }

    public static MathResult<decimal> RegPolyArea(decimal nDec, decimal a)
    {
        if (!TryReadRegPolySides(nDec, out int n) || a <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        return DslElementaryMath.NarrowDouble((n * da * da) / (4.0 * Math.Tan(Math.PI / n)));
    }

    public static MathResult<decimal> RegPolySideFromArea(decimal nDec, decimal area)
    {
        if (!TryReadRegPolySides(nDec, out int n) || area <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(Math.Sqrt((4.0 * (double)area * Math.Tan(Math.PI / n)) / n));
    }

    public static MathResult<decimal> CircleArea(decimal r)
        => r <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(Math.PI * (double)r * (double)r);

    public static MathResult<decimal> CircleRadiusFromArea(decimal area)
        => area <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(Math.Sqrt((double)area / Math.PI));

    public static MathResult<decimal> SectorArea(decimal r, decimal angleRad)
    {
        double theta = (double)angleRad;
        if (r <= 0m || !double.IsFinite(theta) || theta <= 0.0 || theta > MathConstants.TwoPi)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(0.5 * (double)r * (double)r * theta);
    }

    public static MathResult<decimal> SegmentAreaAngle(decimal r, decimal angleRad)
    {
        double theta = (double)angleRad;
        if (r <= 0m || !double.IsFinite(theta) || theta <= 0.0 || theta > MathConstants.TwoPi)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(0.5 * (double)r * (double)r * (theta - Math.Sin(theta)));
    }

    public static MathResult<decimal> SegmentAreaRh(decimal r, decimal h)
    {
        if (r <= 0m || h <= 0m || (h / 2m) > r)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(ComputeSegmentAreaRhDouble((double)r, (double)h));
    }

    public static MathResult<decimal> SegmentAreaCh(decimal chord, decimal h)
    {
        if (chord <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double c = (double)chord;
        double dh = (double)h;
        double r = (0.5 * dh) + ((c * c) / (8.0 * dh));
        if (!double.IsFinite(r) || r <= 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        return DslElementaryMath.NarrowDouble(ComputeSegmentAreaRhDouble(r, dh));
    }

    public static MathResult<decimal> EllipseArea(decimal a, decimal b)
        => a <= 0m || b <= 0m
            ? MathResult<decimal>.Fail(MathErrorCode.DomainError)
            : DslElementaryMath.NarrowDouble(Math.PI * (double)a * (double)b);

    public static MathResult<decimal> EllipseSectorArea(decimal a, decimal b, decimal theta1Rad, decimal theta2Rad)
    {
        if (a <= 0m ||
            b <= 0m ||
            Math.Abs(theta1Rad) > DslElementaryMath.RadianTrigRangeLimit ||
            Math.Abs(theta2Rad) > DslElementaryMath.RadianTrigRangeLimit ||
            theta2Rad <= theta1Rad ||
            (double)(theta2Rad - theta1Rad) > MathConstants.TwoPi)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        double e1 = ContinuousEccentricAnomaly(da, db, (double)theta1Rad);
        double e2 = ContinuousEccentricAnomaly(da, db, (double)theta2Rad);
        return DslElementaryMath.NarrowDouble(0.5 * da * db * (e2 - e1));
    }

    public static MathResult<decimal> EllipseSegmentArea(decimal a, decimal b, decimal h)
    {
        if (a <= 0m || b <= 0m || h <= 0m || (h / 2m) > a)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        double dh = (double)h;
        return DslElementaryMath.NarrowDouble((db / da) * ComputeSegmentAreaRhDouble(da, dh));
    }

    public static MathResult<decimal> ParabolaSegmentArea(decimal chord, decimal h)
    {
        if (chord <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked((2m * chord * h) / 3m)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> HyperbolaSegmentArea(decimal a, decimal b, decimal x)
    {
        if (a <= 0m || b <= 0m || x <= a)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        double u = (double)x / da;
        double area = da * db * ((u * Math.Sqrt(Math.Max(0.0, (u - 1.0) * (u + 1.0)))) - Math.Acosh(u));
        return DslElementaryMath.NarrowDouble(area);
    }

    public static MathResult<decimal> HyperbolaSectorArea(decimal a, decimal b, decimal x)
    {
        if (a <= 0m || b <= 0m || x <= a)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        double u = (double)x / da;
        return DslElementaryMath.NarrowDouble(0.5 * da * db * Math.Acosh(u));
    }

    // ---- 4.7 Inscribed & Circumscribed Figures ----

    public static MathResult<decimal> RegPolyInradius(decimal nDec, decimal a)
    {
        if (!TryReadRegPolySides(nDec, out int n) || a <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble((double)a / (2.0 * Math.Tan(Math.PI / n)));
    }

    public static MathResult<decimal> RegPolyCircumradius(decimal nDec, decimal a)
    {
        if (!TryReadRegPolySides(nDec, out int n) || a <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble((double)a / (2.0 * Math.Sin(Math.PI / n)));
    }

    public static MathErrorCode CircleInscribedRegPoly(decimal nDec, decimal radius, scoped Span<decimal> result)
    {
        if (!TryReadRegPolySides(nDec, out int n) || radius <= 0m || result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        double r = (double)radius;
        double side = 2.0 * r * Math.Sin(Math.PI / n);
        double perimeter = n * side;
        double area = 0.5 * n * r * r * Math.Sin(MathConstants.TwoPi / n);

        return TryWriteDouble(result, 0, side) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, perimeter) is var e1 && e1 != MathErrorCode.None ? e1 :
               TryWriteDouble(result, 2, area);
    }

    public static MathErrorCode CircleCircumscribedRegPoly(decimal nDec, decimal inradius, scoped Span<decimal> result)
    {
        if (!TryReadRegPolySides(nDec, out int n) || inradius <= 0m || result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        double r = (double)inradius;
        double tanPiN = Math.Tan(Math.PI / n);
        double side = 2.0 * r * tanPiN;
        double perimeter = n * side;
        double area = n * r * r * tanPiN;

        return TryWriteDouble(result, 0, side) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, perimeter) is var e1 && e1 != MathErrorCode.None ? e1 :
               TryWriteDouble(result, 2, area);
    }

    public static MathResult<decimal> RectCircumradius(decimal a, decimal b)
    {
        if (a <= 0m || b <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        MathResult<decimal> hyp = DslElementaryMath.Hypot(a, b);
        return hyp.IsOk
            ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(hyp.Value / 2m))
            : hyp;
    }

    // ---- 4.10 Right-Triangle Inverse Solvers & Law of Cosines ----

    public static MathErrorCode RtTriFromAngleBaseDeg(decimal angleDeg, decimal baseLen, scoped Span<decimal> result)
    {
        if (angleDeg <= 0m || angleDeg >= 90m || baseLen <= 0m || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> cosRes = DslElementaryMath.CosDeg(angleDeg);
        MathResult<decimal> tanRes = DslElementaryMath.TanDeg(angleDeg);
        if (!cosRes.IsOk)
        {
            return cosRes.Error;
        }

        if (!tanRes.IsOk)
        {
            return tanRes.Error;
        }

        MathResult<decimal> hypRes = SafeDecimalDivide(baseLen, cosRes.Value, (double)baseLen / Math.Cos((double)angleDeg * MathConstants.DegToRad));
        if (!hypRes.IsOk)
        {
            return hypRes.Error;
        }

        MathResult<decimal> heightRes = SafeDecimalMultiply(baseLen, tanRes.Value, (double)baseLen * Math.Tan((double)angleDeg * MathConstants.DegToRad));
        if (!heightRes.IsOk)
        {
            return heightRes.Error;
        }

        result[0] = hypRes.Value;
        result[1] = heightRes.Value;
        return MathErrorCode.None;
    }

    public static MathErrorCode RtTriFromAngleHeightDeg(decimal angleDeg, decimal height, scoped Span<decimal> result)
    {
        if (angleDeg <= 0m || angleDeg >= 90m || height <= 0m || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> tanRes = DslElementaryMath.TanDeg(angleDeg);
        MathResult<decimal> sinRes = DslElementaryMath.SinDeg(angleDeg);
        if (!tanRes.IsOk)
        {
            return tanRes.Error;
        }

        if (!sinRes.IsOk)
        {
            return sinRes.Error;
        }

        MathResult<decimal> baseRes = SafeDecimalDivide(height, tanRes.Value, (double)height / Math.Tan((double)angleDeg * MathConstants.DegToRad));
        if (!baseRes.IsOk)
        {
            return baseRes.Error;
        }

        MathResult<decimal> hypRes = SafeDecimalDivide(height, sinRes.Value, (double)height / Math.Sin((double)angleDeg * MathConstants.DegToRad));
        if (!hypRes.IsOk)
        {
            return hypRes.Error;
        }

        result[0] = baseRes.Value;
        result[1] = hypRes.Value;
        return MathErrorCode.None;
    }

    public static MathErrorCode RtTriFromAngleHypDeg(decimal angleDeg, decimal hyp, scoped Span<decimal> result)
    {
        if (angleDeg <= 0m || angleDeg >= 90m || hyp <= 0m || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> cosRes = DslElementaryMath.CosDeg(angleDeg);
        MathResult<decimal> sinRes = DslElementaryMath.SinDeg(angleDeg);
        if (!cosRes.IsOk)
        {
            return cosRes.Error;
        }

        if (!sinRes.IsOk)
        {
            return sinRes.Error;
        }

        MathResult<decimal> baseRes = SafeDecimalMultiply(hyp, cosRes.Value, (double)hyp * Math.Cos((double)angleDeg * MathConstants.DegToRad));
        if (!baseRes.IsOk)
        {
            return baseRes.Error;
        }

        MathResult<decimal> heightRes = SafeDecimalMultiply(hyp, sinRes.Value, (double)hyp * Math.Sin((double)angleDeg * MathConstants.DegToRad));
        if (!heightRes.IsOk)
        {
            return heightRes.Error;
        }

        result[0] = baseRes.Value;
        result[1] = heightRes.Value;
        return MathErrorCode.None;
    }

    public static MathErrorCode RtTriFromBaseHeightDeg(decimal baseLen, decimal height, scoped Span<decimal> result)
    {
        if (baseLen <= 0m || height <= 0m || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> angleRes = DslElementaryMath.Atan2Deg(height, baseLen);
        if (!angleRes.IsOk)
        {
            return angleRes.Error;
        }

        MathResult<decimal> hypRes = DslElementaryMath.Hypot(baseLen, height);
        if (!hypRes.IsOk)
        {
            return hypRes.Error;
        }

        result[0] = angleRes.Value;
        result[1] = hypRes.Value;
        return MathErrorCode.None;
    }

    public static MathErrorCode RtTriFromBaseHypDeg(decimal baseLen, decimal hyp, scoped Span<decimal> result)
    {
        if (baseLen <= 0m || hyp <= baseLen || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> angleRes = DslElementaryMath.AcosDeg(baseLen / hyp);
        if (!angleRes.IsOk)
        {
            return angleRes.Error;
        }

        double b = (double)baseLen;
        double c = (double)hyp;
        double h = Math.Sqrt(Math.Max(0.0, (c - b) * (c + b)));
        result[0] = angleRes.Value;
        return TryWriteDouble(result, 1, h);
    }

    public static MathErrorCode RtTriFromHeightHypDeg(decimal height, decimal hyp, scoped Span<decimal> result)
    {
        if (height <= 0m || hyp <= height || result.Length < 2)
        {
            return MathErrorCode.DomainError;
        }

        MathResult<decimal> angleRes = DslElementaryMath.AsinDeg(height / hyp);
        if (!angleRes.IsOk)
        {
            return angleRes.Error;
        }

        double h = (double)height;
        double c = (double)hyp;
        double b = Math.Sqrt(Math.Max(0.0, (c - h) * (c + h)));
        result[0] = angleRes.Value;
        return TryWriteDouble(result, 1, b);
    }

    public static MathErrorCode TriAnglesSssDeg(decimal a, decimal b, decimal c, scoped Span<decimal> result)
    {
        if (result.Length < 3)
        {
            return MathErrorCode.DomainError;
        }

        MathErrorCode err = DslElementaryMath.TriangleMetricsSss(a, b, c, out _, out _, out _);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        ComputeSssAnglesDeg((double)a, (double)b, (double)c, out double degA, out double degB, out double degC);
        return TryWriteDouble(result, 0, degA) is var e0 && e0 != MathErrorCode.None ? e0 :
               TryWriteDouble(result, 1, degB) is var e1 && e1 != MathErrorCode.None ? e1 :
               TryWriteDouble(result, 2, degC);
    }

    // ---- Shared Helpers ----

    private static MathErrorCode TryWriteDouble(scoped Span<decimal> dst, int index, double value)
    {
        MathResult<decimal> res = DslElementaryMath.NarrowDouble(value);
        if (!res.IsOk)
        {
            return res.Error;
        }

        dst[index] = res.Value;
        return MathErrorCode.None;
    }

    private static MathResult<decimal> CheckedDecimalProduct(decimal x, decimal y)
    {
        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(x * y)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    private static MathResult<decimal> CheckedDecimalProductHalf(decimal x, decimal y)
    {
        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(0.5m * x * y)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    private static bool TryReadRegPolySides(decimal nDec, out int n)
        => DslMathValidation.TryReadInt32InRange(nDec, MinRegPolySides, MaxRegPolySides, out n);

    private static bool TryValidateQuadSides(
        decimal a,
        decimal b,
        decimal c,
        decimal d,
        out double s,
        out double da,
        out double db,
        out double dc,
        out double dd)
    {
        s = 0.0;
        da = (double)a;
        db = (double)b;
        dc = (double)c;
        dd = (double)d;

        if (a <= 0m || b <= 0m || c <= 0m || d <= 0m)
        {
            return false;
        }

        s = 0.5 * (da + db + dc + dd);
        double maxSide = Math.Max(Math.Max(da, db), Math.Max(dc, dd));
        return double.IsFinite(s) && maxSide < s;
    }

    private static double ComputeSegmentAreaRhDouble(double r, double h)
    {
        double cosTheta = Math.Clamp(1.0 - (h / r), -1.0, 1.0);
        double term1 = r * r * Math.Acos(cosTheta);
        double term2 = (r - h) * Math.Sqrt(Math.Max(0.0, ((2.0 * r) - h) * h));
        return term1 - term2;
    }

    private static double ContinuousEccentricAnomaly(double a, double b, double theta)
    {
        double sinT = Math.Sin(theta);
        double cosT = Math.Cos(theta);
        return theta + Math.Atan2((a - b) * sinT * cosT, (b * cosT * cosT) + (a * sinT * sinT));
    }

    private static void ComputeSssAnglesDeg(
        double da,
        double db,
        double dc,
        out double degA,
        out double degB,
        out double degC)
    {
        double cosA = Math.Clamp(((db * db) + (dc * dc) - (da * da)) / (2.0 * db * dc), -1.0, 1.0);
        double cosB = Math.Clamp(((da * da) + (dc * dc) - (db * db)) / (2.0 * da * dc), -1.0, 1.0);
        degA = Math.Acos(cosA) * MathConstants.RadToDeg;
        degB = Math.Acos(cosB) * MathConstants.RadToDeg;
        degC = 180.0 - degA - degB;
    }

    private static MathResult<decimal> SafeDecimalDivide(decimal num, decimal den, double fallbackDouble)
    {
        if (den == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(num / den)));
        }
        catch (OverflowException)
        {
            return DslElementaryMath.NarrowDouble(fallbackDouble);
        }
    }

    private static MathResult<decimal> SafeDecimalMultiply(decimal x, decimal y, double fallbackDouble)
    {
        try
        {
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(x * y)));
        }
        catch (OverflowException)
        {
            return DslElementaryMath.NarrowDouble(fallbackDouble);
        }
    }
}
