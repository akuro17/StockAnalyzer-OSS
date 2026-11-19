using System;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslSpecialFunctionsMath
{
    private const int EllipticMaxIterations = 64;
    private const int JacobiAgmMaxSteps = 32;
    private const int HypergeometricMaxTerms = 256;

    // ---- 12. Elliptic Integrals (Carlson RF, RD, RJ, RC) ----

    public static MathResult<decimal> EllipK(decimal k)
    {
        if (Math.Abs(k) >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dk = (double)k;
        double val = CarlsonRF(0.0, 1.0 - dk * dk, 1.0);
        return double.IsNaN(val)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> EllipE(decimal k)
    {
        if (Math.Abs(k) > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (Math.Abs(k) == 1m)
        {
            return MathResult<decimal>.Ok(1m);
        }

        double dk = (double)k;
        double m = dk * dk;
        double y = 1.0 - m;
        double val = CarlsonRF(0.0, y, 1.0) - (m / 3.0) * CarlsonRD(0.0, y, 1.0);
        return double.IsNaN(val)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> EllipF(decimal phi, decimal k)
    {
        if (Math.Abs(k) > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dPhi = (double)phi;
        double dk = (double)k;
        double mPeriods = Math.Round(dPhi / Math.PI);
        double phi0 = dPhi - mPeriods * Math.PI;
        double sinPhi = Math.Sin(phi0);
        double cosPhi = Math.Cos(phi0);
        double sin2 = sinPhi * sinPhi;
        double cos2 = cosPhi * cosPhi;
        double k2 = dk * dk;
        double y = 1.0 - k2 * sin2;

        if (Math.Abs(k) == 1m && (Math.Abs(cosPhi) < 1e-15 || mPeriods != 0.0))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double baseVal = sinPhi * CarlsonRF(cos2, y, 1.0);
        if (mPeriods != 0.0)
        {
            baseVal += 2.0 * mPeriods * CarlsonRF(0.0, 1.0 - k2, 1.0);
        }

        return double.IsNaN(baseVal)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(baseVal);
    }

    public static MathResult<decimal> EllipEInc(decimal phi, decimal k)
    {
        if (Math.Abs(k) > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dPhi = (double)phi;
        double dk = (double)k;
        double mPeriods = Math.Round(dPhi / Math.PI);
        double phi0 = dPhi - mPeriods * Math.PI;
        double sinPhi = Math.Sin(phi0);
        double cosPhi = Math.Cos(phi0);
        double sin2 = sinPhi * sinPhi;
        double cos2 = cosPhi * cosPhi;
        double k2 = dk * dk;
        double y = 1.0 - k2 * sin2;

        double baseVal = sinPhi * CarlsonRF(cos2, y, 1.0)
            - (k2 / 3.0) * sinPhi * sin2 * CarlsonRD(cos2, y, 1.0);
        if (mPeriods != 0.0)
        {
            double completeE = Math.Abs(k) == 1m
                ? 1.0
                : CarlsonRF(0.0, 1.0 - k2, 1.0) - (k2 / 3.0) * CarlsonRD(0.0, 1.0 - k2, 1.0);
            baseVal += 2.0 * mPeriods * completeE;
        }

        return double.IsNaN(baseVal)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(baseVal);
    }

    public static MathResult<decimal> EllipPi(decimal n, decimal phi, decimal k)
    {
        if (Math.Abs(k) > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dn = (double)n;
        double dPhi = (double)phi;
        double dk = (double)k;
        double mPeriods = Math.Round(dPhi / Math.PI);
        double phi0 = dPhi - mPeriods * Math.PI;
        double sinPhi = Math.Sin(phi0);
        double cosPhi = Math.Cos(phi0);
        double sin2 = sinPhi * sinPhi;
        double cos2 = cosPhi * cosPhi;
        double k2 = dk * dk;
        double y = 1.0 - k2 * sin2;
        double p = 1.0 - dn * sin2;
        if (p <= 0.0 || (mPeriods != 0.0 && (dn >= 1.0 || Math.Abs(k) == 1m)))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double baseVal = sinPhi * CarlsonRF(cos2, y, 1.0)
            + (dn / 3.0) * sinPhi * sin2 * CarlsonRJ(cos2, y, 1.0, p);
        if (mPeriods != 0.0)
        {
            double completePi = CarlsonRF(0.0, 1.0 - k2, 1.0)
                + (dn / 3.0) * CarlsonRJ(0.0, 1.0 - k2, 1.0, 1.0 - dn);
            baseVal += 2.0 * mPeriods * completePi;
        }

        return double.IsNaN(baseVal)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(baseVal);
    }

    private static double CarlsonRF(double x, double y, double z)
    {
        for (int i = 0; i < EllipticMaxIterations; i++)
        {
            double sx = Math.Sqrt(x);
            double sy = Math.Sqrt(y);
            double sz = Math.Sqrt(z);
            double lambda = sx * (sy + sz) + sy * sz;
            x = 0.25 * (x + lambda);
            y = 0.25 * (y + lambda);
            z = 0.25 * (z + lambda);
            double mean = (x + y + z) / 3.0;
            if (mean == 0.0) return double.NaN;
            double dx = (mean - x) / mean;
            double dy = (mean - y) / mean;
            double dz = (mean - z) / mean;
            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) < 0.0025)
            {
                double e2 = dx * dy - dz * dz;
                double e3 = dx * dy * dz;
                return (1.0 + (e2 / 24.0 - 0.1 - 3.0 * e3 / 44.0) * e2 + e3 / 14.0) / Math.Sqrt(mean);
            }
        }

        return double.NaN;
    }

    private static double CarlsonRD(double x, double y, double z)
    {
        double sum = 0.0;
        double fac = 1.0;
        for (int i = 0; i < EllipticMaxIterations; i++)
        {
            double sx = Math.Sqrt(x);
            double sy = Math.Sqrt(y);
            double sz = Math.Sqrt(z);
            double lambda = sx * (sy + sz) + sy * sz;
            sum += fac / (sz * (z + lambda));
            fac *= 0.25;
            x = 0.25 * (x + lambda);
            y = 0.25 * (y + lambda);
            z = 0.25 * (z + lambda);
            double mean = 0.2 * (x + y + 3.0 * z);
            if (mean == 0.0) return double.NaN;
            double dx = (mean - x) / mean;
            double dy = (mean - y) / mean;
            double dz = (mean - z) / mean;
            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) < 0.0015)
            {
                double ea = dx * dy;
                double eb = dz * dz;
                double ec = ea - eb;
                double ed = ea - 6.0 * eb;
                double ee = ed + 2.0 * ec;
                double poly = 1.0
                    + ed * (-3.0 / 14.0 + (9.0 / 88.0) * ed - (9.0 / 52.0) * dz * ee)
                    + dz * (ec / 6.0 + dz * (-9.0 * ee / 22.0 + (3.0 / 26.0) * dz * ea));
                return 3.0 * sum + fac * poly / (mean * Math.Sqrt(mean));
            }
        }

        return double.NaN;
    }

    private static double CarlsonRC(double x, double y)
    {
        double w;
        if (y > 0.0)
        {
            w = 1.0;
        }
        else
        {
            double x0 = x;
            x = x - y;
            y = -y;
            w = Math.Sqrt(x0) / Math.Sqrt(x);
        }

        for (int i = 0; i < EllipticMaxIterations; i++)
        {
            double lambda = 2.0 * Math.Sqrt(x) * Math.Sqrt(y) + y;
            x = 0.25 * (x + lambda);
            y = 0.25 * (y + lambda);
            double mean = (x + 2.0 * y) / 3.0;
            double s = (y - mean) / mean;
            if (Math.Abs(s) < 0.0012)
            {
                return w * (1.0 + s * s * (0.3 + s * (1.0 / 7.0 + s * (0.375 + s * (9.0 / 22.0))))) / Math.Sqrt(mean);
            }
        }

        return double.NaN;
    }

    private static double CarlsonRJ(double x, double y, double z, double p)
    {
        double sum = 0.0;
        double fac = 1.0;
        for (int i = 0; i < EllipticMaxIterations; i++)
        {
            double sx = Math.Sqrt(x);
            double sy = Math.Sqrt(y);
            double sz = Math.Sqrt(z);
            double lambda = sx * (sy + sz) + sy * sz;
            double alpha = p * (sx + sy + sz) + sx * sy * sz;
            alpha *= alpha;
            double beta = p * (p + lambda) * (p + lambda);
            sum += fac * CarlsonRC(alpha, beta);
            fac *= 0.25;
            x = 0.25 * (x + lambda);
            y = 0.25 * (y + lambda);
            z = 0.25 * (z + lambda);
            p = 0.25 * (p + lambda);
            double mean = 0.2 * (x + y + z + 2.0 * p);
            double dx = (mean - x) / mean;
            double dy = (mean - y) / mean;
            double dz = (mean - z) / mean;
            double dp = (mean - p) / mean;
            if (Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Max(Math.Abs(dz), Math.Abs(dp))) < 0.0015)
            {
                double ea = dx * (dy + dz) + dy * dz;
                double eb = dx * dy * dz;
                double ec = dp * dp;
                double ed = ea - 3.0 * ec;
                double ee = eb + 2.0 * dp * (ea - ec);
                double poly = 1.0
                    + ed * (-3.0 / 14.0 + (9.0 / 88.0) * ed - (9.0 / 52.0) * ee)
                    + eb * (1.0 / 6.0 + dp * (-6.0 / 11.0 + (3.0 / 13.0) * dp))
                    + dp * ((ea - ec) / 3.0 - dp * (ea - 3.0 * ec) / 11.0);
                return 3.0 * sum + fac * poly / (mean * Math.Sqrt(mean));
            }
        }

        return double.NaN;
    }

    // ---- 13. Jacobi Elliptic Functions (sn, cn, dn) ----

    public static MathResult<decimal> JacobiSn(decimal u, decimal m)
    {
        if (!TryEvaluateJacobi(u, m, out double sn, out _, out _))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(sn);
    }

    public static MathResult<decimal> JacobiCn(decimal u, decimal m)
    {
        if (!TryEvaluateJacobi(u, m, out _, out double cn, out _))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(cn);
    }

    public static MathResult<decimal> JacobiDn(decimal u, decimal m)
    {
        if (!TryEvaluateJacobi(u, m, out _, out _, out double dn))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(dn);
    }

    private static bool TryEvaluateJacobi(decimal u, decimal m, out double sn, out double cn, out double dn)
    {
        sn = 0.0;
        cn = 0.0;
        dn = 0.0;
        if (m < 0m || m > 1m || Math.Abs(u) > 10_000m)
        {
            return false;
        }

        double du = (double)u;
        if (m == 0m)
        {
            sn = Math.Sin(du);
            cn = Math.Cos(du);
            dn = 1.0;
            return true;
        }

        if (m == 1m)
        {
            sn = Math.Tanh(du);
            double sech = 1.0 / Math.Cosh(du);
            cn = sech;
            dn = sech;
            return true;
        }

        // AGM + descending Landen transformation (Abramowitz & Stegun 16.4)
        Span<double> a = stackalloc double[JacobiAgmMaxSteps];
        Span<double> c = stackalloc double[JacobiAgmMaxSteps];
        a[0] = 1.0;
        double b = Math.Sqrt(1.0 - (double)m);
        c[0] = Math.Sqrt((double)m);
        int n = 0;
        while (Math.Abs(c[n]) > Eps && n + 1 < JacobiAgmMaxSteps)
        {
            double nextA = 0.5 * (a[n] + b);
            double nextB = Math.Sqrt(a[n] * b);
            c[n + 1] = 0.5 * (a[n] - b);
            a[n + 1] = nextA;
            b = nextB;
            n++;
        }

        double phi = (1 << n) * a[n] * du;
        for (int k = n; k >= 1; k--)
        {
            phi = 0.5 * (phi + Math.Asin(Math.Clamp((c[k] / a[k]) * Math.Sin(phi), -1.0, 1.0)));
        }

        sn = Math.Sin(phi);
        cn = Math.Cos(phi);
        dn = Math.Sqrt(Math.Max(0.0, 1.0 - (double)m * sn * sn));
        return true;
    }

    // ---- 14. Orthogonal Polynomials ----

    public static MathResult<decimal> ChebyshevT(decimal n, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        if (ni == 0) return MathResult<decimal>.Ok(1m);
        if (ni == 1) return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(x));

        double dx = (double)x;
        double t0 = 1.0;
        double t1 = dx;
        for (int k = 2; k <= ni; k++)
        {
            double tk = 2.0 * dx * t1 - t0;
            if (!double.IsFinite(tk)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            t0 = t1;
            t1 = tk;
        }

        return DslElementaryMath.NarrowDouble(t1);
    }

    public static MathResult<decimal> ChebyshevU(decimal n, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        if (ni == 0) return MathResult<decimal>.Ok(1m);
        if (ni == 1) return DslElementaryMath.NarrowDouble(2.0 * (double)x);

        double dx = (double)x;
        double u0 = 1.0;
        double u1 = 2.0 * dx;
        for (int k = 2; k <= ni; k++)
        {
            double uk = 2.0 * dx * u1 - u0;
            if (!double.IsFinite(uk)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            u0 = u1;
            u1 = uk;
        }

        return DslElementaryMath.NarrowDouble(u1);
    }

    public static MathResult<decimal> HermiteH(decimal n, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        if (ni == 0) return MathResult<decimal>.Ok(1m);
        if (ni == 1) return DslElementaryMath.NarrowDouble(2.0 * (double)x);

        double dx = (double)x;
        double h0 = 1.0;
        double h1 = 2.0 * dx;
        for (int k = 2; k <= ni; k++)
        {
            double hk = 2.0 * dx * h1 - 2.0 * (k - 1) * h0;
            if (!double.IsFinite(hk)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            h0 = h1;
            h1 = hk;
        }

        return DslElementaryMath.NarrowDouble(h1);
    }

    public static MathResult<decimal> LaguerreL(decimal n, decimal alpha, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m || alpha <= -1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        if (ni == 0) return MathResult<decimal>.Ok(1m);
        double da = (double)alpha;
        double dx = (double)x;
        double l0 = 1.0;
        double l1 = 1.0 + da - dx;
        if (ni == 1) return DslElementaryMath.NarrowDouble(l1);

        for (int k = 2; k <= ni; k++)
        {
            double lk = ((2.0 * k - 1.0 + da - dx) * l1 - (k - 1.0 + da) * l0) / k;
            if (!double.IsFinite(lk)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            l0 = l1;
            l1 = lk;
        }

        return DslElementaryMath.NarrowDouble(l1);
    }

    public static MathResult<decimal> JacobiP(decimal n, decimal alpha, decimal beta, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m || alpha <= -1m || beta <= -1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        if (ni == 0) return MathResult<decimal>.Ok(1m);
        double da = (double)alpha;
        double db = (double)beta;
        double dx = (double)x;
        double p0 = 1.0;
        double p1 = 0.5 * (da - db + (da + db + 2.0) * dx);
        if (ni == 1) return DslElementaryMath.NarrowDouble(p1);

        for (int k = 2; k <= ni; k++)
        {
            double c1 = 2.0 * k * (k + da + db) * (2.0 * k + da + db - 2.0);
            double c2 = (2.0 * k + da + db - 1.0) * (da * da - db * db);
            double c3 = (2.0 * k + da + db - 2.0) * (2.0 * k + da + db - 1.0) * (2.0 * k + da + db);
            double c4 = 2.0 * (k + da - 1.0) * (k + db - 1.0) * (2.0 * k + da + db);
            double pk = ((c2 + c3 * dx) * p1 - c4 * p0) / c1;
            if (!double.IsFinite(pk)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            p0 = p1;
            p1 = pk;
        }

        return DslElementaryMath.NarrowDouble(p1);
    }

    // ---- 15. Hypergeometric Functions ----

    public static MathResult<decimal> Hyp0F1(decimal b, decimal z)
    {
        if (b <= 0m && b == decimal.Truncate(b))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (z == 0m) return MathResult<decimal>.Ok(1m);

        double db = (double)b;
        double dz = (double)z;
        double term = 1.0;
        double sum = 1.0;
        for (int k = 0; k < HypergeometricMaxTerms; k++)
        {
            term *= dz / ((db + k) * (k + 1.0));
            if (!double.IsFinite(term)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            sum += term;
            if (!double.IsFinite(sum)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            if (Math.Abs(term) <= Math.Abs(sum) * Eps)
            {
                return DslElementaryMath.NarrowDouble(sum);
            }
        }

        return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
    }

    public static MathResult<decimal> Hyp1F1(decimal a, decimal b, decimal z)
    {
        if (b <= 0m && b == decimal.Truncate(b))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (z == 0m || a == 0m) return MathResult<decimal>.Ok(1m);

        double da = (double)a;
        double db = (double)b;
        double dz = (double)z;

        // Kummer transformation for negative z prevents alternating cancellation.
        double scale = 1.0;
        if (dz < 0.0)
        {
            scale = Math.Exp(dz);
            da = db - da;
            dz = -dz;
        }

        double term = 1.0;
        double sum = 1.0;
        for (int k = 0; k < HypergeometricMaxTerms; k++)
        {
            term *= ((da + k) * dz) / ((db + k) * (k + 1.0));
            if (!double.IsFinite(term)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            sum += term;
            if (!double.IsFinite(sum)) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            if (Math.Abs(term) <= Math.Abs(sum) * Eps)
            {
                return DslElementaryMath.NarrowDouble(scale * sum);
            }
        }

        return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
    }

    public static MathResult<decimal> Hyp2F1(decimal a, decimal b, decimal c, decimal z)
    {
        if ((c <= 0m && c == decimal.Truncate(c)) || z > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (z == 0m || a == 0m || b == 0m)
        {
            return MathResult<decimal>.Ok(1m);
        }

        // Terminating polynomial when a or b is a non-positive integer (valid even at z = 1 when c - a - b <= 0).
        if ((a < 0m && a == decimal.Truncate(a) && a >= -HypergeometricMaxTerms) ||
            (b < 0m && b == decimal.Truncate(b) && b >= -HypergeometricMaxTerms))
        {
            return TryHyp2F1Series((double)a, (double)b, (double)c, (double)z, out double polySum)
                ? DslElementaryMath.NarrowDouble(polySum)
                : MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
        }

        if (z == 1m && c - a - b <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (z == 1m)
        {
            double da1 = (double)a;
            double db1 = (double)b;
            double dc1 = (double)c;
            double cab = dc1 - da1 - db1;
            double cma = dc1 - da1;
            double cmb = dc1 - db1;
            if ((cma <= 0.0 && cma == Math.Truncate(cma)) || (cmb <= 0.0 && cmb == Math.Truncate(cmb)))
            {
                return MathResult<decimal>.Ok(0m);
            }

            double lgC = LogAbsGammaDouble(dc1, out int sC);
            double lgCab = LogAbsGammaDouble(cab, out int sCab);
            double lgCma = LogAbsGammaDouble(cma, out int sCma);
            double lgCmb = LogAbsGammaDouble(cmb, out int sCmb);
            double val1 = (sC * sCab * sCma * sCmb) * Math.Exp(lgC + lgCab - lgCma - lgCmb);
            return DslElementaryMath.NarrowDouble(val1);
        }

        double da = (double)a;
        double db = (double)b;
        double dc = (double)c;
        double dz = (double)z;

        // Pfaff transformation for z < -0.5 maps into (0, 1); swap so |a| <= |b| for milder factor growth.
        double pfaffFactor = 1.0;
        if (dz < -0.5)
        {
            if (Math.Abs(da) > Math.Abs(db))
            {
                (da, db) = (db, da);
            }

            pfaffFactor = Math.Pow(1.0 - dz, -da);
            db = dc - db;
            dz = dz / (dz - 1.0);
        }

        if (dz <= 0.5)
        {
            return TryHyp2F1Series(da, db, dc, dz, out double seriesSum)
                ? DslElementaryMath.NarrowDouble(pfaffFactor * seriesSum)
                : MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
        }

        // For 0.5 < dz < 1, transform to w = 1 - dz in (0, 0.5) when c - a - b is not an integer.
        double s = dc - da - db;
        if (Math.Abs(s - Math.Round(s)) > 1e-9)
        {
            double w = 1.0 - dz;
            if (!TryHyp2F1Series(da, db, 1.0 - s, w, out double f1)
                || !TryHyp2F1Series(dc - da, dc - db, 1.0 + s, w, out double f2))
            {
                return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
            }

            double lgC = LogAbsGammaDouble(dc, out int sC);
            double lgS = LogAbsGammaDouble(s, out int sS);
            double lgMs = LogAbsGammaDouble(-s, out int sMs);
            double lgA = LogAbsGammaDouble(da, out int sA);
            double lgB = LogAbsGammaDouble(db, out int sB);
            double lgCa = LogAbsGammaDouble(dc - da, out int sCa);
            double lgCb = LogAbsGammaDouble(dc - db, out int sCb);

            double term1 = (sC * sS * sCa * sCb) * Math.Exp(lgC + lgS - lgCa - lgCb) * f1;
            double term2 = (sC * sMs * sA * sB) * Math.Exp(lgC + lgMs - lgA - lgB) * Math.Pow(w, s) * f2;
            return DslElementaryMath.NarrowDouble(pfaffFactor * (term1 + term2));
        }

        return TryHyp2F1Series(da, db, dc, dz, out double directSum)
            ? DslElementaryMath.NarrowDouble(pfaffFactor * directSum)
            : MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
    }

    private static bool TryHyp2F1Series(double a, double b, double c, double z, out double sum)
    {
        double term = 1.0;
        sum = 1.0;
        for (int k = 0; k < HypergeometricMaxTerms; k++)
        {
            term *= ((a + k) * (b + k) * z) / ((c + k) * (k + 1.0));
            if (!double.IsFinite(term)) return false;
            sum += term;
            if (!double.IsFinite(sum)) return false;
            if (Math.Abs(term) <= Math.Abs(sum) * Eps)
            {
                return true;
            }
        }

        return false;
    }
}
