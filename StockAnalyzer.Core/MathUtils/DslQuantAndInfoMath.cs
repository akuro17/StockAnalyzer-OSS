using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Convention-free quantitative finance, information theory and spectral kernels of the DSL v5: Black-Scholes-Merton prices and Greeks,
/// NPV/IRR (cash flow 0 at t = 0), maximum drawdown and empirical CVaR (pure <see cref="decimal"/>), Shannon entropy and KL divergence (nats),
/// Goertzel amplitude and the one-sided DFT magnitude spectrum. Existing kernels (<see cref="SpecialFunctionsMath"/>, <see cref="ShannonEntropyMath"/>,
/// <see cref="GoertzelMath"/>, <see cref="FourierTransform"/>, <see cref="DslNumericalAnalysisMath.RootBrent{TFunc}"/>) are reused unchanged.
/// </summary>
public static class DslQuantAndInfoMath
{
    /// <summary>Implied volatility search interval lower bound.</summary>
    public const double ImpliedVolMin = 1e-6;

    /// <summary>Implied volatility search interval upper bound.</summary>
    public const double ImpliedVolMax = 10.0;

    /// <summary>Root tolerance of the implied volatility and IRR Brent searches.</summary>
    public const double SolverTolerance = 1e-10;

    /// <summary>Newton iteration cap of <see cref="Irr"/>.</summary>
    public const int IrrMaxNewtonIterations = 100;

    /// <summary>Number of sub-intervals of the IRR fallback bracket scan.</summary>
    public const int IrrScanIntervals = 64;

    /// <summary>Lowest rate of the IRR fallback bracket scan (exclusive lower bound of valid rates is -1).</summary>
    public const double IrrScanMinRate = -0.9999;

    /// <summary>Highest rate of the IRR fallback bracket scan.</summary>
    public const double IrrScanMaxRate = 100.0;

    /// <summary>Default start rate of the IRR Newton iteration.</summary>
    public const double IrrDefaultGuess = 0.1;

    /// <summary>Tolerance of a probability vector sum (<c>|sum - 1|</c>).</summary>
    public const double ProbabilitySumTolerance = 1e-6;

    private const double IrrFunctionTolerance = 1e-12;
    private const double IrrLooseFunctionTolerance = 1e-8;
    private const double IrrStepTolerance = 1e-12;
    private const double IrrDerivativeFloor = 1e-15;
    private const int SpectrumBudgetDivisor = 32;
    private const double InvSqrtTwoPi = 0.3989422804014327;

    // ---- Black-Scholes-Merton ----

    /// <summary>
    /// Black-Scholes-Merton price with continuous dividend yield <paramref name="q"/>. <c>T == 0</c> returns the intrinsic value; otherwise <c>sigma &gt; 0</c> is required.
    /// <c>S &lt;= 0</c>, <c>K &lt;= 0</c>, <c>T &lt; 0</c> or a non-finite input is a domain error.
    /// </summary>
    public static MathResult<double> BsPrice(double s, double k, double t, double r, double sigma, bool isCall, double q)
    {
        if (!AreFinite(s, k, t, r, sigma, q) || s <= 0.0 || k <= 0.0 || t < 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (t == 0.0)
        {
            return Finite(isCall ? Math.Max(s - k, 0.0) : Math.Max(k - s, 0.0));
        }

        if (sigma <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        D1D2(s, k, t, r, sigma, q, out double d1, out double d2);
        double discountedSpot = s * Math.Exp(-q * t);
        double discountedStrike = k * Math.Exp(-r * t);
        return Finite(isCall
            ? (discountedSpot * SpecialFunctionsMath.NormalCdf(d1)) - (discountedStrike * SpecialFunctionsMath.NormalCdf(d2))
            : (discountedStrike * SpecialFunctionsMath.NormalCdf(-d2)) - (discountedSpot * SpecialFunctionsMath.NormalCdf(-d1)));
    }

    /// <summary>Delta: call <c>e^(-qT) N(d1)</c>, put <c>e^(-qT) (N(d1) - 1)</c>.</summary>
    public static MathResult<double> BsDelta(double s, double k, double t, double r, double sigma, bool isCall, double q)
    {
        if (!ValidateGreekInputs(s, k, t, r, sigma, q))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        D1D2(s, k, t, r, sigma, q, out double d1, out _);
        double discount = Math.Exp(-q * t);
        double nd1 = SpecialFunctionsMath.NormalCdf(d1);
        return Finite(isCall ? discount * nd1 : discount * (nd1 - 1.0));
    }

    /// <summary>Gamma <c>e^(-qT) phi(d1) / (S sigma sqrt(T))</c> (call and put alike).</summary>
    public static MathResult<double> BsGamma(double s, double k, double t, double r, double sigma, double q)
    {
        if (!ValidateGreekInputs(s, k, t, r, sigma, q))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        D1D2(s, k, t, r, sigma, q, out double d1, out _);
        return Finite(Math.Exp(-q * t) * NormalPdf(d1) / (s * sigma * Math.Sqrt(t)));
    }

    /// <summary>Vega <c>S e^(-qT) phi(d1) sqrt(T)</c>: the sensitivity to one full unit of volatility (no per-cent scaling).</summary>
    public static MathResult<double> BsVega(double s, double k, double t, double r, double sigma, double q)
    {
        if (!ValidateGreekInputs(s, k, t, r, sigma, q))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        D1D2(s, k, t, r, sigma, q, out double d1, out _);
        return Finite(s * Math.Exp(-q * t) * NormalPdf(d1) * Math.Sqrt(t));
    }

    /// <summary>Theta <c>-dV/dT</c> per unit of <c>T</c> (the time unit of <c>T</c>; no per-day scaling).</summary>
    public static MathResult<double> BsTheta(double s, double k, double t, double r, double sigma, bool isCall, double q)
    {
        if (!ValidateGreekInputs(s, k, t, r, sigma, q))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        D1D2(s, k, t, r, sigma, q, out double d1, out double d2);
        double discountedSpot = s * Math.Exp(-q * t);
        double discountedStrike = k * Math.Exp(-r * t);
        double decay = -discountedSpot * NormalPdf(d1) * sigma / (2.0 * Math.Sqrt(t));
        return Finite(isCall
            ? decay - (r * discountedStrike * SpecialFunctionsMath.NormalCdf(d2)) + (q * discountedSpot * SpecialFunctionsMath.NormalCdf(d1))
            : decay + (r * discountedStrike * SpecialFunctionsMath.NormalCdf(-d2)) - (q * discountedSpot * SpecialFunctionsMath.NormalCdf(-d1)));
    }

    /// <summary>
    /// Implied volatility: the root of <c>BsPrice(sigma) - price</c> on <c>[1e-6, 10]</c> by <see cref="DslNumericalAnalysisMath.RootBrent{TFunc}"/> (<c>tol = 1e-10</c>).
    /// A price on or outside the no-arbitrage bounds, or <c>S, K, T &lt;= 0</c>, is a domain error; a root that is not bracketed by the interval is a domain error as well.
    /// </summary>
    public static MathResult<double> BsImpliedVol(double price, double s, double k, double t, double r, bool isCall, double q, ref DslExecutionBudget budget)
    {
        if (!AreFinite(price, s, k, t, r, q) || s <= 0.0 || k <= 0.0 || t <= 0.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double discountedSpot = s * Math.Exp(-q * t);
        double discountedStrike = k * Math.Exp(-r * t);
        double lower = isCall ? Math.Max(discountedSpot - discountedStrike, 0.0) : Math.Max(discountedStrike - discountedSpot, 0.0);
        double upper = isCall ? discountedSpot : discountedStrike;
        if (!(price > lower) || !(price < upper))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        var objective = new ImpliedVolObjective(s, k, t, r, q, isCall, price);
        return DslNumericalAnalysisMath.RootBrent(objective, ImpliedVolMin, ImpliedVolMax, SolverTolerance, ref budget);
    }

    private readonly struct ImpliedVolObjective(double s, double k, double t, double r, double q, bool isCall, double target) : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            MathResult<double> price = BsPrice(s, k, t, r, x, isCall, q);
            y = price.IsOk ? price.Value - target : double.NaN;
            if (!price.IsOk)
            {
                budget.LastError = price.Error;
            }

            return price.IsOk;
        }
    }

    // ---- Cash flows ----

    /// <summary><c>NPV = sum_(t = 0..n-1) CF_t / (1 + r)^t</c> (the first cash flow is at t = 0) evaluated by Horner's rule in <c>1 / (1 + r)</c>. <c>n == 0</c> or <c>r &lt;= -1</c> is a domain error.</summary>
    public static MathResult<double> Npv(double rate, scoped ReadOnlySpan<double> cashFlows)
    {
        if (cashFlows.Length == 0 || !double.IsFinite(rate) || rate <= -1.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double u = 1.0 / (1.0 + rate);
        double acc = 0.0;
        for (int t = cashFlows.Length - 1; t >= 0; t--)
        {
            acc = (acc * u) + cashFlows[t];
        }

        return double.IsFinite(acc)
            ? MathResult<double>.Ok(acc == 0.0 ? 0.0 : acc)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    /// <summary>
    /// Internal rate of return: the root <c>r &gt; -1</c> of <see cref="Npv"/>. Newton-Raphson from <paramref name="guess"/> (<see cref="IrrMaxNewtonIterations"/> iterations, one budget unit each);
    /// when it leaves <c>(-0.9999, 100)</c>, meets a vanishing derivative or does not converge, the lowest sign-change interval of a log-spaced scan of <c>[-0.9999, 100]</c>
    /// (<see cref="IrrScanIntervals"/> intervals) is refined with Brent's method. <c>n &lt; 2</c>, cash flows of one sign only, or <c>guess &lt;= -1</c> is a domain error.
    /// </summary>
    public static MathResult<double> Irr(scoped ReadOnlySpan<double> cashFlows, double guess, ref DslExecutionBudget budget)
    {
        if (cashFlows.Length < 2 || !double.IsFinite(guess) || guess <= -1.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        bool hasPositive = false;
        bool hasNegative = false;
        for (int i = 0; i < cashFlows.Length; i++)
        {
            hasPositive |= cashFlows[i] > 0.0;
            hasNegative |= cashFlows[i] < 0.0;
        }

        if (!hasPositive || !hasNegative)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double rate = guess;
        for (int iter = 0; iter < IrrMaxNewtonIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double f = IrrValueAndDerivative(cashFlows, rate, out double derivative);
            if (!double.IsFinite(f))
            {
                break;
            }

            if (Math.Abs(f) <= IrrFunctionTolerance)
            {
                return MathResult<double>.Ok(rate);
            }

            if (!double.IsFinite(derivative) || Math.Abs(derivative) <= IrrDerivativeFloor)
            {
                break;
            }

            double next = rate - (f / derivative);
            if (!double.IsFinite(next) || next <= IrrScanMinRate || next >= IrrScanMaxRate)
            {
                break;
            }

            double fNext = IrrValueAndDerivative(cashFlows, next, out _);
            if (Math.Abs(fNext) <= IrrFunctionTolerance ||
                (Math.Abs(next - rate) <= IrrStepTolerance * Math.Max(1.0, Math.Abs(next)) && Math.Abs(fNext) <= IrrLooseFunctionTolerance))
            {
                return MathResult<double>.Ok(next);
            }

            rate = next;
        }

        return IrrBracketedSearch(cashFlows, ref budget);
    }

    private static MathResult<double> IrrBracketedSearch(scoped ReadOnlySpan<double> cashFlows, ref DslExecutionBudget budget)
    {
        double[] rented = ArrayPool<double>.Shared.Rent(cashFlows.Length);
        try
        {
            cashFlows.CopyTo(rented);
            var objective = new IrrObjective(rented, cashFlows.Length);
            double logLow = Math.Log(1.0 + IrrScanMinRate);
            double logHigh = Math.Log(1.0 + IrrScanMaxRate);
            double previousRate = IrrScanMinRate;
            double previousValue = IrrValueAndDerivative(cashFlows, previousRate, out _);
            for (int i = 1; i <= IrrScanIntervals; i++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
                }

                double rate = i == IrrScanIntervals
                    ? IrrScanMaxRate
                    : Math.Exp(logLow + ((logHigh - logLow) * i / IrrScanIntervals)) - 1.0;
                double value = IrrValueAndDerivative(cashFlows, rate, out _);
                if (double.IsFinite(previousValue) && double.IsFinite(value) && Math.Sign(previousValue) * Math.Sign(value) <= 0)
                {
                    return DslNumericalAnalysisMath.RootBrent(objective, previousRate, rate, SolverTolerance, ref budget);
                }

                previousRate = rate;
                previousValue = value;
            }

            return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static double IrrValueAndDerivative(scoped ReadOnlySpan<double> cashFlows, double rate, out double derivative)
    {
        // f(r) = sum CF_t u^t and f'(r) = -sum t CF_t u^(t+1) with u = 1 / (1 + r).
        double u = 1.0 / (1.0 + rate);
        double power = 1.0;
        double value = 0.0;
        double slope = 0.0;
        for (int t = 0; t < cashFlows.Length; t++)
        {
            value += cashFlows[t] * power;
            slope -= t * cashFlows[t] * power * u;
            power *= u;
        }

        derivative = slope;
        return value;
    }

    private readonly struct IrrObjective(double[] cashFlows, int count) : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = IrrValueAndDerivative(cashFlows.AsSpan(0, count), x, out _);
            return true;
        }
    }

    // ---- Risk (pure decimal) ----

    /// <summary>
    /// Maximum drawdown <c>max_t (M_t - E_t) / M_t</c> (running peak <c>M_t</c>) of a strictly positive equity series, a non-negative ratio in <c>[0, 1]</c>.
    /// <c>n &lt; 2</c> or any value <c>&lt;= 0</c> is a domain error. Pure <see cref="decimal"/> arithmetic.
    /// </summary>
    public static MathResult<decimal> MaxDrawdown(scoped ReadOnlySpan<decimal> equity)
    {
        if (equity.Length < 2)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        decimal peak = 0m;
        decimal worst = 0m;
        try
        {
            for (int i = 0; i < equity.Length; i++)
            {
                decimal value = equity[i];
                if (value <= 0m)
                {
                    return MathResult<decimal>.Fail(MathErrorCode.DomainError);
                }

                if (value > peak)
                {
                    peak = value;
                }

                decimal drawdown = checked((peak - value) / peak);
                if (drawdown > worst)
                {
                    worst = drawdown;
                }
            }
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        return MathResult<decimal>.Ok(worst);
    }

    /// <summary>
    /// Empirical conditional value at risk, the positive loss ratio <c>-(1 / (k + 1)) sum_(i = 0..k) R_(i)</c> over the ascending order statistics, with
    /// <c>k = clamp(floor((1 - c) n), 0, n - 1)</c> (the convention of <see cref="PortfolioRiskCalculator"/>, with the product evaluated exactly in <see cref="decimal"/>).
    /// <c>n == 0</c> or <c>c</c> outside <c>(0, 1)</c> is a domain error.
    /// </summary>
    public static MathResult<decimal> EmpiricalCvar(scoped ReadOnlySpan<decimal> returns, decimal confidence)
    {
        int n = returns.Length;
        if (n == 0 || confidence <= 0m || confidence >= 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> sorted = stackalloc decimal[n];
        returns.CopyTo(sorted);
        sorted.Sort();
        int k = (int)Math.Clamp(decimal.Floor((1m - confidence) * n), 0m, n - 1);
        try
        {
            decimal sum = 0m;
            for (int i = 0; i <= k; i++)
            {
                sum = checked(sum + sorted[i]);
            }

            decimal cvar = checked(-sum / (k + 1));
            return MathResult<decimal>.Ok(cvar == 0m ? 0m : cvar);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    // ---- Information theory ----

    /// <summary>Shannon entropy in nats of a probability vector (all <c>p_i &gt;= 0</c>, <c>|sum - 1| &lt;= 1e-6</c>) by <see cref="ShannonEntropyMath.Nats(ReadOnlySpan{double}, double, double)"/>.</summary>
    public static MathResult<double> ShannonEntropy(scoped ReadOnlySpan<double> probabilities)
    {
        if (!IsProbabilityVector(probabilities))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        return Finite(ShannonEntropyMath.Nats(probabilities, 1.0, ShannonEntropyMath.NegligibleShare));
    }

    /// <summary>
    /// Kullback-Leibler divergence in nats, <c>sum_(p_i &gt; 1e-12) p_i ln(p_i / q_i)</c>. Unequal lengths are a dimension mismatch; a non-probability vector or
    /// <c>p_i &gt; 1e-12</c> with <c>q_i &lt;= 1e-12</c> is a domain error.
    /// </summary>
    public static MathResult<double> KlDivergence(scoped ReadOnlySpan<double> p, scoped ReadOnlySpan<double> q)
    {
        if (p.Length != q.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (!IsProbabilityVector(p) || !IsProbabilityVector(q))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double sum = 0.0;
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i] <= ShannonEntropyMath.NegligibleShare)
            {
                continue;
            }

            if (q[i] <= ShannonEntropyMath.NegligibleShare)
            {
                return MathResult<double>.Fail(MathErrorCode.DomainError);
            }

            sum += p[i] * Math.Log(p[i] / q[i]);
        }

        return Finite(sum);
    }

    // ---- Spectral ----

    /// <summary>Goertzel amplitude of the trailing <paramref name="period"/> samples at that period (<see cref="GoertzelMath"/>, price-native amplitude).</summary>
    public static MathResult<double> Goertzel(scoped ReadOnlySpan<double> series, int period)
    {
        if (period < GoertzelMath.MinTargetPeriod || period > DslArrayArena.MaxArrayLength || series.Length < period)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        Span<double> amplitude = stackalloc double[period];
        GoertzelMath.CalculateRollingAmplitude(series.Slice(series.Length - period, period), period, amplitude);
        return Finite(amplitude[period - 1]);
    }

    /// <summary>
    /// One-sided amplitude spectrum <c>A_k = (c_k / n) |DFT(x - mean)_k|</c>, <c>k = 0..floor(n / 2)</c>, <c>c_k = 1</c> for <c>k = 0</c> and for <c>k = n / 2</c> of an even <c>n</c>, otherwise 2.
    /// Consumes <c>max(1, n (floor(n / 2) + 1) / 32)</c> budget units; <c>n &lt; 2</c> is a domain error. <paramref name="amplitudes"/> needs <c>floor(n / 2) + 1</c> entries.
    /// </summary>
    public static MathErrorCode DftMagnitude(scoped ReadOnlySpan<double> series, scoped Span<double> amplitudes, ref DslExecutionBudget budget)
    {
        int n = series.Length;
        if (n < 2)
        {
            return MathErrorCode.DomainError;
        }

        int bins = (n / 2) + 1;
        if (amplitudes.Length < bins)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!budget.TryConsume((int)Math.Max(1L, (long)n * bins / SpectrumBudgetDivisor)))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double mean = 0.0;
        for (int i = 0; i < n; i++)
        {
            mean += series[i];
        }

        mean /= n;
        Span<double> centered = stackalloc double[n];
        Span<double> real = stackalloc double[n];
        Span<double> imaginary = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            centered[i] = series[i] - mean;
        }

        FourierTransform.Forward(centered, real, imaginary);
        for (int k = 0; k < bins; k++)
        {
            double scale = k == 0 || (n % 2 == 0 && k == n / 2) ? 1.0 : 2.0;
            double value = scale / n * Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
            if (!double.IsFinite(value))
            {
                return MathErrorCode.NonFiniteResult;
            }

            amplitudes[k] = value;
        }

        return MathErrorCode.None;
    }

    // ---- Shared helpers ----

    private static bool IsProbabilityVector(scoped ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return false;
        }

        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]) || values[i] < 0.0)
            {
                return false;
            }

            sum += values[i];
        }

        return Math.Abs(sum - 1.0) <= ProbabilitySumTolerance;
    }

    private static bool ValidateGreekInputs(double s, double k, double t, double r, double sigma, double q)
        => AreFinite(s, k, t, r, sigma, q) && s > 0.0 && k > 0.0 && t > 0.0 && sigma > 0.0;

    private static void D1D2(double s, double k, double t, double r, double sigma, double q, out double d1, out double d2)
    {
        double volSqrtT = sigma * Math.Sqrt(t);
        d1 = (Math.Log(s / k) + ((r - q + (0.5 * sigma * sigma)) * t)) / volSqrtT;
        d2 = d1 - volSqrtT;
    }

    private static double NormalPdf(double z) => InvSqrtTwoPi * Math.Exp(-0.5 * z * z);

    private static bool AreFinite(double a, double b, double c, double d, double e, double f)
        => double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d) && double.IsFinite(e) && double.IsFinite(f);

    private static MathResult<double> Finite(double value)
        => double.IsFinite(value)
            ? MathResult<double>.Ok(value == 0.0 ? 0.0 : value)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
}
