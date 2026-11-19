using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation elementary, trigonometric, combinatorial, polynomial, geometric, and complex
/// mathematical kernels for StockAnalyzer DSL and <see cref="Calculator"/>.
/// All IEEE-754 <see cref="double"/> results narrow once through
/// <see cref="IndicatorCalculationHelper.ToDecimalOrNull"/>; raw <c>(decimal)</c> casts are prohibited.
/// </summary>
public static class DslElementaryMath
{
    private const decimal DegenerateCoefficientEpsilon = 1e-12m;
    private const decimal RadianTrigRangeLimit = 1_000_000_000m;
    private const decimal SmallTaylorCutoff = 1e-4m;
    private const decimal ComplexZeroNormEpsilon = 1e-28m;
    private const double TanAsymptoteEpsilon = 1e-15;
    private const double HalfLogTwoPi = 0.91893853320467274178;

    private static readonly double[] LanczosTailCoeffs =
    {
        676.5203681218851,
        -1259.1392167224028,
        771.32342877765313,
        -176.61502916214059,
        12.507343278686905,
        -0.13857109526572012,
        9.9843695780195716e-6,
        1.5056327351493116e-7,
    };

    internal static decimal NormalizeZero(decimal value) => value == 0m ? 0m : value;

    internal static MathResult<decimal> NarrowDouble(double raw)
    {
        if (double.IsNaN(raw))
        {
            return MathResult<decimal>.Fail(MathErrorCode.NonFiniteResult);
        }

        decimal? narrowed = IndicatorCalculationHelper.ToDecimalOrNull(raw);
        if (narrowed is not { } value)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        return MathResult<decimal>.Ok(NormalizeZero(value));
    }

    internal static MathResult<decimal> SqrtDecimal(decimal x)
    {
        if (x < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m || x == 1m)
        {
            return MathResult<decimal>.Ok(x);
        }

        return NarrowDouble(Math.Sqrt((double)x));
    }

    /// <summary>
    /// Lanczos log-gamma for positive real arguments (<c>z &gt;= 0.5</c>); shared internally
    /// without invoking <see cref="BocpdMath.LogGamma"/> on negative or pole inputs.
    /// </summary>
    internal static double LogGammaPositive(double z)
    {
        z -= 1.0;
        double x = 0.99999999999980993
            + 676.5203681218851 / (z + 1.0)
            - 1259.1392167224028 / (z + 2.0)
            + 771.32342877765313 / (z + 3.0)
            - 176.61502916214059 / (z + 4.0)
            + 12.507343278686905 / (z + 5.0)
            - 0.13857109526572012 / (z + 6.0)
            + 9.9843695780195716e-6 / (z + 7.0)
            + 1.5056327351493116e-7 / (z + 8.0);
        double t = z + 7.5;
        return HalfLogTwoPi + (z + 0.5) * Math.Log(t) - t + Math.Log(x);
    }

    // ---- Extended Logarithms, Exponentials & Cube Root ----

    public static MathResult<decimal> Log10(decimal x)
    {
        if (x <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 1m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Log10((double)x));
    }

    public static MathResult<decimal> Log2(decimal x)
    {
        if (x <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 1m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Log2((double)x));
    }

    public static MathResult<decimal> LogBase(decimal b, decimal x)
    {
        if (b <= 0m || b == 1m || x <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 1m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (x == b)
        {
            return MathResult<decimal>.Ok(1m);
        }

        return NarrowDouble(Math.Log((double)x) / Math.Log((double)b));
    }

    public static MathResult<decimal> Expm1(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (Math.Abs(x) <= SmallTaylorCutoff)
        {
            try
            {
                decimal acc = 1m;
                for (int k = 8; k >= 2; k--)
                {
                    acc = checked(1m + (x * acc) / k);
                }

                return MathResult<decimal>.Ok(NormalizeZero(checked(x * acc)));
            }
            catch (OverflowException)
            {
                return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            }
        }

        double d = (double)x;
        return NarrowDouble(Expm1Double(d));
    }

    internal static double Expm1Double(double d)
    {
        if (Math.Abs(d) < 0.5)
        {
            double acc = 1.0;
            for (int k = 15; k >= 2; k--)
            {
                acc = 1.0 + (d * acc) / k;
            }

            return d * acc;
        }

        return Math.Exp(d) - 1.0;
    }

    public static MathResult<decimal> Log1p(decimal x)
    {
        if (x <= -1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (Math.Abs(x) <= SmallTaylorCutoff)
        {
            try
            {
                decimal acc = -1m / 8m;
                acc = checked(1m / 7m + x * acc);
                acc = checked(-1m / 6m + x * acc);
                acc = checked(1m / 5m + x * acc);
                acc = checked(-1m / 4m + x * acc);
                acc = checked(1m / 3m + x * acc);
                acc = checked(-0.5m + x * acc);
                acc = checked(1m + x * acc);
                return MathResult<decimal>.Ok(NormalizeZero(checked(x * acc)));
            }
            catch (OverflowException)
            {
                return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
            }
        }

        double d = (double)x;
        return NarrowDouble(Log1pDouble(d));
    }

    internal static double Log1pDouble(double d)
    {
        double u = 1.0 + d;
        return u == 1.0 ? d : Math.Log(u) * (d / (u - 1.0));
    }

    public static MathResult<decimal> Cbrt(decimal x)
    {
        if (x == 0m || x == 1m || x == -1m)
        {
            return MathResult<decimal>.Ok(x);
        }

        // Pre-scale |x| < 1e-3m by 10^(3k) so intermediate y^2 and y*(r - y) avoid decimal 1e-28 underflow.
        int scaleSteps = 0;
        decimal xScaled = x;
        while (Math.Abs(xScaled) < 1e-3m)
        {
            xScaled *= 1000m;
            scaleSteps++;
        }

        double seedRaw = Math.Cbrt((double)xScaled);
        MathResult<decimal> seedResult = NarrowDouble(seedRaw);
        if (!seedResult.IsOk)
        {
            return seedResult;
        }

        decimal y = seedResult.Value;
        if (y == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        try
        {
            // Overflow-safe Halley form: y_{n+1} = y + y * (r - y) / (2y + r), where r = x / y^2.
            for (int i = 0; i < 2; i++)
            {
                decimal yy = checked(y * y);
                decimal r = checked(xScaled / yy);
                y = checked(y + (y * (r - y)) / (2m * y + r));
            }

            if (scaleSteps > 0)
            {
                decimal divisor = 1m;
                for (int i = 0; i < scaleSteps; i++)
                {
                    divisor *= 10m;
                }

                y /= divisor;
            }

            return MathResult<decimal>.Ok(NormalizeZero(y));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- Trigonometric & Hyperbolic Functions ----

    public static MathResult<decimal> Sin(decimal x)
    {
        if (Math.Abs(x) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 0m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Sin((double)x));
    }

    public static MathResult<decimal> Cos(decimal x)
    {
        if (Math.Abs(x) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 0m ? MathResult<decimal>.Ok(1m) : NarrowDouble(Math.Cos((double)x));
    }

    public static MathResult<decimal> Tan(decimal x)
    {
        if (Math.Abs(x) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double dx = (double)x;
        double c = Math.Cos(dx);
        if (Math.Abs(c) < TanAsymptoteEpsilon)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return NarrowDouble(Math.Sin(dx) / c);
    }

    public static MathResult<decimal> SinDeg(decimal deg)
    {
        decimal r = ReduceDegrees(deg);
        switch (r)
        {
            case 0m or 180m:
                return MathResult<decimal>.Ok(0m);
            case 90m:
                return MathResult<decimal>.Ok(1m);
            case 270m:
                return MathResult<decimal>.Ok(-1m);
            case 30m or 150m:
                return MathResult<decimal>.Ok(0.5m);
            case 210m or 330m:
                return MathResult<decimal>.Ok(-0.5m);
        }

        decimal sign = 1m;
        if (r > 180m)
        {
            r -= 180m;
            sign = -1m;
        }

        if (r > 90m)
        {
            r = 180m - r;
        }

        double raw = r <= 45m
            ? Math.Sin((double)r * MathConstants.DegToRad)
            : Math.Cos((double)(90m - r) * MathConstants.DegToRad);
        MathResult<decimal> res = NarrowDouble(raw);
        return res.IsOk ? MathResult<decimal>.Ok(NormalizeZero(sign * res.Value)) : res;
    }

    public static MathResult<decimal> CosDeg(decimal deg)
    {
        decimal r = ReduceDegrees(deg);
        switch (r)
        {
            case 90m or 270m:
                return MathResult<decimal>.Ok(0m);
            case 0m:
                return MathResult<decimal>.Ok(1m);
            case 180m:
                return MathResult<decimal>.Ok(-1m);
            case 60m or 300m:
                return MathResult<decimal>.Ok(0.5m);
            case 120m or 240m:
                return MathResult<decimal>.Ok(-0.5m);
        }

        decimal sign = 1m;
        if (r > 180m)
        {
            r = 360m - r;
        }

        if (r > 90m)
        {
            r = 180m - r;
            sign = -1m;
        }

        double raw = r <= 45m
            ? Math.Cos((double)r * MathConstants.DegToRad)
            : Math.Sin((double)(90m - r) * MathConstants.DegToRad);
        MathResult<decimal> res = NarrowDouble(raw);
        return res.IsOk ? MathResult<decimal>.Ok(NormalizeZero(sign * res.Value)) : res;
    }

    public static MathResult<decimal> TanDeg(decimal deg)
    {
        decimal r = ReduceDegrees(deg);
        if (r >= 180m)
        {
            r -= 180m;
        }

        switch (r)
        {
            case 90m:
                return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            case 0m:
                return MathResult<decimal>.Ok(0m);
            case 45m:
                return MathResult<decimal>.Ok(1m);
            case 135m:
                return MathResult<decimal>.Ok(-1m);
        }

        decimal sign = 1m;
        if (r > 90m)
        {
            r = 180m - r;
            sign = -1m;
        }

        double raw = r <= 45m
            ? Math.Tan((double)r * MathConstants.DegToRad)
            : 1.0 / Math.Tan((double)(90m - r) * MathConstants.DegToRad);
        MathResult<decimal> res = NarrowDouble(raw);
        return res.IsOk ? MathResult<decimal>.Ok(NormalizeZero(sign * res.Value)) : res;
    }

    private static decimal ReduceDegrees(decimal deg)
    {
        decimal r = deg % 360m;
        if (r < 0m)
        {
            r += 360m;
            if (r == 360m)
            {
                r = 0m;
            }
        }

        return NormalizeZero(r);
    }

    public static MathResult<decimal> Asin(decimal x)
    {
        if (x < -1m || x > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 0m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Asin((double)x));
    }

    public static MathResult<decimal> Acos(decimal x)
    {
        if (x < -1m || x > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 1m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Acos((double)x));
    }

    public static MathResult<decimal> Atan(decimal x)
        => x == 0m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Atan((double)x));

    public static MathResult<decimal> Atan2(decimal y, decimal x)
    {
        if (y == 0m && x == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (y == 0m && x > 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        return NarrowDouble(Math.Atan2((double)y, (double)x));
    }

    public static MathResult<decimal> Sinh(decimal x)
        => x == 0m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Sinh((double)x));

    public static MathResult<decimal> Cosh(decimal x)
        => x == 0m ? MathResult<decimal>.Ok(1m) : NarrowDouble(Math.Cosh((double)x));

    public static MathResult<decimal> Tanh(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (x >= 20m)
        {
            return MathResult<decimal>.Ok(1m);
        }

        if (x <= -20m)
        {
            return MathResult<decimal>.Ok(-1m);
        }

        return NarrowDouble(Math.Tanh((double)x));
    }

    public static MathResult<decimal> Asinh(decimal x)
        => x == 0m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Asinh((double)x));

    public static MathResult<decimal> Acosh(decimal x)
    {
        if (x < 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 1m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Acosh((double)x));
    }

    public static MathResult<decimal> Atanh(decimal x)
    {
        if (x <= -1m || x >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return x == 0m ? MathResult<decimal>.Ok(0m) : NarrowDouble(Math.Atanh((double)x));
    }

    // ---- Sequences, Combinatorics & Number Theory ----

    public static MathResult<decimal> SumArith(decimal a, decimal d, decimal n)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 1_000_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (n == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        try
        {
            long ni = (long)n;
            decimal pairs = ni * (ni - 1L) / 2L;
            return MathResult<decimal>.Ok(NormalizeZero(checked(n * a + pairs * d)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> SumGeom(decimal a, decimal r, decimal n)
    {
        if (n != decimal.Truncate(n) || n < 0m || n > 1_000_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (n == 0m || a == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        try
        {
            if (r == 1m)
            {
                return MathResult<decimal>.Ok(NormalizeZero(checked(a * n)));
            }

            if (r == -1m)
            {
                return MathResult<decimal>.Ok((((long)n & 1L) == 0 ? 0m : NormalizeZero(a)));
            }

            int ni = (int)n;
            if (ni <= 64)
            {
                decimal rPowN = PowPositiveDecimal(r, ni);
                return MathResult<decimal>.Ok(NormalizeZero(checked(a * (1m - rPowN) / (1m - r))));
            }
        }
        catch (OverflowException)
        {
            // Fall through to double evaluation so tiny 'a' with large r^n does not falsely overflow when final sum fits.
        }

        double dr = (double)r;
        double rn = Math.Pow(Math.Abs(dr), (double)n);
        if (dr < 0.0 && ((long)n & 1L) != 0)
        {
            rn = -rn;
        }

        double sum = (double)a * (1.0 - rn) / (1.0 - dr);
        return NarrowDouble(sum);
    }

    public static MathResult<decimal> Fact(decimal n)
    {
        if (n != decimal.Truncate(n) || n < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (n > 27m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        int ni = (int)n;
        decimal acc = 1m;
        for (int i = 2; i <= ni; i++)
        {
            acc *= i;
        }

        return MathResult<decimal>.Ok(acc);
    }

    public static MathResult<decimal> DoubleFact(decimal n)
    {
        if (n != decimal.Truncate(n) || n < -1m || (n < 0m && (-n) % 2m == 0m))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (n > 45m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        if (n <= 0m)
        {
            return MathResult<decimal>.Ok(1m);
        }

        int ni = (int)n;
        decimal acc = 1m;
        for (int i = ni; i >= 2; i -= 2)
        {
            acc *= i;
        }

        return MathResult<decimal>.Ok(acc);
    }

    public static MathResult<decimal> Perm(decimal n, decimal k)
    {
        if (n != decimal.Truncate(n) || k != decimal.Truncate(k) || n < 0m || k < 0m || k > n || n > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        int ki = (int)k;
        if (ki == 0)
        {
            return MathResult<decimal>.Ok(1m);
        }

        try
        {
            decimal acc = 1m;
            for (int i = 0; i < ki; i++)
            {
                acc = checked(acc * (ni - i));
            }

            return MathResult<decimal>.Ok(acc);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Comb(decimal n, decimal k)
    {
        if (n != decimal.Truncate(n) || k != decimal.Truncate(k) || n < 0m || k < 0m || k > n || n > 100m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int ni = (int)n;
        int ki = (int)k;
        int m = Math.Min(ki, ni - ki);
        if (m == 0)
        {
            return MathResult<decimal>.Ok(1m);
        }

        try
        {
            decimal acc = 1m;
            for (int i = 1; i <= m; i++)
            {
                long num = ni - m + i;
                long den = i;
                long g1 = Gcd(num, den);
                num /= g1;
                den /= g1;

                if (den > 1)
                {
                    long rem = (long)(acc % den);
                    long g2 = Gcd(rem, den);
                    acc /= g2;
                    den /= g2;
                    if (den > 1)
                    {
                        acc /= den;
                    }
                }

                acc = checked(acc * num);
            }

            return MathResult<decimal>.Ok(acc);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> LComb(decimal n, decimal k)
    {
        if (n != decimal.Truncate(n) || k != decimal.Truncate(k) || n < 0m || k < 0m || k > n || n > 1_000_000_000m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (k == 0m || k == n)
        {
            return MathResult<decimal>.Ok(0m);
        }

        return NarrowDouble(LCombDouble((long)n, (long)k));
    }

    internal static double LCombDouble(long n, long k)
    {
        long m = Math.Min(k, n - k);
        if (m <= 0)
        {
            return 0.0;
        }

        if (m == 1)
        {
            return Math.Log(n);
        }

        if (m <= 64)
        {
            double sum = 0.0;
            long baseN = n - m;
            for (long j = 1; j <= m; j++)
            {
                sum += Math.Log((double)(baseN + j) / j);
            }

            return sum;
        }

        // Cancellation-free Lanczos log1p difference for large n and m > 64.
        double dn = n;
        double dm = m;
        double z2 = n - m;
        double t1 = dn + 7.5;
        double t2 = z2 + 7.5;
        double a2 = 0.99999999999980993;
        double deltaA = 0.0;
        for (int i = 0; i < LanczosTailCoeffs.Length; i++)
        {
            double ci = LanczosTailCoeffs[i];
            double shift = i + 1.0;
            double d1 = dn + shift;
            double d2 = z2 + shift;
            a2 += ci / d2;
            deltaA += ci * (-dm / (d1 * d2));
        }

        double ratioLogGamma = dm * (Math.Log(t1) - 1.0)
            + (z2 + 0.5) * Log1pDouble(dm / t2)
            + Log1pDouble(deltaA / a2);
        return ratioLogGamma - LogGammaPositive(dm + 1.0);
    }

    public static MathResult<decimal> Fib(decimal n)
    {
        if (n != decimal.Truncate(n) || n < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (n > 139m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        int ni = (int)n;
        if (ni == 0)
        {
            return MathResult<decimal>.Ok(0m);
        }

        try
        {
            (_, decimal fn) = FibPair(ni - 1);
            return MathResult<decimal>.Ok(fn);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Lucas(decimal n)
    {
        if (n != decimal.Truncate(n) || n < 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (n > 138m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        int ni = (int)n;
        if (ni == 0)
        {
            return MathResult<decimal>.Ok(2m);
        }

        if (ni == 1)
        {
            return MathResult<decimal>.Ok(1m);
        }

        try
        {
            (decimal fnm1, decimal fn) = FibPair(ni - 1);
            // L_n = F_{n-1} + F_{n+1} = F_n + 2 * F_{n-1}, avoiding computing F_{n+1} directly.
            return MathResult<decimal>.Ok(checked(fn + 2m * fnm1));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    private static (decimal Fn, decimal Fnp1) FibPair(int n)
    {
        if (n == 0)
        {
            return (0m, 1m);
        }

        (decimal a, decimal b) = FibPair(n >> 1);
        decimal c = checked(a * (2m * b - a));
        decimal d = checked(a * a + b * b);
        if ((n & 1) == 0)
        {
            return (c, d);
        }

        return (d, checked(c + d));
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            long t = a % b;
            a = b;
            b = t;
        }

        return a < 0 ? -a : a;
    }

    private static decimal PowPositiveDecimal(decimal baseValue, int exponent)
    {
        decimal result = 1m;
        decimal factor = baseValue;
        int exp = exponent;
        while (exp > 0)
        {
            if ((exp & 1) != 0)
            {
                result = checked(result * factor);
            }

            exp >>= 1;
            if (exp > 0)
            {
                factor = checked(factor * factor);
            }
        }

        return result;
    }

    // ---- Equations & Polynomial Solvers ----

    public static MathResult<decimal> SolveLinear(decimal a, decimal b)
    {
        if (a == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            return MathResult<decimal>.Ok(NormalizeZero(checked(-b / a)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> QuadraticDiscriminant(decimal a, decimal b, decimal c)
    {
        try
        {
            return MathResult<decimal>.Ok(NormalizeZero(checked(b * b - 4m * a * c)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathErrorCode SolveQuadratic(decimal a, decimal b, decimal c, Span<decimal> roots, out int rootCount)
    {
        rootCount = 0;
        if (roots.Length < 2)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<decimal> coeffs = [a, b, c];
        if (!TryScalePolynomialCoefficients(coeffs, out decimal maxAbs))
        {
            return MathErrorCode.DivisionByZero;
        }

        a = coeffs[0];
        b = coeffs[1];
        c = coeffs[2];
        decimal threshold = DegenerateCoefficientEpsilon * maxAbs;
        if (Math.Abs(a) < threshold)
        {
            if (Math.Abs(b) < threshold)
            {
                return MathErrorCode.DivisionByZero;
            }

            MathResult<decimal> linear = SolveLinear(b, c);
            if (!linear.IsOk)
            {
                return linear.Error;
            }

            roots[0] = linear.Value;
            rootCount = 1;
            return MathErrorCode.None;
        }

        try
        {
            decimal d = checked(b * b - 4m * a * c);
            if (d < 0m)
            {
                rootCount = 0;
                return MathErrorCode.None;
            }

            if (d == 0m)
            {
                roots[0] = NormalizeZero(checked(-b / (2m * a)));
                rootCount = 1;
                return MathErrorCode.None;
            }

            MathResult<decimal> sqrtRes = SqrtDecimal(d);
            if (!sqrtRes.IsOk)
            {
                return sqrtRes.Error;
            }

            decimal sqrtD = sqrtRes.Value;
            // Refine 1 Newton step in decimal for full 28-digit square-root accuracy when intermediate fits.
            if (sqrtD > 0m)
            {
                sqrtD = 0.5m * (sqrtD + d / sqrtD);
            }

            decimal q = b >= 0m ? -0.5m * (b + sqrtD) : -0.5m * (b - sqrtD);
            decimal r1 = checked(q / a);
            decimal r2 = checked(c / q);
            r1 = NormalizeZero(r1);
            r2 = NormalizeZero(r2);
            if (r1 <= r2)
            {
                roots[0] = r1;
                roots[1] = r2;
            }
            else
            {
                roots[0] = r2;
                roots[1] = r1;
            }

            rootCount = 2;
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    public static MathErrorCode SolveCubic(decimal a, decimal b, decimal c, decimal d, Span<decimal> roots, out int rootCount)
    {
        rootCount = 0;
        if (roots.Length < 3)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<decimal> coeffs = [a, b, c, d];
        if (!TryScalePolynomialCoefficients(coeffs, out decimal maxAbs))
        {
            return MathErrorCode.DivisionByZero;
        }

        if (Math.Abs(coeffs[0]) < DegenerateCoefficientEpsilon * maxAbs)
        {
            return SolveQuadratic(coeffs[1], coeffs[2], coeffs[3], roots, out rootCount);
        }

        double da = (double)coeffs[0];
        double A = (double)coeffs[1] / da;
        double B = (double)coeffs[2] / da;
        double C = (double)coeffs[3] / da;
        double shift = A / 3.0;
        double p = B - A * A / 3.0;
        double q = (2.0 * A * A * A / 27.0) - (A * B / 3.0) + C;
        double disc = 0.25 * q * q + (p * p * p) / 27.0;

        Span<double> raw = stackalloc double[3];
        int count;
        if (disc > 1e-15)
        {
            double s = Math.Sqrt(disc);
            raw[0] = Math.Cbrt(-0.5 * q + s) + Math.Cbrt(-0.5 * q - s) - shift;
            count = 1;
        }
        else if (Math.Abs(disc) <= 1e-15)
        {
            if (Math.Abs(p) < 1e-15 && Math.Abs(q) < 1e-15)
            {
                raw[0] = -shift;
                count = 1;
            }
            else
            {
                double r1 = 3.0 * q / p - shift;
                double r2 = -1.5 * q / p - shift;
                raw[0] = Math.Min(r1, r2);
                raw[1] = Math.Max(r1, r2);
                count = 2;
            }
        }
        else
        {
            double r = 2.0 * Math.Sqrt(-p / 3.0);
            double cosArg = Math.Clamp((1.5 * q / p) * Math.Sqrt(-3.0 / p), -1.0, 1.0);
            double phi = Math.Acos(cosArg) / 3.0;
            raw[0] = r * Math.Cos(phi) - shift;
            raw[1] = r * Math.Cos(phi - MathConstants.TwoPi / 3.0) - shift;
            raw[2] = r * Math.Cos(phi - 2.0 * MathConstants.TwoPi / 3.0) - shift;
            count = 3;
        }

        for (int i = 0; i < count; i++)
        {
            raw[i] = PolishCubicRoot(raw[i], A, B, C);
            MathResult<decimal> narrowed = NarrowDouble(raw[i]);
            if (!narrowed.IsOk)
            {
                return narrowed.Error;
            }

            roots[i] = narrowed.Value;
        }

        SortSpan(roots.Slice(0, count));
        rootCount = count;
        return MathErrorCode.None;
    }

    public static MathErrorCode SolveQuartic(decimal a, decimal b, decimal c, decimal d, decimal e, Span<decimal> roots, out int rootCount)
    {
        rootCount = 0;
        if (roots.Length < 4)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<decimal> coeffs = [a, b, c, d, e];
        if (!TryScalePolynomialCoefficients(coeffs, out decimal maxAbs))
        {
            return MathErrorCode.DivisionByZero;
        }

        if (Math.Abs(coeffs[0]) < DegenerateCoefficientEpsilon * maxAbs)
        {
            return SolveCubic(coeffs[1], coeffs[2], coeffs[3], coeffs[4], roots, out rootCount);
        }

        double da = (double)coeffs[0];
        double B = (double)coeffs[1] / da;
        double C = (double)coeffs[2] / da;
        double D = (double)coeffs[3] / da;
        double E = (double)coeffs[4] / da;

        double shift = B * 0.25;
        double b2 = B * B;
        double p = C - 0.375 * b2;
        double q = 0.125 * b2 * B - 0.5 * B * C + D;
        double r = (-3.0 / 256.0) * b2 * b2 + 0.0625 * b2 * C - 0.25 * B * D + E;

        Span<double> raw = stackalloc double[4];
        int count = 0;

        if (Math.Abs(q) < 1e-14)
        {
            // Biquadratic y^4 + p y^2 + r = 0
            double disc = p * p - 4.0 * r;
            if (disc >= 0.0)
            {
                double sd = Math.Sqrt(disc);
                double u1 = 0.5 * (-p - sd);
                double u2 = 0.5 * (-p + sd);
                if (u1 >= 0.0)
                {
                    double su = Math.Sqrt(u1);
                    raw[count++] = -su - shift;
                    if (su > 0.0)
                    {
                        raw[count++] = su - shift;
                    }
                }

                if (u2 >= 0.0 && Math.Abs(u2 - u1) > 1e-15)
                {
                    double su = Math.Sqrt(u2);
                    raw[count++] = -su - shift;
                    if (su > 0.0)
                    {
                        raw[count++] = su - shift;
                    }
                }
            }
        }
        else
        {
            // Unshifted Ferrari resolvent cubic for u = w^2 = 2z - p >= 0:
            // u^3 + 2p u^2 + (p^2 - 4r) u - q^2 = 0.
            // Solving for u directly (with asymptotic seed + Newton polishing) eliminates 2z - p cancellation.
            double uCoeffB = 2.0 * p;
            double uCoeffC = p * p - 4.0 * r;
            double uCoeffD = -q * q;
            double u = SolveResolventMaxRealRoot(uCoeffB, uCoeffC, uCoeffD);
            if ((u <= 0.0 || (p > 0.0 && u <= 1e-6 * p)) && uCoeffC > 0.0)
            {
                u = (q * q) / uCoeffC;
            }

            for (int iter = 0; iter < 3 && u > 0.0; iter++)
            {
                double gVal = ((u + uCoeffB) * u + uCoeffC) * u + uCoeffD;
                double gDeriv = (3.0 * u + 2.0 * uCoeffB) * u + uCoeffC;
                if (Math.Abs(gDeriv) <= 1e-300)
                {
                    break;
                }

                double uNext = u - gVal / gDeriv;
                if (!(uNext > 0.0) || !double.IsFinite(uNext))
                {
                    break;
                }

                double gNext = ((uNext + uCoeffB) * uNext + uCoeffC) * uNext + uCoeffD;
                if (Math.Abs(gNext) <= Math.Abs(gVal))
                {
                    u = uNext;
                }
                else
                {
                    break;
                }
            }

            double w2 = u < 0.0 && u > -1e-12 ? 0.0 : u;
            double z = 0.5 * (w2 + p);
            if (w2 >= 0.0)
            {
                double w = Math.Sqrt(w2);
                double halfQOverW = w > 1e-15 ? 0.5 * q / w : 0.0;
                double d1 = w2 - 4.0 * (z - halfQOverW);
                if (d1 >= 0.0)
                {
                    double sd1 = Math.Sqrt(d1);
                    raw[count++] = 0.5 * (w - sd1) - shift;
                    if (sd1 > 0.0)
                    {
                        raw[count++] = 0.5 * (w + sd1) - shift;
                    }
                }

                double d2 = w2 - 4.0 * (z + halfQOverW);
                if (d2 >= 0.0)
                {
                    double sd2 = Math.Sqrt(d2);
                    raw[count++] = 0.5 * (-w - sd2) - shift;
                    if (sd2 > 0.0)
                    {
                        raw[count++] = 0.5 * (-w + sd2) - shift;
                    }
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            raw[i] = PolishQuarticRoot(raw[i], B, C, D, E);
            MathResult<decimal> narrowed = NarrowDouble(raw[i]);
            if (!narrowed.IsOk)
            {
                return narrowed.Error;
            }

            roots[i] = narrowed.Value;
        }

        SortSpan(roots.Slice(0, count));
        rootCount = count;
        return MathErrorCode.None;
    }

    private static bool TryScalePolynomialCoefficients(Span<decimal> coeffs, out decimal maxAbs)
    {
        maxAbs = 0m;
        for (int i = 0; i < coeffs.Length; i++)
        {
            decimal abs = Math.Abs(coeffs[i]);
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }
        }

        if (maxAbs == 0m)
        {
            return false;
        }

        if (maxAbs < 1m)
        {
            decimal scale = 1m;
            decimal probe = maxAbs;
            while (probe < 1m)
            {
                probe *= 10m;
                scale *= 10m;
            }

            for (int i = 0; i < coeffs.Length; i++)
            {
                coeffs[i] *= scale;
            }

            maxAbs = probe;
        }
        else if (maxAbs > 1e10m)
        {
            decimal divisor = 1m;
            decimal probe = maxAbs;
            while (probe >= 10m)
            {
                probe /= 10m;
                divisor *= 10m;
            }

            for (int i = 0; i < coeffs.Length; i++)
            {
                coeffs[i] /= divisor;
            }

            maxAbs = probe;
        }

        return true;
    }

    private static double PolishCubicRoot(double x, double a, double b, double c)
    {
        for (int iter = 0; iter < 2; iter++)
        {
            double f = ((x + a) * x + b) * x + c;
            double df = (3.0 * x + 2.0 * a) * x + b;
            if (Math.Abs(df) <= 1e-14 * (3.0 * x * x + 2.0 * Math.Abs(a) * Math.Abs(x) + Math.Abs(b) + 1.0))
            {
                break;
            }

            double xNext = x - f / df;
            if (!double.IsFinite(xNext))
            {
                break;
            }

            double fNext = ((xNext + a) * xNext + b) * xNext + c;
            if (Math.Abs(fNext) < Math.Abs(f))
            {
                x = xNext;
            }
            else
            {
                break;
            }
        }

        return x;
    }

    private static double PolishQuarticRoot(double x, double b, double c, double d, double e)
    {
        for (int iter = 0; iter < 2; iter++)
        {
            double f = (((x + b) * x + c) * x + d) * x + e;
            double df = ((4.0 * x + 3.0 * b) * x + 2.0 * c) * x + d;
            if (Math.Abs(df) <= 1e-14 * (4.0 * Math.Abs(x * x * x) + 3.0 * Math.Abs(b) * x * x + 2.0 * Math.Abs(c) * Math.Abs(x) + Math.Abs(d) + 1.0))
            {
                break;
            }

            double xNext = x - f / df;
            if (!double.IsFinite(xNext))
            {
                break;
            }

            double fNext = (((xNext + b) * xNext + c) * xNext + d) * xNext + e;
            if (Math.Abs(fNext) < Math.Abs(f))
            {
                x = xNext;
            }
            else
            {
                break;
            }
        }

        return x;
    }

    private static double SolveResolventMaxRealRoot(double A, double B, double C)
    {
        double shift = A / 3.0;
        double p = B - A * A / 3.0;
        double q = (2.0 * A * A * A / 27.0) - (A * B / 3.0) + C;
        double disc = 0.25 * q * q + (p * p * p) / 27.0;
        if (disc >= 0.0)
        {
            double s = Math.Sqrt(disc);
            return Math.Cbrt(-0.5 * q + s) + Math.Cbrt(-0.5 * q - s) - shift;
        }

        double r = 2.0 * Math.Sqrt(-p / 3.0);
        double cosArg = Math.Clamp((1.5 * q / p) * Math.Sqrt(-3.0 / p), -1.0, 1.0);
        double phi = Math.Acos(cosArg) / 3.0;
        double r0 = r * Math.Cos(phi) - shift;
        double r1 = r * Math.Cos(phi - MathConstants.TwoPi / 3.0) - shift;
        double r2 = r * Math.Cos(phi - 2.0 * MathConstants.TwoPi / 3.0) - shift;
        return Math.Max(r0, Math.Max(r1, r2));
    }

    private static void SortSpan(Span<decimal> span)
    {
        for (int i = 1; i < span.Length; i++)
        {
            decimal key = span[i];
            int j = i - 1;
            while (j >= 0 && span[j] > key)
            {
                span[j + 1] = span[j];
                j--;
            }

            span[j + 1] = key;
        }
    }

    // ---- Geometry & Triangle Metrics ----

    public static MathResult<decimal> Hypot(decimal x, decimal y)
    {
        decimal ax = Math.Abs(x);
        decimal ay = Math.Abs(y);
        if (ax == 0m)
        {
            return MathResult<decimal>.Ok(ay);
        }

        if (ay == 0m)
        {
            return MathResult<decimal>.Ok(ax);
        }

        decimal m = ax >= ay ? ax : ay;
        decimal min = ax >= ay ? ay : ax;
        decimal r = min / m;
        decimal onePlusR2 = 1m + r * r;
        MathResult<decimal> rootRes = SqrtDecimal(onePlusR2);
        if (!rootRes.IsOk)
        {
            return rootRes;
        }

        decimal root = 0.5m * (rootRes.Value + onePlusR2 / rootRes.Value);
        try
        {
            return MathResult<decimal>.Ok(NormalizeZero(checked(m * root)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Dist2D(decimal x1, decimal y1, decimal x2, decimal y2)
    {
        try
        {
            return Hypot(checked(x2 - x1), checked(y2 - y1));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Dist3D(decimal x1, decimal y1, decimal z1, decimal x2, decimal y2, decimal z2)
    {
        try
        {
            decimal dx = Math.Abs(checked(x2 - x1));
            decimal dy = Math.Abs(checked(y2 - y1));
            decimal dz = Math.Abs(checked(z2 - z1));
            decimal m = Math.Max(dx, Math.Max(dy, dz));
            if (m == 0m)
            {
                return MathResult<decimal>.Ok(0m);
            }

            if (m >= 1e-10m && m <= 1e13m)
            {
                decimal directSumSq = dx * dx + dy * dy + dz * dz;
                MathResult<decimal> directRootRes = SqrtDecimal(directSumSq);
                if (!directRootRes.IsOk)
                {
                    return directRootRes;
                }

                decimal directRoot = 0.5m * (directRootRes.Value + directSumSq / directRootRes.Value);
                return MathResult<decimal>.Ok(NormalizeZero(directRoot));
            }

            decimal rx = dx / m;
            decimal ry = dy / m;
            decimal rz = dz / m;
            decimal sumSq = rx * rx + ry * ry + rz * rz;
            MathResult<decimal> rootRes = SqrtDecimal(sumSq);
            if (!rootRes.IsOk)
            {
                return rootRes;
            }

            decimal root = 0.5m * (rootRes.Value + sumSq / rootRes.Value);
            return MathResult<decimal>.Ok(NormalizeZero(checked(m * root)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathErrorCode TriangleMetricsSss(
        decimal a,
        decimal b,
        decimal c,
        out decimal area,
        out decimal inradius,
        out decimal circumradius)
    {
        area = 0m;
        inradius = 0m;
        circumradius = 0m;

        if (a <= 0m || b <= 0m || c <= 0m)
        {
            return MathErrorCode.DomainError;
        }

        // Sort descending: sa >= sb >= sc > 0
        decimal sa = a;
        decimal sb = b;
        decimal sc = c;
        if (sa < sb) (sa, sb) = (sb, sa);
        if (sb < sc) (sb, sc) = (sc, sb);
        if (sa < sb) (sa, sb) = (sb, sa);

        if (sc - (sa - sb) <= 0m)
        {
            return MathErrorCode.DomainError;
        }

        // Kahan stabilized Heron formula evaluated in double to prevent intermediate overflow, narrowed once.
        double da = (double)sa;
        double db = (double)sb;
        double dc = (double)sc;
        double prod = (da + (db + dc)) * (dc - (da - db)) * (dc + (da - db)) * (da + (db - dc));
        if (!(prod > 0.0))
        {
            return MathErrorCode.DomainError;
        }

        double areaD = 0.25 * Math.Sqrt(prod);
        MathResult<decimal> areaRes = NarrowDouble(areaD);
        if (!areaRes.IsOk)
        {
            return areaRes.Error;
        }

        double perimeterD = da + db + dc;
        MathResult<decimal> inRes = NarrowDouble(2.0 * areaD / perimeterD);
        if (!inRes.IsOk)
        {
            return inRes.Error;
        }

        MathResult<decimal> circumRes = NarrowDouble((da * db * dc) / (4.0 * areaD));
        if (!circumRes.IsOk)
        {
            return circumRes.Error;
        }

        area = areaRes.Value;
        inradius = inRes.Value;
        circumradius = circumRes.Value;
        return MathErrorCode.None;
    }

    public static MathResult<decimal> SphereVolume(decimal r)
    {
        if (r <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dr = (double)r;
        return NarrowDouble((4.0 / 3.0) * Math.PI * dr * dr * dr);
    }

    public static MathResult<decimal> ConeSurfaceArea(decimal r, decimal h)
    {
        if (r <= 0m || h <= 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dr = (double)r;
        double dh = (double)h;
        double slant = Math.Sqrt(dr * dr + dh * dh);
        return NarrowDouble(Math.PI * dr * (dr + slant));
    }

    public static MathResult<decimal> TorusVolume(decimal majorR, decimal minorR)
    {
        if (majorR <= 0m || minorR <= 0m || majorR < minorR)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double dMajor = (double)majorR;
        double dMinor = (double)minorR;
        return NarrowDouble(2.0 * Math.PI * Math.PI * dMajor * dMinor * dMinor);
    }

    public static MathResult<decimal> PolygonArea(ReadOnlySpan<DslPoint2D> pts)
    {
        if (pts.Length < 3 || pts.Length > 256)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double cx = 0.0;
        double cy = 0.0;
        for (int i = 0; i < pts.Length; i++)
        {
            cx += (double)pts[i].X;
            cy += (double)pts[i].Y;
        }

        cx /= pts.Length;
        cy /= pts.Length;

        double sum = 0.0;
        for (int i = 0; i < pts.Length; i++)
        {
            int next = i + 1 < pts.Length ? i + 1 : 0;
            double x1 = (double)pts[i].X - cx;
            double y1 = (double)pts[i].Y - cy;
            double x2 = (double)pts[next].X - cx;
            double y2 = (double)pts[next].Y - cy;
            sum += x1 * y2 - x2 * y1;
        }

        return NarrowDouble(0.5 * Math.Abs(sum));
    }

    // ---- Complex Arithmetic ----

    public static MathResult<DslComplex> ComplexAdd(DslComplex a, DslComplex b)
    {
        try
        {
            return MathResult<DslComplex>.Ok(new DslComplex(
                NormalizeZero(checked(a.Re + b.Re)),
                NormalizeZero(checked(a.Im + b.Im))));
        }
        catch (OverflowException)
        {
            return MathResult<DslComplex>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<DslComplex> ComplexSubtract(DslComplex a, DslComplex b)
    {
        try
        {
            return MathResult<DslComplex>.Ok(new DslComplex(
                NormalizeZero(checked(a.Re - b.Re)),
                NormalizeZero(checked(a.Im - b.Im))));
        }
        catch (OverflowException)
        {
            return MathResult<DslComplex>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<DslComplex> ComplexMultiply(DslComplex a, DslComplex b)
    {
        try
        {
            decimal re = checked(a.Re * b.Re - a.Im * b.Im);
            decimal im = checked(a.Re * b.Im + a.Im * b.Re);
            return MathResult<DslComplex>.Ok(new DslComplex(NormalizeZero(re), NormalizeZero(im)));
        }
        catch (OverflowException)
        {
            return MathResult<DslComplex>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<DslComplex> ComplexDivide(DslComplex a, DslComplex b)
    {
        MathResult<decimal> absB = Hypot(b.Re, b.Im);
        if (!absB.IsOk)
        {
            return MathResult<DslComplex>.Fail(absB.Error);
        }

        if (absB.Value < ComplexZeroNormEpsilon)
        {
            return MathResult<DslComplex>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            // Smith's algorithm avoids intermediate square overflow.
            decimal re;
            decimal im;
            if (Math.Abs(b.Re) >= Math.Abs(b.Im))
            {
                decimal r = b.Im / b.Re;
                decimal denom = checked(b.Re + b.Im * r);
                re = checked((a.Re + a.Im * r) / denom);
                im = checked((a.Im - a.Re * r) / denom);
            }
            else
            {
                decimal r = b.Re / b.Im;
                decimal denom = checked(b.Im + b.Re * r);
                re = checked((a.Re * r + a.Im) / denom);
                im = checked((a.Im * r - a.Re) / denom);
            }

            return MathResult<DslComplex>.Ok(new DslComplex(NormalizeZero(re), NormalizeZero(im)));
        }
        catch (OverflowException)
        {
            return MathResult<DslComplex>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> ComplexAbs(DslComplex z) => Hypot(z.Re, z.Im);

    public static MathResult<decimal> ComplexArg(DslComplex z) => Atan2(z.Im, z.Re);

    public static MathResult<DslComplex> ComplexExp(DslComplex z)
    {
        if (Math.Abs(z.Im) > RadianTrigRangeLimit)
        {
            return MathResult<DslComplex>.Fail(MathErrorCode.DomainError);
        }

        double expRe = Math.Exp((double)z.Re);
        double im = (double)z.Im;
        MathResult<decimal> reRes = NarrowDouble(expRe * Math.Cos(im));
        if (!reRes.IsOk)
        {
            return MathResult<DslComplex>.Fail(reRes.Error);
        }

        MathResult<decimal> imRes = NarrowDouble(expRe * Math.Sin(im));
        if (!imRes.IsOk)
        {
            return MathResult<DslComplex>.Fail(imRes.Error);
        }

        return MathResult<DslComplex>.Ok(new DslComplex(reRes.Value, imRes.Value));
    }

    public static MathResult<DslComplex> ComplexLn(DslComplex z)
    {
        if (z.Re == 0m && z.Im == 0m)
        {
            return MathResult<DslComplex>.Fail(MathErrorCode.DomainError);
        }

        MathResult<decimal> abs = Hypot(z.Re, z.Im);
        if (!abs.IsOk)
        {
            return MathResult<DslComplex>.Fail(abs.Error);
        }

        MathResult<decimal> lnAbs = NarrowDouble(Math.Log((double)abs.Value));
        if (!lnAbs.IsOk)
        {
            return MathResult<DslComplex>.Fail(lnAbs.Error);
        }

        MathResult<decimal> arg = Atan2(z.Im, z.Re);
        if (!arg.IsOk)
        {
            return MathResult<DslComplex>.Fail(arg.Error);
        }

        return MathResult<DslComplex>.Ok(new DslComplex(lnAbs.Value, arg.Value));
    }

    public static MathResult<DslComplex> ComplexSqrt(DslComplex z)
    {
        if (z.Re == 0m && z.Im == 0m)
        {
            return MathResult<DslComplex>.Ok(new DslComplex(0m, 0m));
        }

        double re = (double)z.Re;
        double im = (double)z.Im;
        double r = Math.Sqrt(re * re + im * im);
        if (!double.IsFinite(r))
        {
            MathResult<decimal> abs = Hypot(z.Re, z.Im);
            if (!abs.IsOk)
            {
                return MathResult<DslComplex>.Fail(abs.Error);
            }

            r = (double)abs.Value;
        }

        double outRe;
        double outIm;
        if (re >= 0.0)
        {
            outRe = Math.Sqrt(0.5 * (r + re));
            outIm = im == 0.0 ? 0.0 : 0.5 * im / outRe;
        }
        else
        {
            outIm = Math.CopySign(Math.Sqrt(0.5 * (r - re)), im == 0.0 ? 1.0 : im);
            outRe = 0.5 * Math.Abs(im) / Math.Abs(outIm);
        }

        MathResult<decimal> rRes = NarrowDouble(outRe);
        if (!rRes.IsOk)
        {
            return MathResult<DslComplex>.Fail(rRes.Error);
        }

        MathResult<decimal> iRes = NarrowDouble(outIm);
        if (!iRes.IsOk)
        {
            return MathResult<DslComplex>.Fail(iRes.Error);
        }

        return MathResult<DslComplex>.Ok(new DslComplex(rRes.Value, iRes.Value));
    }

    public static MathResult<DslComplex> ComplexPow(DslComplex z, DslComplex w)
    {
        if (z.Re == 0m && z.Im == 0m)
        {
            if (w.Re == 0m && w.Im == 0m)
            {
                return MathResult<DslComplex>.Ok(new DslComplex(1m, 0m));
            }

            return w.Re > 0m && w.Im == 0m
                ? MathResult<DslComplex>.Ok(new DslComplex(0m, 0m))
                : MathResult<DslComplex>.Fail(MathErrorCode.DomainError);
        }

        MathResult<DslComplex> lnZ = ComplexLn(z);
        if (!lnZ.IsOk)
        {
            return lnZ;
        }

        MathResult<DslComplex> prod = ComplexMultiply(w, lnZ.Value);
        if (!prod.IsOk)
        {
            return prod;
        }

        return ComplexExp(prod.Value);
    }

    // ---- Bayes & Zero-Allocation Sample Statistics / Activations ----

    public static MathResult<decimal> Bayes(decimal pA, decimal pBGivenA, decimal pB)
    {
        if (pB == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        if (pA < 0m || pA > 1m || pBGivenA < 0m || pBGivenA > 1m || pB < 0m || pB > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal joint = checked(pA * pBGivenA);
            if (joint > checked(pB + DegenerateCoefficientEpsilon))
            {
                return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            }

            decimal posterior = joint / pB;
            return MathResult<decimal>.Ok(NormalizeZero(posterior > 1m ? 1m : posterior));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Variance(ReadOnlySpan<decimal> values, int ddof)
    {
        if (ddof is not (0 or 1) || values.Length < 1 + ddof)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal sum = 0m;
            for (int i = 0; i < values.Length; i++)
            {
                sum = checked(sum + values[i]);
            }

            decimal mean = sum / values.Length;
            decimal m2 = 0m;
            for (int i = 0; i < values.Length; i++)
            {
                decimal dev = checked(values[i] - mean);
                m2 = checked(m2 + checked(dev * dev));
            }

            double popDev = Math.Sqrt((double)m2 / values.Length);
            double flatThreshold = Math.Max(
                ComparisonSeriesDefaults.FlatWindowDeviationEpsilon,
                WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs((double)mean));
            if (!(popDev >= flatThreshold))
            {
                return MathResult<decimal>.Ok(0m);
            }

            return MathResult<decimal>.Ok(NormalizeZero(checked(m2 / (values.Length - ddof))));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    public static MathResult<decimal> Median(ReadOnlySpan<decimal> values)
    {
        if (values.Length == 0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> buffer = values.Length <= 256
            ? stackalloc decimal[values.Length]
            : new decimal[values.Length];
        values.CopyTo(buffer);
        decimal? median = OrderStatisticsMath.Median(buffer);
        if (median is not { } result)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        return MathResult<decimal>.Ok(NormalizeZero(result));
    }

    public static MathResult<decimal> Sigmoid(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0.5m);
        }

        if (x >= 40m)
        {
            return MathResult<decimal>.Ok(1m);
        }

        if (x <= -40m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        return NarrowDouble(1.0 / (1.0 + Math.Exp(-(double)x)));
    }

    public static MathResult<decimal> Gelu(decimal x)
    {
        if (x == 0m)
        {
            return MathResult<decimal>.Ok(0m);
        }

        double dx = (double)x;
        return NarrowDouble(dx * SpecialFunctionsMath.NormalCdf(dx));
    }
}
