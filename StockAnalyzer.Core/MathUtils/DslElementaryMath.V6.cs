using System;
using System.Numerics;
using StockAnalyzer.Core.Calculator;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// DSL v6 additions to <see cref="DslElementaryMath"/>: exact 53-bit integer number theory,
/// 53-bit unsigned bitwise operations, banker's rounding (<c>round_even</c>), and complex trigonometric/hyperbolic functions.
/// </summary>
public static partial class DslElementaryMath
{
    private const decimal MaxExact53BitInteger = 9_007_199_254_740_991m;
    private const ulong BitMask53 = 0x001F_FFFF_FFFF_FFFFUL;
    private const double HyperbolicOverflowLimit = 709.0;
    private const double AsymptoticRatioCutoff = 20.0;
    private const double DecayAsymptoticRatioCutoff = 35.0;

    internal static double HypotDouble(double x, double y)
    {
        double ax = Math.Abs(x);
        double ay = Math.Abs(y);
        if (ax < ay)
        {
            (ax, ay) = (ay, ax);
        }

        if (ax == 0.0)
        {
            return 0.0;
        }

        double r = ay / ax;
        return ax * Math.Sqrt(1.0 + (r * r));
    }

    private static readonly ulong[] DeterministicMillerRabinWitnesses =
    {
        2UL, 3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 19UL, 23UL, 29UL, 31UL, 37UL,
    };

    private static bool TryReadExactInt53(decimal raw, out long value)
    {
        if (raw != decimal.Truncate(raw) || Math.Abs(raw) > MaxExact53BitInteger)
        {
            value = 0L;
            return false;
        }

        value = (long)raw;
        return true;
    }

    private static bool TryReadUInt53(decimal raw, out ulong value)
    {
        if (raw != decimal.Truncate(raw) || raw < 0m || raw > MaxExact53BitInteger)
        {
            value = 0UL;
            return false;
        }

        value = (ulong)raw;
        return true;
    }

    private static ulong GcdUInt64(ulong a, ulong b)
    {
        while (b != 0UL)
        {
            ulong t = a % b;
            a = b;
            b = t;
        }

        return a;
    }

    private static ulong ModPowUInt64(ulong baseVal, ulong exp, ulong mod)
    {
        ulong result = 1UL;
        ulong factor = baseVal % mod;
        while (exp > 0UL)
        {
            if ((exp & 1UL) != 0UL)
            {
                result = (ulong)(((UInt128)result * factor) % mod);
            }

            exp >>= 1;
            if (exp > 0UL)
            {
                factor = (ulong)(((UInt128)factor * factor) % mod);
            }
        }

        return result;
    }

    private static bool IsPrimeUInt64(ulong n)
    {
        if (n < 2UL)
        {
            return false;
        }

        foreach (ulong p in DeterministicMillerRabinWitnesses)
        {
            if (n == p)
            {
                return true;
            }

            if (n % p == 0UL)
            {
                return false;
            }
        }

        int s = BitOperations.TrailingZeroCount(n - 1UL);
        ulong d = (n - 1UL) >> s;

        foreach (ulong witness in DeterministicMillerRabinWitnesses)
        {
            if (witness >= n)
            {
                break;
            }

            ulong x = ModPowUInt64(witness, d, n);
            if (x == 1UL || x == n - 1UL)
            {
                continue;
            }

            bool composite = true;
            for (int r = 1; r < s; r++)
            {
                x = (ulong)(((UInt128)x * x) % n);
                if (x == n - 1UL)
                {
                    composite = false;
                    break;
                }
            }

            if (composite)
            {
                return false;
            }
        }

        return true;
    }

    // ---- Number Theory & Exact Floor Division ----

    public static MathResult<decimal> Gcd(decimal a, decimal b)
    {
        if (!TryReadExactInt53(a, out long la) || !TryReadExactInt53(b, out long lb))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        ulong ua = (ulong)(la < 0L ? -la : la);
        ulong ub = (ulong)(lb < 0L ? -lb : lb);
        return MathResult<decimal>.Ok((decimal)GcdUInt64(ua, ub));
    }

    public static MathResult<decimal> Lcm(decimal a, decimal b)
    {
        if (!TryReadExactInt53(a, out long la) || !TryReadExactInt53(b, out long lb))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (la == 0L || lb == 0L)
        {
            return MathResult<decimal>.Ok(0m);
        }

        ulong ua = (ulong)(la < 0L ? -la : la);
        ulong ub = (ulong)(lb < 0L ? -lb : lb);
        ulong g = GcdUInt64(ua, ub);
        UInt128 prod = (UInt128)(ua / g) * ub;
        if (prod > (UInt128)decimal.MaxValue)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        return MathResult<decimal>.Ok((decimal)prod);
    }

    public static MathResult<decimal> IsPrime(decimal n)
    {
        if (!TryReadExactInt53(n, out long ln))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (ln < 2L)
        {
            return MathResult<decimal>.Ok(0m);
        }

        if (ln <= PrimeNumberHelper.MaxCachedPrime)
        {
            (int lo, _) = PrimeNumberHelper.FindNearestPrimes((int)ln);
            return MathResult<decimal>.Ok(lo == (int)ln ? 1m : 0m);
        }

        return MathResult<decimal>.Ok(IsPrimeUInt64((ulong)ln) ? 1m : 0m);
    }

    public static MathResult<decimal> NextPrime(decimal n)
    {
        if (!TryReadExactInt53(n, out long ln))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (ln < 2L)
        {
            return MathResult<decimal>.Ok(2m);
        }

        if (ln < PrimeNumberHelper.Primes[^1])
        {
            (_, int hi) = PrimeNumberHelper.FindNearestPrimes((int)ln + 1);
            return MathResult<decimal>.Ok((decimal)hi);
        }

        ulong cand = (ulong)ln + 1UL;
        if ((cand & 1UL) == 0UL)
        {
            cand++;
        }

        const ulong maxU53 = 9_007_199_254_740_991UL;
        while (cand <= maxU53)
        {
            if (IsPrimeUInt64(cand))
            {
                return MathResult<decimal>.Ok((decimal)cand);
            }

            cand += 2UL;
        }

        return MathResult<decimal>.Fail(MathErrorCode.DomainError);
    }

    public static MathResult<decimal> ModPow(decimal baseVal, decimal exp, decimal mod)
    {
        if (!TryReadExactInt53(baseVal, out long lb) ||
            !TryReadExactInt53(exp, out long le) ||
            !TryReadExactInt53(mod, out long lm) ||
            le < 0L ||
            lm < 1L)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (lm == 1L)
        {
            return MathResult<decimal>.Ok(0m);
        }

        long rem = lb % lm;
        if (rem < 0L)
        {
            rem += lm;
        }

        return MathResult<decimal>.Ok((decimal)ModPowUInt64((ulong)rem, (ulong)le, (ulong)lm));
    }

    public static MathResult<decimal> ModInv(decimal a, decimal mod)
    {
        if (!TryReadExactInt53(a, out long la) ||
            !TryReadExactInt53(mod, out long lm) ||
            lm < 2L)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        long a0 = la % lm;
        if (a0 < 0L)
        {
            a0 += lm;
        }

        long t = 0L;
        long newT = 1L;
        long r = lm;
        long newR = a0;
        while (newR != 0L)
        {
            long q = r / newR;
            (t, newT) = (newT, t - q * newT);
            (r, newR) = (newR, r - q * newR);
        }

        if (r != 1L)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (t < 0L)
        {
            t += lm;
        }

        return MathResult<decimal>.Ok((decimal)t);
    }

    public static MathResult<decimal> FloorDiv(decimal a, decimal b)
    {
        if (b == 0m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            decimal rem = a % b;
            decimal q = checked((a - rem) / b);
            if (rem != 0m && (a < 0m) != (b < 0m))
            {
                q = checked(q - 1m);
            }

            return MathResult<decimal>.Ok(NormalizeZero(q));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- 53-Bit Unsigned Bitwise Operations ----

    public static MathResult<decimal> BitAnd(decimal a, decimal b)
        => TryReadUInt53(a, out ulong ua) && TryReadUInt53(b, out ulong ub)
            ? MathResult<decimal>.Ok((decimal)(ua & ub))
            : MathResult<decimal>.Fail(MathErrorCode.DomainError);

    public static MathResult<decimal> BitOr(decimal a, decimal b)
        => TryReadUInt53(a, out ulong ua) && TryReadUInt53(b, out ulong ub)
            ? MathResult<decimal>.Ok((decimal)(ua | ub))
            : MathResult<decimal>.Fail(MathErrorCode.DomainError);

    public static MathResult<decimal> BitXor(decimal a, decimal b)
        => TryReadUInt53(a, out ulong ua) && TryReadUInt53(b, out ulong ub)
            ? MathResult<decimal>.Ok((decimal)(ua ^ ub))
            : MathResult<decimal>.Fail(MathErrorCode.DomainError);

    public static MathResult<decimal> BitNot(decimal a)
        => TryReadUInt53(a, out ulong ua)
            ? MathResult<decimal>.Ok((decimal)((~ua) & BitMask53))
            : MathResult<decimal>.Fail(MathErrorCode.DomainError);

    public static MathResult<decimal> BitShift(decimal a, decimal n, bool shiftLeft)
    {
        if (!TryReadUInt53(a, out ulong ua) || !DslMathValidation.TryReadInt32InRange(n, 0, 52, out int shift))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        ulong result = shiftLeft ? ((ua << shift) & BitMask53) : (ua >> shift);
        return MathResult<decimal>.Ok((decimal)result);
    }

    public static MathResult<decimal> PopCount(decimal a)
        => TryReadUInt53(a, out ulong ua)
            ? MathResult<decimal>.Ok((decimal)BitOperations.PopCount(ua))
            : MathResult<decimal>.Fail(MathErrorCode.DomainError);

    // ---- Banker's Rounding ----

    public static MathResult<decimal> RoundEven(decimal x, decimal digits)
    {
        if (!DslMathValidation.TryReadInt32InRange(digits, -ExpressionCalculator.MaxRoundEvenDigits, ExpressionCalculator.MaxRoundEvenDigits, out int d))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            if (d >= 0)
            {
                return MathResult<decimal>.Ok(NormalizeZero(Math.Round(x, d, MidpointRounding.ToEven)));
            }

            decimal scale = 1m;
            for (int i = 0; i < -d; i++)
            {
                scale *= 10m;
            }

            decimal rem = x % scale;
            decimal q = checked((x - rem) / scale);
            decimal absR = Math.Abs(rem);
            decimal half = scale * 0.5m;
            bool roundAway = absR > half || (absR == half && (decimal.Truncate(Math.Abs(q)) % 2m) != 0m);
            if (roundAway)
            {
                q = checked(q + (x >= 0m ? 1m : -1m));
            }

            decimal rounded = checked(q * scale);
            return MathResult<decimal>.Ok(NormalizeZero(rounded));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- Extended Complex Trigonometric & Hyperbolic Functions ----

    public static MathResult<decimal> ComplexSin(decimal a, decimal b, bool takeReal)
    {
        if (Math.Abs(a) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        if (Math.Abs(db) > HyperbolicOverflowLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        double val = takeReal ? Math.Sin(da) * Math.Cosh(db) : Math.Cos(da) * Math.Sinh(db);
        return NarrowDouble(val);
    }

    public static MathResult<decimal> ComplexCos(decimal a, decimal b, bool takeReal)
    {
        if (Math.Abs(a) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        if (Math.Abs(db) > HyperbolicOverflowLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        double val = takeReal ? Math.Cos(da) * Math.Cosh(db) : -Math.Sin(da) * Math.Sinh(db);
        return NarrowDouble(val);
    }

    public static MathResult<decimal> ComplexTan(decimal a, decimal b, bool takeReal)
    {
        if (Math.Abs(a) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        double cutoff = takeReal ? DecayAsymptoticRatioCutoff : AsymptoticRatioCutoff;
        if (Math.Abs(db) > cutoff)
        {
            return MathResult<decimal>.Ok(takeReal ? 0m : (db > 0.0 ? 1m : -1m));
        }

        double cosA = Math.Cos(da);
        double sinhB = Math.Sinh(db);
        if (HypotDouble(cosA, sinhB) < TanAsymptoteEpsilon)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double denom = 2.0 * ((cosA * cosA) + (sinhB * sinhB));
        double val = takeReal ? Math.Sin(2.0 * da) / denom : Math.Sinh(2.0 * db) / denom;
        return NarrowDouble(val);
    }

    public static MathResult<decimal> ComplexSinh(decimal a, decimal b, bool takeReal)
    {
        if (Math.Abs(b) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        if (Math.Abs(da) > HyperbolicOverflowLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        double val = takeReal ? Math.Sinh(da) * Math.Cos(db) : Math.Cosh(da) * Math.Sin(db);
        return NarrowDouble(val);
    }

    public static MathResult<decimal> ComplexCosh(decimal a, decimal b, bool takeReal)
    {
        if (Math.Abs(b) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        if (Math.Abs(da) > HyperbolicOverflowLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        double val = takeReal ? Math.Cosh(da) * Math.Cos(db) : Math.Sinh(da) * Math.Sin(db);
        return NarrowDouble(val);
    }

    public static MathResult<decimal> ComplexTanh(decimal a, decimal b, bool takeReal)
    {
        if (Math.Abs(b) > RadianTrigRangeLimit)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double da = (double)a;
        double db = (double)b;
        double cutoff = takeReal ? AsymptoticRatioCutoff : DecayAsymptoticRatioCutoff;
        if (Math.Abs(da) > cutoff)
        {
            return MathResult<decimal>.Ok(takeReal ? (da > 0.0 ? 1m : -1m) : 0m);
        }

        double sinhA = Math.Sinh(da);
        double cosB = Math.Cos(db);
        if (HypotDouble(sinhA, cosB) < TanAsymptoteEpsilon)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double denom = 2.0 * ((sinhA * sinhA) + (cosB * cosB));
        double val = takeReal ? Math.Sinh(2.0 * da) / denom : Math.Sin(2.0 * db) / denom;
        return NarrowDouble(val);
    }
}
