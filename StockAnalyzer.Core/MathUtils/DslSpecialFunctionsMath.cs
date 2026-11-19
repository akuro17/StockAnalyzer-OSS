using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation special mathematical functions (23 groups) for StockAnalyzer DSL.
/// Delegates <see cref="SpecialFunctionsMath"/> (<c>Erfc</c>, <c>NormalCdf</c>, <c>NormalQuantile</c>, <c>RegularizedGammaQ</c>)
/// and <see cref="FresnelMath"/> (<c>CosineIntegral</c>, <c>SineIntegral</c>) without modifying those SSoT classes.
/// </summary>
public static partial class DslSpecialFunctionsMath
{
    private const double SqrtPi = 1.7724538509055160273;
    private const double TwoOverSqrtPi = 1.1283791670955125739;
    private const double Sqrt2 = 1.4142135623730950488;
    private const double Tiny = 1e-300;
    private const double Eps = 1e-15;
    private const int MaxSeriesIterations = 5000;

    // Exact Bernoulli numbers B_0..B_44 for decimal return (B_{2k+1} = 0 for k >= 1).
    private static readonly (decimal Num, decimal Den)[] EvenBernoulliRationals =
    {
        (1m, 1m),                                  // B_0
        (1m, 6m),                                  // B_2
        (-1m, 30m),                                // B_4
        (1m, 42m),                                 // B_6
        (-1m, 30m),                                // B_8
        (5m, 66m),                                 // B_10
        (-691m, 2730m),                            // B_12
        (7m, 6m),                                  // B_14
        (-3617m, 510m),                            // B_16
        (43867m, 798m),                            // B_18
        (-174611m, 330m),                          // B_20
        (854513m, 138m),                           // B_22
        (-236364091m, 2730m),                      // B_24
        (8553103m, 6m),                            // B_26
        (-23749461029m, 870m),                     // B_28
        (8615841276005m, 14322m),                  // B_30
        (-7709321041217m, 510m),                   // B_32
        (2577687858367m, 6m),                      // B_34
        (-26315271553053477373m, 1919190m),        // B_36
        (2929993913841559m, 6m),                   // B_38
        (-261082718496449122051m, 13530m),         // B_40
        (1520097643918070802691m, 1806m),          // B_42
        (-27833269579301024235023m, 690m),         // B_44
    };

    private static readonly double[] BernoulliEvenDouble =
    {
        1.0,
        1.0 / 6.0,
        -1.0 / 30.0,
        1.0 / 42.0,
        -1.0 / 30.0,
        5.0 / 66.0,
        -691.0 / 2730.0,
        7.0 / 6.0,
        -3617.0 / 510.0,
        43867.0 / 798.0,
        -174611.0 / 330.0,
        854513.0 / 138.0,
    };

    // ---- 1. Gamma, LogAbsGamma, Digamma, Incomplete Gamma ----

    public static MathResult<decimal> LogAbsGamma(decimal x, out int sign)
    {
        sign = 1;
        if (x <= 0m && x == decimal.Truncate(x))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double lg;
        if (x < 0m)
        {
            decimal floorX = decimal.Floor(x);
            decimal rawFrac = x - floorX;
            decimal dist = rawFrac <= 0.5m ? rawFrac : 1m - rawFrac;
            sign = (floorX % 2m == 0m) ? 1 : -1;
            lg = Math.Log(Math.PI / Math.Sin(Math.PI * (double)dist)) - DslElementaryMath.LogGammaPositive(1.0 - (double)x);
        }
        else if (x < 0.5m)
        {
            double dx = (double)x;
            lg = Math.Log(Math.PI / Math.Sin(Math.PI * dx)) - DslElementaryMath.LogGammaPositive(1.0 - dx);
        }
        else
        {
            lg = DslElementaryMath.LogGammaPositive((double)x);
        }

        return DslElementaryMath.NarrowDouble(lg);
    }

    internal static double LogAbsGammaDouble(double x, out int sign)
    {
        if (x >= 0.5)
        {
            sign = 1;
            return DslElementaryMath.LogGammaPositive(x);
        }

        if (x <= 0.0 && x == Math.Truncate(x))
        {
            sign = 0;
            return double.NaN;
        }

        double floorX = Math.Floor(x);
        double frac = x - floorX;
        sign = (Math.IEEERemainder(floorX, 2.0) == 0.0) ? 1 : -1;
        return Math.Log(Math.PI / Math.Sin(Math.PI * frac)) - DslElementaryMath.LogGammaPositive(1.0 - x);
    }

    public static MathResult<decimal> Gamma(decimal x)
    {
        if (x <= 0m && x == decimal.Truncate(x))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x >= 1m && x <= 28m && x == decimal.Truncate(x))
        {
            return DslElementaryMath.Fact(x - 1m);
        }

        if (x > 172m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        MathResult<decimal> lg = LogAbsGamma(x, out int sign);
        if (!lg.IsOk)
        {
            return lg;
        }

        double val = sign * Math.Exp((double)lg.Value);
        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> Digamma(decimal x)
    {
        if (x <= 0m && x == decimal.Truncate(x))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double result = 0.0;
        double z;
        if (x < 0m)
        {
            decimal floorX = decimal.Floor(x);
            decimal rawFrac = x - floorX;
            decimal dist = rawFrac <= 0.5m ? rawFrac : 1m - rawFrac;
            double cotTerm = Math.PI / Math.Tan(Math.PI * (double)dist);
            result = rawFrac <= 0.5m ? -cotTerm : cotTerm;
            z = 1.0 - (double)x;
        }
        else
        {
            z = (double)x;
        }

        while (z < 8.0)
        {
            result -= 1.0 / z;
            z += 1.0;
        }

        double invZ = 1.0 / z;
        double invZ2 = invZ * invZ;
        result += Math.Log(z) - 0.5 * invZ
            - invZ2 * (1.0 / 12.0
                - invZ2 * (1.0 / 120.0
                    - invZ2 * (1.0 / 252.0
                        - invZ2 * (1.0 / 240.0
                            - invZ2 * (1.0 / 132.0
                                - invZ2 * (691.0 / 32760.0 - invZ2 / 12.0))))));
        return DslElementaryMath.NarrowDouble(result);
    }

    public static MathResult<decimal> IncGammaP(decimal a, decimal x)
    {
        if (a <= 0m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double p = RegularizedGammaPDouble((double)a, (double)x);
        return double.IsNaN(p)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(p);
    }

    public static MathResult<decimal> IncGammaQ(decimal a, decimal x)
    {
        if (a <= 0m || x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double q = RegularizedGammaQDouble((double)a, (double)x);
        return double.IsNaN(q)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(q);
    }

    internal static double RegularizedGammaPDouble(double a, double x)
    {
        if (!(a > 0.0) || x < 0.0 || double.IsNaN(a) || double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x == 0.0)
        {
            return 0.0;
        }

        if (double.IsPositiveInfinity(x))
        {
            return 1.0;
        }

        if (x < a + 1.0)
        {
            return RegularizedGammaPSeries(a, x);
        }

        double q = RegularizedGammaQDouble(a, x);
        return double.IsNaN(q) ? double.NaN : 1.0 - q;
    }

    internal static double RegularizedGammaQDouble(double a, double x)
    {
        double q = SpecialFunctionsMath.RegularizedGammaQ(a, x);
        if (!double.IsNaN(q))
        {
            return q;
        }

        if (!(a > 0.0) || x < 0.0 || double.IsNaN(a) || double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x < a + 1.0)
        {
            double p = RegularizedGammaPSeries(a, x);
            return double.IsNaN(p) ? double.NaN : 1.0 - p;
        }

        return RegularizedGammaQCf(a, x);
    }

    private static double RegularizedGammaPSeries(double a, double x)
    {
        double logPrefactor = -x + a * Math.Log(x) - DslElementaryMath.LogGammaPositive(a);
        double term = 1.0 / a;
        double sum = term;
        for (int n = 1; n <= MaxSeriesIterations; n++)
        {
            term *= x / (a + n);
            sum += term;
            if (Math.Abs(term) <= Math.Abs(sum) * Eps)
            {
                return Math.Clamp(sum * Math.Exp(logPrefactor), 0.0, 1.0);
            }
        }

        return double.NaN;
    }

    private static double RegularizedGammaQCf(double a, double x)
    {
        double logPrefactor = -x + a * Math.Log(x) - DslElementaryMath.LogGammaPositive(a);
        double b = x + 1.0 - a;
        double c = 1.0 / Tiny;
        double d = Math.Abs(b) < Tiny ? 1.0 / Tiny : 1.0 / b;
        double h = d;
        for (int i = 1; i <= MaxSeriesIterations; i++)
        {
            double an = -i * (i - a);
            b += 2.0;
            d = an * d + b;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = b + an / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1.0 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1.0) <= Eps)
            {
                return Math.Clamp(Math.Exp(logPrefactor) * h, 0.0, 1.0);
            }
        }

        return double.NaN;
    }

    // ---- 2. Beta & Regularized Incomplete Beta ----

    public static MathResult<decimal> LBeta(decimal a, decimal b)
    {
        if (a <= 0m || b <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(LBetaDouble((double)a, (double)b));
    }

    public static MathResult<decimal> Beta(decimal a, decimal b)
    {
        if (a <= 0m || b <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(Math.Exp(LBetaDouble((double)a, (double)b)));
    }

    public static MathResult<decimal> IncBeta(decimal x, decimal a, decimal b)
    {
        if (x < 0m || x > 1m || a <= 0m || b <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double res = IncBetaDouble((double)x, (double)a, (double)b);
        return double.IsNaN(res)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(res);
    }

    internal static double LBetaDouble(double a, double b)
        => LogAbsGammaDouble(a, out _) + LogAbsGammaDouble(b, out _) - LogAbsGammaDouble(a + b, out _);

    internal static double IncBetaDouble(double x, double a, double b)
    {
        if (double.IsNaN(x) || double.IsNaN(a) || double.IsNaN(b) || x < 0.0 || x > 1.0 || !(a > 0.0) || !(b > 0.0))
        {
            return double.NaN;
        }

        if (x == 0.0) return 0.0;
        if (x == 1.0) return 1.0;

        double bt = Math.Exp(LogAbsGammaDouble(a + b, out _)
            - LogAbsGammaDouble(a, out _)
            - LogAbsGammaDouble(b, out _)
            + a * Math.Log(x)
            + b * Math.Log(1.0 - x));

        if (x < (a + 1.0) / (a + b + 2.0))
        {
            double cf = BetaContinuedFraction(a, b, x);
            return double.IsNaN(cf) ? double.NaN : Math.Clamp(bt * cf / a, 0.0, 1.0);
        }
        else
        {
            double cf = BetaContinuedFraction(b, a, 1.0 - x);
            return double.IsNaN(cf) ? double.NaN : Math.Clamp(1.0 - bt * cf / b, 0.0, 1.0);
        }
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        double qab = a + b;
        double qap = a + 1.0;
        double qam = a - 1.0;
        double c = 1.0;
        double d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < Tiny) d = Tiny;
        d = 1.0 / d;
        double h = d;

        for (int m = 1; m <= MaxSeriesIterations; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1.0 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1.0) <= Eps)
            {
                return h;
            }
        }

        return double.NaN;
    }

    // ---- 3. Zeta Functions ----

    public static MathResult<decimal> Zeta(decimal s)
    {
        if (s == 1m || Math.Abs(s) > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (s == 0m)
        {
            return MathResult<decimal>.Ok(-0.5m);
        }

        if (s < 0m && s == decimal.Truncate(s) && s % 2m == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double ds = (double)s;
        if (ds < 0.0)
        {
            double oneMinusS = 1.0 - ds;
            double z = HurwitzZetaDouble(oneMinusS, 1.0);
            double lg = LogAbsGammaDouble(oneMinusS, out _);
            double sinVal = Math.Sin(0.5 * Math.PI * ds);
            double raw = Math.Pow(2.0, ds) * Math.Pow(Math.PI, ds - 1.0) * sinVal * Math.Exp(lg) * z;
            return DslElementaryMath.NarrowDouble(raw);
        }

        return DslElementaryMath.NarrowDouble(HurwitzZetaDouble(ds, 1.0));
    }

    public static MathResult<decimal> HurwitzZeta(decimal s, decimal q)
    {
        if (s == 1m || q <= 0m || Math.Abs(s) > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (s == 0m)
        {
            return MathResult<decimal>.Ok(0.5m - q);
        }

        double res = HurwitzZetaDouble((double)s, (double)q);
        return double.IsNaN(res)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(res);
    }

    private static double HurwitzZetaDouble(double s, double q)
    {
        if (s == 0.0)
        {
            return 0.5 - q;
        }

        if (s < 0.0)
        {
            return HurwitzZetaNegativeDouble(s, q);
        }

        return HurwitzZetaEulerMaclaurin(s, q, nShift: 16);
    }

    private static double HurwitzZetaNegativeDouble(double s, double q)
    {
        // For moderate q <= 64, shift q -> q' in (0, 1] by subtracting exact finite terms:
        // zeta(s, q) = zeta(s, q') - sum_{j=0}^{m-1} (j + q')^(-s).
        if (q <= 64.0)
        {
            int shiftM = q > 1.0 ? (int)Math.Ceiling(q) - 1 : 0;
            double qRed = q - shiftM;
            double baseVal;

            if (qRed == 1.0)
            {
                baseVal = RiemannZetaNegativeDouble(s);
            }
            else if (qRed == 0.5)
            {
                baseVal = (Math.Pow(2.0, s) - 1.0) * RiemannZetaNegativeDouble(s);
            }
            else if (s > -5.0)
            {
                // Bounded nShift=8 keeps 8^|s| < 3.3e4 so Euler-Maclaurin retains >= 11 digits.
                baseVal = HurwitzZetaEulerMaclaurin(s, qRed, nShift: 8);
            }
            else
            {
                // DLMF 25.11.18 Hurwitz Fourier series converges rapidly for s <= -5 and q' in (0, 1).
                baseVal = HurwitzZetaFourierFunctional(s, qRed);
            }

            if (double.IsNaN(baseVal) || shiftM == 0)
            {
                return baseVal;
            }

            double shiftSum = 0.0;
            for (int j = 0; j < shiftM; j++)
            {
                shiftSum += Math.Pow(qRed + j, -s);
            }

            return baseVal - shiftSum;
        }

        // For large q > 64 and s < 0, direct N=0 Euler-Maclaurin has no catastrophic cancellation
        // when |s| is small relative to 2*pi*q; reject asymptotic divergence rather than returning noise.
        if (Math.Abs(s) <= 2.0 * Math.PI * q * 0.5)
        {
            return HurwitzZetaEulerMaclaurin(s, q, nShift: 0);
        }

        return double.NaN;
    }

    private static double RiemannZetaNegativeDouble(double s)
    {
        double rem = Math.IEEERemainder(s, 2.0);
        if (rem == 0.0 && s < 0.0)
        {
            return 0.0;
        }

        double oneMinusS = 1.0 - s;
        double z = HurwitzZetaEulerMaclaurin(oneMinusS, 1.0, nShift: 16);
        double lg = DslElementaryMath.LogGammaPositive(oneMinusS);
        double sinVal = Math.Sin(0.5 * Math.PI * s);
        return Math.Pow(2.0, s) * Math.Pow(Math.PI, s - 1.0) * sinVal * Math.Exp(lg) * z;
    }

    private static double HurwitzZetaFourierFunctional(double s, double qPrime)
    {
        double oneMinusS = 1.0 - s;
        double prefactor = Math.Exp(
            Math.Log(2.0)
            + DslElementaryMath.LogGammaPositive(oneMinusS)
            + (s - 1.0) * Math.Log(MathConstants.TwoPi));

        double rMod = Math.IEEERemainder(s, 4.0);
        double sinHalf;
        double cosHalf;
        if (rMod == 0.0)
        {
            sinHalf = 0.0;
            cosHalf = 1.0;
        }
        else if (rMod == 1.0 || rMod == -3.0)
        {
            sinHalf = 1.0;
            cosHalf = 0.0;
        }
        else if (Math.Abs(rMod) == 2.0)
        {
            sinHalf = 0.0;
            cosHalf = -1.0;
        }
        else if (rMod == -1.0 || rMod == 3.0)
        {
            sinHalf = -1.0;
            cosHalf = 0.0;
        }
        else
        {
            sinHalf = Math.Sin(0.5 * Math.PI * s);
            cosHalf = Math.Cos(0.5 * Math.PI * s);
        }

        double twoPiQ = MathConstants.TwoPi * qPrime;
        double sumCos = 0.0;
        double sumSin = 0.0;
        for (int n = 1; n <= MaxSeriesIterations; n++)
        {
            double invPow = Math.Pow(n, -oneMinusS);
            double ang = twoPiQ * n;
            double termCos = Math.Cos(ang) * invPow;
            double termSin = Math.Sin(ang) * invPow;
            sumCos += termCos;
            sumSin += termSin;

            if (invPow <= Eps * Math.Max(1.0, Math.Abs(sinHalf * sumCos + cosHalf * sumSin)))
            {
                return prefactor * (sinHalf * sumCos + cosHalf * sumSin);
            }
        }

        return double.NaN;
    }

    private static double HurwitzZetaEulerMaclaurin(double s, double q, int nShift)
    {
        double sum = 0.0;
        for (int k = 0; k < nShift; k++)
        {
            sum += Math.Pow(q + k, -s);
        }

        double qn = q + nShift;
        double qnPowMinusS = Math.Pow(qn, -s);
        sum += qnPowMinusS * qn / (s - 1.0) + 0.5 * qnPowMinusS;

        double invQn2 = 1.0 / (qn * qn);
        double prod = s * qnPowMinusS / (2.0 * qn);
        double termAcc = BernoulliEvenDouble[1] * prod;
        sum += termAcc;

        for (int k = 2; k <= 11; k++)
        {
            int m2 = 2 * k;
            prod *= (s + m2 - 3.0) * (s + m2 - 2.0) / ((m2 - 1.0) * m2) * invQn2;
            double term = BernoulliEvenDouble[k] * prod;
            sum += term;
            if (Math.Abs(term) <= Math.Abs(sum) * Eps)
            {
                break;
            }
        }

        return sum;
    }

    // ---- 4. Error Functions ----

    public static MathResult<decimal> Erf(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double dx = (double)x;
        if (Math.Abs(dx) < 1e-15)
        {
            return DslElementaryMath.NarrowDouble(TwoOverSqrtPi * dx);
        }

        double val = dx >= 0.0 ? 1.0 - SpecialFunctionsMath.Erfc(dx) : SpecialFunctionsMath.Erfc(-dx) - 1.0;
        return DslElementaryMath.NarrowDouble(val);
    }

    public static MathResult<decimal> Erfc(decimal x)
        => DslElementaryMath.NarrowDouble(SpecialFunctionsMath.Erfc((double)x));

    public static MathResult<decimal> Erfcx(decimal x)
    {
        double dx = (double)x;
        if (dx <= 26.5)
        {
            return DslElementaryMath.NarrowDouble(Math.Exp(dx * dx) * SpecialFunctionsMath.Erfc(dx));
        }

        // Asymptotic continued fraction / series for large positive x where exp(x^2) overflows double.
        double inv2x2 = 0.5 / (dx * dx);
        double term = 1.0;
        double sum = 1.0;
        for (int k = 1; k <= 30; k++)
        {
            term *= -(2 * k - 1) * inv2x2;
            sum += term;
            if (Math.Abs(term) <= Math.Abs(sum) * Eps)
            {
                break;
            }
        }

        return DslElementaryMath.NarrowDouble(sum / (SqrtPi * dx));
    }

    public static MathResult<decimal> Erfi(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double dx = (double)x;
        double ax = Math.Abs(dx);
        if (ax > 9.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        double raw;
        if (ax <= 4.0)
        {
            double x2 = dx * dx;
            double term = dx;
            double sum = term;
            for (int k = 1; k <= 80; k++)
            {
                term *= x2 / k;
                double add = term / (2 * k + 1);
                sum += add;
                if (Math.Abs(add) <= Math.Abs(sum) * Eps)
                {
                    break;
                }
            }

            raw = TwoOverSqrtPi * sum;
        }
        else
        {
            // Asymptotic expansion: erfi(x) ~ exp(x^2) / (sqrt(pi) * x) * sum (2k-1)!! / (2x^2)^k
            double inv2x2 = 0.5 / (dx * dx);
            double term = 1.0;
            double sum = 1.0;
            double prevAbs = 1.0;
            for (int k = 1; k <= 30; k++)
            {
                term *= (2 * k - 1) * inv2x2;
                double curAbs = Math.Abs(term);
                if (curAbs > prevAbs)
                {
                    break;
                }

                prevAbs = curAbs;
                sum += term;
                if (curAbs <= Math.Abs(sum) * Eps)
                {
                    break;
                }
            }

            raw = (Math.Exp(dx * dx) / (SqrtPi * dx)) * sum;
        }

        return DslElementaryMath.NarrowDouble(raw);
    }

    public static MathResult<decimal> ErfInv(decimal y)
    {
        if (y <= -1m || y >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (y == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double dy = (double)y;
        double ay = Math.Abs(dy);
        double x = ay < 0.75
            ? SpecialFunctionsMath.NormalQuantile(0.5 + 0.5 * ay) / Sqrt2
            : -SpecialFunctionsMath.NormalQuantile(0.5 * (1.0 - ay)) / Sqrt2;

        // 2 Halley refinements
        for (int i = 0; i < 2; i++)
        {
            double f = ay < 0.75
                ? (1.0 - SpecialFunctionsMath.Erfc(x)) - ay
                : (1.0 - ay) - SpecialFunctionsMath.Erfc(x);
            double deriv = TwoOverSqrtPi * Math.Exp(-x * x);
            double u = f / deriv;
            x -= u / (1.0 + x * u);
        }

        return DslElementaryMath.NarrowDouble(dy < 0.0 ? -x : x);
    }

    public static MathResult<decimal> ErfcInv(decimal y)
    {
        if (y <= 0m || y >= 2m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (y == 1m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double dy = (double)y;
        double x = -SpecialFunctionsMath.NormalQuantile(0.5 * dy) / Sqrt2;
        for (int i = 0; i < 2; i++)
        {
            double f = SpecialFunctionsMath.Erfc(x) - dy;
            double deriv = -TwoOverSqrtPi * Math.Exp(-x * x);
            double u = f / deriv;
            x -= u / (1.0 + x * u);
        }

        return DslElementaryMath.NarrowDouble(x);
    }

    // ---- 5. Legendre, Bernoulli, Stirling, Pochhammer, Brillouin, Langevin ----

    public static MathResult<decimal> LegendreP(decimal l, decimal x)
    {
        if (l != decimal.Truncate(l) || l < 0m || l > 100m || x < -1m || x > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int n = (int)l;
        if (n == 0) return MathResult<decimal>.Ok(1m);
        if (n == 1) return MathResult<decimal>.Ok(x);

        double dx = (double)x;
        double p0 = 1.0;
        double p1 = dx;
        for (int k = 2; k <= n; k++)
        {
            double pk = ((2.0 * k - 1.0) * dx * p1 - (k - 1.0) * p0) / k;
            p0 = p1;
            p1 = pk;
        }

        return DslElementaryMath.NarrowDouble(p1);
    }

    public static MathResult<decimal> LegendreQ(decimal l, decimal x)
    {
        if (l != decimal.Truncate(l) || l < 0m || l > 100m || x <= -1m || x >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int n = (int)l;
        double dx = (double)x;
        double q0 = 0.5 * Math.Log((1.0 + dx) / (1.0 - dx));
        if (n == 0) return DslElementaryMath.NarrowDouble(q0);

        double q1 = dx * q0 - 1.0;
        if (n == 1) return DslElementaryMath.NarrowDouble(q1);

        for (int k = 2; k <= n; k++)
        {
            double qk = ((2.0 * k - 1.0) * dx * q1 - (k - 1.0) * q0) / k;
            q0 = q1;
            q1 = qk;
        }

        return DslElementaryMath.NarrowDouble(q1);
    }

    public static MathResult<decimal> AssocLegendre(decimal l, decimal m, decimal x)
    {
        if (l != decimal.Truncate(l) || m != decimal.Truncate(m)
            || l < 0m || l > 100m || m < 0m || m > l || x < -1m || x > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int li = (int)l;
        int mi = (int)m;
        double dx = (double)x;

        double pmm = 1.0;
        if (mi > 0)
        {
            double somx2 = Math.Sqrt((1.0 - dx) * (1.0 + dx));
            double fact = 1.0;
            for (int i = 1; i <= mi; i++)
            {
                pmm *= -fact * somx2;
                fact += 2.0;
            }
        }

        if (li == mi)
        {
            return DslElementaryMath.NarrowDouble(pmm);
        }

        double pmmp1 = dx * (2 * mi + 1) * pmm;
        if (li == mi + 1)
        {
            return DslElementaryMath.NarrowDouble(pmmp1);
        }

        double pll = 0.0;
        for (int ll = mi + 2; ll <= li; ll++)
        {
            pll = (dx * (2 * ll - 1) * pmmp1 - (ll + mi - 1) * pmm) / (ll - mi);
            pmm = pmmp1;
            pmmp1 = pll;
        }

        return DslElementaryMath.NarrowDouble(pll);
    }

    public static MathResult<decimal> Bernoulli(decimal n)
    {
        if (n != decimal.Truncate(n) || n < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (n == 1m)
        {
            return MathResult<decimal>.Ok(-0.5m);
        }

        if (n % 2m != 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (n > 44m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        int idx = (int)n / 2;
        (decimal num, decimal den) = EvenBernoulliRationals[idx];
        return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(num / den));
    }

    public static MathResult<decimal> Stirling1(decimal n, decimal k)
    {
        if (n != decimal.Truncate(n) || k != decimal.Truncate(k) || k < 0m || n < k || n > 27m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        int ki = (int)k;
        if (ni == 0 && ki == 0) return MathResult<decimal>.Ok(1m);
        if (ki == 0) return MathResult<decimal>.Ok(0m);
        if (ki == ni) return MathResult<decimal>.Ok(1m);

        Span<decimal> dp = stackalloc decimal[ki + 1];
        dp[0] = 1m;
        try
        {
            for (int i = 1; i <= ni; i++)
            {
                int maxJ = Math.Min(i, ki);
                for (int j = maxJ; j >= 1; j--)
                {
                    dp[j] = checked(dp[j - 1] - (i - 1) * dp[j]);
                }

                dp[0] = 0m;
            }

            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(dp[ki]));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Stirling2(decimal n, decimal k)
    {
        if (n != decimal.Truncate(n) || k != decimal.Truncate(k) || k < 0m || n < k || n > 27m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        int ki = (int)k;
        if (ni == 0 && ki == 0) return MathResult<decimal>.Ok(1m);
        if (ki == 0) return MathResult<decimal>.Ok(0m);
        if (ki == ni || ki == 1) return MathResult<decimal>.Ok(1m);

        Span<decimal> dp = stackalloc decimal[ki + 1];
        dp[0] = 1m;
        try
        {
            for (int i = 1; i <= ni; i++)
            {
                int maxJ = Math.Min(i, ki);
                for (int j = maxJ; j >= 1; j--)
                {
                    dp[j] = checked(dp[j - 1] + j * dp[j]);
                }

                dp[0] = 0m;
            }

            return MathResult<decimal>.Ok(dp[ki]);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Pochhammer(decimal x, decimal n)
    {
        if (n == 0m)
        {
            return MathResult<decimal>.Ok(1m);
        }

        if (n == decimal.Truncate(n) && n > 0m && n <= 100m)
        {
            int count = (int)n;
            try
            {
                decimal acc = 1m;
                for (int i = 0; i < count; i++)
                {
                    acc = checked(acc * (x + i));
                }

                return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(acc));
            }
            catch (OverflowException)
            {
                return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            }
        }

        if (n > 100m || n < -100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        decimal xPlusN = x + n;
        bool xIsNonPosInt = x <= 0m && x == decimal.Truncate(x);
        bool xPlusNIsNonPosInt = xPlusN <= 0m && xPlusN == decimal.Truncate(xPlusN);

        if (xPlusNIsNonPosInt && !xIsNonPosInt)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (xIsNonPosInt && !xPlusNIsNonPosInt)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (xIsNonPosInt && xPlusNIsNonPosInt)
        {
            // Both x and x+n are non-positive integers: ratio of poles (-1)^n * (-x)! / (-(x+n))!
            int k = (int)(-x);
            int m = (int)(-xPlusN);
            double lgRatio = DslElementaryMath.LogGammaPositive(k + 1.0) - DslElementaryMath.LogGammaPositive(m + 1.0);
            int sign = (((int)n) & 1) == 0 ? 1 : -1;
            return DslElementaryMath.NarrowDouble(sign * Math.Exp(lgRatio));
        }

        MathResult<decimal> lgNum = LogAbsGamma(xPlusN, out int signNum);
        if (!lgNum.IsOk) return lgNum;
        MathResult<decimal> lgDen = LogAbsGamma(x, out int signDen);
        if (!lgDen.IsOk) return lgDen;

        double raw = (signNum * signDen) * Math.Exp((double)lgNum.Value - (double)lgDen.Value);
        return DslElementaryMath.NarrowDouble(raw);
    }

    public static MathResult<decimal> Brillouin(decimal j, decimal x)
    {
        if (j <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (x > 500m) return MathResult<decimal>.Ok(1m);
        if (x < -500m) return MathResult<decimal>.Ok(-1m);

        double dj = (double)j;
        double dx = (double)x;
        double a = (2.0 * dj + 1.0) / (2.0 * dj);
        double b = 1.0 / (2.0 * dj);

        if (Math.Abs(dx) < 1e-4)
        {
            double x2 = dx * dx;
            double c1 = (dj + 1.0) / (3.0 * dj);
            double c3 = (a * a * a * a - b * b * b * b) / 45.0;
            double c5 = 2.0 * (Math.Pow(a, 6) - Math.Pow(b, 6)) / 945.0;
            return DslElementaryMath.NarrowDouble(dx * (c1 - x2 * (c3 - c5 * x2)));
        }

        double ax = a * dx;
        double bx = b * dx;
        double cothA = Math.Abs(ax) > 20.0 ? Math.CopySign(1.0, ax) : 1.0 / Math.Tanh(ax);
        double cothB = Math.Abs(bx) > 20.0 ? Math.CopySign(1.0, bx) : 1.0 / Math.Tanh(bx);
        return DslElementaryMath.NarrowDouble(a * cothA - b * cothB);
    }

    public static MathResult<decimal> Langevin(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (x > 500m) return MathResult<decimal>.Ok(1m);
        if (x < -500m) return MathResult<decimal>.Ok(-1m);

        double dx = (double)x;
        if (Math.Abs(dx) < 1e-4)
        {
            double x2 = dx * dx;
            return DslElementaryMath.NarrowDouble(dx * (1.0 / 3.0 - x2 * (1.0 / 45.0 - 2.0 * x2 / 945.0)));
        }

        double coth = Math.Abs(dx) > 20.0 ? Math.CopySign(1.0, dx) : 1.0 / Math.Tanh(dx);
        return DslElementaryMath.NarrowDouble(coth - 1.0 / dx);
    }

    // ---- 6. Integral & Fresnel Functions ----

    public static MathResult<decimal> ExpIntEi(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x < 0m)
        {
            double e1 = ExpIntE1Double(-(double)x);
            return double.IsNaN(e1)
                ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
                : DslElementaryMath.NarrowDouble(-e1);
        }

        if (x > 70m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        double dx = (double)x;
        return DslElementaryMath.NarrowDouble(ExpIntEiPositiveDouble(dx));
    }

    private static double ExpIntEiPositiveDouble(double dx)
    {
        if (dx <= 40.0)
        {
            double sum = 0.0;
            double fact = 1.0;
            for (int k = 1; k <= 200; k++)
            {
                fact *= dx / k;
                double term = fact / k;
                sum += term;
                if (Math.Abs(term) <= Math.Abs(sum) * Eps)
                {
                    break;
                }
            }

            return MathConstants.EulerGamma + Math.Log(dx) + sum;
        }

        double sumAsym = 1.0;
        double termAsym = 1.0;
        for (int k = 1; k <= 30; k++)
        {
            double prev = Math.Abs(termAsym);
            termAsym *= k / dx;
            if (Math.Abs(termAsym) > prev) break;
            sumAsym += termAsym;
            if (Math.Abs(termAsym) <= Math.Abs(sumAsym) * Eps) break;
        }

        return Math.Exp(dx) / dx * sumAsym;
    }

    public static MathResult<decimal> ExpIntE1(decimal x)
    {
        if (x <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double e1 = ExpIntE1Double((double)x);
        return double.IsNaN(e1)
            ? MathResult<decimal>.Fail(MathErrorCode.ConvergenceFailure)
            : DslElementaryMath.NarrowDouble(e1);
    }

    private static double ExpIntE1Double(double x)
    {
        if (x > 710.0)
        {
            return 0.0;
        }

        if (x <= 1.0)
        {
            double sum = 0.0;
            double fact = 1.0;
            for (int k = 1; k <= 100; k++)
            {
                fact *= -x / k;
                double term = fact / k;
                sum += term;
                if (Math.Abs(term) <= Math.Abs(sum) * Eps)
                {
                    break;
                }
            }

            return -MathConstants.EulerGamma - Math.Log(x) - sum;
        }

        // Modified Lentz continued fraction for E_1(x)
        double b = x + 1.0;
        double c = 1.0 / Tiny;
        double d = 1.0 / b;
        double h = d;
        for (int i = 1; i <= 500; i++)
        {
            double a = -i * i;
            b += 2.0;
            d = 1.0 / (a * d + b);
            c = b + a / c;
            double delta = c * d;
            h *= delta;
            if (Math.Abs(delta - 1.0) <= Eps)
            {
                return h * Math.Exp(-x);
            }
        }

        return double.NaN;
    }

    public static MathResult<decimal> SinIntSi(decimal x)
    {
        if (Math.Abs(x) > 100_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        ComputeSiCi((double)Math.Abs(x), out double si, out _);
        return DslElementaryMath.NarrowDouble(x < 0m ? -si : si);
    }

    public static MathResult<decimal> CosIntCi(decimal x)
    {
        if (x <= 0m || x > 100_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        ComputeSiCi((double)x, out _, out double ci);
        return DslElementaryMath.NarrowDouble(ci);
    }

    private static void ComputeSiCi(double x, out double si, out double ci)
    {
        if (x <= 4.0)
        {
            double x2 = -x * x;
            double sumSi = x;
            double termSi = x;
            double sumCi = 0.0;
            double termCi = 1.0;
            for (int k = 1; k <= 60; k++)
            {
                termSi *= x2 / ((2 * k) * (2 * k + 1));
                double addSi = termSi / (2 * k + 1);
                sumSi += addSi;

                termCi *= x2 / ((2 * k - 1) * (2 * k));
                double addCi = termCi / (2 * k);
                sumCi += addCi;

                if (Math.Abs(addSi) <= Math.Abs(sumSi) * Eps && Math.Abs(addCi) <= (Math.Abs(sumCi) + 1.0) * Eps)
                {
                    break;
                }
            }

            si = sumSi;
            ci = MathConstants.EulerGamma + Math.Log(x) + sumCi;
            return;
        }

        // Auxiliary functions f(x), g(x) via continued fraction for x > 4 (Numerical Recipes cisi):
        // f(x) + i*g(x) = e^{ix} * E_1(ix) -> modified Lentz in complex arithmetic.
        double bRe = 1.0;
        double bIm = x;
        double cRe = 1.0 / Tiny;
        double cIm = 0.0;
        double invB = 1.0 / (bRe * bRe + bIm * bIm);
        double dRe = bRe * invB;
        double dIm = -bIm * invB;
        double hRe = dRe;
        double hIm = dIm;

        for (int i = 2; i <= 500; i++)
        {
            double a = -(i - 1) * (i - 1);
            bRe += 2.0;

            double adRe = a * dRe + bRe;
            double adIm = a * dIm + bIm;
            double invAd = 1.0 / (adRe * adRe + adIm * adIm);
            dRe = adRe * invAd;
            dIm = -adIm * invAd;

            double invC = 1.0 / (cRe * cRe + cIm * cIm);
            cRe = bRe + a * cRe * invC;
            cIm = bIm - a * cIm * invC;

            double delRe = dRe * cRe - dIm * cIm;
            double delIm = dRe * cIm + dIm * cRe;

            double nextHRe = hRe * delRe - hIm * delIm;
            double nextHIm = hRe * delIm + hIm * delRe;
            hRe = nextHRe;
            hIm = nextHIm;

            if (Math.Abs(delRe - 1.0) + Math.Abs(delIm) <= Eps)
            {
                break;
            }
        }

        // e^{-ix} * (hRe + i*hIm) = Ci - i * (pi/2 - Si) -> Wait: NR uses 1/(x - i*...) so:
        // Actually h = 1 / (1 + i x - ...) = e^{i x} E_1(i x) = g(x) + i f(x)
        // Wait: at large x, 1 / (1 + ix) = (1 - ix)/(1 + x^2) ~ -i/x, so Re(h) ~ 1/x^2 = g(x), Im(h) ~ -1/x = -f(x)!
        // Let's verify: Si(x) = pi/2 - f(x)*cos(x) - g(x)*sin(x), Ci(x) = f(x)*sin(x) - g(x)*cos(x).
        // With standard form h = f + i g? Let's verify signs using E_1(ix) = \int_0^\infty e^{-ix - t}/(t+ix) dt:
        // 1/(1+ix) has Re ~ 1/x^2, Im ~ -1/x. Since f(x) ~ 1/x and g(x) ~ 1/x^2, we have g = hRe, f = -hIm!
        double f = -hIm;
        double g = hRe;
        double sinX = Math.Sin(x);
        double cosX = Math.Cos(x);
        si = 0.5 * Math.PI - f * cosX - g * sinX;
        ci = f * sinX - g * cosX;
    }

    public static MathResult<decimal> LogIntLi(decimal x)
    {
        if (x <= 0m || x == 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double lnX = Math.Log((double)x);
        if (lnX > 70.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        if (lnX < 0.0)
        {
            double e1 = ExpIntE1Double(-lnX);
            return DslElementaryMath.NarrowDouble(-e1);
        }

        return DslElementaryMath.NarrowDouble(ExpIntEiPositiveDouble(lnX));
    }

    public static MathResult<decimal> FresnelS(decimal x)
    {
        if (Math.Abs(x) > 10_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(FresnelMath.SineIntegral((double)x));
    }

    public static MathResult<decimal> FresnelC(decimal x)
    {
        if (Math.Abs(x) > 10_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return DslElementaryMath.NarrowDouble(FresnelMath.CosineIntegral((double)x));
    }
}
