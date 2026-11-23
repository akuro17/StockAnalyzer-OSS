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

    /// <summary>Rho <c>dV/dr</c> per 1.0 unit of <c>r</c>: call <c>K T e^(-rT) N(d2)</c>, put <c>-K T e^(-rT) N(-d2)</c>.</summary>
    public static MathResult<double> BsRho(double s, double k, double t, double r, double sigma, bool isCall, double q)
    {
        if (!ValidateGreekInputs(s, k, t, r, sigma, q))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        D1D2(s, k, t, r, sigma, q, out _, out double d2);
        double factor = k * t * Math.Exp(-r * t);
        return Finite(isCall
            ? factor * SpecialFunctionsMath.NormalCdf(d2)
            : -factor * SpecialFunctionsMath.NormalCdf(-d2));
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
    /// The search runs on the cash flows divided by <c>max |CF_t|</c> (values in [-1, 1]), so the convergence tolerances are relative to the amount scale and the result does not depend on it.
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

        // The IRR is the root of a homogeneous equation: dividing every cash flow by M = max |CF_t| (> 0, both signs are present) leaves it unchanged
        // and turns every tolerance below into a relative one, so the amount scale (1e-15 .. 1e15) cannot decide the convergence.
        double maxAbs = 0.0;
        for (int i = 0; i < cashFlows.Length; i++)
        {
            maxAbs = Math.Max(maxAbs, Math.Abs(cashFlows[i]));
        }

        Span<double> normalized = stackalloc double[cashFlows.Length];
        for (int i = 0; i < cashFlows.Length; i++)
        {
            normalized[i] = cashFlows[i] / maxAbs;
        }

        cashFlows = normalized;
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
    /// Consumes <c>max(1, n (floor(n / 2) + 1) / DslExecutionBudget.CheapOperationsPerUnit)</c> budget units; <c>n &lt; 2</c> is a domain error. <paramref name="amplitudes"/> needs <c>floor(n / 2) + 1</c> entries.
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

        if (!budget.TryConsume((int)Math.Max(1L, (long)n * bins / DslExecutionBudget.CheapOperationsPerUnit)))
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

    // ---- DSL v6 Domain 7: Calendar Dates, Day Count Conventions & Irregular Cash Flows ----

    /// <summary>Earliest supported serial day relative to 1970-01-01 (<c>0001-01-01</c>).</summary>
    public const int MinSerialDate = -719162;

    /// <summary>Latest supported serial day relative to 1970-01-01 (<c>9999-12-31</c>).</summary>
    public const int MaxSerialDate = 2932896;

    /// <summary>
    /// Validates a <see cref="decimal"/>-encoded integer serial date in [<see cref="MinSerialDate"/>, <see cref="MaxSerialDate"/>]
    /// (proleptic Gregorian <c>0001-01-01</c> to <c>9999-12-31</c>, Unix epoch <c>1970-01-01 = 0</c>).
    /// Shared across <c>date_serial</c>, <c>days_between</c>, <c>yearfrac</c>, <c>xnpv</c>, and <c>xirr</c>.
    /// </summary>
    public static bool TryReadSerialDate(decimal raw, out int serial)
        => DslMathValidation.TryReadInt32InRange(raw, MinSerialDate, MaxSerialDate, out serial);

    /// <summary>
    /// Converts proleptic Gregorian (<paramref name="yearRaw"/>, <paramref name="monthRaw"/>, <paramref name="dayRaw"/>)
    /// into an integer serial day relative to <c>1970-01-01 = 0</c>.
    /// </summary>
    public static MathResult<decimal> DateSerial(decimal yearRaw, decimal monthRaw, decimal dayRaw)
    {
        if (!DslMathValidation.TryReadInt32InRange(yearRaw, 1, 9999, out int yyyy) ||
            !DslMathValidation.TryReadInt32InRange(monthRaw, 1, 12, out int mm) ||
            !DslMathValidation.TryReadInt32InRange(dayRaw, 1, DaysInMonth(yyyy, mm), out int dd))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int serial = DaysFromCivil(yyyy, mm, dd);
        return TryReadSerialDate(serial, out _)
            ? MathResult<decimal>.Ok(serial)
            : MathResult<decimal>.Fail(MathErrorCode.DomainError);
    }

    /// <summary>Exact integer day difference <c>d2 - d1</c> between two serial dates.</summary>
    public static MathResult<decimal> DaysBetween(decimal d1Raw, decimal d2Raw)
    {
        if (!TryReadSerialDate(d1Raw, out int d1) || !TryReadSerialDate(d2Raw, out int d2))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        return MathResult<decimal>.Ok((decimal)checked(d2 - d1));
    }

    /// <summary>
    /// Day-count year fraction between serial dates <paramref name="d1Raw"/> and <paramref name="d2Raw"/>
    /// (<c>d2 >= d1</c>). Bases: <c>0</c> = US 30/360 NASD, <c>1</c> = ACT/ACT ISDA (differs from Excel <c>YEARFRAC</c> across leap years),
    /// <c>2</c> = ACT/360, <c>3</c> = ACT/365F, <c>4</c> = European 30E/360 (ICMA / ISDA).
    /// </summary>
    public static MathResult<decimal> YearFrac(decimal d1Raw, decimal d2Raw, decimal basisRaw)
    {
        if (!TryReadSerialDate(d1Raw, out int s1) ||
            !TryReadSerialDate(d2Raw, out int s2) ||
            s2 < s1 ||
            !DslMathValidation.TryReadInt32InRange(basisRaw, 0, 4, out int basis))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (s1 == s2)
        {
            return MathResult<decimal>.Ok(0m);
        }

        switch (basis)
        {
            case 0:
            {
                CivilFromDays(s1, out int y1, out int m1, out int d1);
                CivilFromDays(s2, out int y2, out int m2, out int d2);

                // Exact US 30/360 (NASD / SIA) step order:
                if (m1 == 2 && d1 == DaysInMonth(y1, 2))
                {
                    if (m2 == 2 && d2 == DaysInMonth(y2, 2))
                    {
                        d2 = 30;
                    }

                    d1 = 30;
                }

                if (d2 == 31 && d1 >= 30)
                {
                    d2 = 30;
                }

                if (d1 == 31)
                {
                    d1 = 30;
                }

                decimal numerator = (360m * (y2 - y1)) + (30m * (m2 - m1)) + (d2 - d1);
                return MathResult<decimal>.Ok(numerator / 360m);
            }

            case 4:
            {
                CivilFromDays(s1, out int y1, out int m1, out int d1);
                CivilFromDays(s2, out int y2, out int m2, out int d2);
                if (d1 == 31) d1 = 30;
                if (d2 == 31) d2 = 30;
                decimal numerator = (360m * (y2 - y1)) + (30m * (m2 - m1)) + (d2 - d1);
                return MathResult<decimal>.Ok(numerator / 360m);
            }

            case 1:
            {
                CivilFromDays(s1, out int y1, out _, out _);
                CivilFromDays(s2, out int y2, out _, out _);
                if (y1 == y2)
                {
                    return MathResult<decimal>.Ok((decimal)(s2 - s1) / DaysInYear(y1));
                }

                int nextJan1 = DaysFromCivil(y1 + 1, 1, 1);
                int endJan1 = DaysFromCivil(y2, 1, 1);
                decimal firstPart = (decimal)(nextJan1 - s1) / DaysInYear(y1);
                decimal fullYears = y2 - y1 - 1;
                decimal lastPart = (decimal)(s2 - endJan1) / DaysInYear(y2);
                return MathResult<decimal>.Ok(firstPart + fullYears + lastPart);
            }

            case 2:
                return MathResult<decimal>.Ok((decimal)(s2 - s1) / 360m);

            default:
                return MathResult<decimal>.Ok((decimal)(s2 - s1) / 365m);
        }
    }

    /// <summary>Irregular net present value using ACT/365F discount exponents <c>(d_i - d_0)/365</c>.</summary>
    public static MathResult<double> Xnpv(double rate, scoped ReadOnlySpan<double> cashFlows, scoped ReadOnlySpan<int> serialDates)
    {
        if (cashFlows.Length != serialDates.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (cashFlows.Length == 0 || !double.IsFinite(rate) || rate <= -1.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        int d0 = serialDates[0];
        double baseLog = Math.Log(1.0 + rate);
        double acc = 0.0;
        for (int i = 0; i < cashFlows.Length; i++)
        {
            int di = serialDates[i];
            if (di < d0)
            {
                return MathResult<double>.Fail(MathErrorCode.DomainError);
            }

            double cf = cashFlows[i];
            if (!double.IsFinite(cf))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            if (di == d0)
            {
                acc += cf;
            }
            else
            {
                double t = (di - d0) / 365.0;
                double discount = Math.Exp(-t * baseLog);
                if (!double.IsFinite(discount))
                {
                    return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
                }

                acc += cf * discount;
            }
        }

        return double.IsFinite(acc)
            ? MathResult<double>.Ok(acc == 0.0 ? 0.0 : acc)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    /// <summary>
    /// Irregular internal rate of return: solves <c>xnpv(r, cf, dates) == 0</c> on scale-normalized cash flows
    /// in log-rate space <c>u = ln(1 + r)</c> (<c>|u| &lt;= min(30, 600 / t_max)</c>) using Newton-Raphson from
    /// <c>ln(1 + guess)</c>, falling back to a piecewise-dense sign-change bracket scan selecting the root closest
    /// to <c>ln(1 + guess)</c> (leftmost interval on tie) and refining with Brent's method.
    /// </summary>
    public static MathResult<double> Xirr(
        scoped ReadOnlySpan<double> cashFlows,
        scoped ReadOnlySpan<int> serialDates,
        double guess,
        ref DslExecutionBudget budget)
    {
        if (cashFlows.Length != serialDates.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (cashFlows.Length < 2 || !double.IsFinite(guess) || guess <= -1.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        int n = cashFlows.Length;
        int d0 = serialDates[0];
        int maxDate = d0;
        bool hasPositive = false;
        bool hasNegative = false;
        double maxAbs = 0.0;
        Span<double> tYears = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            int di = serialDates[i];
            if (di < d0)
            {
                return MathResult<double>.Fail(MathErrorCode.DomainError);
            }

            if (di > maxDate)
            {
                maxDate = di;
            }

            double cf = cashFlows[i];
            if (!double.IsFinite(cf))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            hasPositive |= cf > 0.0;
            hasNegative |= cf < 0.0;
            maxAbs = Math.Max(maxAbs, Math.Abs(cf));
            tYears[i] = (di - d0) / 365.0;
        }

        if (!hasPositive || !hasNegative || maxDate <= d0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double tMaxYears = (maxDate - d0) / 365.0;
        double uBound = Math.Min(30.0, 600.0 / tMaxYears);
        double uGuess = Math.Log(1.0 + guess);
        if (!double.IsFinite(uGuess))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        Span<double> normalized = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            normalized[i] = cashFlows[i] / maxAbs;
        }

        double u = Math.Clamp(uGuess, -uBound, uBound);
        for (int iter = 0; iter < IrrMaxNewtonIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double f = XirrLogValueAndDerivative(normalized, tYears, u, out double derivative);
            if (!double.IsFinite(f))
            {
                break;
            }

            if (Math.Abs(f) <= IrrFunctionTolerance)
            {
                return RecoverRateFromLogRate(u);
            }

            if (!double.IsFinite(derivative) || Math.Abs(derivative) <= IrrDerivativeFloor)
            {
                break;
            }

            double next = u - (f / derivative);
            if (!double.IsFinite(next) || next < -uBound || next > uBound)
            {
                break;
            }

            double fNext = XirrLogValueAndDerivative(normalized, tYears, next, out _);
            if (!double.IsFinite(fNext))
            {
                break;
            }

            if (Math.Abs(fNext) <= IrrFunctionTolerance ||
                (Math.Abs(next - u) <= IrrStepTolerance * Math.Max(1.0, Math.Abs(next)) && Math.Abs(fNext) <= IrrLooseFunctionTolerance))
            {
                return RecoverRateFromLogRate(next);
            }

            u = next;
        }

        return XirrBracketedSearch(normalized, tYears, -uBound, uBound, uGuess, ref budget);
    }

    private static MathResult<double> RecoverRateFromLogRate(double u)
    {
        double rate = DslElementaryMath.Expm1Double(u);
        if (!double.IsFinite(rate) || rate <= -1.0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        return MathResult<double>.Ok(rate == 0.0 ? 0.0 : rate);
    }

    private static MathResult<double> XirrBracketedSearch(
        scoped ReadOnlySpan<double> normalized,
        scoped ReadOnlySpan<double> tYears,
        double uMin,
        double uMax,
        double uGuess,
        ref DslExecutionBudget budget)
    {
        int n = normalized.Length;
        double[] rentedCf = ArrayPool<double>.Shared.Rent(n);
        double[] rentedT = ArrayPool<double>.Shared.Rent(n);
        try
        {
            normalized.CopyTo(rentedCf);
            tYears.CopyTo(rentedT);
            var objective = new XirrLogObjective(rentedCf, rentedT, n);

            const double coreHalfWidth = 2.0;
            const int coreSteps = 96;
            const int wingSteps = 32;
            const double tieEpsilon = 1e-12;

            bool hasBracket = false;
            double bestLeft = 0.0;
            double bestRight = 0.0;
            double bestDistance = double.PositiveInfinity;

            double coreStart = Math.Max(uMin, -coreHalfWidth);
            double coreEnd = Math.Min(uMax, coreHalfWidth);

            if (uMin < coreStart)
            {
                ScanXirrLogRegion(normalized, tYears, uMin, coreStart, wingSteps, uGuess, tieEpsilon,
                    ref hasBracket, ref bestLeft, ref bestRight, ref bestDistance, ref budget, out MathErrorCode err);
                if (err != MathErrorCode.None) return MathResult<double>.Fail(err);
            }

            if (coreStart < coreEnd)
            {
                ScanXirrLogRegion(normalized, tYears, coreStart, coreEnd, coreSteps, uGuess, tieEpsilon,
                    ref hasBracket, ref bestLeft, ref bestRight, ref bestDistance, ref budget, out MathErrorCode err);
                if (err != MathErrorCode.None) return MathResult<double>.Fail(err);
            }

            if (coreEnd < uMax)
            {
                ScanXirrLogRegion(normalized, tYears, coreEnd, uMax, wingSteps, uGuess, tieEpsilon,
                    ref hasBracket, ref bestLeft, ref bestRight, ref bestDistance, ref budget, out MathErrorCode err);
                if (err != MathErrorCode.None) return MathResult<double>.Fail(err);
            }

            if (!hasBracket)
            {
                return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
            }

            MathResult<double> uRoot = DslNumericalAnalysisMath.RootBrent(objective, bestLeft, bestRight, SolverTolerance, ref budget);
            return uRoot.IsOk ? RecoverRateFromLogRate(uRoot.Value) : uRoot;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rentedCf);
            ArrayPool<double>.Shared.Return(rentedT);
        }
    }

    private static void ScanXirrLogRegion(
        scoped ReadOnlySpan<double> normalized,
        scoped ReadOnlySpan<double> tYears,
        double a,
        double b,
        int steps,
        double uGuess,
        double tieEpsilon,
        ref bool hasBracket,
        ref double bestLeft,
        ref double bestRight,
        ref double bestDistance,
        ref DslExecutionBudget budget,
        out MathErrorCode error)
    {
        error = MathErrorCode.None;
        double previousU = a;
        double previousVal = XirrLogValueAndDerivative(normalized, tYears, previousU, out _);
        for (int i = 1; i <= steps; i++)
        {
            if (!budget.TryConsume(1))
            {
                error = MathErrorCode.BudgetExceeded;
                return;
            }

            double uCur = i == steps ? b : a + ((b - a) * i / steps);
            double val = XirrLogValueAndDerivative(normalized, tYears, uCur, out _);
            if (double.IsFinite(previousVal) && double.IsFinite(val) && Math.Sign(previousVal) * Math.Sign(val) <= 0)
            {
                double dist = Math.Abs((0.5 * (previousU + uCur)) - uGuess);
                if (!hasBracket || dist < bestDistance - tieEpsilon)
                {
                    hasBracket = true;
                    bestLeft = previousU;
                    bestRight = uCur;
                    bestDistance = dist;
                }
            }

            previousU = uCur;
            previousVal = val;
        }
    }

    private static double XirrLogValueAndDerivative(
        scoped ReadOnlySpan<double> normalized,
        scoped ReadOnlySpan<double> tYears,
        double u,
        out double derivative)
    {
        double value = 0.0;
        double slope = 0.0;
        for (int i = 0; i < normalized.Length; i++)
        {
            double t = tYears[i];
            double discount = t == 0.0 ? 1.0 : Math.Exp(-u * t);
            if (!double.IsFinite(discount))
            {
                derivative = double.NaN;
                return double.NaN;
            }

            double term = normalized[i] * discount;
            value += term;
            slope -= t * term;
        }

        derivative = slope;
        return value;
    }

    private readonly struct XirrLogObjective(double[] cashFlows, double[] tYears, int count) : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = XirrLogValueAndDerivative(cashFlows.AsSpan(0, count), tYears.AsSpan(0, count), x, out _);
            return true;
        }
    }

    private static bool IsLeapYear(int yyyy)
        => (yyyy % 4 == 0 && yyyy % 100 != 0) || (yyyy % 400 == 0);

    private static int DaysInYear(int yyyy) => IsLeapYear(yyyy) ? 366 : 365;

    private static int DaysInMonth(int yyyy, int mm) => mm switch
    {
        2 => IsLeapYear(yyyy) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    private static int DaysFromCivil(int y, int m, int d)
    {
        int yAdj = y - (m <= 2 ? 1 : 0);
        int era = (yAdj >= 0 ? yAdj : yAdj - 399) / 400;
        int yoe = yAdj - (era * 400);
        int doy = (((153 * (m + (m > 2 ? -3 : 9))) + 2) / 5) + d - 1;
        int doe = (yoe * 365) + (yoe / 4) - (yoe / 100) + doy;
        return (era * 146097) + doe - 719468;
    }

    private static void CivilFromDays(int serial, out int y, out int m, out int d)
    {
        long z = serial + 719468L;
        long era = (z >= 0L ? z : z - 146096L) / 146097L;
        long doe = z - (era * 146097L);
        long yoe = (doe - (doe / 1460L) + (doe / 36524L) - (doe / 146096L)) / 365L;
        long yBase = yoe + (era * 400L);
        long doy = doe - ((365L * yoe) + (yoe / 4L) - (yoe / 100L));
        long mp = ((5L * doy) + 2L) / 153L;
        d = (int)(doy - (((153L * mp) + 2L) / 5L) + 1L);
        m = (int)(mp + (mp < 10L ? 3L : -9L));
        y = (int)(yBase + (m <= 2 ? 1L : 0L));
    }
}
