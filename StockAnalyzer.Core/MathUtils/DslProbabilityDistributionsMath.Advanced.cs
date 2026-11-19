using System;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslProbabilityDistributionsMath
{
    // ---- 8. Non-Central Chi-Square Distribution ----

    public static MathResult<decimal> NcChi2Pdf(decimal x, decimal nu, decimal lambda, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return Chi2Pdf(x, nu);
        if (x == 0m)
        {
            if (nu < 2m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            if (nu == 2m) return DslElementaryMath.NarrowDouble(0.5 * Math.Exp(-0.5 * (double)lambda));
            return MathResult<decimal>.Ok(0m);
        }

        double halfLam = 0.5 * (double)lambda;
        double dx = (double)x;
        double dnu = (double)nu;
        double halfNuM1 = 0.5 * dnu - 1.0;
        double kStartRaw = Math.Floor(0.5 * (Math.Sqrt((double)lambda * dx + halfNuM1 * halfNuM1) - halfNuM1));
        int k0 = kStartRaw > 1_000_000_000.0 ? (int)Math.Floor(halfLam) : Math.Max(0, (int)kStartRaw);

        double logT0 = k0 * Math.Log(halfLam) - halfLam - DslElementaryMath.LogGammaPositive(k0 + 1.0)
            + LogChi2PdfDouble(dx, dnu + 2.0 * k0);
        double t0 = Math.Exp(logT0);
        double total = t0;

        bool convergedUp = false;
        double prevUp = t0;
        double tUp = t0;
        double lamXOver4 = 0.25 * (double)lambda * dx;
        for (int step = 1; step <= MaxSeriesTerms; step++)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            int k = k0 + step;
            tUp *= lamXOver4 / (k * (0.5 * dnu + k - 1.0));
            total += tUp;
            if (tUp <= total * Eps && tUp <= prevUp)
            {
                convergedUp = true;
                break;
            }

            prevUp = tUp;
        }

        bool convergedDn = k0 == 0;
        double prevDn = t0;
        double tDn = t0;
        for (int step = 1; step <= MaxSeriesTerms && !convergedDn; step++)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            int k = k0 - step;
            tDn *= (4.0 * (k + 1.0) * (0.5 * dnu + k)) / ((double)lambda * dx);
            total += tDn;
            if (k == 0 || (tDn <= total * Eps && tDn <= prevDn))
            {
                convergedDn = true;
                break;
            }

            prevDn = tDn;
        }

        if (!convergedUp || !convergedDn)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
        }

        return DslElementaryMath.NarrowDouble(total);
    }

    public static MathResult<decimal> NcChi2LogPdf(decimal x, decimal nu, decimal lambda, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return Chi2LogPdf(x, nu);
        if (x == 0m)
        {
            return nu == 2m
                ? DslElementaryMath.NarrowDouble(Math.Log(0.5) - 0.5 * (double)lambda)
                : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dx = (double)x;
        double dnu = (double)nu;
        double dlam = (double)lambda;
        // Direct Bessel I representation: 0.5 * exp(-(x+lam)/2) * (x/lam)^((nu-2)/4) * I_{nu/2-1}(sqrt(lam*x))
        double order = 0.5 * dnu - 1.0;
        double arg = Math.Sqrt(dlam * dx);
        if (order <= 100.0 && arg <= 100_000.0)
        {
            DslSpecialFunctionsMath.BesselIkWithSign(order, arg, order == Math.Truncate(order), out double ie, out _);
            if (ie > 0.0)
            {
                double logPdf = Math.Log(0.5) - 0.5 * (dx + dlam) + arg + 0.5 * order * Math.Log(dx / dlam) + Math.Log(ie);
                return DslElementaryMath.NarrowDouble(logPdf);
            }
        }

        MathResult<decimal> pdf = NcChi2Pdf(x, nu, lambda, ref budget);
        if (!pdf.IsOk) return pdf;
        return pdf.Value > 0m ? DslElementaryMath.NarrowDouble(Math.Log((double)pdf.Value)) : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> NcChi2Cdf(decimal x, decimal nu, decimal lambda, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return Chi2Cdf(x, nu);
        if (x == 0m) return MathResult<decimal>.Ok(0m);
        double res = NcChi2CdfDouble((double)x, (double)nu, (double)lambda, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NcChi2Sf(decimal x, decimal nu, decimal lambda, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return Chi2Sf(x, nu);
        if (x == 0m) return MathResult<decimal>.Ok(1m);
        double res = NcChi2SfDouble((double)x, (double)nu, (double)lambda, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NcChi2Icdf(decimal p, decimal nu, decimal lambda, ref DslExecutionBudget budget)
    {
        if (p <= 0m || p >= 1m || nu <= 0m || nu > 1_000_000m || lambda < 0m || lambda > 10_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return Chi2Icdf(p, nu, ref budget);

        double dp = (double)p;
        double dnu = (double)nu;
        double dlam = (double)lambda;
        double mean = dnu + dlam;
        double std = Math.Sqrt(2.0 * (dnu + 2.0 * dlam));
        double lo = 0.0;
        double hi = Math.Max(mean + 10.0 * std, 20.0);
        while (true)
        {
            double cdfHi = NcChi2CdfDouble(hi, dnu, dlam, ref budget);
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            if (double.IsNaN(cdfHi)) return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
            if (cdfHi >= dp || hi > 1e12) break;
            hi *= 2.0;
        }

        double x = Math.Clamp(mean + SpecialFunctionsMath.NormalQuantile(dp) * std, 1e-10, hi * 0.99);
        for (int iter = 0; iter < MaxQuantileIterations; iter++)
        {
            double cdf = NcChi2CdfDouble(x, dnu, dlam, ref budget);
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            if (double.IsNaN(cdf)) return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
            double diff = cdf - dp;
            if (Math.Abs(diff) <= 1e-13) return DslElementaryMath.NarrowDouble(x);
            if (diff < 0.0) lo = x;
            else hi = x;
            x = 0.5 * (lo + hi);
            if (hi - lo <= 1e-13 * Math.Max(1.0, x)) return DslElementaryMath.NarrowDouble(x);
        }

        return DslElementaryMath.NarrowDouble(x);
    }

    private static double NcChi2CdfDouble(double x, double nu, double lambda, ref DslExecutionBudget budget)
    {
        double halfLam = 0.5 * lambda;
        int k0 = (int)Math.Floor(halfLam);
        double w0 = PoissonWeight(k0, halfLam);
        double sum = w0 * DslSpecialFunctionsMath.RegularizedGammaPDouble(0.5 * nu + k0, 0.5 * x);
        double wSum = w0;

        bool convergedUp = false;
        double wUp = w0;
        for (int k = k0 + 1; k <= k0 + MaxSeriesTerms; k++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            wUp *= halfLam / k;
            wSum += wUp;
            double pTerm = wUp * DslSpecialFunctionsMath.RegularizedGammaPDouble(0.5 * nu + k, 0.5 * x);
            sum += pTerm;
            if (k > halfLam && wUp * halfLam / (k + 1.0 - halfLam) <= Eps)
            {
                convergedUp = true;
                break;
            }
        }

        bool convergedDn = k0 == 0;
        double wDn = w0;
        for (int step = 1; step <= MaxSeriesTerms && !convergedDn; step++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            int k = k0 - step;
            wDn *= (k + 1) / halfLam;
            wSum += wDn;
            double pTerm = wDn * DslSpecialFunctionsMath.RegularizedGammaPDouble(0.5 * nu + k, 0.5 * x);
            sum += pTerm;
            if (k == 0 || wDn * (k + 1) <= Eps)
            {
                convergedDn = true;
                break;
            }
        }

        if ((!convergedUp || !convergedDn) && Math.Max(0.0, 1.0 - wSum) > 1e-12)
        {
            return double.NaN;
        }

        if (convergedUp && convergedDn && wSum > 0.0)
        {
            sum /= wSum;
        }

        return Math.Clamp(sum, 0.0, 1.0);
    }

    private static double NcChi2SfDouble(double x, double nu, double lambda, ref DslExecutionBudget budget)
    {
        double halfLam = 0.5 * lambda;
        int k0 = (int)Math.Floor(halfLam);
        double w0 = PoissonWeight(k0, halfLam);
        double sum = w0 * DslSpecialFunctionsMath.RegularizedGammaQDouble(0.5 * nu + k0, 0.5 * x);
        double wSum = w0;

        bool convergedUp = false;
        double wUp = w0;
        for (int k = k0 + 1; k <= k0 + MaxSeriesTerms; k++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            wUp *= halfLam / k;
            wSum += wUp;
            sum += wUp * DslSpecialFunctionsMath.RegularizedGammaQDouble(0.5 * nu + k, 0.5 * x);
            if (k > halfLam && wUp * halfLam / (k + 1.0 - halfLam) <= Eps)
            {
                convergedUp = true;
                break;
            }
        }

        bool convergedDn = k0 == 0;
        double wDn = w0;
        for (int step = 1; step <= MaxSeriesTerms && !convergedDn; step++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            int k = k0 - step;
            wDn *= (k + 1) / halfLam;
            wSum += wDn;
            sum += wDn * DslSpecialFunctionsMath.RegularizedGammaQDouble(0.5 * nu + k, 0.5 * x);
            if (k == 0 || wDn * (k + 1) <= Eps)
            {
                convergedDn = true;
                break;
            }
        }

        if ((!convergedUp || !convergedDn) && Math.Max(0.0, 1.0 - wSum) > 1e-12)
        {
            return double.NaN;
        }

        if (convergedUp && convergedDn && wSum > 0.0)
        {
            sum /= wSum;
        }

        return Math.Clamp(sum, 0.0, 1.0);
    }

    private static double LogChi2PdfDouble(double x, double nu)
    {
        double k2 = 0.5 * nu;
        return (k2 - 1.0) * Math.Log(x) - 0.5 * x - k2 * Math.Log(2.0) - DslElementaryMath.LogGammaPositive(k2);
    }

    private static double PoissonWeight(int k, double mean)
        => Math.Exp(k * Math.Log(mean) - mean - DslElementaryMath.LogGammaPositive(k + 1.0));

    // ---- 9. Non-Central Student's t Distribution ----

    public static MathResult<decimal> NctPdf(decimal x, decimal nu, decimal delta, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || Math.Abs(delta) > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (delta == 0m) return StudentTPdf(x, nu);
        if (!budget.TryConsume(20)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return DslElementaryMath.NarrowDouble(NctPdfDouble((double)x, (double)nu, (double)delta));
    }

    public static MathResult<decimal> NctLogPdf(decimal x, decimal nu, decimal delta, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || Math.Abs(delta) > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (delta == 0m) return StudentTLogPdf(x, nu);
        MathResult<decimal> pdf = NctPdf(x, nu, delta, ref budget);
        if (!pdf.IsOk) return pdf;
        return pdf.Value > 0m ? DslElementaryMath.NarrowDouble(Math.Log((double)pdf.Value)) : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> NctCdf(decimal x, decimal nu, decimal delta, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || Math.Abs(delta) > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (delta == 0m) return StudentTCdf(x, nu);
        double res = NctCdfDouble((double)x, (double)nu, (double)delta, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NctSf(decimal x, decimal nu, decimal delta, ref DslExecutionBudget budget)
    {
        if (nu <= 0m || nu > 1_000_000m || Math.Abs(delta) > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (delta == 0m) return StudentTSf(x, nu);
        double res = NctCdfDouble(-(double)x, (double)nu, -(double)delta, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NctIcdf(decimal p, decimal nu, decimal delta, ref DslExecutionBudget budget)
    {
        if (p <= 0m || p >= 1m || nu <= 0m || nu > 1_000_000m || Math.Abs(delta) > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (delta == 0m) return StudentTIcdf(p, nu, ref budget);
        double dp = (double)p;
        double dnu = (double)nu;
        double ddel = (double)delta;
        double x0 = ddel + SpecialFunctionsMath.NormalQuantile(dp);
        double lo = Math.Min(x0 - 10.0, -20.0);
        double hi = Math.Max(x0 + 10.0, 20.0);
        while (NctCdfDouble(lo, dnu, ddel, ref budget) > dp)
        {
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            lo *= 2.0;
        }

        while (NctCdfDouble(hi, dnu, ddel, ref budget) < dp)
        {
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            hi *= 2.0;
        }

        double x = 0.5 * (lo + hi);
        for (int iter = 0; iter < MaxQuantileIterations; iter++)
        {
            double cdf = NctCdfDouble(x, dnu, ddel, ref budget);
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            double diff = cdf - dp;
            if (Math.Abs(diff) <= 1e-13) break;
            if (diff < 0.0) lo = x;
            else hi = x;
            double pdf = NctPdfDouble(x, dnu, ddel);
            double next = pdf > 1e-300 ? x - diff / pdf : 0.5 * (lo + hi);
            if (!(next > lo && next < hi)) next = 0.5 * (lo + hi);
            if (Math.Abs(next - x) <= 1e-13 * Math.Max(1.0, Math.Abs(next)))
            {
                x = next;
                break;
            }

            x = next;
        }

        return DslElementaryMath.NarrowDouble(x);
    }

    private static double NctPdfDouble(double x, double nu, double delta)
    {
        // Log-stabilized Laplace-centered 20-point Gauss-Legendre quadrature over s = sqrt(chi2_nu).
        double logNorm = -0.5 * Math.Log(Math.PI * nu)
            - (0.5 * nu - 1.0) * Math.Log(2.0)
            - DslElementaryMath.LogGammaPositive(0.5 * nu);
        double a = 1.0 + (x * x) / nu;
        double b = (delta * x) / Math.Sqrt(nu);
        double sStar = (b + Math.Sqrt(b * b + 4.0 * a * nu)) / (2.0 * a);
        double sigma = 1.0 / Math.Sqrt(a + nu / (sStar * sStar));
        double lo = Math.Max(0.0, sStar - 10.0 * sigma);
        double hi = sStar + 10.0 * sigma;
        double halfW = 0.5 * (hi - lo);
        double mid = 0.5 * (hi + lo);
        double maxExp = nu * Math.Log(sStar) - 0.5 * sStar * sStar - 0.5 * Math.Pow((x / Math.Sqrt(nu)) * sStar - delta, 2);

        double sum = 0.0;
        for (int i = 0; i < Gl20Nodes.Length; i++)
        {
            double s1 = mid - halfW * Gl20Nodes[i];
            double s2 = mid + halfW * Gl20Nodes[i];
            sum += Gl20Weights[i] * (Integrand(s1) + Integrand(s2));
        }

        return Math.Exp(logNorm + maxExp) * halfW * sum;

        double Integrand(double s)
        {
            if (!(s > 0.0)) return 0.0;
            double z = (x / Math.Sqrt(nu)) * s - delta;
            double lp = nu * Math.Log(s) - 0.5 * s * s - 0.5 * z * z - maxExp;
            return lp < -745.0 ? 0.0 : Math.Exp(lp);
        }
    }

    private static double NctCdfDouble(double x, double nu, double delta, ref DslExecutionBudget budget)
    {
        if (x < 0.0)
        {
            return 1.0 - NctCdfDouble(-x, nu, -delta, ref budget);
        }

        if (x == 0.0)
        {
            return SpecialFunctionsMath.NormalCdf(-delta);
        }

        double y = (x * x) / (x * x + nu);
        double lambda = 0.5 * delta * delta;
        double halfNu = 0.5 * nu;
        int j0 = (int)Math.Floor(lambda);
        double p0 = PoissonWeight(j0, lambda);
        double q0 = (delta / Sqrt2) * Math.Exp(j0 * Math.Log(lambda) - lambda - DslElementaryMath.LogGammaPositive(j0 + 1.5));

        double sum = p0 * DslSpecialFunctionsMath.IncBetaDouble(y, j0 + 0.5, halfNu)
                   + q0 * DslSpecialFunctionsMath.IncBetaDouble(y, j0 + 1.0, halfNu);

        bool convergedUp = false;
        double pUp = p0;
        double qUp = q0;
        for (int j = j0 + 1; j <= j0 + MaxSeriesTerms; j++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            pUp *= lambda / j;
            qUp *= lambda / (j + 0.5);
            double term = pUp * DslSpecialFunctionsMath.IncBetaDouble(y, j + 0.5, halfNu)
                        + qUp * DslSpecialFunctionsMath.IncBetaDouble(y, j + 1.0, halfNu);
            sum += term;
            if (Math.Abs(pUp) + Math.Abs(qUp) <= Eps && j > lambda)
            {
                convergedUp = true;
                break;
            }
        }

        bool convergedDn = j0 == 0;
        double pDn = p0;
        double qDn = q0;
        for (int step = 1; step <= MaxSeriesTerms && !convergedDn; step++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            int j = j0 - step;
            pDn *= (j + 1) / lambda;
            qDn *= (j + 1.5) / lambda;
            double term = pDn * DslSpecialFunctionsMath.IncBetaDouble(y, j + 0.5, halfNu)
                        + qDn * DslSpecialFunctionsMath.IncBetaDouble(y, j + 1.0, halfNu);
            sum += term;
            if (j == 0 || Math.Abs(pDn) + Math.Abs(qDn) <= Eps)
            {
                convergedDn = true;
                break;
            }
        }

        if (!convergedUp || !convergedDn)
        {
            return double.NaN;
        }

        return Math.Clamp(SpecialFunctionsMath.NormalCdf(-delta) + 0.5 * sum, 0.0, 1.0);
    }

    // ---- 10. Non-Central F Distribution ----

    public static MathResult<decimal> NcfPdf(decimal x, decimal d1, decimal d2, decimal lambda, ref DslExecutionBudget budget)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return FPdf(x, d1, d2);
        if (x == 0m)
        {
            if (d1 < 2m) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            return d1 == 2m ? DslElementaryMath.NarrowDouble(Math.Exp(-0.5 * (double)lambda)) : MathResult<decimal>.Ok(0m);
        }

        double halfLam = 0.5 * (double)lambda;
        double dx = (double)x;
        double a = (double)d1;
        double b = (double)d2;
        int k0 = (int)Math.Floor(halfLam);
        double w0 = PoissonWeight(k0, halfLam);
        double total = w0 * FPdfDouble(dx, a + 2.0 * k0, b);

        double wUp = w0;
        for (int k = k0 + 1; k <= k0 + MaxSeriesTerms; k++)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            wUp *= halfLam / k;
            double term = wUp * FPdfDouble(dx, a + 2.0 * k, b);
            total += term;
            if (term <= total * Eps && k > halfLam) break;
        }

        double wDn = w0;
        for (int k = k0 - 1; k >= 0 && (k0 - k) <= MaxSeriesTerms; k--)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            wDn *= (k + 1) / halfLam;
            double term = wDn * FPdfDouble(dx, a + 2.0 * k, b);
            total += term;
            if (term <= total * Eps) break;
        }

        return DslElementaryMath.NarrowDouble(total);
    }

    public static MathResult<decimal> NcfLogPdf(decimal x, decimal d1, decimal d2, decimal lambda, ref DslExecutionBudget budget)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return FLogPdf(x, d1, d2);
        if (x == 0m)
        {
            return d1 == 2m
                ? DslElementaryMath.NarrowDouble(-0.5 * (double)lambda)
                : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        MathResult<decimal> pdf = NcfPdf(x, d1, d2, lambda, ref budget);
        if (!pdf.IsOk) return pdf;
        return pdf.Value > 0m ? DslElementaryMath.NarrowDouble(Math.Log((double)pdf.Value)) : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> NcfCdf(decimal x, decimal d1, decimal d2, decimal lambda, ref DslExecutionBudget budget)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return FCdf(x, d1, d2);
        if (x == 0m) return MathResult<decimal>.Ok(0m);
        double res = NcfCdfDouble((double)x, (double)d1, (double)d2, (double)lambda, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NcfSf(decimal x, decimal d1, decimal d2, decimal lambda, ref DslExecutionBudget budget)
    {
        if (d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || lambda < 0m || lambda > 10_000m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return FSf(x, d1, d2);
        if (x == 0m) return MathResult<decimal>.Ok(1m);
        double res = NcfSfDouble((double)x, (double)d1, (double)d2, (double)lambda, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NcfIcdf(decimal p, decimal d1, decimal d2, decimal lambda, ref DslExecutionBudget budget)
    {
        if (p <= 0m || p >= 1m || d1 <= 0m || d1 > 1_000_000m || d2 <= 0m || d2 > 1_000_000m || lambda < 0m || lambda > 10_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lambda == 0m) return FIcdf(p, d1, d2, ref budget);
        double dp = (double)p;
        double a = (double)d1;
        double b = (double)d2;
        double dlam = (double)lambda;

        double lo = 0.0;
        double hi = Math.Max(10.0, (a + dlam) / a * 4.0);
        while (NcfCdfDouble(hi, a, b, dlam, ref budget) < dp)
        {
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            hi *= 2.0;
            if (hi > 1e12) break;
        }

        double x = 0.5 * (lo + hi);
        for (int iter = 0; iter < MaxQuantileIterations; iter++)
        {
            double cdf = NcfCdfDouble(x, a, b, dlam, ref budget);
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            double diff = cdf - dp;
            if (Math.Abs(diff) <= 1e-12) break;
            if (diff < 0.0) lo = x;
            else hi = x;
            x = 0.5 * (lo + hi);
            if (hi - lo <= 1e-12 * Math.Max(1.0, x)) break;
        }

        return DslElementaryMath.NarrowDouble(x);
    }

    private static double FPdfDouble(double x, double a, double b)
    {
        double logPdf = 0.5 * (a * Math.Log(a) + b * Math.Log(b))
            + (0.5 * a - 1.0) * Math.Log(x)
            - 0.5 * (a + b) * Math.Log(a * x + b)
            - DslSpecialFunctionsMath.LBetaDouble(0.5 * a, 0.5 * b);
        return Math.Exp(logPdf);
    }

    private static double NcfCdfDouble(double x, double a, double b, double lambda, ref DslExecutionBudget budget)
    {
        double u = (a * x) / (a * x + b);
        double halfLam = 0.5 * lambda;
        int k0 = (int)Math.Floor(halfLam);
        double w0 = PoissonWeight(k0, halfLam);
        double sum = w0 * DslSpecialFunctionsMath.IncBetaDouble(u, 0.5 * a + k0, 0.5 * b);
        double wSum = w0;

        bool convergedUp = false;
        double wUp = w0;
        for (int k = k0 + 1; k <= k0 + MaxSeriesTerms; k++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            wUp *= halfLam / k;
            wSum += wUp;
            sum += wUp * DslSpecialFunctionsMath.IncBetaDouble(u, 0.5 * a + k, 0.5 * b);
            if (k > halfLam && wUp * halfLam / (k + 1.0 - halfLam) <= Eps)
            {
                convergedUp = true;
                break;
            }
        }

        bool convergedDn = k0 == 0;
        double wDn = w0;
        for (int step = 1; step <= MaxSeriesTerms && !convergedDn; step++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            int k = k0 - step;
            wDn *= (k + 1) / halfLam;
            wSum += wDn;
            sum += wDn * DslSpecialFunctionsMath.IncBetaDouble(u, 0.5 * a + k, 0.5 * b);
            if (k == 0 || wDn * (k + 1) <= Eps)
            {
                convergedDn = true;
                break;
            }
        }

        if ((!convergedUp || !convergedDn) && Math.Max(0.0, 1.0 - wSum) > 1e-12)
        {
            return double.NaN;
        }

        if (convergedUp && convergedDn && wSum > 0.0)
        {
            sum /= wSum;
        }

        return Math.Clamp(sum, 0.0, 1.0);
    }

    private static double NcfSfDouble(double x, double a, double b, double lambda, ref DslExecutionBudget budget)
    {
        double oneMinusU = b / (a * x + b);
        double halfLam = 0.5 * lambda;
        int k0 = (int)Math.Floor(halfLam);
        double w0 = PoissonWeight(k0, halfLam);
        double sum = w0 * DslSpecialFunctionsMath.IncBetaDouble(oneMinusU, 0.5 * b, 0.5 * a + k0);
        double wSum = w0;

        bool convergedUp = false;
        double wUp = w0;
        for (int k = k0 + 1; k <= k0 + MaxSeriesTerms; k++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            wUp *= halfLam / k;
            wSum += wUp;
            sum += wUp * DslSpecialFunctionsMath.IncBetaDouble(oneMinusU, 0.5 * b, 0.5 * a + k);
            if (k > halfLam && wUp * halfLam / (k + 1.0 - halfLam) <= Eps)
            {
                convergedUp = true;
                break;
            }
        }

        bool convergedDn = k0 == 0;
        double wDn = w0;
        for (int step = 1; step <= MaxSeriesTerms && !convergedDn; step++)
        {
            if (!budget.TryConsume(1)) return double.NaN;
            int k = k0 - step;
            wDn *= (k + 1) / halfLam;
            wSum += wDn;
            sum += wDn * DslSpecialFunctionsMath.IncBetaDouble(oneMinusU, 0.5 * b, 0.5 * a + k);
            if (k == 0 || wDn * (k + 1) <= Eps)
            {
                convergedDn = true;
                break;
            }
        }

        if ((!convergedUp || !convergedDn) && Math.Max(0.0, 1.0 - wSum) > 1e-12)
        {
            return double.NaN;
        }

        if (convergedUp && convergedDn && wSum > 0.0)
        {
            sum /= wSum;
        }

        return Math.Clamp(sum, 0.0, 1.0);
    }

    // ---- 11. Von Mises Distribution ----

    public static MathResult<decimal> VonMisesPdf(decimal x, decimal mu, decimal kappa)
    {
        if (kappa < 0m || kappa > 700m || x < -MathConstants.PiDecimal || x > MathConstants.PiDecimal)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (kappa == 0m) return DslElementaryMath.NarrowDouble(1.0 / MathConstants.TwoPi);
        double dk = (double)kappa;
        DslSpecialFunctionsMath.BesselIkWithSign(0.0, dk, true, out double i0e, out _);
        double diff = (double)x - (double)mu;
        double val = Math.Exp(dk * (Math.Cos(diff) - 1.0)) / (MathConstants.TwoPi * i0e);
        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> VonMisesLogPdf(decimal x, decimal mu, decimal kappa)
    {
        if (kappa < 0m || kappa > 700m || x < -MathConstants.PiDecimal || x > MathConstants.PiDecimal)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (kappa == 0m) return DslElementaryMath.NarrowDouble(-Math.Log(MathConstants.TwoPi));
        double dk = (double)kappa;
        DslSpecialFunctionsMath.BesselIkWithSign(0.0, dk, true, out double i0e, out _);
        double diff = (double)x - (double)mu;
        double logPdf = dk * (Math.Cos(diff) - 1.0) - Math.Log(MathConstants.TwoPi * i0e);
        return DslElementaryMath.NarrowDouble(logPdf);
    }

    public static MathResult<decimal> VonMisesCdf(decimal x, decimal mu, decimal kappa)
    {
        if (kappa < 0m || kappa > 700m || x < -MathConstants.PiDecimal || x > MathConstants.PiDecimal)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == -MathConstants.PiDecimal) return MathResult<decimal>.Ok(0m);
        if (x == MathConstants.PiDecimal) return MathResult<decimal>.Ok(1m);
        if (kappa == 0m) return DslElementaryMath.NarrowDouble(((double)x + Math.PI) / MathConstants.TwoPi);
        return DslElementaryMath.NarrowDouble(VonMisesCdfDouble((double)x, (double)mu, (double)kappa));
    }

    public static MathResult<decimal> VonMisesSf(decimal x, decimal mu, decimal kappa)
    {
        MathResult<decimal> cdf = VonMisesCdf(x, mu, kappa);
        return cdf.IsOk ? MathResult<decimal>.Ok(1m - cdf.Value) : cdf;
    }

    public static MathResult<decimal> VonMisesIcdf(decimal p, decimal mu, decimal kappa, ref DslExecutionBudget budget)
    {
        if (kappa < 0m || kappa > 700m || p < 0m || p > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 0m) return MathResult<decimal>.Ok(-MathConstants.PiDecimal);
        if (p == 1m) return MathResult<decimal>.Ok(MathConstants.PiDecimal);
        double dp = (double)p;
        double dmu = (double)mu;
        double dk = (double)kappa;
        if (kappa == 0m) return DslElementaryMath.NarrowDouble(-Math.PI + dp * MathConstants.TwoPi);

        double lo = -Math.PI;
        double hi = Math.PI;
        double x = 0.0;
        for (int iter = 0; iter < MaxQuantileIterations; iter++)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            double cdf = VonMisesCdfDouble(x, dmu, dk);
            double diff = cdf - dp;
            if (Math.Abs(diff) <= 1e-13) break;
            if (diff < 0.0) lo = x;
            else hi = x;
            x = 0.5 * (lo + hi);
            if (hi - lo <= 1e-13) break;
        }

        return DslElementaryMath.NarrowDouble(x);
    }

    private static double VonMisesCdfDouble(double x, double mu, double kappa)
    {
        // Miller downward recurrence for Bessel ratios I_j(kappa) / I_0(kappa), j = 1..M
        const int maxOrder = 280;
        Span<double> ratios = stackalloc double[maxOrder + 1];
        double r = 0.0;
        double invK = 2.0 / kappa;
        for (int j = maxOrder + 40; j >= 1; j--)
        {
            r = 1.0 / (j * invK + r);
            if (j <= maxOrder)
            {
                ratios[j] = r;
            }
        }

        double prod = 1.0;
        double a1 = x - mu;
        double a0 = -Math.PI - mu;
        double series = 0.0;
        for (int j = 1; j <= maxOrder; j++)
        {
            prod *= ratios[j];
            if (prod < Eps) break;
            series += (prod / j) * (Math.Sin(j * a1) - Math.Sin(j * a0));
        }

        double cdf = (x + Math.PI) / MathConstants.TwoPi + series / Math.PI;
        return Math.Clamp(cdf, 0.0, 1.0);
    }

    // ---- 12. Discrete Distributions: Poisson, Binomial, Negative Binomial, Geometric, Hypergeometric ----

    public static MathResult<decimal> PoissonPmf(decimal k, decimal lambda)
    {
        if (lambda <= 0m || lambda > 1_000_000_000m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dk = (double)k;
        double dl = (double)lambda;
        double logPmf = dk * Math.Log(dl) - dl - DslElementaryMath.LogGammaPositive(dk + 1.0);
        return DslElementaryMath.NarrowDouble(Math.Exp(logPmf));
    }

    public static MathResult<decimal> PoissonLogPmf(decimal k, decimal lambda)
    {
        if (lambda <= 0m || lambda > 1_000_000_000m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dk = (double)k;
        double dl = (double)lambda;
        return DslElementaryMath.NarrowDouble(dk * Math.Log(dl) - dl - DslElementaryMath.LogGammaPositive(dk + 1.0));
    }

    public static MathResult<decimal> PoissonCdf(decimal k, decimal lambda)
    {
        if (lambda <= 0m || lambda > 1_000_000_000m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double q = DslSpecialFunctionsMath.RegularizedGammaQDouble((double)k + 1.0, (double)lambda);
        return double.IsNaN(q) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(q);
    }

    public static MathResult<decimal> PoissonSf(decimal k, decimal lambda)
    {
        if (lambda <= 0m || lambda > 1_000_000_000m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double p = DslSpecialFunctionsMath.RegularizedGammaPDouble((double)k + 1.0, (double)lambda);
        return double.IsNaN(p) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(p);
    }

    private static double AdjustDiscreteLowerTailProb(decimal p, double relGuard)
    {
        double dp = (double)p;
        // Compensate both double ULP loss and fixed-point decimal quantization (<= 0.5e-28)
        // when inverting tiny CDF outputs below 1e-14 while keeping the guard strictly positive.
        return Math.Max(dp * 0.5, dp * (1.0 - relGuard) - 0.5e-28);
    }

    public static MathResult<decimal> PoissonIcdf(decimal p, decimal lambda, ref DslExecutionBudget budget)
    {
        if (lambda <= 0m || lambda > 1_000_000_000m || p < 0m || p >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 0m) return MathResult<decimal>.Ok(0m);
        double dp = AdjustDiscreteLowerTailProb(p, 1e-14);
        double dl = (double)lambda;
        long lo = 0;
        long kGuess = Math.Max(0L, (long)Math.Floor(dl + SpecialFunctionsMath.NormalQuantile(dp) * Math.Sqrt(dl)));
        long hi = Math.Max(1L, kGuess + 16L);
        while (DslSpecialFunctionsMath.RegularizedGammaQDouble(hi + 1.0, dl) < dp)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            lo = hi + 1;
            hi = Math.Max(hi * 2, hi + 16);
        }

        while (lo < hi)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            long mid = lo + (hi - lo) / 2;
            if (DslSpecialFunctionsMath.RegularizedGammaQDouble(mid + 1.0, dl) >= dp) hi = mid;
            else lo = mid + 1;
        }

        return MathResult<decimal>.Ok(lo);
    }

    public static MathResult<decimal> BinomPmf(decimal k, decimal n, decimal p)
    {
        if (n != decimal.Truncate(n) || n < 1m || n > 10_000_000m || p < 0m || p > 1m
            || k != decimal.Truncate(k) || k < 0m || k > n)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 0m) return MathResult<decimal>.Ok(k == 0m ? 1m : 0m);
        if (p == 1m) return MathResult<decimal>.Ok(k == n ? 1m : 0m);

        double dn = (double)n;
        double dk = (double)k;
        double dp = (double)p;
        double lc = DslElementaryMath.LCombDouble((long)n, (long)k);
        return DslElementaryMath.NarrowDouble(Math.Exp(lc + dk * Math.Log(dp) + (dn - dk) * DslElementaryMath.Log1pDouble(-dp)));
    }

    public static MathResult<decimal> BinomLogPmf(decimal k, decimal n, decimal p)
    {
        if (n != decimal.Truncate(n) || n < 1m || n > 10_000_000m || p < 0m || p > 1m
            || k != decimal.Truncate(k) || k < 0m || k > n)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if ((p == 0m && k > 0m) || (p == 1m && k < n)) return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        if (p == 0m || p == 1m) return MathResult<decimal>.Ok(0m);

        double dn = (double)n;
        double dk = (double)k;
        double dp = (double)p;
        double lc = DslElementaryMath.LCombDouble((long)n, (long)k);
        return DslElementaryMath.NarrowDouble(lc + dk * Math.Log(dp) + (dn - dk) * DslElementaryMath.Log1pDouble(-dp));
    }

    public static MathResult<decimal> BinomCdf(decimal k, decimal n, decimal p)
    {
        if (n != decimal.Truncate(n) || n < 1m || n > 10_000_000m || p < 0m || p > 1m
            || k != decimal.Truncate(k) || k < 0m || k > n)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (k == n) return MathResult<decimal>.Ok(1m);
        if (p == 0m) return MathResult<decimal>.Ok(1m);
        if (p == 1m) return MathResult<decimal>.Ok(0m);
        double res = DslSpecialFunctionsMath.IncBetaDouble(1.0 - (double)p, (double)(n - k), (double)k + 1.0);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> BinomSf(decimal k, decimal n, decimal p)
    {
        if (n != decimal.Truncate(n) || n < 1m || n > 10_000_000m || p < 0m || p > 1m
            || k != decimal.Truncate(k) || k < 0m || k > n)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (k == n) return MathResult<decimal>.Ok(0m);
        if (p == 0m) return MathResult<decimal>.Ok(0m);
        if (p == 1m) return MathResult<decimal>.Ok(1m);
        double res = DslSpecialFunctionsMath.IncBetaDouble((double)p, (double)k + 1.0, (double)(n - k));
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> BinomIcdf(decimal prob, decimal n, decimal p, ref DslExecutionBudget budget)
    {
        if (n != decimal.Truncate(n) || n < 1m || n > 10_000_000m || p < 0m || p > 1m || prob < 0m || prob > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (prob == 0m || p == 0m) return MathResult<decimal>.Ok(0m);
        if (prob == 1m || p == 1m) return MathResult<decimal>.Ok(n);

        double dp = AdjustDiscreteLowerTailProb(prob, 1e-14);
        double dProb = (double)p;
        long lo = 0;
        long hi = (long)n;
        while (lo < hi)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            long mid = lo + (hi - lo) / 2;
            double cdf = mid == (long)n
                ? 1.0
                : DslSpecialFunctionsMath.IncBetaDouble(1.0 - dProb, (long)n - mid, mid + 1.0);
            if (cdf >= dp) hi = mid;
            else lo = mid + 1;
        }

        return MathResult<decimal>.Ok(lo);
    }

    public static MathResult<decimal> NBinomPmf(decimal k, decimal r, decimal p)
    {
        if (r <= 0m || r > 10_000_000m || p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return MathResult<decimal>.Ok(k == 0m ? 1m : 0m);
        double dk = (double)k;
        double dr = (double)r;
        double dp = (double)p;
        double logPmf = DslElementaryMath.LogGammaPositive(dk + dr)
            - DslElementaryMath.LogGammaPositive(dk + 1.0)
            - DslSpecialFunctionsMath.LogAbsGammaDouble(dr, out _)
            + dr * Math.Log(dp)
            + dk * DslElementaryMath.Log1pDouble(-dp);
        return DslElementaryMath.NarrowDouble(Math.Exp(logPmf));
    }

    public static MathResult<decimal> NBinomLogPmf(decimal k, decimal r, decimal p)
    {
        if (r <= 0m || r > 10_000_000m || p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return k == 0m ? MathResult<decimal>.Ok(0m) : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double dk = (double)k;
        double dr = (double)r;
        double dp = (double)p;
        double logPmf = DslElementaryMath.LogGammaPositive(dk + dr)
            - DslElementaryMath.LogGammaPositive(dk + 1.0)
            - DslSpecialFunctionsMath.LogAbsGammaDouble(dr, out _)
            + dr * Math.Log(dp)
            + dk * DslElementaryMath.Log1pDouble(-dp);
        return DslElementaryMath.NarrowDouble(logPmf);
    }

    public static MathResult<decimal> NBinomCdf(decimal k, decimal r, decimal p)
    {
        if (r <= 0m || r > 10_000_000m || p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return MathResult<decimal>.Ok(1m);
        double res = DslSpecialFunctionsMath.IncBetaDouble((double)p, (double)r, (double)k + 1.0);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NBinomSf(decimal k, decimal r, decimal p)
    {
        if (r <= 0m || r > 10_000_000m || p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return MathResult<decimal>.Ok(0m);
        double res = DslSpecialFunctionsMath.IncBetaDouble(1.0 - (double)p, (double)k + 1.0, (double)r);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> NBinomIcdf(decimal prob, decimal r, decimal p, ref DslExecutionBudget budget)
    {
        if (r <= 0m || r > 10_000_000m || p <= 0m || p > 1m || prob < 0m || prob >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (prob == 0m || p == 1m) return MathResult<decimal>.Ok(0m);
        double dp = AdjustDiscreteLowerTailProb(prob, 1e-14);
        double dr = (double)r;
        double dProb = (double)p;
        long lo = 0;
        long hi = Math.Max(16L, (long)Math.Ceiling(dr * (1.0 - dProb) / dProb * 2.0));
        while (DslSpecialFunctionsMath.IncBetaDouble(dProb, dr, hi + 1.0) < dp)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            lo = hi + 1;
            hi *= 2;
        }

        while (lo < hi)
        {
            if (!budget.TryConsume(1)) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            long mid = lo + (hi - lo) / 2;
            if (DslSpecialFunctionsMath.IncBetaDouble(dProb, dr, mid + 1.0) >= dp) hi = mid;
            else lo = mid + 1;
        }

        return MathResult<decimal>.Ok(lo);
    }

    public static MathResult<decimal> GeomPmf(decimal k, decimal p)
    {
        if (p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return MathResult<decimal>.Ok(k == 1m ? 1m : 0m);
        double dp = (double)p;
        return DslElementaryMath.NarrowDouble(Math.Exp(Math.Log(dp) + ((double)k - 1.0) * DslElementaryMath.Log1pDouble(-dp)));
    }

    public static MathResult<decimal> GeomLogPmf(decimal k, decimal p)
    {
        if (p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return k == 1m ? MathResult<decimal>.Ok(0m) : MathResult<decimal>.Fail(MathErrorCode.DomainError);
        double dp = (double)p;
        return DslElementaryMath.NarrowDouble(Math.Log(dp) + ((double)k - 1.0) * DslElementaryMath.Log1pDouble(-dp));
    }

    public static MathResult<decimal> GeomCdf(decimal k, decimal p)
    {
        if (p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return MathResult<decimal>.Ok(1m);
        double dp = (double)p;
        return DslElementaryMath.NarrowDouble(-DslElementaryMath.Expm1Double((double)k * DslElementaryMath.Log1pDouble(-dp)));
    }

    public static MathResult<decimal> GeomSf(decimal k, decimal p)
    {
        if (p <= 0m || p > 1m || k != decimal.Truncate(k) || k < 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 1m) return MathResult<decimal>.Ok(0m);
        double dp = (double)p;
        return DslElementaryMath.NarrowDouble(Math.Exp((double)k * DslElementaryMath.Log1pDouble(-dp)));
    }

    public static MathResult<decimal> GeomIcdf(decimal prob, decimal p)
    {
        if (p <= 0m || p > 1m || prob < 0m || prob >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (prob == 0m || p == 1m) return MathResult<decimal>.Ok(1m);
        double dp = AdjustDiscreteLowerTailProb(prob, 1e-14);
        double k = Math.Ceiling(DslElementaryMath.Log1pDouble(-dp) / DslElementaryMath.Log1pDouble(-(double)p));
        return DslElementaryMath.NarrowDouble(Math.Max(1.0, k));
    }

    public static MathResult<decimal> HypergeomPmf(decimal k, decimal popN, decimal succK, decimal drawsN)
    {
        if (!TryValidateHypergeom(k, popN, succK, drawsN, out long lk, out long lN, out long lK, out long ln, out _, out _))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(HypergeomPmfDouble(lk, lN, lK, ln));
    }

    public static MathResult<decimal> HypergeomLogPmf(decimal k, decimal popN, decimal succK, decimal drawsN)
    {
        if (!TryValidateHypergeom(k, popN, succK, drawsN, out long lk, out long lN, out long lK, out long ln, out _, out _))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(HypergeomLogPmfDouble(lk, lN, lK, ln));
    }

    public static MathResult<decimal> HypergeomCdf(decimal k, decimal popN, decimal succK, decimal drawsN, ref DslExecutionBudget budget)
    {
        if (!TryValidateHypergeom(k, popN, succK, drawsN, out long lk, out long lN, out long lK, out long ln, out long kMin, out long kMax))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double res = HypergeomCdfDouble(lk, lN, lK, ln, kMin, kMax, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> HypergeomSf(decimal k, decimal popN, decimal succK, decimal drawsN, ref DslExecutionBudget budget)
    {
        if (!TryValidateHypergeom(k, popN, succK, drawsN, out long lk, out long lN, out long lK, out long ln, out long kMin, out long kMax))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double res = HypergeomSfDouble(lk, lN, lK, ln, kMin, kMax, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return double.IsNaN(res) ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure) : DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> HypergeomIcdf(decimal prob, decimal popN, decimal succK, decimal drawsN, ref DslExecutionBudget budget)
    {
        if (popN != decimal.Truncate(popN) || succK != decimal.Truncate(succK) || drawsN != decimal.Truncate(drawsN)
            || popN < 1m || popN > 10_000_000m || succK < 0m || succK > popN || drawsN < 0m || drawsN > popN
            || prob < 0m || prob > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        long lN = (long)popN;
        long lK = (long)succK;
        long ln = (long)drawsN;
        long kMin = Math.Max(0L, ln + lK - lN);
        long kMax = Math.Min(ln, lK);
        if (prob == 0m) return MathResult<decimal>.Ok(kMin);
        if (prob == 1m) return MathResult<decimal>.Ok(kMax);

        double dp = AdjustDiscreteLowerTailProb(prob, 1e-13);
        long lo = kMin;
        long hi = kMax;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            double cdf = HypergeomCdfDouble(mid, lN, lK, ln, kMin, kMax, ref budget);
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            if (double.IsNaN(cdf)) return MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure);
            if (cdf >= dp) hi = mid;
            else lo = mid + 1;
        }

        return MathResult<decimal>.Ok(lo);
    }

    private static bool TryValidateHypergeom(
        decimal k,
        decimal popN,
        decimal succK,
        decimal drawsN,
        out long lk,
        out long lN,
        out long lK,
        out long ln,
        out long kMin,
        out long kMax)
    {
        lk = lN = lK = ln = kMin = kMax = 0;
        if (popN != decimal.Truncate(popN) || succK != decimal.Truncate(succK) || drawsN != decimal.Truncate(drawsN)
            || k != decimal.Truncate(k) || popN < 1m || popN > 10_000_000m
            || succK < 0m || succK > popN || drawsN < 0m || drawsN > popN)
        {
            return false;
        }

        lN = (long)popN;
        lK = (long)succK;
        ln = (long)drawsN;
        kMin = Math.Max(0L, ln + lK - lN);
        kMax = Math.Min(ln, lK);
        if (k < kMin || k > kMax) return false;
        lk = (long)k;
        return true;
    }

    private static double HypergeomLogPmfDouble(long k, long N, long K, long n)
        => LogBinom(K, k) + LogBinom(N - K, n - k) - LogBinom(N, n);

    private static double HypergeomPmfDouble(long k, long N, long K, long n)
        => Math.Exp(HypergeomLogPmfDouble(k, N, K, n));

    private static double LogBinom(long n, long k)
        => k <= 0 || k >= n ? 0.0 : DslElementaryMath.LCombDouble(n, k);

    private static double HypergeomCdfDouble(long k, long N, long K, long n, long kMin, long kMax, ref DslExecutionBudget budget)
    {
        if (k >= kMax) return 1.0;
        long mode = (long)Math.Floor(((n + 1.0) * (K + 1.0)) / (N + 2.0));
        if (k < mode)
        {
            return HypergeomTailSum(k, kMin, step: -1, N, K, n, ref budget);
        }

        double upper = HypergeomTailSum(k + 1, kMax, step: 1, N, K, n, ref budget);
        return double.IsNaN(upper) ? double.NaN : Math.Clamp(1.0 - upper, 0.0, 1.0);
    }

    private static double HypergeomSfDouble(long k, long N, long K, long n, long kMin, long kMax, ref DslExecutionBudget budget)
    {
        if (k >= kMax) return 0.0;
        long mode = (long)Math.Floor(((n + 1.0) * (K + 1.0)) / (N + 2.0));
        if (k >= mode)
        {
            return HypergeomTailSum(k + 1, kMax, step: 1, N, K, n, ref budget);
        }

        double lower = HypergeomTailSum(k, kMin, step: -1, N, K, n, ref budget);
        return double.IsNaN(lower) ? double.NaN : Math.Clamp(1.0 - lower, 0.0, 1.0);
    }

    private static double HypergeomTailSum(long start, long end, int step, long N, long K, long n, ref DslExecutionBudget budget)
    {
        double term = HypergeomPmfDouble(start, N, K, n);
        double sum = term;
        int count = 0;
        for (long i = start; i != end; i += step)
        {
            if (++count > MaxHypergeomTerms) return double.NaN;
            if (!budget.TryConsume(1)) return double.NaN;
            if (step > 0)
            {
                term *= (double)(K - i) * (n - i) / ((i + 1.0) * (N - K - n + i + 1.0));
            }
            else
            {
                term *= (double)i * (N - K - n + i) / ((K - i + 1.0) * (n - i + 1.0));
            }

            sum += term;
            if (term <= sum * Eps) break;
        }

        return Math.Clamp(sum, 0.0, 1.0);
    }

    // ---- 13. Studentized Range (Tukey q) Distribution ----

    public static MathResult<decimal> TukeyQPdf(decimal q, decimal kGroups, decimal nu, ref DslExecutionBudget budget)
    {
        if (kGroups != decimal.Truncate(kGroups) || kGroups < 2m || kGroups > 100m
            || nu < 1m || nu > 1_000_000m || q < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (q == 0m) return MathResult<decimal>.Ok(0m);
        double res = TukeyQPdfDouble((double)q, (int)kGroups, (double)nu, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> TukeyQLogPdf(decimal q, decimal kGroups, decimal nu, ref DslExecutionBudget budget)
    {
        if (kGroups != decimal.Truncate(kGroups) || kGroups < 2m || kGroups > 100m
            || nu < 1m || nu > 1_000_000m || q <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double res = TukeyQPdfDouble((double)q, (int)kGroups, (double)nu, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return res > 0.0 ? DslElementaryMath.NarrowDouble(Math.Log(res)) : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> TukeyQCdf(decimal q, decimal kGroups, decimal nu, ref DslExecutionBudget budget)
    {
        if (kGroups != decimal.Truncate(kGroups) || kGroups < 2m || kGroups > 100m
            || nu < 1m || nu > 1_000_000m || q < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (q == 0m) return MathResult<decimal>.Ok(0m);
        double res = TukeyQCdfDouble((double)q, (int)kGroups, (double)nu, ref budget);
        if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
        return DslElementaryMath.NarrowDouble(res);
    }

    public static MathResult<decimal> TukeyQSf(decimal q, decimal kGroups, decimal nu, ref DslExecutionBudget budget)
    {
        MathResult<decimal> cdf = TukeyQCdf(q, kGroups, nu, ref budget);
        return cdf.IsOk ? MathResult<decimal>.Ok(1m - cdf.Value) : cdf;
    }

    public static MathResult<decimal> TukeyQIcdf(decimal p, decimal kGroups, decimal nu, ref DslExecutionBudget budget)
    {
        if (kGroups != decimal.Truncate(kGroups) || kGroups < 2m || kGroups > 100m
            || nu < 1m || nu > 1_000_000m || p < 0m || p >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (p == 0m) return MathResult<decimal>.Ok(0m);
        double dp = (double)p;
        int k = (int)kGroups;
        double dnu = (double)nu;

        double lo = 0.0;
        double hi = 8.0;
        while (TukeyQCdfDouble(hi, k, dnu, ref budget) < dp)
        {
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            lo = hi;
            hi *= 2.0;
            if (hi > 1e6) break;
        }

        double q = 0.5 * (lo + hi);
        for (int iter = 0; iter < 64; iter++)
        {
            double cdf = TukeyQCdfDouble(q, k, dnu, ref budget);
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            double diff = cdf - dp;
            if (Math.Abs(diff) <= 1e-11) break;
            if (diff < 0.0) lo = q;
            else hi = q;
            double pdf = TukeyQPdfDouble(q, k, dnu, ref budget);
            if (budget.LastError == MathErrorCode.BudgetExceeded) return MathResult<decimal>.Fail(MathErrorCode.BudgetExceeded);
            double next = pdf > 1e-300 ? q - diff / pdf : 0.5 * (lo + hi);
            if (!(next > lo && next < hi)) next = 0.5 * (lo + hi);
            if (Math.Abs(next - q) <= 1e-11 * Math.Max(1.0, next))
            {
                q = next;
                break;
            }

            q = next;
        }

        return DslElementaryMath.NarrowDouble(q);
    }

    private static double TukeyQCdfInf(double q, int k)
    {
        // Integrate over [-9, 9] using 20-point GL on 2 subintervals [-9, 0] and [0, 9] (40 points)
        return IntegrateInf(-9.0, 0.0) + IntegrateInf(0.0, 9.0);

        double IntegrateInf(double a, double b)
        {
            double mid = 0.5 * (a + b);
            double half = 0.5 * (b - a);
            double sum = 0.0;
            for (int i = 0; i < Gl20Nodes.Length; i++)
            {
                sum += Gl20Weights[i] * (Integrand(mid - half * Gl20Nodes[i]) + Integrand(mid + half * Gl20Nodes[i]));
            }

            return half * sum;
        }

        double Integrand(double z)
        {
            double phi = Math.Exp(-0.5 * z * z) / Sqrt2Pi;
            double diff = Math.Max(0.0, SpecialFunctionsMath.NormalCdf(z + q) - SpecialFunctionsMath.NormalCdf(z));
            return k * phi * Math.Pow(diff, k - 1);
        }
    }

    private static double TukeyQPdfInf(double q, int k)
    {
        return IntegrateInf(-9.0, 0.0) + IntegrateInf(0.0, 9.0);

        double IntegrateInf(double a, double b)
        {
            double mid = 0.5 * (a + b);
            double half = 0.5 * (b - a);
            double sum = 0.0;
            for (int i = 0; i < Gl20Nodes.Length; i++)
            {
                sum += Gl20Weights[i] * (Integrand(mid - half * Gl20Nodes[i]) + Integrand(mid + half * Gl20Nodes[i]));
            }

            return half * sum;
        }

        double Integrand(double z)
        {
            double phi1 = Math.Exp(-0.5 * z * z) / Sqrt2Pi;
            double phi2 = Math.Exp(-0.5 * (z + q) * (z + q)) / Sqrt2Pi;
            double diff = Math.Max(0.0, SpecialFunctionsMath.NormalCdf(z + q) - SpecialFunctionsMath.NormalCdf(z));
            return k * (k - 1) * phi1 * phi2 * (k == 2 ? 1.0 : Math.Pow(diff, k - 2));
        }
    }

    private static double TukeyQCdfDouble(double q, int k, double nu, ref DslExecutionBudget budget)
    {
        if (nu > 100_000.0)
        {
            if (!budget.TryConsume(20)) return double.NaN;
            return Math.Clamp(TukeyQCdfInf(q, k), 0.0, 1.0);
        }

        double halfNu = 0.5 * nu;
        double logNorm = Math.Log(2.0) + halfNu * Math.Log(halfNu) - DslElementaryMath.LogGammaPositive(halfNu);
        double sMode = nu > 1.0 ? Math.Sqrt((nu - 1.0) / nu) : 0.0;
        double sigma = 1.0 / Math.Sqrt(2.0 * nu);
        double lo = Math.Max(0.0, sMode - 9.0 * sigma);
        double hi = nu <= 4.0 ? 8.0 : sMode + 9.0 * sigma;
        double mid = 0.5 * (lo + hi);
        double half = 0.5 * (hi - lo);

        double sum = 0.0;
        for (int i = 0; i < Gl20Nodes.Length; i++)
        {
            if (!budget.TryConsume(20)) return double.NaN;
            double s1 = mid - half * Gl20Nodes[i];
            double s2 = mid + half * Gl20Nodes[i];
            sum += Gl20Weights[i] * (OuterIntegrand(s1) + OuterIntegrand(s2));
        }

        return Math.Clamp(half * sum, 0.0, 1.0);

        double OuterIntegrand(double s)
        {
            if (!(s > 0.0)) return 0.0;
            double lg = logNorm + (nu - 1.0) * Math.Log(s) - 0.5 * nu * s * s;
            if (lg < -745.0) return 0.0;
            return Math.Exp(lg) * TukeyQCdfInf(q * s, k);
        }
    }

    private static double TukeyQPdfDouble(double q, int k, double nu, ref DslExecutionBudget budget)
    {
        if (nu > 100_000.0)
        {
            if (!budget.TryConsume(20)) return double.NaN;
            return Math.Max(0.0, TukeyQPdfInf(q, k));
        }

        double halfNu = 0.5 * nu;
        double logNorm = Math.Log(2.0) + halfNu * Math.Log(halfNu) - DslElementaryMath.LogGammaPositive(halfNu);
        double sMode = nu > 1.0 ? Math.Sqrt((nu - 1.0) / nu) : 0.0;
        double sigma = 1.0 / Math.Sqrt(2.0 * nu);
        double lo = Math.Max(0.0, sMode - 9.0 * sigma);
        double hi = nu <= 4.0 ? 8.0 : sMode + 9.0 * sigma;
        double mid = 0.5 * (lo + hi);
        double half = 0.5 * (hi - lo);

        double sum = 0.0;
        for (int i = 0; i < Gl20Nodes.Length; i++)
        {
            if (!budget.TryConsume(20)) return double.NaN;
            double s1 = mid - half * Gl20Nodes[i];
            double s2 = mid + half * Gl20Nodes[i];
            sum += Gl20Weights[i] * (OuterIntegrand(s1) + OuterIntegrand(s2));
        }

        return Math.Max(0.0, half * sum);

        double OuterIntegrand(double s)
        {
            if (!(s > 0.0)) return 0.0;
            double lg = logNorm + nu * Math.Log(s) - 0.5 * nu * s * s;
            if (lg < -745.0) return 0.0;
            return Math.Exp(lg) * TukeyQPdfInf(q * s, k);
        }
    }
}
