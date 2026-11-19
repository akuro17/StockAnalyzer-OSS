using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation continued-fraction term functor (<c>b_0 + a_1 / (b_1 + a_2 / (b_2 + ...))</c>)
/// for <see cref="DslNumericalAnalysisMath.CfracEval{TTerms}"/>.
/// </summary>
public interface IContinuedFractionTerms
{
    /// <summary>
    /// Supplies coefficient pair <c>(a_n, b_n)</c> for step <paramref name="n"/> (<c>n = 0</c> supplies <c>b_0</c> with <c>a_0 = 0</c>).
    /// </summary>
    bool TryGetTerm(int n, ref DslExecutionBudget budget, out double a, out double b);
}

/// <summary>
/// Zero-allocation numerical analysis kernels (central differentiation, Brent/Newton/Secant root-finding,
/// modified Lentz continued fraction evaluation, and RK4/Dormand-Prince RK45 ODE solvers) for StockAnalyzer DSL.
/// </summary>
public static class DslNumericalAnalysisMath
{
    public const int MaxRootIterations = 128;
    public const int MaxContinuedFractionDepth = 256;
    public const int MaxOdeDimension = 8;
    public const int MaxOdeSteps = 2_000;

    private const double MinStepH = 1e-9;
    private const double MaxStepH = 1e-2;
    private const double MinTol = 1e-12;
    private const double MaxTol = 1e-2;
    private const double DerivativeZeroEpsilon = 1e-12;
    private const double StateDivergenceLimit = 1e15;
    private const double LentzTiny = 1e-30;

    internal static MathErrorCode ResolveFunctorError(in DslExecutionBudget budget)
        => budget.LastError != MathErrorCode.None ? budget.LastError : MathErrorCode.DomainError;

    // ---- Central Numerical Differentiation ----

    /// <summary>
    /// Evaluates central finite-difference derivative <c>(f(x+h) - f(x-h)) / (2h)</c>.
    /// Pass <c>h = 0.0</c> to select <c>max(1e-6, 1e-5 * |x|)</c> automatically; explicit <paramref name="h"/> must satisfy <c>1e-9 &lt;= h &lt;= 1e-2</c>.
    /// </summary>
    public static MathResult<double> DiffCentral<TFunc>(
        TFunc f,
        double x,
        double h,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(x) || !double.IsFinite(h))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double scale = Math.Max(1.0, Math.Abs(x));
        double effectiveH;
        if (h == 0.0)
        {
            effectiveH = Math.Max(1e-6, 1e-5 * Math.Abs(x));
        }
        else if (h < MinStepH * scale || h > MaxStepH * scale)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }
        else
        {
            effectiveH = h;
        }

        if (!budget.TryConsume(1))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        if (!f.TryEvaluate(x + effectiveH, ref budget, out double fPlus))
        {
            return MathResult<double>.Fail(ResolveFunctorError(in budget));
        }

        if (!f.TryEvaluate(x - effectiveH, ref budget, out double fMinus))
        {
            return MathResult<double>.Fail(ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(fPlus) || !double.IsFinite(fMinus))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double deriv = (fPlus - fMinus) / (2.0 * effectiveH);
        return double.IsFinite(deriv)
            ? MathResult<double>.Ok(deriv == 0.0 ? 0.0 : deriv)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- Nonlinear Root Finding ----

    /// <summary>
    /// Finds a root of <paramref name="f"/> on bracket <c>[a, b]</c> via Brent's method
    /// (<c>tol in [1e-12, 1e-2]</c>, <c>MaxIter = 128</c>, requires <c>f(a)*f(b) &lt;= 0</c>).
    /// </summary>
    public static MathResult<double> RootBrent<TFunc>(
        TFunc f,
        double a,
        double b,
        double tol,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(tol))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (a == b || tol < MinTol || tol > MaxTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (!f.TryEvaluate(a, ref budget, out double fa))
        {
            return MathResult<double>.Fail(ResolveFunctorError(in budget));
        }

        if (!f.TryEvaluate(b, ref budget, out double fb))
        {
            return MathResult<double>.Fail(ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(fa) || !double.IsFinite(fb))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (fa == 0.0)
        {
            return MathResult<double>.Ok(a == 0.0 ? 0.0 : a);
        }

        if (fb == 0.0)
        {
            return MathResult<double>.Ok(b == 0.0 ? 0.0 : b);
        }

        if ((fa > 0.0 && fb > 0.0) || (fa < 0.0 && fb < 0.0))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double c = b;
        double fc = fb;
        double d = b - a;
        double e = d;
        const double eps = 2.2204460492503131e-16;

        for (int iter = 0; iter < MaxRootIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            if ((fb > 0.0 && fc > 0.0) || (fb < 0.0 && fc < 0.0))
            {
                c = a;
                fc = fa;
                d = b - a;
                e = d;
            }

            if (Math.Abs(fc) < Math.Abs(fb))
            {
                a = b;
                b = c;
                c = a;
                fa = fb;
                fb = fc;
                fc = fa;
            }

            double tol1 = 2.0 * eps * Math.Abs(b) + 0.5 * tol;
            double xm = 0.5 * (c - b);
            if (Math.Abs(xm) <= tol1 || fb == 0.0)
            {
                return MathResult<double>.Ok(b == 0.0 ? 0.0 : b);
            }

            if (Math.Abs(e) >= tol1 && Math.Abs(fa) > Math.Abs(fb))
            {
                double s = fb / fa;
                double p;
                double q;
                if (a == c)
                {
                    p = 2.0 * xm * s;
                    q = 1.0 - s;
                }
                else
                {
                    q = fa / fc;
                    double r = fb / fc;
                    p = s * (2.0 * xm * q * (q - r) - (b - a) * (r - 1.0));
                    q = (q - 1.0) * (r - 1.0) * (s - 1.0);
                }

                if (p > 0.0)
                {
                    q = -q;
                }

                p = Math.Abs(p);
                double min1 = 3.0 * xm * q - Math.Abs(tol1 * q);
                double min2 = Math.Abs(e * q);
                if (2.0 * p < (min1 < min2 ? min1 : min2))
                {
                    e = d;
                    d = p / q;
                }
                else
                {
                    d = xm;
                    e = d;
                }
            }
            else
            {
                d = xm;
                e = d;
            }

            a = b;
            fa = fb;
            b += Math.Abs(d) > tol1 ? d : Math.CopySign(tol1, xm);
            if (!f.TryEvaluate(b, ref budget, out fb))
            {
                return MathResult<double>.Fail(ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fb))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    /// <summary>
    /// Finds a root of <paramref name="f"/> from <paramref name="x0"/> using explicit derivative <paramref name="df"/>.
    /// Rejects <c>|df(x)| &lt; 1e-12</c> with <see cref="MathErrorCode.DivisionByZero"/>,
    /// divergence <c>|x| &gt; 1e15</c> with <see cref="MathErrorCode.ArithmeticOverflow"/>, and exceeding 128 iterations with <see cref="MathErrorCode.ConvergenceFailure"/>.
    /// </summary>
    public static MathResult<double> RootNewton<TFunc, TDeriv>(
        TFunc f,
        TDeriv df,
        double x0,
        double tol,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
        where TDeriv : struct, IScalarFunction
    {
        if (!double.IsFinite(x0) || !double.IsFinite(tol))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (tol < MinTol || tol > MaxTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double x = x0;
        for (int iter = 0; iter < MaxRootIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            if (!f.TryEvaluate(x, ref budget, out double fx))
            {
                return MathResult<double>.Fail(ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fx))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            if (Math.Abs(fx) <= tol)
            {
                return MathResult<double>.Ok(x == 0.0 ? 0.0 : x);
            }

            if (!df.TryEvaluate(x, ref budget, out double dfx))
            {
                return MathResult<double>.Fail(ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(dfx))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            if (Math.Abs(dfx) < DerivativeZeroEpsilon)
            {
                return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
            }

            double step = fx / dfx;
            double xNext = x - step;
            if (!double.IsFinite(xNext) || Math.Abs(xNext) > StateDivergenceLimit)
            {
                return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
            }

            if (Math.Abs(step) <= tol * Math.Max(1.0, Math.Abs(xNext)))
            {
                return MathResult<double>.Ok(xNext == 0.0 ? 0.0 : xNext);
            }

            x = xNext;
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    /// <summary>
    /// Newton-Raphson root-finder using central finite differences for <c>f'(x)</c>.
    /// </summary>
    public static MathResult<double> RootNewtonNumeric<TFunc>(
        TFunc f,
        double x0,
        double tol,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        var deriv = new CentralDiffScalarAdapter<TFunc>(f);
        return RootNewton(f, deriv, x0, tol, ref budget);
    }

    private readonly struct CentralDiffScalarAdapter<TFunc> : IScalarFunction
        where TFunc : struct, IScalarFunction
    {
        private readonly TFunc _inner;

        public CentralDiffScalarAdapter(TFunc inner) => _inner = inner;

        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            MathResult<double> d = DiffCentral(_inner, x, 0.0, ref budget);
            if (!d.IsOk)
            {
                budget.LastError = d.Error;
                y = double.NaN;
                return false;
            }

            y = d.Value;
            return true;
        }
    }

    /// <summary>
    /// Finds a root of <paramref name="f"/> via the secant method starting from <paramref name="x0"/>, <paramref name="x1"/>.
    /// </summary>
    public static MathResult<double> RootSecant<TFunc>(
        TFunc f,
        double x0,
        double x1,
        double tol,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(x0) || !double.IsFinite(x1) || !double.IsFinite(tol))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (x0 == x1 || tol < MinTol || tol > MaxTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (!f.TryEvaluate(x0, ref budget, out double f0) ||
            !f.TryEvaluate(x1, ref budget, out double f1))
        {
            return MathResult<double>.Fail(ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(f0) || !double.IsFinite(f1))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (Math.Abs(f0) <= tol)
        {
            return MathResult<double>.Ok(x0 == 0.0 ? 0.0 : x0);
        }

        if (Math.Abs(f1) <= tol)
        {
            return MathResult<double>.Ok(x1 == 0.0 ? 0.0 : x1);
        }

        for (int iter = 0; iter < MaxRootIterations; iter++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double denom = f1 - f0;
            if (Math.Abs(denom) < DerivativeZeroEpsilon)
            {
                return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
            }

            double step = f1 * (x1 - x0) / denom;
            double x2 = x1 - step;
            if (!double.IsFinite(x2) || Math.Abs(x2) > StateDivergenceLimit)
            {
                return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
            }

            if (!f.TryEvaluate(x2, ref budget, out double f2))
            {
                return MathResult<double>.Fail(ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(f2))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            if (Math.Abs(f2) <= tol || Math.Abs(step) <= tol * Math.Max(1.0, Math.Abs(x2)))
            {
                return MathResult<double>.Ok(x2 == 0.0 ? 0.0 : x2);
            }

            x0 = x1;
            f0 = f1;
            x1 = x2;
            f1 = f2;
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    // ---- Modified Lentz Continued Fraction Evaluation ----

    /// <summary>
    /// Evaluates a generalized continued fraction <c>b_0 + a_1/(b_1 + a_2/(b_2 + ...))</c> using Thompson-Barnett
    /// modified Lentz iteration (<c>tiny = 1e-30</c>, <c>MaxDepth = 256</c>, <c>tol in [1e-12, 1e-2]</c>).
    /// </summary>
    public static MathResult<double> CfracEval<TTerms>(
        TTerms terms,
        double tol,
        ref DslExecutionBudget budget)
        where TTerms : struct, IContinuedFractionTerms
    {
        if (!double.IsFinite(tol))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (tol < MinTol || tol > MaxTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (!terms.TryGetTerm(0, ref budget, out _, out double b0))
        {
            return MathResult<double>.Fail(ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(b0))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double f = b0 == 0.0 ? LentzTiny : b0;
        double c = f;
        double d = 0.0;

        for (int n = 1; n <= MaxContinuedFractionDepth; n++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            if (!terms.TryGetTerm(n, ref budget, out double a, out double b))
            {
                return MathResult<double>.Fail(ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(a) || !double.IsFinite(b))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            d = b + a * d;
            if (Math.Abs(d) < LentzTiny)
            {
                d = LentzTiny;
            }

            c = b + a / c;
            if (Math.Abs(c) < LentzTiny)
            {
                c = LentzTiny;
            }

            d = 1.0 / d;
            double delta = c * d;
            f *= delta;
            if (!double.IsFinite(f))
            {
                return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
            }

            if (Math.Abs(delta - 1.0) <= tol)
            {
                return MathResult<double>.Ok(f == 0.0 ? 0.0 : f);
            }
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    // ---- ODE Solvers (RK4 & Dormand-Prince RK45) ----

    /// <summary>
    /// Integrates an ODE system (<c>1 &lt;= Dimension &lt;= 8</c>) from <paramref name="t0"/> to <paramref name="t1"/>
    /// with fixed-step classical 4th-order Runge-Kutta (<c>1 &lt;= nSteps &lt;= 2,000</c>).
    /// </summary>
    public static MathErrorCode OdeRk4<TSys>(
        TSys system,
        double t0,
        double t1,
        scoped ReadOnlySpan<double> y0,
        int nSteps,
        scoped Span<double> yOut,
        ref DslExecutionBudget budget)
        where TSys : struct, IOdeSystem
    {
        int dim = system.Dimension;
        if (dim < 1 || dim > MaxOdeDimension || y0.Length < dim || yOut.Length < dim)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(t0) || !double.IsFinite(t1) || ValidateFinite(y0.Slice(0, dim)) is not MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (t0 == t1 || nSteps < 1 || nSteps > MaxOdeSteps)
        {
            return MathErrorCode.DomainError;
        }

        Span<double> y = stackalloc double[dim];
        Span<double> yTmp = stackalloc double[dim];
        Span<double> k1 = stackalloc double[dim];
        Span<double> k2 = stackalloc double[dim];
        Span<double> k3 = stackalloc double[dim];
        Span<double> k4 = stackalloc double[dim];
        y0.Slice(0, dim).CopyTo(y);

        double h = (t1 - t0) / nSteps;
        double t = t0;

        for (int step = 0; step < nSteps; step++)
        {
            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            if (!system.TryEvaluate(t, y, k1, ref budget))
            {
                return ResolveFunctorError(in budget);
            }

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + 0.5 * h * k1[i];
            if (!system.TryEvaluate(t + 0.5 * h, yTmp, k2, ref budget))
            {
                return ResolveFunctorError(in budget);
            }

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + 0.5 * h * k2[i];
            if (!system.TryEvaluate(t + 0.5 * h, yTmp, k3, ref budget))
            {
                return ResolveFunctorError(in budget);
            }

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + h * k3[i];
            double tNext = step + 1 == nSteps ? t1 : t + h;
            if (!system.TryEvaluate(tNext, yTmp, k4, ref budget))
            {
                return ResolveFunctorError(in budget);
            }

            double normSq = 0.0;
            for (int i = 0; i < dim; i++)
            {
                double next = y[i] + (h / 6.0) * (k1[i] + 2.0 * k2[i] + 2.0 * k3[i] + k4[i]);
                if (!double.IsFinite(next))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                y[i] = next;
                normSq += next * next;
            }

            if (Math.Sqrt(normSq) > StateDivergenceLimit)
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            t = tNext;
        }

        y.CopyTo(yOut);
        return MathErrorCode.None;
    }

    /// <summary>
    /// Integrates an ODE system (<c>1 &lt;= Dimension &lt;= 8</c>) from <paramref name="t0"/> to <paramref name="t1"/>
    /// with adaptive Dormand-Prince RK45 (<c>tol in [1e-12, 1e-2]</c>, <c>MaxSteps = 2,000</c>).
    /// </summary>
    public static MathErrorCode OdeRk45<TSys>(
        TSys system,
        double t0,
        double t1,
        scoped ReadOnlySpan<double> y0,
        double tol,
        scoped Span<double> yOut,
        ref DslExecutionBudget budget)
        where TSys : struct, IOdeSystem
    {
        int dim = system.Dimension;
        if (dim < 1 || dim > MaxOdeDimension || y0.Length < dim || yOut.Length < dim)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(t0) || !double.IsFinite(t1) || !double.IsFinite(tol) ||
            ValidateFinite(y0.Slice(0, dim)) is not MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (t0 == t1 || tol < MinTol || tol > MaxTol)
        {
            return MathErrorCode.DomainError;
        }

        double span = t1 - t0;
        double dir = Math.Sign(span);
        double absSpan = Math.Abs(span);
        double hMin = 1e-9 * absSpan;
        double h = Math.Min(absSpan, Math.Max(hMin, 0.1 * absSpan));

        Span<double> y = stackalloc double[dim];
        Span<double> yNext = stackalloc double[dim];
        Span<double> yTmp = stackalloc double[dim];
        Span<double> k1 = stackalloc double[dim];
        Span<double> k2 = stackalloc double[dim];
        Span<double> k3 = stackalloc double[dim];
        Span<double> k4 = stackalloc double[dim];
        Span<double> k5 = stackalloc double[dim];
        Span<double> k6 = stackalloc double[dim];
        Span<double> k7 = stackalloc double[dim];
        y0.Slice(0, dim).CopyTo(y);

        double t = t0;
        if (!system.TryEvaluate(t, y, k1, ref budget))
        {
            return ResolveFunctorError(in budget);
        }

        for (int step = 0; step < MaxOdeSteps; step++)
        {
            double remaining = Math.Abs(t1 - t);
            if (remaining <= hMin * 0.5)
            {
                y.CopyTo(yOut);
                return MathErrorCode.None;
            }

            if (h > remaining)
            {
                h = remaining;
            }

            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double hs = dir * h;

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + hs * (0.2 * k1[i]);
            if (!system.TryEvaluate(t + 0.2 * hs, yTmp, k2, ref budget)) return ResolveFunctorError(in budget);

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + hs * ((3.0 / 40.0) * k1[i] + (9.0 / 40.0) * k2[i]);
            if (!system.TryEvaluate(t + 0.3 * hs, yTmp, k3, ref budget)) return ResolveFunctorError(in budget);

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + hs * ((44.0 / 45.0) * k1[i] - (56.0 / 15.0) * k2[i] + (32.0 / 9.0) * k3[i]);
            if (!system.TryEvaluate(t + 0.8 * hs, yTmp, k4, ref budget)) return ResolveFunctorError(in budget);

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + hs * ((19372.0 / 6561.0) * k1[i] - (25360.0 / 2187.0) * k2[i] + (64448.0 / 6561.0) * k3[i] - (212.0 / 729.0) * k4[i]);
            if (!system.TryEvaluate(t + (8.0 / 9.0) * hs, yTmp, k5, ref budget)) return ResolveFunctorError(in budget);

            for (int i = 0; i < dim; i++) yTmp[i] = y[i] + hs * ((9017.0 / 3168.0) * k1[i] - (355.0 / 33.0) * k2[i] + (46732.0 / 5247.0) * k3[i] + (49.0 / 176.0) * k4[i] - (5103.0 / 18656.0) * k5[i]);
            double tEnd = Math.Abs(h - remaining) <= hMin * 0.5 ? t1 : t + hs;
            if (!system.TryEvaluate(tEnd, yTmp, k6, ref budget)) return ResolveFunctorError(in budget);

            double normSq = 0.0;
            for (int i = 0; i < dim; i++)
            {
                double val = y[i] + hs * ((35.0 / 384.0) * k1[i] + (500.0 / 1113.0) * k3[i] + (125.0 / 192.0) * k4[i] - (2187.0 / 6784.0) * k5[i] + (11.0 / 84.0) * k6[i]);
                if (!double.IsFinite(val))
                {
                    return MathErrorCode.ArithmeticOverflow;
                }

                yNext[i] = val;
                normSq += val * val;
            }

            if (Math.Sqrt(normSq) > StateDivergenceLimit)
            {
                return MathErrorCode.ArithmeticOverflow;
            }

            if (!system.TryEvaluate(tEnd, yNext, k7, ref budget)) return ResolveFunctorError(in budget);

            double errNormSq = 0.0;
            for (int i = 0; i < dim; i++)
            {
                double err = hs * (
                    (71.0 / 57600.0) * k1[i] -
                    (71.0 / 16695.0) * k3[i] +
                    (71.0 / 1920.0) * k4[i] -
                    (17253.0 / 339200.0) * k5[i] +
                    (22.0 / 525.0) * k6[i] -
                    (1.0 / 40.0) * k7[i]);
                double scale = tol * (1.0 + Math.Max(Math.Abs(y[i]), Math.Abs(yNext[i])));
                double ratio = err / scale;
                errNormSq += ratio * ratio;
            }

            double errNorm = Math.Sqrt(errNormSq / dim);
            if (!double.IsFinite(errNorm))
            {
                return MathErrorCode.NonFiniteResult;
            }

            if (errNorm <= 1.0)
            {
                yNext.CopyTo(y);
                k7.CopyTo(k1);
                t = tEnd;
                if (t == t1)
                {
                    y.CopyTo(yOut);
                    return MathErrorCode.None;
                }

                double grow = errNorm == 0.0 ? 5.0 : Math.Min(5.0, 0.9 * Math.Pow(errNorm, -0.2));
                h = Math.Max(hMin, h * grow);
            }
            else
            {
                double shrink = Math.Max(0.1, 0.9 * Math.Pow(errNorm, -0.25));
                double nextH = h * shrink;
                if (nextH < hMin)
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                h = nextH;
            }
        }

        return MathErrorCode.ConvergenceFailure;
    }

    private static MathErrorCode ValidateFinite(ReadOnlySpan<double> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                return MathErrorCode.NonFiniteResult;
            }
        }

        return MathErrorCode.None;
    }
}
