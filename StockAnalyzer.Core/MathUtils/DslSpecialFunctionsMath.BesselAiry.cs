using System;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslSpecialFunctionsMath
{
    private const decimal BesselOrderLimit = 100m;
    private const decimal BesselArgLimit = 100_000m;
    private const decimal StruveOrderLimit = 50m;
    private const decimal StruveArgLimit = 10_000m;
    private const decimal KelvinArgLimit = 10_000m;
    private const decimal AiryMinArg = -10_000m;

    // ---- 7. Cylindrical Bessel Functions ----

    public static MathResult<decimal> BesselJ(decimal nu, decimal x)
    {
        if (Math.Abs(nu) > BesselOrderLimit || Math.Abs(x) > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        bool isInt = nu == decimal.Truncate(nu);
        if (x < 0m && !isInt)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            if (nu == 0m) return MathResult<decimal>.Ok(1m);
            if (nu > 0m || isInt) return MathResult<decimal>.Ok(0m);
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dnu = (double)nu;
        double ax = (double)Math.Abs(x);
        BesselJyWithSign(dnu, ax, isInt, out double j, out _);
        if (x < 0m)
        {
            int n = (int)Math.Abs(nu);
            if ((n & 1) != 0) j = -j;
        }

        return DslElementaryMath.NarrowDouble(j);
    }

    public static MathResult<decimal> BesselY(decimal nu, decimal x)
    {
        if (Math.Abs(nu) > BesselOrderLimit || x <= 0m || x > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        bool isInt = nu == decimal.Truncate(nu);
        BesselJyWithSign((double)nu, (double)x, isInt, out _, out double y);
        return DslElementaryMath.NarrowDouble(y);
    }

    public static MathResult<decimal> BesselI(decimal nu, decimal x)
        => EvaluateBesselI(nu, x, exponentiallyScaled: false);

    public static MathResult<decimal> BesselIe(decimal nu, decimal x)
        => EvaluateBesselI(nu, x, exponentiallyScaled: true);

    private static MathResult<decimal> EvaluateBesselI(decimal nu, decimal x, bool exponentiallyScaled)
    {
        if (Math.Abs(nu) > BesselOrderLimit || Math.Abs(x) > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        bool isInt = nu == decimal.Truncate(nu);
        if (x < 0m && !isInt)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            if (nu == 0m) return MathResult<decimal>.Ok(1m);
            if (nu > 0m || isInt) return MathResult<decimal>.Ok(0m);
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dnu = (double)nu;
        double ax = (double)Math.Abs(x);
        BesselIkWithSign(dnu, ax, isInt, out double ie, out _);
        double val = exponentiallyScaled ? ie : ie * Math.Exp(ax);
        if (x < 0m && (((int)Math.Abs(nu)) & 1) != 0)
        {
            val = -val;
        }

        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> BesselK(decimal nu, decimal x)
        => EvaluateBesselK(nu, x, exponentiallyScaled: false);

    public static MathResult<decimal> BesselKe(decimal nu, decimal x)
        => EvaluateBesselK(nu, x, exponentiallyScaled: true);

    private static MathResult<decimal> EvaluateBesselK(decimal nu, decimal x, bool exponentiallyScaled)
    {
        if (Math.Abs(nu) > BesselOrderLimit || x <= 0m || x > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double ax = (double)x;
        BesselIkWithSign((double)Math.Abs(nu), ax, isInt: false, out _, out double ke);
        double val = exponentiallyScaled ? ke : ke * Math.Exp(-ax);
        return DslElementaryMath.NarrowDouble(val);
    }

    internal static void BesselJyWithSign(double nu, double x, bool isInt, out double j, out double y)
    {
        if (nu >= 0.0)
        {
            BessjyPositive(nu, x, out j, out y, out _, out _);
            return;
        }

        double anu = -nu;
        BessjyPositive(anu, x, out double jPos, out double yPos, out _, out _);
        if (isInt)
        {
            int n = (int)anu;
            double s = (n & 1) == 0 ? 1.0 : -1.0;
            j = s * jPos;
            y = s * yPos;
            return;
        }

        double cosPi = Math.Cos(Math.PI * anu);
        double sinPi = Math.Sin(Math.PI * anu);
        j = cosPi * jPos - sinPi * yPos;
        y = sinPi * jPos + cosPi * yPos;
    }

    internal static void BesselIkWithSign(double nu, double x, bool isInt, out double ie, out double ke)
    {
        double anu = Math.Abs(nu);
        BessikPositiveScaled(anu, x, out double iePos, out ke);
        if (nu >= 0.0 || isInt)
        {
            ie = iePos;
            return;
        }

        double sinPi = Math.Sin(Math.PI * anu);
        ie = iePos + (2.0 / Math.PI) * sinPi * Math.Exp(-2.0 * x) * ke;
    }

    private static void BessjyPositive(double xnu, double x, out double rj, out double ry, out double rjp, out double ryp)
    {
        if (x > Math.Max(30.0, 0.35 * xnu * xnu + 20.0))
        {
            double mu = 4.0 * xnu * xnu;
            double eightX = 8.0 * x;
            double p = 1.0;
            double q = 0.0;
            double term = 1.0;
            for (int k = 1; k <= 30; k++)
            {
                int m = 2 * k - 1;
                term *= (mu - m * m) / (k * eightX);
                if ((k & 1) != 0)
                {
                    q += (((k - 1) & 2) == 0 ? 1.0 : -1.0) * term;
                }
                else
                {
                    p += ((k & 2) == 0 ? 1.0 : -1.0) * term;
                }

                if (Math.Abs(term) <= Eps) break;
            }

            double chi = x - (0.5 * xnu + 0.25) * Math.PI;
            double scale = Math.Sqrt(2.0 / (Math.PI * x));
            double sinChi = Math.Sin(chi);
            double cosChi = Math.Cos(chi);
            rj = scale * (p * cosChi - q * sinChi);
            ry = scale * (p * sinChi + q * cosChi);
            rjp = 0.0;
            ryp = 0.0;
            return;
        }

        const double xMin = 2.0;
        const int maxIt = 10000;
        const double fpMin = 1e-30;

        int nl = x < xMin ? (int)(xnu + 0.5) : Math.Max(0, (int)(xnu - x + 1.5));
        double xmu = xnu - nl;
        double xmu2 = xmu * xmu;
        double xi = 1.0 / x;
        double xi2 = 2.0 * xi;
        double w = xi2 / Math.PI;
        int isign = 1;
        double h = xnu * xi;
        if (h < fpMin) h = fpMin;
        double b = xi2 * xnu;
        double d = 0.0;
        double c = h;

        for (int i = 1; i <= maxIt; i++)
        {
            b += xi2;
            d = b - d;
            if (Math.Abs(d) < fpMin) d = fpMin;
            c = b - 1.0 / c;
            if (Math.Abs(c) < fpMin) c = fpMin;
            d = 1.0 / d;
            double del = c * d;
            h *= del;
            if (d < 0.0) isign = -isign;
            if (Math.Abs(del - 1.0) <= Eps) break;
        }

        double rjl = isign * fpMin;
        double rjpl = h * rjl;
        double rjl1 = rjl;
        double rjp1 = rjpl;
        double fact = xnu * xi;
        for (int l = nl; l >= 1; l--)
        {
            double rjtemp = fact * rjl + rjpl;
            fact -= xi;
            rjpl = fact * rjtemp - rjl;
            rjl = rjtemp;
        }

        if (rjl == 0.0) rjl = Eps;
        double f = rjpl / rjl;
        double rjmu;
        double rymu;
        double rymup;
        double ry1;

        if (x < xMin)
        {
            double x2 = 0.5 * x;
            double pimu = Math.PI * xmu;
            double fact2 = Math.Abs(pimu) < Eps ? 1.0 : pimu / Math.Sin(pimu);
            double d2 = -Math.Log(x2);
            double e = xmu * d2;
            double fact3 = Math.Abs(e) < Eps ? 1.0 : Math.Sinh(e) / e;

            double eExp = Math.Exp(e);
            ComputeTemmeGammas(xmu, out double gam1, out double gam2, out double gamPl, out double gamMi);
            double ff = (2.0 / Math.PI) * fact2 * (gam1 * Math.Cosh(e) + gam2 * fact3 * d2);
            double p = eExp / (gamPl * Math.PI);
            double q = 1.0 / (eExp * Math.PI * gamMi);
            double pimu2 = 0.5 * pimu;
            double fact4 = Math.Abs(pimu2) < Eps ? 1.0 : Math.Sin(pimu2) / pimu2;
            double r = Math.PI * pimu2 * fact4 * fact4;
            c = 1.0;
            d = -x2 * x2;
            double sum = ff + r * q;
            double sum1 = p;
            for (int i = 1; i <= maxIt; i++)
            {
                ff = (i * ff + p + q) / (i * i - xmu2);
                c *= d / i;
                p /= i - xmu;
                q /= i + xmu;
                double del = c * (ff + r * q);
                sum += del;
                double del1 = c * p - i * del;
                sum1 += del1;
                if (Math.Abs(del) < (1.0 + Math.Abs(sum)) * Eps) break;
            }

            rymu = -sum;
            ry1 = -sum1 * xi2;
            rymup = xmu * xi * rymu - ry1;
            rjmu = w / (rymup - f * rymu);
        }
        else
        {
            double a = 0.25 - xmu2;
            double p = -0.5 * xi;
            double q = 1.0;
            double br = 2.0 * x;
            double bi = 2.0;
            double fact2 = a * xi / (p * p + q * q);
            double cr = br + q * fact2;
            double ci = bi + p * fact2;
            double den = br * br + bi * bi;
            double dr = br / den;
            double di = -bi / den;
            double dlr = cr * dr - ci * di;
            double dli = cr * di + ci * dr;
            double temp = p * dlr - q * dli;
            q = p * dli + q * dlr;
            p = temp;

            for (int i = 2; i <= maxIt; i++)
            {
                a += 2 * (i - 1);
                bi += 2.0;
                dr = a * dr + br;
                di = a * di + bi;
                if (Math.Abs(dr) + Math.Abs(di) < fpMin) dr = fpMin;
                fact2 = a / (cr * cr + ci * ci);
                cr = br + cr * fact2;
                ci = bi - ci * fact2;
                if (Math.Abs(cr) + Math.Abs(ci) < fpMin) cr = fpMin;
                den = dr * dr + di * di;
                dr /= den;
                di /= -den;
                dlr = cr * dr - ci * di;
                dli = cr * di + ci * dr;
                temp = p * dlr - q * dli;
                q = p * dli + q * dlr;
                p = temp;
                if (Math.Abs(dlr - 1.0) + Math.Abs(dli) <= Eps) break;
            }

            double gam = (p - f) / q;
            rjmu = Math.Sqrt(w / ((p - f) * gam + q));
            rjmu = Math.CopySign(rjmu, rjl);
            rymu = rjmu * gam;
            rymup = rymu * (p + q / gam);
            ry1 = xmu * xi * rymu - rymup;
        }

        double scaleJ = rjmu / rjl;
        rj = rjl1 * scaleJ;
        rjp = rjp1 * scaleJ;
        for (int i = 1; i <= nl; i++)
        {
            double rytemp = (xmu + i) * xi2 * ry1 - rymu;
            rymu = ry1;
            ry1 = rytemp;
        }

        ry = rymu;
        ryp = xnu * xi * rymu - ry1;
    }

    private static void ComputeTemmeGammas(double x, out double gam1, out double gam2, out double gamPl, out double gamMi)
    {
        double gpInv = 1.0 / Math.Exp(DslElementaryMath.LogGammaPositive(1.0 + x));
        double gmInv = 1.0 / Math.Exp(DslElementaryMath.LogGammaPositive(1.0 - x));
        gamPl = gpInv;
        gamMi = gmInv;
        gam2 = 0.5 * (gmInv + gpInv);
        gam1 = Math.Abs(x) < 1e-8
            ? -MathConstants.EulerGamma
            : (gmInv - gpInv) / (2.0 * x);
    }

    private static void BessikPositiveScaled(double xnu, double x, out double ie, out double ke)
    {
        if (x > Math.Max(30.0, 0.35 * xnu * xnu + 20.0))
        {
            double mu = 4.0 * xnu * xnu;
            double eightX = 8.0 * x;
            double sumI = 1.0;
            double sumK = 1.0;
            double term = 1.0;
            for (int k = 1; k <= 30; k++)
            {
                int m = 2 * k - 1;
                term *= (mu - m * m) / (k * eightX);
                sumK += term;
                sumI += ((k & 1) == 0 ? term : -term);
                if (Math.Abs(term) <= Eps) break;
            }

            ie = sumI / Math.Sqrt(MathConstants.TwoPi * x);
            ke = sumK * Math.Sqrt(0.5 * Math.PI / x);
            return;
        }

        const double xMin = 2.0;
        const int maxIt = 10000;
        const double fpMin = 1e-30;

        int nl = (int)(xnu + 0.5);
        double xmu = xnu - nl;
        double xmu2 = xmu * xmu;
        double xi = 1.0 / x;
        double xi2 = 2.0 * xi;
        double h = xnu * xi;
        if (h < fpMin) h = fpMin;
        double b = xi2 * xnu;
        double d = 0.0;
        double c = h;

        for (int i = 1; i <= maxIt; i++)
        {
            b += xi2;
            d = 1.0 / (b + d);
            c = b + 1.0 / c;
            double del = c * d;
            h *= del;
            if (Math.Abs(del - 1.0) <= Eps) break;
        }

        double ril = fpMin;
        double ripl = h * ril;
        double ril1 = ril;
        double fact = xnu * xi;
        for (int l = nl; l >= 1; l--)
        {
            double ritemp = fact * ril + ripl;
            fact -= xi;
            ripl = fact * ritemp + ril;
            ril = ritemp;
        }

        double f = ripl / ril;
        double rkmu;
        double rk1;
        bool kAlreadyScaled;

        if (x < xMin)
        {
            double x2 = 0.5 * x;
            double pimu = Math.PI * xmu;
            double fact2 = Math.Abs(pimu) < Eps ? 1.0 : pimu / Math.Sin(pimu);
            double d2 = -Math.Log(x2);
            double e = xmu * d2;
            double fact3 = Math.Abs(e) < Eps ? 1.0 : Math.Sinh(e) / e;
            ComputeTemmeGammas(xmu, out double gam1, out double gam2, out double gamPl, out double gamMi);

            double ff = fact2 * (gam1 * Math.Cosh(e) + gam2 * fact3 * d2);
            double sum = ff;
            double eExp = Math.Exp(e);
            double p = 0.5 * eExp / gamPl;
            double q = 0.5 / (eExp * gamMi);
            c = 1.0;
            d = x2 * x2;
            double sum1 = p;
            for (int i = 1; i <= maxIt; i++)
            {
                ff = (i * ff + p + q) / (i * i - xmu2);
                c *= d / i;
                p /= i - xmu;
                q /= i + xmu;
                double del = c * ff;
                sum += del;
                double del1 = c * (p - i * ff);
                sum1 += del1;
                if (Math.Abs(del) < Math.Abs(sum) * Eps) break;
            }

            rkmu = sum;
            rk1 = sum1 * xi2;
            kAlreadyScaled = false;
        }
        else
        {
            b = 2.0 * (1.0 + x);
            d = 1.0 / b;
            double delh = d;
            h = delh;
            double q1 = 0.0;
            double q2 = 1.0;
            double a1 = 0.25 - xmu2;
            double qcap = c = a1;
            double a = -a1;
            double s = 1.0 + qcap * delh;
            for (int i = 2; i <= maxIt; i++)
            {
                a -= 2 * (i - 1);
                c = -a * c / i;
                double qnew = (q1 - b * q2) / a;
                q1 = q2;
                q2 = qnew;
                qcap += c * qnew;
                b += 2.0;
                d = 1.0 / (b + a * d);
                delh = (b * d - 1.0) * delh;
                h += delh;
                double dels = qcap * delh;
                s += dels;
                if (Math.Abs(dels / s) <= Eps) break;
            }

            h = a1 * h;
            // Omit Math.Exp(-x) so rkmu is directly e^x * K_mu(x).
            rkmu = Math.Sqrt(Math.PI / (2.0 * x)) / s;
            rk1 = rkmu * (xmu + x + 0.5 - h) * xi;
            kAlreadyScaled = true;
        }

        double rkmup = xmu * xi * rkmu - rk1;
        double rimu = xi / (f * rkmu - rkmup);
        double ri = rimu * ril1 / ril;

        for (int i = 1; i <= nl; i++)
        {
            double rktemp = (xmu + i) * xi2 * rk1 + rkmu;
            rkmu = rk1;
            rk1 = rktemp;
        }

        if (kAlreadyScaled)
        {
            ke = rkmu;
            ie = ri * Math.Exp(-2.0 * x);
        }
        else
        {
            ke = rkmu * Math.Exp(x);
            ie = ri * Math.Exp(-x);
        }
    }

    // ---- 8. Spherical Bessel Functions ----

    public static MathResult<decimal> SphBesselJ(decimal n, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m || Math.Abs(x) > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(ni == 0 ? 1m : 0m);
        }

        double dx = (double)x;
        double ax = Math.Abs(dx);
        double val;
        if (ax < 1e-4 && ni > 0)
        {
            // Power series avoids 0/0 or Bessel half-order prefactor precision loss for tiny x.
            double x2 = -0.5 * ax * ax;
            double term = Math.Pow(ax, ni) / DoubleFactorialDouble(2 * ni + 1);
            double sum = term;
            for (int k = 1; k <= 20; k++)
            {
                term *= x2 / (k * (2 * ni + 2 * k + 1));
                sum += term;
                if (Math.Abs(term) <= Math.Abs(sum) * Eps) break;
            }

            val = sum;
        }
        else
        {
            BessjyPositive(ni + 0.5, ax, out double jHalf, out _, out _, out _);
            val = Math.Sqrt(0.5 * Math.PI / ax) * jHalf;
        }

        if (dx < 0.0 && (ni & 1) != 0)
        {
            val = -val;
        }

        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> SphBesselY(decimal n, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m || x <= 0m || x > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        double dx = (double)x;
        BessjyPositive(ni + 0.5, dx, out _, out double yHalf, out _, out _);
        return DslElementaryMath.NarrowDouble(Math.Sqrt(0.5 * Math.PI / dx) * yHalf);
    }

    public static MathResult<decimal> SphBesselI(decimal n, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m || Math.Abs(x) > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(ni == 0 ? 1m : 0m);
        }

        double dx = (double)x;
        double ax = Math.Abs(dx);
        BessikPositiveScaled(ni + 0.5, ax, out double ieHalf, out _);
        double val = Math.Sqrt(0.5 * Math.PI / ax) * ieHalf * Math.Exp(ax);
        if (dx < 0.0 && (ni & 1) != 0)
        {
            val = -val;
        }

        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> SphBesselK(decimal n, decimal x)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 100m || x <= 0m || x > BesselArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        double dx = (double)x;
        BessikPositiveScaled(ni + 0.5, dx, out _, out double keHalf);
        double val = Math.Sqrt(0.5 * Math.PI / dx) * keHalf * Math.Exp(-dx);
        return DslElementaryMath.NarrowDouble(val);
    }

    private static double DoubleFactorialDouble(int m)
    {
        double acc = 1.0;
        for (int i = m; i >= 2; i -= 2)
        {
            acc *= i;
        }

        return acc;
    }

    // ---- 9. Airy Functions ----

    public static MathResult<decimal> AiryAi(decimal x)
    {
        if (x < AiryMinArg)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateAiry((double)x, out double ai, out _, out _, out _);
        return DslElementaryMath.NarrowDouble(ai);
    }

    public static MathResult<decimal> AiryBi(decimal x)
    {
        if (x < AiryMinArg)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateAiry((double)x, out _, out double bi, out _, out _);
        return DslElementaryMath.NarrowDouble(bi);
    }

    public static MathResult<decimal> AiryAiPrime(decimal x)
    {
        if (x < AiryMinArg)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateAiry((double)x, out _, out _, out double aip, out _);
        return DslElementaryMath.NarrowDouble(aip);
    }

    public static MathResult<decimal> AiryBiPrime(decimal x)
    {
        if (x < AiryMinArg)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateAiry((double)x, out _, out _, out _, out double bip);
        return DslElementaryMath.NarrowDouble(bip);
    }

    private static void EvaluateAiry(double x, out double ai, out double bi, out double aip, out double bip)
    {
        const double ai0 = 0.35502805388781723926;
        const double aip0 = -0.25881940379280679840;
        const double sqrt3 = 1.7320508075688772935;

        if (Math.Abs(x) <= 2.5)
        {
            double x3 = x * x * x;
            double f = 1.0;
            double fp = 0.0;
            double g = x;
            double gp = 1.0;
            double termF = 1.0;
            double termG = x;

            for (int k = 1; k <= 40; k++)
            {
                int m = 3 * k;
                termF *= x3 / ((m - 1) * m);
                f += termF;
                fp += m * termF / x;

                termG *= x3 / (m * (m + 1));
                g += termG;
                gp += (m + 1) * termG / x;

                if (Math.Abs(termF) + Math.Abs(termG) <= Eps) break;
            }

            if (x == 0.0)
            {
                fp = 0.0;
                gp = 1.0;
            }

            ai = ai0 * f + aip0 * g;
            bi = sqrt3 * (ai0 * f - aip0 * g);
            aip = ai0 * fp + aip0 * gp;
            bip = sqrt3 * (ai0 * fp - aip0 * gp);
            return;
        }

        double absX = Math.Abs(x);
        double sqrtAbsX = Math.Sqrt(absX);
        double zeta = (2.0 / 3.0) * absX * sqrtAbsX;

        if (x > 0.0)
        {
            BessikPositiveScaled(1.0 / 3.0, zeta, out double ie13, out double ke13);
            BessikPositiveScaled(2.0 / 3.0, zeta, out double ie23, out double ke23);
            double expZ = Math.Exp(zeta);
            double expNegZ = Math.Exp(-zeta);

            // Also I_{-1/3}, I_{-2/3} via I_{-nu} = I_nu + (2/pi)*sin(nu*pi)*K_nu
            double sinPi3 = 0.5 * sqrt3;
            double ieM13 = ie13 + (2.0 / Math.PI) * sinPi3 * Math.Exp(-2.0 * zeta) * ke13;
            double ieM23 = ie23 + (2.0 / Math.PI) * sinPi3 * Math.Exp(-2.0 * zeta) * ke23;

            ai = (sqrtAbsX / (Math.PI * sqrt3)) * ke13 * expNegZ;
            aip = -(absX / (Math.PI * sqrt3)) * ke23 * expNegZ;
            bi = Math.Sqrt(absX / 3.0) * (ie13 + ieM13) * expZ;
            bip = (absX / sqrt3) * (ie23 + ieM23) * expZ;
        }
        else
        {
            BessjyPositive(1.0 / 3.0, zeta, out double j13, out double y13, out _, out _);
            BessjyPositive(2.0 / 3.0, zeta, out double j23, out double y23, out _, out _);
            double jM13 = 0.5 * j13 - 0.5 * sqrt3 * y13;
            double jM23 = -0.5 * j23 - 0.5 * sqrt3 * y23;

            ai = (sqrtAbsX / 3.0) * (j13 + jM13);
            bi = Math.Sqrt(absX / 3.0) * (jM13 - j13);
            aip = -(absX / 3.0) * (jM23 - j23);
            bip = (absX / sqrt3) * (j23 + jM23);
        }
    }

    // ---- 10. Kelvin Functions ----

    public static MathResult<decimal> KelvinBer(decimal x)
    {
        if (x < 0m || x > KelvinArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateKelvinBerBei((double)x, out double ber, out _);
        return DslElementaryMath.NarrowDouble(ber);
    }

    public static MathResult<decimal> KelvinBei(decimal x)
    {
        if (x < 0m || x > KelvinArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateKelvinBerBei((double)x, out _, out double bei);
        return DslElementaryMath.NarrowDouble(bei);
    }

    public static MathResult<decimal> KelvinKer(decimal x)
    {
        if (x <= 0m || x > KelvinArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateKelvinKerKei((double)x, out double ker, out _);
        return DslElementaryMath.NarrowDouble(ker);
    }

    public static MathResult<decimal> KelvinKei(decimal x)
    {
        if (x <= 0m || x > KelvinArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        EvaluateKelvinKerKei((double)x, out _, out double kei);
        return DslElementaryMath.NarrowDouble(kei);
    }

    private static void EvaluateKelvinBerBei(double x, out double ber, out double bei)
    {
        if (x <= 16.0)
        {
            double x2 = 0.25 * x * x;
            double x4 = x2 * x2;
            double termBer = 1.0;
            double sumBer = 1.0;
            double termBei = x2;
            double sumBei = x2;

            for (int k = 1; k <= 30; k++)
            {
                double m = 2 * k;
                termBer *= -x4 / ((m - 1) * (m - 1) * m * m);
                sumBer += termBer;

                termBei *= -x4 / (m * m * (m + 1) * (m + 1));
                sumBei += termBei;

                if (Math.Abs(termBer) + Math.Abs(termBei) <= Eps) break;
            }

            ber = sumBer;
            bei = sumBei;
            return;
        }

        double alpha = x / Sqrt2;
        double scale = Math.Exp(alpha) / Math.Sqrt(MathConstants.TwoPi * x);
        double sumRe = 0.0;
        double sumIm = 0.0;
        double ck = 1.0;
        for (int k = 0; k <= 24; k++)
        {
            if (k > 0)
            {
                int m = 2 * k - 1;
                ck *= (m * m) / (8.0 * k * x);
            }

            double phase = alpha - Math.PI / 8.0 - k * Math.PI * 0.25;
            double addRe = ck * Math.Cos(phase);
            double addIm = ck * Math.Sin(phase);
            sumRe += addRe;
            sumIm += addIm;
            if (ck <= Eps) break;
        }

        ber = scale * sumRe;
        bei = scale * sumIm;
    }

    private static void EvaluateKelvinKerKei(double x, out double ker, out double kei)
    {
        if (x <= 16.0)
        {
            EvaluateKelvinBerBei(x, out double ber, out double bei);
            double x2 = 0.25 * x * x;
            double x4 = x2 * x2;
            double termBer = 1.0;
            double termBei = x2;
            double harmonicBer = 0.0;
            double harmonicBei = 1.0;
            double sumKer = 0.0;
            double sumKei = x2;

            for (int k = 1; k <= 30; k++)
            {
                double m = 2 * k;
                termBer *= -x4 / ((m - 1) * (m - 1) * m * m);
                harmonicBer += 1.0 / (m - 1.0) + 1.0 / m;
                sumKer += harmonicBer * termBer;

                termBei *= -x4 / (m * m * (m + 1) * (m + 1));
                harmonicBei += 1.0 / m + 1.0 / (m + 1.0);
                sumKei += harmonicBei * termBei;

                if (Math.Abs(harmonicBer * termBer) + Math.Abs(harmonicBei * termBei) <= Eps) break;
            }

            double logHalfXPlusGamma = Math.Log(0.5 * x) + MathConstants.EulerGamma;
            ker = -logHalfXPlusGamma * ber + 0.25 * Math.PI * bei + sumKer;
            kei = -logHalfXPlusGamma * bei - 0.25 * Math.PI * ber + sumKei;
            return;
        }

        double alpha = x / Sqrt2;
        double scale = Math.Sqrt(0.5 * Math.PI / x) * Math.Exp(-alpha);
        double sumRe = 0.0;
        double sumIm = 0.0;
        double ck = 1.0;
        for (int k = 0; k <= 24; k++)
        {
            if (k > 0)
            {
                int m = 2 * k - 1;
                ck *= -(m * m) / (8.0 * k * x);
            }

            double phase = -alpha - Math.PI / 8.0 - k * Math.PI * 0.25;
            sumRe += ck * Math.Cos(phase);
            sumIm += ck * Math.Sin(phase);
            if (Math.Abs(ck) <= Eps) break;
        }

        ker = scale * sumRe;
        kei = scale * sumIm;
    }

    // ---- 11. Struve Functions ----

    public static MathResult<decimal> StruveH(decimal nu, decimal x)
    {
        if (Math.Abs(nu) > StruveOrderLimit || Math.Abs(x) > StruveArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x < 0m && nu != decimal.Truncate(nu))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            return nu >= -1m ? MathResult<decimal>.Ok(0m) : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dnu = (double)nu;
        double ax = (double)Math.Abs(x);
        double val = StruveDouble(dnu, ax, modified: false);
        if (x < 0m)
        {
            int n = (int)Math.Abs(nu);
            if (((n + 1) & 1) != 0) val = -val;
        }

        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> StruveL(decimal nu, decimal x)
    {
        if (Math.Abs(nu) > StruveOrderLimit || Math.Abs(x) > StruveArgLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x < 0m && nu != decimal.Truncate(nu))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            return nu >= -1m ? MathResult<decimal>.Ok(0m) : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dnu = (double)nu;
        double ax = (double)Math.Abs(x);
        double val = StruveDouble(dnu, ax, modified: true);
        if (x < 0m)
        {
            int n = (int)Math.Abs(nu);
            if (((n + 1) & 1) != 0) val = -val;
        }

        return DslElementaryMath.NarrowDouble(val);
    }

    private static double StruveDouble(double nu, double x, bool modified)
    {
        if (x <= 20.0)
        {
            double halfX = 0.5 * x;
            double signArg = modified ? 1.0 : -1.0;
            double x2 = signArg * halfX * halfX;
            double lg1 = LogAbsGammaDouble(1.5, out _);
            double lg2 = LogAbsGammaDouble(nu + 1.5, out int s2);
            double term = Math.Exp((nu + 1.0) * Math.Log(halfX) - lg1 - lg2) * s2;
            double sum = term;

            for (int k = 1; k <= 80; k++)
            {
                term *= x2 / ((k + 0.5) * (k + nu + 0.5));
                sum += term;
                if (Math.Abs(term) <= Math.Abs(sum) * Eps) break;
            }

            return sum;
        }

        // Asymptotic expansion: H_nu(x) = Y_nu(x) + 1/pi * sum, L_nu(x) = I_nu(x) - 1/pi * sum
        double halfXBig = 0.5 * x;
        double invX2 = 1.0 / (halfXBig * halfXBig);
        double lgNu = LogAbsGammaDouble(nu + 0.5, out int sNu);
        double asymTerm = Math.Exp((nu - 1.0) * Math.Log(halfXBig) - Math.Log(SqrtPi) - lgNu) * sNu;
        double asymSum = asymTerm;
        double signStep = modified ? -1.0 : 1.0;
        for (int k = 1; k <= 20; k++)
        {
            double prev = Math.Abs(asymTerm);
            asymTerm *= signStep * (k - 0.5) * invX2 * (nu + 0.5 - k);
            if (Math.Abs(asymTerm) > prev) break;
            asymSum += asymTerm;
            if (Math.Abs(asymTerm) <= Math.Abs(asymSum) * Eps) break;
        }

        if (!modified)
        {
            BesselJyWithSign(nu, x, nu == Math.Truncate(nu), out _, out double yNu);
            return yNu + asymSum;
        }

        BesselIkWithSign(nu, x, nu == Math.Truncate(nu), out double ieNu, out _);
        return ieNu * Math.Exp(x) - asymSum;
    }
}
