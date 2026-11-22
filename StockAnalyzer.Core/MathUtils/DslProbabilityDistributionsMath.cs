using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation probability distributions (26 distributions, canonical argument order
/// <c>(x_or_k_or_p, param1, param2, ...)</c>) for StockAnalyzer DSL.
/// </summary>
public static partial class DslProbabilityDistributionsMath
{
    private const double Sqrt2 = 1.4142135623730950488;
    private const double Sqrt2Pi = 2.5066282746310005024;
    private const double HalfLogTwoPi = 0.91893853320467274178;
    private const double Eps = 1e-14;
    private const int MaxQuantileIterations = 128;
    private const int MaxSeriesTerms = 1_000;
    private const int MaxHypergeomTerms = 10_000;

    // 20-point Gauss-Legendre nodes and weights on [-1, 1] (symmetric pairs, positive half listed).
    private static readonly double[] Gl20Nodes =
    {
        0.07652652113349733375, 0.22778585114164507808, 0.37370608871541956067, 0.51086700195082709800,
        0.63605368072651502545, 0.74633190646015079261, 0.83911697182221882339, 0.91223442825132590586,
        0.96397192727791379127, 0.99312859918509492479,
    };

    private static readonly double[] Gl20Weights =
    {
        0.15275338713072585070, 0.14917298647260374679, 0.14209610931838205133, 0.13168863844917662690,
        0.11819453196151841731, 0.10193011981724043504, 0.08327674157670474872, 0.06267204833410906357,
        0.04060142980038694133, 0.01761400713915211831,
    };

    // ---- 1. Normal Distribution (Standard or Parameterized) ----

    public static MathResult<decimal> NormPdf(decimal x, decimal mu = 0m, decimal sigma = 1m)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)sigma;
        if (Math.Abs(z) > 38.0) return MathResult<decimal>.Ok(0m);
        return DslElementaryMath.NarrowDouble(Math.Exp(-0.5 * z * z) / ((double)sigma * Sqrt2Pi));
    }

    public static MathResult<decimal> NormLogPdf(decimal x, decimal mu = 0m, decimal sigma = 1m)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double ds = (double)sigma;
        double z = ((double)x - (double)mu) / ds;
        return DslElementaryMath.NarrowDouble(-Math.Log(ds) - HalfLogTwoPi - 0.5 * z * z);
    }

    public static MathResult<decimal> NormCdf(decimal x, decimal mu = 0m, decimal sigma = 1m)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)sigma;
        if (z > 8.3) return MathResult<decimal>.Ok(1m);
        return DslElementaryMath.NarrowDouble(SpecialFunctionsMath.NormalCdf(z));
    }

    public static MathResult<decimal> NormSf(decimal x, decimal mu = 0m, decimal sigma = 1m)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)sigma;
        if (z < -8.3) return MathResult<decimal>.Ok(1m);
        return DslElementaryMath.NarrowDouble(0.5 * SpecialFunctionsMath.Erfc(z / Sqrt2));
    }

    public static MathResult<decimal> NormIcdf(decimal p, decimal mu = 0m, decimal sigma = 1m)
    {
        if (p <= 0m || p >= 1m || sigma <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double q = SpecialFunctionsMath.NormalQuantile((double)p);
        return DslElementaryMath.NarrowDouble((double)mu + (double)sigma * q);
    }

    // ---- 2. Log-Normal Distribution ----

    public static MathResult<decimal> LogNormPdf(decimal x, decimal mu, decimal sigma)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= 0m) return MathResult<decimal>.Ok(0m);
        double dx = (double)x;
        double ds = (double)sigma;
        double z = (Math.Log(dx) - (double)mu) / ds;
        return DslElementaryMath.NarrowDouble(Math.Exp(-0.5 * z * z) / (dx * ds * Sqrt2Pi));
    }

    public static MathResult<decimal> LogNormLogPdf(decimal x, decimal mu, decimal sigma)
    {
        if (sigma <= 0m || x <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double dx = (double)x;
        double ds = (double)sigma;
        double z = (Math.Log(dx) - (double)mu) / ds;
        return DslElementaryMath.NarrowDouble(-Math.Log(dx) - Math.Log(ds) - HalfLogTwoPi - 0.5 * z * z);
    }

    public static MathResult<decimal> LogNormCdf(decimal x, decimal mu, decimal sigma)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= 0m) return MathResult<decimal>.Ok(0m);
        double z = (Math.Log((double)x) - (double)mu) / (double)sigma;
        return DslElementaryMath.NarrowDouble(SpecialFunctionsMath.NormalCdf(z));
    }

    public static MathResult<decimal> LogNormSf(decimal x, decimal mu, decimal sigma)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= 0m) return MathResult<decimal>.Ok(1m);
        double z = (Math.Log((double)x) - (double)mu) / (double)sigma;
        return DslElementaryMath.NarrowDouble(0.5 * SpecialFunctionsMath.Erfc(z / Sqrt2));
    }

    public static MathResult<decimal> LogNormIcdf(decimal p, decimal mu, decimal sigma)
    {
        if (p <= 0m || p >= 1m || sigma <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double z = SpecialFunctionsMath.NormalQuantile((double)p);
        return DslElementaryMath.NarrowDouble(Math.Exp((double)mu + (double)sigma * z));
    }

    // ---- 3. Bivariate Normal Distribution ----

    public static MathResult<decimal> BvNormPdf(
        decimal x,
        decimal y,
        decimal mu1,
        decimal mu2,
        decimal sigma1,
        decimal sigma2,
        decimal rho)
    {
        MathResult<decimal> logRes = BvNormLogPdf(x, y, mu1, mu2, sigma1, sigma2, rho);
        if (!logRes.IsOk) return logRes;
        return DslElementaryMath.NarrowDouble(Math.Exp((double)logRes.Value));
    }

    public static MathResult<decimal> BvNormLogPdf(
        decimal x,
        decimal y,
        decimal mu1,
        decimal mu2,
        decimal sigma1,
        decimal sigma2,
        decimal rho)
    {
        if (sigma1 <= 0m || sigma2 <= 0m || Math.Abs(rho) >= 0.9999999m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double s1 = (double)sigma1;
        double s2 = (double)sigma2;
        double r = (double)rho;
        double z1 = ((double)x - (double)mu1) / s1;
        double z2 = ((double)y - (double)mu2) / s2;
        double omr2 = 1.0 - r * r;
        double q = (z1 * z1 - 2.0 * r * z1 * z2 + z2 * z2) / omr2;
        double logPdf = -Math.Log(MathConstants.TwoPi * s1 * s2 * Math.Sqrt(omr2)) - 0.5 * q;
        return DslElementaryMath.NarrowDouble(logPdf);
    }

    public static MathResult<decimal> BvNormCdf(
        decimal x,
        decimal y,
        decimal mu1,
        decimal mu2,
        decimal sigma1,
        decimal sigma2,
        decimal rho)
    {
        if (sigma1 <= 0m || sigma2 <= 0m || Math.Abs(rho) >= 0.9999999m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double z1 = ((double)x - (double)mu1) / (double)sigma1;
        double z2 = ((double)y - (double)mu2) / (double)sigma2;
        return DslElementaryMath.NarrowDouble(BvNormCdfStandard(z1, z2, (double)rho));
    }

    public static MathResult<decimal> BvNormSf(
        decimal x,
        decimal y,
        decimal mu1,
        decimal mu2,
        decimal sigma1,
        decimal sigma2,
        decimal rho)
    {
        if (sigma1 <= 0m || sigma2 <= 0m || Math.Abs(rho) >= 0.9999999m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double z1 = ((double)x - (double)mu1) / (double)sigma1;
        double z2 = ((double)y - (double)mu2) / (double)sigma2;
        return DslElementaryMath.NarrowDouble(BvNormCdfStandard(-z1, -z2, (double)rho));
    }

    internal static double BvNormCdfStandard(double h, double k, double r)
    {
        if (r == 0.0)
        {
            return SpecialFunctionsMath.NormalCdf(h) * SpecialFunctionsMath.NormalCdf(k);
        }

        // Drezner-Wesolowsky / Genz 20-point Gauss-Legendre integration in theta = asin(rho).
        double asr = Math.Asin(Math.Clamp(r, -1.0, 1.0));
        double halfAsr = 0.5 * asr;
        double hk = h * k;
        double hs = 0.5 * (h * h + k * k);
        double bvn = 0.0;

        for (int i = 0; i < Gl20Nodes.Length; i++)
        {
            double w = Gl20Weights[i];
            double sn1 = Math.Sin(halfAsr * (1.0 - Gl20Nodes[i]));
            double sn2 = Math.Sin(halfAsr * (1.0 + Gl20Nodes[i]));
            bvn += w * (Math.Exp((sn1 * hk - hs) / (1.0 - sn1 * sn1))
                      + Math.Exp((sn2 * hk - hs) / (1.0 - sn2 * sn2)));
        }

        bvn *= halfAsr / MathConstants.TwoPi;
        bvn += SpecialFunctionsMath.NormalCdf(h) * SpecialFunctionsMath.NormalCdf(k);
        return Math.Clamp(bvn, 0.0, 1.0);
    }

    // ---- 4. Chi-Square Distribution ----

    public static MathResult<decimal> Chi2Pdf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m)
        {
            if (nu < 2m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            return MathResult<decimal>.Ok(nu == 2m ? 0.5m : 0m);
        }

        double k2 = 0.5 * (double)nu;
        double dx = (double)x;
        double logPdf = (k2 - 1.0) * Math.Log(dx) - 0.5 * dx - k2 * Math.Log(2.0) - DslElementaryMath.LogGammaPositive(k2);
        return DslElementaryMath.NarrowDouble(Math.Exp(logPdf));
    }

    public static MathResult<decimal> Chi2LogPdf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m)
        {
            if (nu == 2m) return DslElementaryMath.NarrowDouble(-Math.Log(2.0));
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double k2 = 0.5 * (double)nu;
        double dx = (double)x;
        double logPdf = (k2 - 1.0) * Math.Log(dx) - 0.5 * dx - k2 * Math.Log(2.0) - DslElementaryMath.LogGammaPositive(k2);
        return DslElementaryMath.NarrowDouble(logPdf);
    }

    public static MathResult<decimal> Chi2Cdf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double p = DslSpecialFunctionsMath.RegularizedGammaPDouble(0.5 * (double)nu, 0.5 * (double)x);
        return double.IsNaN(p) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(p);
    }

    public static MathResult<decimal> Chi2Sf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double q = DslSpecialFunctionsMath.RegularizedGammaQDouble(0.5 * (double)nu, 0.5 * (double)x);
        return double.IsNaN(q) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(q);
    }

    public static MathResult<decimal> Chi2Icdf(decimal p, decimal nu, ref DslExecutionBudget budget)
    {
        if (p <= 0m || p >= 1m || nu <= 0m || nu > 1_000_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dp = (double)p;
        double dnu = (double)nu;
        double val = Chi2IcdfDouble(dp, dnu, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(val) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(val);
    }

    internal static double Chi2IcdfDouble(double p, double nu, ref DslExecutionBudget budget)
    {
        double k2 = 0.5 * nu;
        double lg = DslSpecialFunctionsMath.LogAbsGammaDouble(k2, out _);

        // Wilson-Hilferty initial guess
        double z = SpecialFunctionsMath.NormalQuantile(p);
        double term = 2.0 / (9.0 * nu);
        double cubeRoot = 1.0 - term + z * Math.Sqrt(term);
        double x = cubeRoot > 0.0 ? nu * cubeRoot * cubeRoot * cubeRoot : 0.5 * nu;
        if (x <= 0.0) x = 1e-12;

        double lo = 0.0;
        double hi = Math.Max(x * 4.0, nu + 20.0 * Math.Sqrt(2.0 * nu) + 50.0);
        while (DslSpecialFunctionsMath.RegularizedGammaPDouble(k2, 0.5 * hi) < p)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            hi *= 2.0;
            if (hi > 1e12) break;
        }

        for (int iter = 0; iter < MaxQuantileIterations; iter++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            double cdf = DslSpecialFunctionsMath.RegularizedGammaPDouble(k2, 0.5 * x);
            double diff = cdf - p;
            if (Math.Abs(diff) <= 1e-14) return x;

            if (diff < 0.0) lo = x;
            else hi = x;

            double logPdf = (k2 - 1.0) * Math.Log(x) - 0.5 * x - k2 * Math.Log(2.0) - lg;
            double pdf = Math.Exp(logPdf);
            double next = pdf > 1e-300 ? x - diff / pdf : 0.5 * (lo + hi);
            if (!(next > lo && next < hi)) next = 0.5 * (lo + hi);
            if (Math.Abs(next - x) <= 1e-14 * Math.Max(1.0, next)) return next;
            x = next;
        }

        return x;
    }

    // ---- 5. Student's t Distribution ----

    public static MathResult<decimal> StudentTPdf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (nu > 100_000m) return NormPdf(x);
        double dx = (double)x;
        double dnu = (double)nu;
        double logPdf = DslElementaryMath.LogGammaPositive(0.5 * (dnu + 1.0))
            - DslElementaryMath.LogGammaPositive(0.5 * dnu)
            - 0.5 * Math.Log(dnu * Math.PI)
            - 0.5 * (dnu + 1.0) * Math.Log(1.0 + (dx * dx) / dnu);
        return DslElementaryMath.NarrowDouble(Math.Exp(logPdf));
    }

    public static MathResult<decimal> StudentTLogPdf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (nu > 100_000m) return NormLogPdf(x);
        double dx = (double)x;
        double dnu = (double)nu;
        double logPdf = DslElementaryMath.LogGammaPositive(0.5 * (dnu + 1.0))
            - DslElementaryMath.LogGammaPositive(0.5 * dnu)
            - 0.5 * Math.Log(dnu * Math.PI)
            - 0.5 * (dnu + 1.0) * Math.Log(1.0 + (dx * dx) / dnu);
        return DslElementaryMath.NarrowDouble(logPdf);
    }

    public static MathResult<decimal> StudentTCdf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (nu > 100_000m) return NormCdf(x);
        double dx = (double)x;
        double dnu = (double)nu;
        double u = dnu / (dnu + dx * dx);
        double ib = DslSpecialFunctionsMath.IncBetaDouble(u, 0.5 * dnu, 0.5);
        double cdf = dx >= 0.0 ? 1.0 - 0.5 * ib : 0.5 * ib;
        return DslElementaryMath.NarrowDouble(cdf);
    }

    public static MathResult<decimal> StudentTSf(decimal x, decimal nu)
    {
        if (nu <= 0m || nu > 1_000_000m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (nu > 100_000m) return NormSf(x);
        double dx = (double)x;
        double dnu = (double)nu;
        double u = dnu / (dnu + dx * dx);
        double ib = DslSpecialFunctionsMath.IncBetaDouble(u, 0.5 * dnu, 0.5);
        double sf = dx >= 0.0 ? 0.5 * ib : 1.0 - 0.5 * ib;
        return DslElementaryMath.NarrowDouble(sf);
    }

    public static MathResult<decimal> StudentTIcdf(decimal p, decimal nu, ref DslExecutionBudget budget)
    {
        if (p <= 0m || p >= 1m || nu <= 0m || nu > 1_000_000m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (nu > 100_000m) return NormIcdf(p);
        if (p == 0.5m) return MathResult<decimal>.Ok(0m);

        double dp = (double)p;
        double dnu = (double)nu;
        if (dnu == 1.0)
        {
            return DslElementaryMath.NarrowDouble(Math.Tan(Math.PI * (dp - 0.5)));
        }

        double sign = dp < 0.5 ? -1.0 : 1.0;
        double qTail = dp < 0.5 ? dp : 1.0 - dp; // 2 * qTail = I_{u}(nu/2, 0.5)
        double targetBeta = 2.0 * qTail;

        double z = SpecialFunctionsMath.NormalQuantile(1.0 - qTail);
        double x = z + (z * z * z + z) / (4.0 * dnu);
        if (!(x > 0.0)) x = 1.0;

        double lo = 0.0;
        double hi = Math.Max(x * 4.0, 10.0);
        while (DslSpecialFunctionsMath.IncBetaDouble(dnu / (dnu + hi * hi), 0.5 * dnu, 0.5) > targetBeta)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            hi *= 2.0;
            if (hi > 1e12) break;
        }

        double logNorm = DslElementaryMath.LogGammaPositive(0.5 * (dnu + 1.0))
            - DslElementaryMath.LogGammaPositive(0.5 * dnu)
            - 0.5 * Math.Log(dnu * Math.PI);

        for (int iter = 0; iter < MaxQuantileIterations; iter++)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            double tail = 0.5 * DslSpecialFunctionsMath.IncBetaDouble(dnu / (dnu + x * x), 0.5 * dnu, 0.5);
            double diff = qTail - tail; // positive when x needs to decrease
            if (Math.Abs(diff) <= 1e-14) break;
            if (tail > qTail) lo = x;
            else hi = x;

            double pdf = Math.Exp(logNorm - 0.5 * (dnu + 1.0) * Math.Log(1.0 + x * x / dnu));
            double next = pdf > 1e-300 ? x + diff / pdf : 0.5 * (lo + hi);
            if (!(next > lo && next < hi)) next = 0.5 * (lo + hi);
            if (Math.Abs(next - x) <= 1e-14 * Math.Max(1.0, next))
            {
                x = next;
                break;
            }

            x = next;
        }

        return DslElementaryMath.NarrowDouble(sign * x);
    }

    // ---- 6. F Distribution ----

    public static MathResult<decimal> FPdf(decimal x, decimal d1, decimal d2)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            if (d1 < 2m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            return MathResult<decimal>.Ok(d1 == 2m ? 1m : 0m);
        }

        double a = (double)d1;
        double b = (double)d2;
        double dx = (double)x;
        double logPdf = 0.5 * (a * Math.Log(a) + b * Math.Log(b))
            + (0.5 * a - 1.0) * Math.Log(dx)
            - 0.5 * (a + b) * Math.Log(a * dx + b)
            - DslSpecialFunctionsMath.LBetaDouble(0.5 * a, 0.5 * b);
        return DslElementaryMath.NarrowDouble(Math.Exp(logPdf));
    }

    public static MathResult<decimal> FLogPdf(decimal x, decimal d1, decimal d2)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            return d1 == 2m
                ? MathResult<decimal>.Ok(0m)
                : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double a = (double)d1;
        double b = (double)d2;
        double dx = (double)x;
        double logPdf = 0.5 * (a * Math.Log(a) + b * Math.Log(b))
            + (0.5 * a - 1.0) * Math.Log(dx)
            - 0.5 * (a + b) * Math.Log(a * dx + b)
            - DslSpecialFunctionsMath.LBetaDouble(0.5 * a, 0.5 * b);
        return DslElementaryMath.NarrowDouble(logPdf);
    }

    public static MathResult<decimal> FCdf(decimal x, decimal d1, decimal d2)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m) return MathResult<decimal>.Ok(0m);
        double a = (double)d1;
        double b = (double)d2;
        double ax = a * (double)x;
        double u = ax / (ax + b);
        return DslElementaryMath.NarrowDouble(DslSpecialFunctionsMath.IncBetaDouble(u, 0.5 * a, 0.5 * b));
    }

    public static MathResult<decimal> FSf(decimal x, decimal d1, decimal d2)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m) return MathResult<decimal>.Ok(1m);
        double a = (double)d1;
        double b = (double)d2;
        double ax = a * (double)x;
        double oneMinusU = b / (ax + b);
        return DslElementaryMath.NarrowDouble(DslSpecialFunctionsMath.IncBetaDouble(oneMinusU, 0.5 * b, 0.5 * a));
    }

    public static MathResult<decimal> FIcdf(decimal p, decimal d1, decimal d2, ref DslExecutionBudget budget)
    {
        if (p <= 0m || p >= 1m || d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double u = BetaIcdfDouble((double)p, 0.5 * (double)d1, 0.5 * (double)d2, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        if (double.IsNaN(u)) return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
        double x = ((double)d2 * u) / ((double)d1 * (1.0 - u));
        return DslElementaryMath.NarrowDouble(x);
    }

    // ---- 7. Exponential, Uniform, Weibull, Gamma, Beta, Laplace, Cauchy, Logistic, Pareto, Levy ----

    public static MathResult<decimal> ExpPdf(decimal x, decimal lambda)
    {
        if (lambda <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x < 0m) return MathResult<decimal>.Ok(0m);
        return DslElementaryMath.NarrowDouble((double)lambda * Math.Exp(-(double)lambda * (double)x));
    }

    public static MathResult<decimal> ExpLogPdf(decimal x, decimal lambda)
    {
        if (lambda <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        return DslElementaryMath.NarrowDouble(Math.Log((double)lambda) - (double)lambda * (double)x);
    }

    public static MathResult<decimal> ExpCdf(decimal x, decimal lambda)
    {
        if (lambda <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= 0m) return MathResult<decimal>.Ok(0m);
        decimal lx = checked(-lambda * x);
        MathResult<decimal> ex = DslElementaryMath.Expm1(lx);
        return ex.IsOk ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(-ex.Value)) : ex;
    }

    public static MathResult<decimal> ExpSf(decimal x, decimal lambda)
    {
        if (lambda <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= 0m) return MathResult<decimal>.Ok(1m);
        return DslElementaryMath.NarrowDouble(Math.Exp(-(double)lambda * (double)x));
    }

    public static MathResult<decimal> ExpIcdf(decimal p, decimal lambda)
    {
        if (lambda <= 0m || p < 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (p == 0m) return MathResult<decimal>.Ok(0m);
        MathResult<decimal> log1mp = DslElementaryMath.Log1p(-p);
        return log1mp.IsOk ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(-log1mp.Value / lambda)) : log1mp;
    }

    public static MathResult<decimal> UnifPdf(decimal x, decimal a, decimal b)
    {
        if (b - a < 1e-12m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x < a || x > b) return MathResult<decimal>.Ok(0m);
        return MathResult<decimal>.Ok(1m / (b - a));
    }

    public static MathResult<decimal> UnifLogPdf(decimal x, decimal a, decimal b)
    {
        if (b - a < 1e-12m || x < a || x > b) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        return DslElementaryMath.NarrowDouble(-Math.Log((double)(b - a)));
    }

    public static MathResult<decimal> UnifCdf(decimal x, decimal a, decimal b)
    {
        if (b - a < 1e-12m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= a) return MathResult<decimal>.Ok(0m);
        if (x >= b) return MathResult<decimal>.Ok(1m);
        return MathResult<decimal>.Ok((x - a) / (b - a));
    }

    public static MathResult<decimal> UnifSf(decimal x, decimal a, decimal b)
    {
        if (b - a < 1e-12m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= a) return MathResult<decimal>.Ok(1m);
        if (x >= b) return MathResult<decimal>.Ok(0m);
        return MathResult<decimal>.Ok((b - x) / (b - a));
    }

    public static MathResult<decimal> UnifIcdf(decimal p, decimal a, decimal b)
    {
        if (b - a < 1e-12m || p < 0m || p > 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (p == 0m) return MathResult<decimal>.Ok(a);
        if (p == 1m) return MathResult<decimal>.Ok(b);
        return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked(a + p * (b - a))));
    }

    public static MathResult<decimal> WeibullPdf(decimal x, decimal k, decimal lambda)
    {
        if (k <= 0m || lambda <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m)
        {
            if (k < 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            return MathResult<decimal>.Ok(k == 1m ? 1m / lambda : 0m);
        }

        double dk = (double)k;
        double dl = (double)lambda;
        double u = (double)x / dl;
        double uk = Math.Pow(u, dk);
        if (uk > 700.0) return MathResult<decimal>.Ok(0m);
        return DslElementaryMath.NarrowDouble((dk / dl) * Math.Pow(u, dk - 1.0) * Math.Exp(-uk));
    }

    public static MathResult<decimal> WeibullLogPdf(decimal x, decimal k, decimal lambda)
    {
        if (k <= 0m || lambda <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m)
        {
            return k == 1m ? DslElementaryMath.NarrowDouble(-Math.Log((double)lambda)) : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dk = (double)k;
        double dl = (double)lambda;
        double u = (double)x / dl;
        return DslElementaryMath.NarrowDouble(Math.Log(dk / dl) + (dk - 1.0) * Math.Log(u) - Math.Pow(u, dk));
    }

    public static MathResult<decimal> WeibullCdf(decimal x, decimal k, decimal lambda)
    {
        if (k <= 0m || lambda <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m) return MathResult<decimal>.Ok(0m);
        double uk = Math.Pow((double)x / (double)lambda, (double)k);
        return uk > 700.0 ? MathResult<decimal>.Ok(1m) : DslElementaryMath.NarrowDouble(-DslElementaryMath.Expm1Double(-uk));
    }

    public static MathResult<decimal> WeibullSf(decimal x, decimal k, decimal lambda)
    {
        if (k <= 0m || lambda <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m) return MathResult<decimal>.Ok(1m);
        double uk = Math.Pow((double)x / (double)lambda, (double)k);
        return uk > 700.0 ? MathResult<decimal>.Ok(0m) : DslElementaryMath.NarrowDouble(Math.Exp(-uk));
    }

    public static MathResult<decimal> WeibullIcdf(decimal p, decimal k, decimal lambda)
    {
        if (k <= 0m || lambda <= 0m || p < 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (p == 0m) return MathResult<decimal>.Ok(0m);
        return DslElementaryMath.NarrowDouble((double)lambda * Math.Pow(-Math.Log(1.0 - (double)p), 1.0 / (double)k));
    }

    public static MathResult<decimal> GammaPdf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m)
        {
            if (alpha < 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            return MathResult<decimal>.Ok(alpha == 1m ? beta : 0m);
        }

        double da = (double)alpha;
        double db = (double)beta;
        double dx = (double)x;
        double logPdf = da * Math.Log(db) - DslSpecialFunctionsMath.LogAbsGammaDouble(da, out _) + (da - 1.0) * Math.Log(dx) - db * dx;
        return DslElementaryMath.NarrowDouble(Math.Exp(logPdf));
    }

    public static MathResult<decimal> GammaLogPdf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m)
        {
            return alpha == 1m ? DslElementaryMath.NarrowDouble(Math.Log((double)beta)) : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)alpha;
        double db = (double)beta;
        double dx = (double)x;
        double logPdf = da * Math.Log(db) - DslSpecialFunctionsMath.LogAbsGammaDouble(da, out _) + (da - 1.0) * Math.Log(dx) - db * dx;
        return DslElementaryMath.NarrowDouble(logPdf);
    }

    public static MathResult<decimal> GammaCdf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double p = DslSpecialFunctionsMath.RegularizedGammaPDouble((double)alpha, (double)beta * (double)x);
        return double.IsNaN(p) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(p);
    }

    public static MathResult<decimal> GammaSf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m || x < 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double q = DslSpecialFunctionsMath.RegularizedGammaQDouble((double)alpha, (double)beta * (double)x);
        return double.IsNaN(q) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(q);
    }

    public static MathResult<decimal> GammaIcdf(decimal p, decimal alpha, decimal beta, ref DslExecutionBudget budget)
    {
        if (p <= 0m || p >= 1m || alpha <= 0m || beta <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double chi = Chi2IcdfDouble((double)p, 2.0 * (double)alpha, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(chi) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(chi / (2.0 * (double)beta));
    }

    public static MathResult<decimal> BetaPdf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x < 0m || x > 1m) return MathResult<decimal>.Ok(0m);
        if ((x == 0m && alpha < 1m) || (x == 1m && beta < 1m)) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m) return alpha == 1m ? MathResult<decimal>.Ok(beta) : MathResult<decimal>.Ok(0m);
        if (x == 1m) return beta == 1m ? MathResult<decimal>.Ok(alpha) : MathResult<decimal>.Ok(0m);

        double da = (double)alpha;
        double db = (double)beta;
        double dx = (double)x;
        double logPdf = (da - 1.0) * Math.Log(dx) + (db - 1.0) * Math.Log(1.0 - dx) - DslSpecialFunctionsMath.LBetaDouble(da, db);
        return DslElementaryMath.NarrowDouble(Math.Exp(logPdf));
    }

    public static MathResult<decimal> BetaLogPdf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m || x < 0m || x > 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 0m) return alpha == 1m ? DslElementaryMath.NarrowDouble(Math.Log((double)beta)) : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x == 1m) return beta == 1m ? DslElementaryMath.NarrowDouble(Math.Log((double)alpha)) : MathResult<decimal>.Fail(MathErrorCode.DomainError);

        double da = (double)alpha;
        double db = (double)beta;
        double dx = (double)x;
        double logPdf = (da - 1.0) * Math.Log(dx) + (db - 1.0) * Math.Log(1.0 - dx) - DslSpecialFunctionsMath.LBetaDouble(da, db);
        return DslElementaryMath.NarrowDouble(logPdf);
    }

    public static MathResult<decimal> BetaCdf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= 0m) return MathResult<decimal>.Ok(0m);
        if (x >= 1m) return MathResult<decimal>.Ok(1m);
        double res = DslSpecialFunctionsMath.IncBetaDouble((double)x, (double)alpha, (double)beta);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> BetaSf(decimal x, decimal alpha, decimal beta)
    {
        if (alpha <= 0m || beta <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= 0m) return MathResult<decimal>.Ok(1m);
        if (x >= 1m) return MathResult<decimal>.Ok(0m);
        double res = DslSpecialFunctionsMath.IncBetaDouble(1.0 - (double)x, (double)beta, (double)alpha);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> BetaIcdf(decimal p, decimal alpha, decimal beta, ref DslExecutionBudget budget)
    {
        if (p < 0m || p > 1m || alpha <= 0m || beta <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (p == 0m) return MathResult<decimal>.Ok(0m);
        if (p == 1m) return MathResult<decimal>.Ok(1m);
        double x = BetaIcdfDouble((double)p, (double)alpha, (double)beta, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(x) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(x);
    }

    internal static double BetaIcdfDouble(double p, double a, double b, ref DslExecutionBudget budget)
    {
        if (a == 1.0 && b == 1.0) return p;
        double lb = DslSpecialFunctionsMath.LBetaDouble(a, b);
        double lo = 0.0;
        double hi = 1.0;
        double x = Math.Clamp(a / (a + b), 1e-8, 1.0 - 1e-8);

        for (int iter = 0; iter < MaxQuantileIterations; iter++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            double cdf = DslSpecialFunctionsMath.IncBetaDouble(x, a, b);
            double diff = cdf - p;
            if (Math.Abs(diff) <= 1e-14) return x;
            if (diff < 0.0) lo = x;
            else hi = x;

            double logPdf = (a - 1.0) * Math.Log(x) + (b - 1.0) * Math.Log(1.0 - x) - lb;
            double pdf = Math.Exp(logPdf);
            double next = pdf > 1e-300 ? x - diff / pdf : 0.5 * (lo + hi);
            if (!(next > lo && next < hi)) next = 0.5 * (lo + hi);
            if (Math.Abs(next - x) <= 1e-14) return next;
            x = next;
        }

        return x;
    }

    public static MathResult<decimal> LaplacePdf(decimal x, decimal mu, decimal b)
    {
        if (b <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = Math.Abs((double)x - (double)mu) / (double)b;
        if (z > 66.0) return MathResult<decimal>.Ok(0m);
        return DslElementaryMath.NarrowDouble(Math.Exp(-z) / (2.0 * (double)b));
    }

    public static MathResult<decimal> LaplaceLogPdf(decimal x, decimal mu, decimal b)
    {
        if (b <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double db = (double)b;
        return DslElementaryMath.NarrowDouble(-Math.Log(2.0 * db) - Math.Abs((double)x - (double)mu) / db);
    }

    public static MathResult<decimal> LaplaceCdf(decimal x, decimal mu, decimal b)
    {
        if (b <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)b;
        return DslElementaryMath.NarrowDouble(z < 0.0 ? 0.5 * Math.Exp(z) : 1.0 - 0.5 * Math.Exp(-z));
    }

    public static MathResult<decimal> LaplaceSf(decimal x, decimal mu, decimal b)
    {
        if (b <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)b;
        return DslElementaryMath.NarrowDouble(z < 0.0 ? 1.0 - 0.5 * Math.Exp(z) : 0.5 * Math.Exp(-z));
    }

    public static MathResult<decimal> LaplaceIcdf(decimal p, decimal mu, decimal b)
    {
        if (b <= 0m || p <= 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (p == 0.5m) return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(mu));
        double dp = (double)p;
        double val = dp < 0.5
            ? (double)mu + (double)b * Math.Log(2.0 * dp)
            : (double)mu - (double)b * Math.Log(2.0 * (1.0 - dp));
        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> CauchyPdf(decimal x, decimal x0, decimal gamma)
    {
        if (gamma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double g = (double)gamma;
        double z = ((double)x - (double)x0) / g;
        return DslElementaryMath.NarrowDouble(1.0 / (Math.PI * g * (1.0 + z * z)));
    }

    public static MathResult<decimal> CauchyLogPdf(decimal x, decimal x0, decimal gamma)
    {
        if (gamma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double g = (double)gamma;
        double z = ((double)x - (double)x0) / g;
        return DslElementaryMath.NarrowDouble(-Math.Log(Math.PI * g) - DslElementaryMath.Log1pDouble(z * z));
    }

    public static MathResult<decimal> CauchyCdf(decimal x, decimal x0, decimal gamma)
    {
        if (gamma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)x0) / (double)gamma;
        double cdf = z < -1.0
            ? Math.Atan(-1.0 / z) / Math.PI
            : z > 1.0
                ? 1.0 - Math.Atan(1.0 / z) / Math.PI
                : 0.5 + Math.Atan(z) / Math.PI;
        return DslElementaryMath.NarrowDouble(cdf);
    }

    public static MathResult<decimal> CauchySf(decimal x, decimal x0, decimal gamma)
    {
        if (gamma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)x0) / (double)gamma;
        double sf = z > 1.0
            ? Math.Atan(1.0 / z) / Math.PI
            : z < -1.0
                ? 1.0 - Math.Atan(-1.0 / z) / Math.PI
                : 0.5 - Math.Atan(z) / Math.PI;
        return DslElementaryMath.NarrowDouble(sf);
    }

    public static MathResult<decimal> CauchyIcdf(decimal p, decimal x0, decimal gamma)
    {
        if (gamma <= 0m || p <= 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (Math.Min(p, 1m - p) < 1e-15m) return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        if (p == 0.5m) return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(x0));
        double cotTerm = p < 0.25m
            ? -1.0 / Math.Tan(Math.PI * (double)p)
            : p > 0.75m
                ? 1.0 / Math.Tan(Math.PI * (double)(1m - p))
                : Math.Tan(Math.PI * ((double)p - 0.5));
        return DslElementaryMath.NarrowDouble((double)x0 + (double)gamma * cotTerm);
    }

    public static MathResult<decimal> LogisticPdf(decimal x, decimal mu, decimal s)
    {
        if (s <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double ds = (double)s;
        double az = Math.Abs(((double)x - (double)mu) / ds);
        if (az > 700.0) return MathResult<decimal>.Ok(0m);
        double e = Math.Exp(-az);
        double opE = 1.0 + e;
        return DslElementaryMath.NarrowDouble(e / (ds * opE * opE));
    }

    public static MathResult<decimal> LogisticLogPdf(decimal x, decimal mu, decimal s)
    {
        if (s <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double ds = (double)s;
        double az = Math.Abs(((double)x - (double)mu) / ds);
        return DslElementaryMath.NarrowDouble(-az - Math.Log(ds) - 2.0 * DslElementaryMath.Log1pDouble(Math.Exp(-az)));
    }

    public static MathResult<decimal> LogisticCdf(decimal x, decimal mu, decimal s)
    {
        if (s <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)s;
        double val = z >= 0.0 ? 1.0 / (1.0 + Math.Exp(-z)) : Math.Exp(z) / (1.0 + Math.Exp(z));
        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> LogisticSf(decimal x, decimal mu, decimal s)
    {
        if (s <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)s;
        double val = z >= 0.0 ? Math.Exp(-z) / (1.0 + Math.Exp(-z)) : 1.0 / (1.0 + Math.Exp(z));
        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> LogisticIcdf(decimal p, decimal mu, decimal s)
    {
        if (s <= 0m || p <= 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double dp = (double)p;
        return DslElementaryMath.NarrowDouble((double)mu + (double)s * Math.Log(dp / (1.0 - dp)));
    }

    public static MathResult<decimal> ParetoPdf(decimal x, decimal xm, decimal alpha)
    {
        if (xm <= 0m || alpha <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x < xm) return MathResult<decimal>.Ok(0m);
        double dx = (double)x;
        double dxm = (double)xm;
        double da = (double)alpha;
        return DslElementaryMath.NarrowDouble(Math.Exp(Math.Log(da) + da * Math.Log(dxm) - (da + 1.0) * Math.Log(dx)));
    }

    public static MathResult<decimal> ParetoLogPdf(decimal x, decimal xm, decimal alpha)
    {
        if (xm <= 0m || alpha <= 0m || x < xm) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double da = (double)alpha;
        return DslElementaryMath.NarrowDouble(Math.Log(da) + da * Math.Log((double)xm) - (da + 1.0) * Math.Log((double)x));
    }

    public static MathResult<decimal> ParetoCdf(decimal x, decimal xm, decimal alpha)
    {
        if (xm <= 0m || alpha <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= xm) return MathResult<decimal>.Ok(0m);
        return DslElementaryMath.NarrowDouble(-DslElementaryMath.Expm1Double((double)alpha * Math.Log((double)xm / (double)x)));
    }

    public static MathResult<decimal> ParetoSf(decimal x, decimal xm, decimal alpha)
    {
        if (xm <= 0m || alpha <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= xm) return MathResult<decimal>.Ok(1m);
        return DslElementaryMath.NarrowDouble(Math.Exp((double)alpha * Math.Log((double)xm / (double)x)));
    }

    public static MathResult<decimal> ParetoIcdf(decimal p, decimal xm, decimal alpha)
    {
        if (xm <= 0m || alpha <= 0m || p < 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (p == 0m) return MathResult<decimal>.Ok(xm);
        return DslElementaryMath.NarrowDouble((double)xm * Math.Exp(-DslElementaryMath.Log1pDouble(-(double)p) / (double)alpha));
    }

    public static MathResult<decimal> LevyPdf(decimal x, decimal mu, decimal c)
    {
        if (c <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= mu + 1e-15m) return MathResult<decimal>.Ok(0m);
        double d = (double)(x - mu);
        double dc = (double)c;
        return DslElementaryMath.NarrowDouble(Math.Sqrt(dc / MathConstants.TwoPi) * Math.Exp(-dc / (2.0 * d)) / Math.Pow(d, 1.5));
    }

    public static MathResult<decimal> LevyLogPdf(decimal x, decimal mu, decimal c)
    {
        if (c <= 0m || x <= mu + 1e-15m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double d = (double)(x - mu);
        double dc = (double)c;
        return DslElementaryMath.NarrowDouble(0.5 * Math.Log(dc / MathConstants.TwoPi) - dc / (2.0 * d) - 1.5 * Math.Log(d));
    }

    public static MathResult<decimal> LevyCdf(decimal x, decimal mu, decimal c)
    {
        if (c <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= mu + 1e-15m) return MathResult<decimal>.Ok(0m);
        return DslElementaryMath.NarrowDouble(SpecialFunctionsMath.Erfc(Math.Sqrt((double)c / (2.0 * (double)(x - mu)))));
    }

    public static MathResult<decimal> LevySf(decimal x, decimal mu, decimal c)
    {
        if (c <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (x <= mu + 1e-15m) return MathResult<decimal>.Ok(1m);
        double arg = Math.Sqrt((double)c / (2.0 * (double)(x - mu)));
        return DslElementaryMath.NarrowDouble(1.0 - SpecialFunctionsMath.Erfc(arg));
    }

    public static MathResult<decimal> LevyIcdf(decimal p, decimal mu, decimal c)
    {
        if (c <= 0m || p <= 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        MathResult<decimal> erfcInv = DslSpecialFunctionsMath.ErfcInv(p);
        if (!erfcInv.IsOk) return erfcInv;
        double e = (double)erfcInv.Value;
        return DslElementaryMath.NarrowDouble((double)mu + (double)c / (2.0 * e * e));
    }

    // ---- 27. Generalized Extreme Value (GEV) ----

    private const double GevShapeThreshold = 1e-8;

    public static MathResult<decimal> GevPdf(decimal x, decimal mu, decimal sigma, decimal xi)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double dsigma = (double)sigma;
        double z = ((double)x - (double)mu) / dsigma;
        double dxi = (double)xi;
        if (Math.Abs(dxi) < GevShapeThreshold)
        {
            if (z < -709.0 || z > 709.0) return MathResult<decimal>.Ok(0m);
            double t = Math.Exp(-z);
            if (t > 750.0) return MathResult<decimal>.Ok(0m);
            return DslElementaryMath.NarrowDouble(t * Math.Exp(-t) / dsigma);
        }

        double xiZ = dxi * z;
        double arg = 1.0 + xiZ;
        if (arg <= 0.0) return MathResult<decimal>.Ok(0m);
        double logArg = Math.Abs(xiZ) < 0.5 ? DslElementaryMath.Log1pDouble(xiZ) : Math.Log(arg);
        double logT = -logArg / dxi;
        double tv = logT > 709.0 ? double.PositiveInfinity : Math.Exp(logT);
        if (tv > 750.0 || double.IsPositiveInfinity(tv)) return MathResult<decimal>.Ok(0m);
        double pdf = Math.Exp(((dxi + 1.0) * logT) - tv) / dsigma;
        return DslElementaryMath.NarrowDouble(pdf);
    }

    public static MathResult<decimal> GevCdf(decimal x, decimal mu, decimal sigma, decimal xi)
    {
        if (sigma <= 0m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double z = ((double)x - (double)mu) / (double)sigma;
        double dxi = (double)xi;
        if (Math.Abs(dxi) < GevShapeThreshold)
        {
            if (z < -709.0) return MathResult<decimal>.Ok(0m);
            double t = Math.Exp(-z);
            return DslElementaryMath.NarrowDouble(Math.Exp(-t));
        }

        double xiZ = dxi * z;
        double arg = 1.0 + xiZ;
        if (arg <= 0.0) return MathResult<decimal>.Ok(dxi > 0.0 ? 0m : 1m);
        double logArg = Math.Abs(xiZ) < 0.5 ? DslElementaryMath.Log1pDouble(xiZ) : Math.Log(arg);
        double logT = -logArg / dxi;
        double tv = logT > 709.0 ? double.PositiveInfinity : Math.Exp(logT);
        return DslElementaryMath.NarrowDouble(double.IsPositiveInfinity(tv) ? 0.0 : Math.Exp(-tv));
    }

    public static MathResult<decimal> GevIcdf(decimal p, decimal mu, decimal sigma, decimal xi)
    {
        if (sigma <= 0m || p <= 0m || p >= 1m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double dmu = (double)mu;
        double dsigma = (double)sigma;
        double dxi = (double)xi;
        double yp = -Math.Log((double)p);
        double res = Math.Abs(dxi) < GevShapeThreshold
            ? dmu - (dsigma * Math.Log(yp))
            : dmu + ((dsigma / dxi) * DslElementaryMath.Expm1Double(-dxi * Math.Log(yp)));
        return DslElementaryMath.NarrowDouble(res);
    }
}
