using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Weight-function selector for <see cref="DslQuadratureMath.QuadGaussSelect{TFunc}"/>.
/// </summary>
public enum GaussWeightType
{
    Legendre = 0,
    Chebyshev1 = 1,
    Chebyshev2 = 2,
    Laguerre = 3,
    Hermite = 4,
    Jacobi = 5,
    Lobatto = 6,
    Gk21 = 7,
}

/// <summary>
/// Zero-allocation numerical quadrature library (11 quadrature schemes + adaptive selector + special-function fallback)
/// using precomputed constant tables and stack-only Golub-Welsch tridiagonal QR.
/// </summary>
public static partial class DslQuadratureMath
{
    public const int MaxTrapezoidIntervals = 4_096;
    public const int MaxChebyshevOrder = 128;
    public const int MaxJacobiOrder = 64;
    public const int MaxDeLevel = 6;
    public const int MaxGk21Subdivisions = 32;
    public const int MaxRombergOrder = 10;

    private const double MinTol = 1e-12;
    private const double MaxTol = 1e-2;
    private const double DeMaxT = 3.5;

    // ---- 1 & 2: Composite Trapezoidal & Simpson Rules ----

    public static MathResult<double> QuadTrapz<TFunc>(
        TFunc f,
        double a,
        double b,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (n < 1 || n > MaxTrapezoidIntervals)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a == b)
        {
            return MathResult<double>.Ok(0.0);
        }

        if (!f.TryEvaluate(a, ref budget, out double fa) || !f.TryEvaluate(b, ref budget, out double fb))
        {
            return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(fa) || !double.IsFinite(fb))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double h = (b - a) / n;
        double sum = 0.5 * (fa + fb);
        for (int i = 1; i < n; i++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            if (!f.TryEvaluate(a + i * h, ref budget, out double fx))
            {
                return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fx))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            sum += fx;
        }

        double res = sum * h;
        return double.IsFinite(res)
            ? MathResult<double>.Ok(res == 0.0 ? 0.0 : res)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<double> QuadSimpson<TFunc>(
        TFunc f,
        double a,
        double b,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (n < 2 || n > MaxTrapezoidIntervals || (n & 1) != 0)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a == b)
        {
            return MathResult<double>.Ok(0.0);
        }

        if (!f.TryEvaluate(a, ref budget, out double fa) || !f.TryEvaluate(b, ref budget, out double fb))
        {
            return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(fa) || !double.IsFinite(fb))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double h = (b - a) / n;
        double sumOdd = 0.0;
        double sumEven = 0.0;
        for (int i = 1; i < n; i++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            if (!f.TryEvaluate(a + i * h, ref budget, out double fx))
            {
                return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fx))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            if ((i & 1) != 0)
            {
                sumOdd += fx;
            }
            else
            {
                sumEven += fx;
            }
        }

        double res = (h / 3.0) * (fa + fb + 4.0 * sumOdd + 2.0 * sumEven);
        return double.IsFinite(res)
            ? MathResult<double>.Ok(res == 0.0 ? 0.0 : res)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- 3: Double Exponential (DE / tanh-sinh, exp-sinh, sinh-sinh) ----

    /// <summary>
    /// Integrates <paramref name="f"/> via Takahasi-Mori Double Exponential quadrature.
    /// Supports finite <c>[a, b]</c> (tanh-sinh), semi-infinite <c>[a, +inf)</c> / <c>(-inf, b]</c> (exp-sinh),
    /// and infinite <c>(-inf, +inf)</c> (sinh-sinh) with <c>MaxLevel = 6</c> and <c>tol in [1e-12, 1e-2]</c>.
    /// </summary>
    public static MathResult<double> QuadDe<TFunc>(
        TFunc f,
        double a,
        double b,
        double tol,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (double.IsNaN(a) || double.IsNaN(b) || !double.IsFinite(tol))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (tol < MinTol || tol > MaxTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a == b && double.IsFinite(a))
        {
            return MathResult<double>.Ok(0.0);
        }

        double sign = 1.0;
        if (a > b)
        {
            (a, b) = (b, a);
            sign = -1.0;
        }

        double h = 0.5;
        if (!TryEvalDePoint(f, a, b, 0.0, ref budget, out double midVal, out MathErrorCode midErr))
        {
            return MathResult<double>.Fail(midErr);
        }

        double sum = midVal;
        int steps = (int)Math.Floor(DeMaxT / h);
        byte posState = 0;
        byte negState = 0;
        for (int k = 1; k <= steps; k++)
        {
            double t = k * h;
            if (!TryEvalDeSide(f, a, b, t, ref budget, sum, ref posState, ref sum, out MathErrorCode posErr))
            {
                return MathResult<double>.Fail(posErr);
            }

            if (!TryEvalDeSide(f, a, b, -t, ref budget, sum, ref negState, ref sum, out MathErrorCode negErr))
            {
                return MathResult<double>.Fail(negErr);
            }

            if (posState >= 2 && negState >= 2)
            {
                break;
            }
        }

        double prevIntegral = h * sum;
        for (int level = 1; level <= MaxDeLevel; level++)
        {
            h *= 0.5;
            double levelSum = 0.0;
            int oddMax = (int)Math.Floor(DeMaxT / h);
            posState = 0;
            negState = 0;
            for (int k = 1; k <= oddMax; k += 2)
            {
                double t = k * h;
                if (!TryEvalDeSide(f, a, b, t, ref budget, sum, ref posState, ref levelSum, out MathErrorCode posErr))
                {
                    return MathResult<double>.Fail(posErr);
                }

                if (!TryEvalDeSide(f, a, b, -t, ref budget, sum, ref negState, ref levelSum, out MathErrorCode negErr))
                {
                    return MathResult<double>.Fail(negErr);
                }

                if (posState >= 2 && negState >= 2)
                {
                    break;
                }
            }

            sum += levelSum;
            double currIntegral = h * sum;
            if (!double.IsFinite(currIntegral))
            {
                return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
            }

            if (level >= 2 && Math.Abs(currIntegral - prevIntegral) <= tol * Math.Max(1.0, Math.Abs(currIntegral)))
            {
                double finalVal = sign * currIntegral;
                return MathResult<double>.Ok(finalVal == 0.0 ? 0.0 : finalVal);
            }

            prevIntegral = currIntegral;
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    private static bool TryEvalDeSide<TFunc>(
        TFunc f,
        double a,
        double b,
        double t,
        ref DslExecutionBudget budget,
        double referenceScale,
        ref byte sideState,
        ref double accumulator,
        out MathErrorCode error)
        where TFunc : struct, IScalarFunction
    {
        error = MathErrorCode.None;
        if (sideState >= 2)
        {
            return true;
        }

        if (!TryEvalDePoint(f, a, b, t, ref budget, out double value, out MathErrorCode sideErr))
        {
            if (sideErr == MathErrorCode.BudgetExceeded)
            {
                error = sideErr;
                return false;
            }

            sideState = 2;
            return true;
        }

        accumulator += value;
        double scale = Math.Max(Math.Abs(referenceScale), Math.Abs(accumulator));
        if (Math.Abs(t) >= 2.0 && Math.Abs(value) <= 1e-18 * scale)
        {
            sideState = sideState == 1 ? (byte)2 : (byte)1;
        }
        else
        {
            sideState = 0;
        }

        return true;
    }

    private static bool TryEvalDePoint<TFunc>(
        TFunc f,
        double a,
        double b,
        double t,
        ref DslExecutionBudget budget,
        out double weighted,
        out MathErrorCode error)
        where TFunc : struct, IScalarFunction
    {
        weighted = 0.0;
        error = MathErrorCode.None;
        if (!budget.TryConsume(1))
        {
            error = MathErrorCode.BudgetExceeded;
            return false;
        }

        const double halfPi = MathConstants.HalfPi;
        double sh = Math.Sinh(t);
        double ch = Math.Cosh(t);
        double u = halfPi * sh;

        double x;
        double weight;
        if (double.IsFinite(a) && double.IsFinite(b))
        {
            double absU = Math.Abs(u);
            if (absU > 22.0)
            {
                return true;
            }

            double len = b - a;
            double exp2U = Math.Exp(2.0 * absU);
            double delta = len / (1.0 + exp2U);
            if (delta < 1e-9 * len && delta < 1e-27 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b))))
            {
                return true;
            }

            x = u >= 0.0 ? b - delta : a + delta;
            if (x <= a || x >= b)
            {
                return true;
            }

            double c = Math.Cosh(u);
            weight = 0.5 * len * (halfPi * ch) / (c * c);
        }
        else if (double.IsFinite(a) && double.IsPositiveInfinity(b))
        {
            if (u > 690.0 || u < -690.0)
            {
                return true;
            }

            double eu = Math.Exp(u);
            x = a + eu;
            weight = halfPi * ch * eu;
        }
        else if (double.IsNegativeInfinity(a) && double.IsFinite(b))
        {
            if (u > 690.0 || u < -690.0)
            {
                return true;
            }

            double eu = Math.Exp(u);
            x = b - eu;
            weight = halfPi * ch * eu;
        }
        else
        {
            if (Math.Abs(u) > 690.0)
            {
                return true;
            }

            x = Math.Sinh(u);
            weight = halfPi * ch * Math.Cosh(u);
        }

        if (!double.IsFinite(x) || !double.IsFinite(weight))
        {
            return true;
        }

        budget.LastError = MathErrorCode.None;
        if (!f.TryEvaluate(x, ref budget, out double fx))
        {
            error = DslNumericalAnalysisMath.ResolveFunctorError(in budget);
            return false;
        }

        if (!double.IsFinite(fx))
        {
            error = MathErrorCode.NonFiniteResult;
            return false;
        }

        weighted = fx * weight;
        if (!double.IsFinite(weighted))
        {
            weighted = 0.0;
        }

        return true;
    }

    // ---- 4: Unified Gaussian Quadrature Router ----

    public static MathResult<double> QuadGaussSelect<TFunc>(
        TFunc f,
        double a,
        double b,
        GaussWeightType weightType,
        int n,
        double tol,
        ref DslExecutionBudget budget,
        double alpha = 0.0,
        double beta = 0.0)
        where TFunc : struct, IScalarFunction => weightType switch
    {
        GaussWeightType.Legendre => QuadGaussLegendre(f, a, b, n, ref budget),
        GaussWeightType.Chebyshev1 => QuadGaussChebyshev(f, 1, n, ref budget),
        GaussWeightType.Chebyshev2 => QuadGaussChebyshev(f, 2, n, ref budget),
        GaussWeightType.Laguerre => QuadGaussLaguerre(f, alpha, n, ref budget),
        GaussWeightType.Hermite => QuadGaussHermite(f, n, ref budget),
        GaussWeightType.Jacobi => QuadGaussJacobi(f, alpha, beta, n, ref budget),
        GaussWeightType.Lobatto => QuadGaussLobatto(f, a, b, n, ref budget),
        GaussWeightType.Gk21 => QuadGk21(f, a, b, tol, ref budget),
        _ => MathResult<double>.Fail(MathErrorCode.DomainError),
    };

    // ---- 5: Gauss-Legendre (n in {4, 8, 16, 32, 64}) ----

    public static MathResult<double> QuadGaussLegendre<TFunc>(
        TFunc f,
        double a,
        double b,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        ReadOnlySpan<double> nodes;
        ReadOnlySpan<double> weights;
        switch (n)
        {
            case 4: nodes = Gl4Nodes; weights = Gl4Weights; break;
            case 8: nodes = Gl8Nodes; weights = Gl8Weights; break;
            case 16: nodes = Gl16Nodes; weights = Gl16Weights; break;
            case 32: nodes = Gl32Nodes; weights = Gl32Weights; break;
            case 64: nodes = Gl64Nodes; weights = Gl64Weights; break;
            default: return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a == b)
        {
            return MathResult<double>.Ok(0.0);
        }

        double mid = 0.5 * (a + b);
        double half = 0.5 * (b - a);
        double sum = 0.0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!budget.TryConsume(2))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double offset = half * nodes[i];
            if (!f.TryEvaluate(mid + offset, ref budget, out double fp) ||
                !f.TryEvaluate(mid - offset, ref budget, out double fm))
            {
                return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fp) || !double.IsFinite(fm))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            sum += weights[i] * (fp + fm);
        }

        double res = half * sum;
        return double.IsFinite(res)
            ? MathResult<double>.Ok(res == 0.0 ? 0.0 : res)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- 6: Gauss-Chebyshev (Kind 1 & Kind 2 on [-1, 1], Closed-Form Trig Nodes) ----

    public static MathResult<double> QuadGaussChebyshev<TFunc>(
        TFunc f,
        int kind,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (kind is not (1 or 2) || n < 2 || n > MaxChebyshevOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double sum = 0.0;
        if (kind == 1)
        {
            double w = Math.PI / n;
            for (int k = 1; k <= n; k++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
                }

                double x = Math.Cos(((2.0 * k - 1.0) * Math.PI) / (2.0 * n));
                if (!f.TryEvaluate(x, ref budget, out double fx))
                {
                    return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
                }

                if (!double.IsFinite(fx))
                {
                    return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
                }

                sum += w * fx;
            }
        }
        else
        {
            double wScale = Math.PI / (n + 1);
            for (int k = 1; k <= n; k++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
                }

                double angle = (k * Math.PI) / (n + 1);
                (double s, double x) = Math.SinCos(angle);
                if (!f.TryEvaluate(x, ref budget, out double fx))
                {
                    return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
                }

                if (!double.IsFinite(fx))
                {
                    return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
                }

                sum += (wScale * s * s) * fx;
            }
        }

        return double.IsFinite(sum)
            ? MathResult<double>.Ok(sum == 0.0 ? 0.0 : sum)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- 7: Gauss-Laguerre on [0, +inf) with weight x^alpha * e^(-x) ----

    public static MathResult<double> QuadGaussLaguerre<TFunc>(
        TFunc f,
        double alpha,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(alpha))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (alpha <= -1.0 || n is not (4 or 8 or 16 or 32 or 64))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (alpha == 0.0)
        {
            ReadOnlySpan<double> nodes = n switch
            {
                4 => Lag4Nodes,
                8 => Lag8Nodes,
                16 => Lag16Nodes,
                32 => Lag32Nodes,
                _ => Lag64Nodes,
            };
            ReadOnlySpan<double> weights = n switch
            {
                4 => Lag4Weights,
                8 => Lag8Weights,
                16 => Lag16Weights,
                32 => Lag32Weights,
                _ => Lag64Weights,
            };
            return EvaluateNodeWeightSum(f, nodes, weights, ref budget);
        }

        Span<double> diag = stackalloc double[n];
        Span<double> sub = stackalloc double[n - 1];
        for (int k = 0; k < n; k++)
        {
            diag[k] = 2.0 * k + alpha + 1.0;
            if (k < n - 1)
            {
                double kf = k + 1.0;
                sub[k] = Math.Sqrt(kf * (kf + alpha));
            }
        }

        double logMu0 = DslSpecialFunctionsMath.LogAbsGammaDouble(alpha + 1.0, out _);
        double mu0 = Math.Exp(logMu0);
        if (!double.IsFinite(mu0))
        {
            return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        Span<double> gwNodes = stackalloc double[n];
        Span<double> gwWeights = stackalloc double[n];
        MathErrorCode gwErr = ComputeGolubWelsch(diag, sub, mu0, gwNodes, gwWeights);
        if (gwErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(gwErr);
        }

        return EvaluateNodeWeightSum(f, gwNodes, gwWeights, ref budget);
    }

    // ---- 8: Gauss-Hermite on (-inf, +inf) with weight e^(-x^2) ----

    public static MathResult<double> QuadGaussHermite<TFunc>(
        TFunc f,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        ReadOnlySpan<double> nodes;
        ReadOnlySpan<double> weights;
        switch (n)
        {
            case 4: nodes = Gh4Nodes; weights = Gh4Weights; break;
            case 8: nodes = Gh8Nodes; weights = Gh8Weights; break;
            case 16: nodes = Gh16Nodes; weights = Gh16Weights; break;
            case 32: nodes = Gh32Nodes; weights = Gh32Weights; break;
            case 64: nodes = Gh64Nodes; weights = Gh64Weights; break;
            default: return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double sum = 0.0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!budget.TryConsume(2))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double x = nodes[i];
            if (!f.TryEvaluate(x, ref budget, out double fp) ||
                !f.TryEvaluate(-x, ref budget, out double fm))
            {
                return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fp) || !double.IsFinite(fm))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            sum += weights[i] * (fp + fm);
        }

        return double.IsFinite(sum)
            ? MathResult<double>.Ok(sum == 0.0 ? 0.0 : sum)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- 9: Gauss-Jacobi on [-1, 1] with weight (1-x)^alpha * (1+x)^beta via stackalloc Golub-Welsch ----

    public static MathResult<double> QuadGaussJacobi<TFunc>(
        TFunc f,
        double alpha,
        double beta,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(alpha) || !double.IsFinite(beta))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (alpha <= -1.0 || beta <= -1.0 || n < 2 || n > MaxJacobiOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double ab = alpha + beta;
        double logMu0 = (ab + 1.0) * Math.Log(2.0) +
                        DslSpecialFunctionsMath.LogAbsGammaDouble(alpha + 1.0, out _) +
                        DslSpecialFunctionsMath.LogAbsGammaDouble(beta + 1.0, out _) -
                        DslSpecialFunctionsMath.LogAbsGammaDouble(ab + 2.0, out _);
        double mu0 = Math.Exp(logMu0);
        if (!double.IsFinite(mu0))
        {
            return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        Span<double> diag = stackalloc double[n];
        Span<double> sub = stackalloc double[n - 1];
        diag[0] = (beta - alpha) / (ab + 2.0);
        double b2MinusA2 = beta * beta - alpha * alpha;
        for (int k = 1; k < n; k++)
        {
            double s = 2.0 * k + ab;
            diag[k] = b2MinusA2 / (s * (s + 2.0));
        }

        for (int k = 1; k < n; k++)
        {
            double kf = k;
            double s = 2.0 * kf + ab;
            if (k == 1 && Math.Abs(ab + 1.0) < 1e-12)
            {
                sub[0] = Math.Sqrt((4.0 * (1.0 + alpha) * (1.0 + beta)) / (2.0 + ab));
            }
            else
            {
                double num = 4.0 * kf * (kf + alpha) * (kf + beta) * (kf + ab);
                double den = s * s * (s * s - 1.0);
                sub[k - 1] = Math.Sqrt(num / den);
            }
        }

        Span<double> nodes = stackalloc double[n];
        Span<double> weights = stackalloc double[n];
        MathErrorCode gwErr = ComputeGolubWelsch(diag, sub, mu0, nodes, weights);
        if (gwErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(gwErr);
        }

        return EvaluateNodeWeightSum(f, nodes, weights, ref budget);
    }

    // ---- 10: Gauss-Lobatto (n in {5, 9, 17, 33}) ----

    public static MathResult<double> QuadGaussLobatto<TFunc>(
        TFunc f,
        double a,
        double b,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        ReadOnlySpan<double> nodes;
        ReadOnlySpan<double> weights;
        switch (n)
        {
            case 5: nodes = Lob5Nodes; weights = Lob5Weights; break;
            case 9: nodes = Lob9Nodes; weights = Lob9Weights; break;
            case 17: nodes = Lob17Nodes; weights = Lob17Weights; break;
            case 33: nodes = Lob33Nodes; weights = Lob33Weights; break;
            default: return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a == b)
        {
            return MathResult<double>.Ok(0.0);
        }

        double mid = 0.5 * (a + b);
        double half = 0.5 * (b - a);

        if (!budget.TryConsume(1))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        if (!f.TryEvaluate(mid, ref budget, out double fMid))
        {
            return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(fMid))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double sum = weights[0] * fMid;
        for (int i = 1; i < nodes.Length; i++)
        {
            if (!budget.TryConsume(2))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double xPos = i == nodes.Length - 1 ? b : mid + half * nodes[i];
            double xNeg = i == nodes.Length - 1 ? a : mid - half * nodes[i];
            if (!f.TryEvaluate(xPos, ref budget, out double fp) ||
                !f.TryEvaluate(xNeg, ref budget, out double fm))
            {
                return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fp) || !double.IsFinite(fm))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            sum += weights[i] * (fp + fm);
        }

        double res = half * sum;
        return double.IsFinite(res)
            ? MathResult<double>.Ok(res == 0.0 ? 0.0 : res)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    // ---- 11: Adaptive Gauss-Kronrod G10/K21 ----

    public static MathResult<double> QuadGk21<TFunc>(
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

        if (tol < MinTol || tol > MaxTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a == b)
        {
            return MathResult<double>.Ok(0.0);
        }

        double sign = 1.0;
        if (a > b)
        {
            (a, b) = (b, a);
            sign = -1.0;
        }

        const int maxCapacity = MaxGk21Subdivisions + 1;
        Span<double> segA = stackalloc double[maxCapacity];
        Span<double> segB = stackalloc double[maxCapacity];
        Span<double> segRes = stackalloc double[maxCapacity];
        Span<double> segErr = stackalloc double[maxCapacity];

        MathErrorCode initErr = EvaluateGk21Panel(f, a, b, ref budget, out double res0, out double err0, out double absSum0);
        if (initErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(initErr);
        }

        double totalRes = res0;
        double totalErr = err0;
        double totalAbs = absSum0;
        segA[0] = a;
        segB[0] = b;
        segRes[0] = res0;
        segErr[0] = err0;
        int count = 1;

        for (int sub = 0; sub < MaxGk21Subdivisions; sub++)
        {
            double reqTol = Math.Max(tol * Math.Max(1.0, Math.Abs(totalRes)), 1e-13 * totalAbs);
            if (totalErr <= reqTol)
            {
                double okVal = sign * totalRes;
                return MathResult<double>.Ok(okVal == 0.0 ? 0.0 : okVal);
            }

            int worst = 0;
            double maxErr = segErr[0];
            for (int i = 1; i < count; i++)
            {
                if (segErr[i] > maxErr)
                {
                    maxErr = segErr[i];
                    worst = i;
                }
            }

            double leftA = segA[worst];
            double rightB = segB[worst];
            double mid = 0.5 * (leftA + rightB);
            if (mid == leftA || mid == rightB)
            {
                break;
            }

            MathErrorCode errLeft = EvaluateGk21Panel(f, leftA, mid, ref budget, out double res1, out double err1, out double abs1);
            if (errLeft != MathErrorCode.None)
            {
                return MathResult<double>.Fail(errLeft);
            }

            MathErrorCode errRight = EvaluateGk21Panel(f, mid, rightB, ref budget, out double res2, out double err2, out double abs2);
            if (errRight != MathErrorCode.None)
            {
                return MathResult<double>.Fail(errRight);
            }

            totalRes = totalRes - segRes[worst] + res1 + res2;
            totalErr = totalErr - segErr[worst] + err1 + err2;
            totalAbs = Math.Max(totalAbs, abs1 + abs2);

            segB[worst] = mid;
            segRes[worst] = res1;
            segErr[worst] = err1;

            segA[count] = mid;
            segB[count] = rightB;
            segRes[count] = res2;
            segErr[count] = err2;
            count++;
        }

        double finalTol = Math.Max(tol * Math.Max(1.0, Math.Abs(totalRes)), 1e-13 * totalAbs);
        if (totalErr <= finalTol && double.IsFinite(totalRes))
        {
            double okVal = sign * totalRes;
            return MathResult<double>.Ok(okVal == 0.0 ? 0.0 : okVal);
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    private static MathErrorCode EvaluateGk21Panel<TFunc>(
        TFunc f,
        double a,
        double b,
        ref DslExecutionBudget budget,
        out double result,
        out double absError,
        out double resAbs)
        where TFunc : struct, IScalarFunction
    {
        result = 0.0;
        absError = 0.0;
        resAbs = 0.0;

        if (!budget.TryConsume(21))
        {
            return MathErrorCode.BudgetExceeded;
        }

        double mid = 0.5 * (a + b);
        double half = 0.5 * (b - a);
        if (!f.TryEvaluate(mid, ref budget, out double fMid))
        {
            return DslNumericalAnalysisMath.ResolveFunctorError(in budget);
        }

        if (!double.IsFinite(fMid))
        {
            return MathErrorCode.NonFiniteResult;
        }

        double sumK21 = Gk21Weights[10] * fMid;
        double sumG10 = 0.0;
        double sumAbs = Gk21Weights[10] * Math.Abs(fMid);

        for (int i = 0; i < 10; i++)
        {
            double dx = half * Gk21Nodes[i];
            if (!f.TryEvaluate(mid + dx, ref budget, out double f1) ||
                !f.TryEvaluate(mid - dx, ref budget, out double f2))
            {
                return DslNumericalAnalysisMath.ResolveFunctorError(in budget);
            }

            if (!double.IsFinite(f1) || !double.IsFinite(f2))
            {
                return MathErrorCode.NonFiniteResult;
            }

            double fSum = f1 + f2;
            sumK21 += Gk21Weights[i] * fSum;
            sumAbs += Gk21Weights[i] * (Math.Abs(f1) + Math.Abs(f2));
            if ((i & 1) == 1)
            {
                sumG10 += Gk10Weights[i >> 1] * fSum;
            }
        }

        double resK = sumK21 * half;
        double resG = sumG10 * half;
        if (!double.IsFinite(resK) || !double.IsFinite(resG))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        result = resK;
        resAbs = sumAbs * Math.Abs(half);
        absError = Math.Abs(resK - resG);
        return MathErrorCode.None;
    }

    // ---- 12: Romberg Integration (Richardson Extrapolation, MaxOrder = 10) ----

    public static MathResult<double> QuadRomberg<TFunc>(
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

        if (tol < MinTol || tol > MaxTol)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (a == b)
        {
            return MathResult<double>.Ok(0.0);
        }

        if (!f.TryEvaluate(a, ref budget, out double fa) || !f.TryEvaluate(b, ref budget, out double fb))
        {
            return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        }

        if (!double.IsFinite(fa) || !double.IsFinite(fb))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        Span<double> rPrev = stackalloc double[MaxRombergOrder + 1];
        Span<double> rCurr = stackalloc double[MaxRombergOrder + 1];

        double h = b - a;
        rPrev[0] = 0.5 * h * (fa + fb);
        int nSteps = 1;

        for (int i = 1; i <= MaxRombergOrder; i++)
        {
            h *= 0.5;
            double midSum = 0.0;
            for (int k = 1; k <= nSteps; k++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
                }

                double x = a + (2.0 * k - 1.0) * h;
                if (!f.TryEvaluate(x, ref budget, out double fx))
                {
                    return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
                }

                if (!double.IsFinite(fx))
                {
                    return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
                }

                midSum += fx;
            }

            rCurr[0] = 0.5 * rPrev[0] + h * midSum;
            double factor = 4.0;
            for (int j = 1; j <= i; j++)
            {
                rCurr[j] = rCurr[j - 1] + (rCurr[j - 1] - rPrev[j - 1]) / (factor - 1.0);
                factor *= 4.0;
            }

            if (!double.IsFinite(rCurr[i]))
            {
                return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
            }

            if (i >= 2 && Math.Abs(rCurr[i] - rPrev[i - 1]) <= tol * Math.Max(1.0, Math.Abs(rCurr[i])))
            {
                double okVal = rCurr[i];
                return MathResult<double>.Ok(okVal == 0.0 ? 0.0 : okVal);
            }

            rCurr.Slice(0, i + 1).CopyTo(rPrev);
            nSteps *= 2;
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    // ---- 13: Special-Function Quadrature Fallback ----

    /// <summary>
    /// Fixed-tolerance (<c>tol = 1e-12</c>) quadrature bridge for special functions and tails,
    /// sharing the caller's <see cref="DslExecutionBudget"/>.
    /// </summary>
    public static MathResult<double> SpecialViaQuad<TFunc>(
        TFunc f,
        double a,
        double b,
        bool preferDoubleExponential,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
        => preferDoubleExponential || double.IsInfinity(a) || double.IsInfinity(b)
            ? QuadDe(f, a, b, MinTol, ref budget)
            : QuadGk21(f, a, b, MinTol, ref budget);

    // ---- DSL v5 Full-Order Gaussian Entries ----
    // The kernels above keep their original order sets (Chebyshev n >= 2, Laguerre/Hermite n in {4,8,16,32,64}, Jacobi n >= 2,
    // Lobatto n in {5,9,17,33}). The *Dsl entries below widen the DSL contract to every order of the v5 specification:
    // they delegate to the original kernel whenever it supports the requested order and compute the remaining orders here.

    /// <summary>Largest order accepted by the DSL Laguerre, Hermite, Jacobi and Lobatto entries.</summary>
    public const int MaxDslGaussOrder = MaxJacobiOrder;

    /// <summary>Smallest order of the DSL Lobatto entry (both end points of <c>[a, b]</c> are nodes).</summary>
    public const int MinLobattoOrder = 2;

    private const double JacobiSingularMomentTolerance = 1e-12;

    /// <summary>Gauss-Chebyshev (kind 1 or 2) for <c>n</c> in <c>[1, 128]</c>; <c>n &gt;= 2</c> delegates to <see cref="QuadGaussChebyshev{TFunc}"/>.</summary>
    public static MathResult<double> QuadGaussChebyshevDsl<TFunc>(
        TFunc f,
        int kind,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (kind is not (1 or 2) || n < 1 || n > MaxChebyshevOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (n >= 2)
        {
            return QuadGaussChebyshev(f, kind, n, ref budget);
        }

        // n == 1: node 0; weight pi (kind 1, integral of f / sqrt(1 - x^2)) or pi / 2 (kind 2, integral of f * sqrt(1 - x^2)).
        double weight = kind == 1 ? Math.PI : 0.5 * Math.PI;
        Span<double> node = [0.0];
        Span<double> single = [weight];
        return EvaluateNodeWeightSum(f, node, single, ref budget);
    }

    /// <summary>Gauss-Laguerre (weight <c>x^alpha e^-x</c>) for <c>n</c> in <c>[1, 64]</c>; table orders delegate to <see cref="QuadGaussLaguerre{TFunc}"/>.</summary>
    public static MathResult<double> QuadGaussLaguerreDsl<TFunc>(
        TFunc f,
        double alpha,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(alpha))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (alpha <= -1.0 || n < 1 || n > MaxDslGaussOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (n is 4 or 8 or 16 or 32 or 64)
        {
            return QuadGaussLaguerre(f, alpha, n, ref budget);
        }

        Span<double> diag = stackalloc double[n];
        Span<double> sub = stackalloc double[n - 1];
        for (int k = 0; k < n; k++)
        {
            diag[k] = 2.0 * k + alpha + 1.0;
            if (k < n - 1)
            {
                double kf = k + 1.0;
                sub[k] = Math.Sqrt(kf * (kf + alpha));
            }
        }

        double mu0 = Math.Exp(DslSpecialFunctionsMath.LogAbsGammaDouble(alpha + 1.0, out _));
        if (!double.IsFinite(mu0))
        {
            return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        Span<double> nodes = stackalloc double[n];
        Span<double> weights = stackalloc double[n];
        MathErrorCode gwErr = ComputeGolubWelsch(diag, sub, mu0, nodes, weights);
        return gwErr != MathErrorCode.None
            ? MathResult<double>.Fail(gwErr)
            : EvaluateNodeWeightSum(f, nodes, weights, ref budget);
    }

    /// <summary>Gauss-Hermite (weight <c>e^-x^2</c>) for <c>n</c> in <c>[1, 64]</c>; table orders delegate to <see cref="QuadGaussHermite{TFunc}"/>.</summary>
    public static MathResult<double> QuadGaussHermiteDsl<TFunc>(
        TFunc f,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (n < 1 || n > MaxDslGaussOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (n is 4 or 8 or 16 or 32 or 64)
        {
            return QuadGaussHermite(f, n, ref budget);
        }

        // Golub-Welsch for the physicists' Hermite weight: a_k = 0, b_k = sqrt(k / 2), mu_0 = sqrt(pi).
        Span<double> diag = stackalloc double[n];
        Span<double> sub = stackalloc double[n - 1];
        diag.Clear();
        for (int k = 1; k < n; k++)
        {
            sub[k - 1] = Math.Sqrt(0.5 * k);
        }

        Span<double> nodes = stackalloc double[n];
        Span<double> weights = stackalloc double[n];
        MathErrorCode gwErr = ComputeGolubWelsch(diag, sub, Math.Sqrt(Math.PI), nodes, weights);
        return gwErr != MathErrorCode.None
            ? MathResult<double>.Fail(gwErr)
            : EvaluateNodeWeightSum(f, nodes, weights, ref budget);
    }

    /// <summary>Gauss-Jacobi for <c>n</c> in <c>[1, 64]</c>; <c>n &gt;= 2</c> delegates to <see cref="QuadGaussJacobi{TFunc}"/>.</summary>
    public static MathResult<double> QuadGaussJacobiDsl<TFunc>(
        TFunc f,
        double alpha,
        double beta,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(alpha) || !double.IsFinite(beta))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (alpha <= -1.0 || beta <= -1.0 || n < 1 || n > MaxDslGaussOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (n >= 2)
        {
            return QuadGaussJacobi(f, alpha, beta, n, ref budget);
        }

        // n == 1: node (beta - alpha) / (alpha + beta + 2), weight mu_0.
        if (!TryJacobiZerothMoment(alpha, beta, out double mu0))
        {
            return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        Span<double> node = [(beta - alpha) / (alpha + beta + 2.0)];
        Span<double> weight = [mu0];
        return EvaluateNodeWeightSum(f, node, weight, ref budget);
    }

    /// <summary>
    /// Gauss-Lobatto on <c>[a, b]</c> (<c>a &lt; b</c>) for <c>n</c> in <c>[2, 64]</c>. Table orders {5, 9, 17, 33} delegate to
    /// <see cref="QuadGaussLobatto{TFunc}"/>; <c>n == 2</c> is the trapezoidal rule; other orders use the interior Gauss-Jacobi(1, 1) nodes of order
    /// <c>n - 2</c> (weights <c>w_J / (1 - t^2)</c>) plus the end points with weight <c>2 / (n (n - 1))</c>.
    /// </summary>
    public static MathResult<double> QuadGaussLobattoDsl<TFunc>(
        TFunc f,
        double a,
        double b,
        int n,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!double.IsFinite(a) || !double.IsFinite(b))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (!(a < b) || n < MinLobattoOrder || n > MaxDslGaussOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (n is 5 or 9 or 17 or 33)
        {
            return QuadGaussLobatto(f, a, b, n, ref budget);
        }

        double mid = 0.5 * (a + b);
        double half = 0.5 * (b - a);
        double endWeight = 2.0 / ((double)n * (n - 1));
        int interior = n - 2;

        Span<double> nodes = stackalloc double[n];
        Span<double> weights = stackalloc double[n];
        nodes[0] = -1.0;
        weights[0] = endWeight;
        nodes[n - 1] = 1.0;
        weights[n - 1] = endWeight;

        if (interior > 0)
        {
            Span<double> diag = stackalloc double[interior];
            Span<double> sub = stackalloc double[interior - 1];
            FillJacobiRecurrence(1.0, 1.0, diag, sub);
            if (!TryJacobiZerothMoment(1.0, 1.0, out double mu0))
            {
                return MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
            }

            Span<double> jacobiNodes = stackalloc double[interior];
            Span<double> jacobiWeights = stackalloc double[interior];
            MathErrorCode gwErr = ComputeGolubWelsch(diag, sub, mu0, jacobiNodes, jacobiWeights);
            if (gwErr != MathErrorCode.None)
            {
                return MathResult<double>.Fail(gwErr);
            }

            for (int i = 0; i < interior; i++)
            {
                double t = jacobiNodes[i];
                nodes[i + 1] = t;
                weights[i + 1] = jacobiWeights[i] / (1.0 - t * t);
            }
        }

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            double x = i == 0 ? a : i == n - 1 ? b : mid + half * nodes[i];
            if (!f.TryEvaluate(x, ref budget, out double fx))
            {
                return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fx))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            sum += weights[i] * fx;
        }

        double res = half * sum;
        return double.IsFinite(res)
            ? MathResult<double>.Ok(res == 0.0 ? 0.0 : res)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    /// <summary><c>mu_0 = 2^(alpha+beta+1) Gamma(alpha+1) Gamma(beta+1) / Gamma(alpha+beta+2)</c> of the Jacobi weight function.</summary>
    private static bool TryJacobiZerothMoment(double alpha, double beta, out double mu0)
    {
        double ab = alpha + beta;
        double logMu0 = (ab + 1.0) * Math.Log(2.0) +
                        DslSpecialFunctionsMath.LogAbsGammaDouble(alpha + 1.0, out _) +
                        DslSpecialFunctionsMath.LogAbsGammaDouble(beta + 1.0, out _) -
                        DslSpecialFunctionsMath.LogAbsGammaDouble(ab + 2.0, out _);
        mu0 = Math.Exp(logMu0);
        return double.IsFinite(mu0);
    }

    /// <summary>Fills the Jacobi three-term recurrence (<c>diag.Length</c> = order, <c>sub.Length</c> = order - 1) used by Golub-Welsch.</summary>
    private static void FillJacobiRecurrence(double alpha, double beta, scoped Span<double> diag, scoped Span<double> sub)
    {
        double ab = alpha + beta;
        diag[0] = (beta - alpha) / (ab + 2.0);
        double b2MinusA2 = beta * beta - alpha * alpha;
        for (int k = 1; k < diag.Length; k++)
        {
            double s = 2.0 * k + ab;
            diag[k] = b2MinusA2 / (s * (s + 2.0));
        }

        for (int k = 1; k < diag.Length; k++)
        {
            double kf = k;
            double s = 2.0 * kf + ab;
            if (k == 1 && Math.Abs(ab + 1.0) < JacobiSingularMomentTolerance)
            {
                sub[0] = Math.Sqrt((4.0 * (1.0 + alpha) * (1.0 + beta)) / (2.0 + ab));
            }
            else
            {
                double num = 4.0 * kf * (kf + alpha) * (kf + beta) * (kf + ab);
                double den = s * s * (s * s - 1.0);
                sub[k - 1] = Math.Sqrt(num / den);
            }
        }
    }

    // ---- Shared Golub-Welsch Tridiagonal QL & Summation Helpers ----

    private static MathResult<double> EvaluateNodeWeightSum<TFunc>(
        TFunc f,
        scoped ReadOnlySpan<double> nodes,
        scoped ReadOnlySpan<double> weights,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        double sum = 0.0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!budget.TryConsume(1))
            {
                return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            }

            if (!f.TryEvaluate(nodes[i], ref budget, out double fx))
            {
                return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            }

            if (!double.IsFinite(fx))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }

            sum += weights[i] * fx;
        }

        return double.IsFinite(sum)
            ? MathResult<double>.Ok(sum == 0.0 ? 0.0 : sum)
            : MathResult<double>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    private static MathErrorCode ComputeGolubWelsch(
        scoped ReadOnlySpan<double> diag,
        scoped ReadOnlySpan<double> subdiag,
        double mu0,
        scoped Span<double> nodes,
        scoped Span<double> weights)
    {
        int n = diag.Length;
        Span<double> d = stackalloc double[n];
        Span<double> e = stackalloc double[n];
        Span<double> z0 = stackalloc double[n];
        diag.CopyTo(d);
        e.Clear();
        subdiag.CopyTo(e);
        z0.Clear();
        z0[0] = 1.0;

        for (int l = 0; l < n; l++)
        {
            int iter = 0;
            while (true)
            {
                int m = l;
                while (m < n - 1 && Math.Abs(e[m]) > 1e-16 * (Math.Abs(d[m]) + Math.Abs(d[m + 1])))
                {
                    m++;
                }

                if (m == l)
                {
                    break;
                }

                if (++iter > 60)
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                double g = (d[l + 1] - d[l]) / (2.0 * e[l]);
                double r = Math.Sqrt(g * g + 1.0);
                g = d[m] - d[l] + e[l] / (g + (g >= 0.0 ? r : -r));
                double s = 1.0;
                double c = 1.0;
                double p = 0.0;
                bool broken = false;

                for (int i = m - 1; i >= l; i--)
                {
                    double f = s * e[i];
                    double b = c * e[i];
                    double r2 = Math.Sqrt(f * f + g * g);
                    e[i + 1] = r2;
                    if (r2 == 0.0)
                    {
                        d[i + 1] -= p;
                        e[m] = 0.0;
                        broken = true;
                        break;
                    }

                    s = f / r2;
                    c = g / r2;
                    g = d[i + 1] - p;
                    double r3 = (d[i] - g) * s + 2.0 * c * b;
                    p = s * r3;
                    d[i + 1] = g + p;
                    g = c * r3 - b;
                    double fi = z0[i + 1];
                    z0[i + 1] = s * z0[i] + c * fi;
                    z0[i] = c * z0[i] - s * fi;
                }

                if (!broken)
                {
                    d[l] -= p;
                    e[l] = g;
                    e[m] = 0.0;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            nodes[i] = d[i];
            weights[i] = mu0 * z0[i] * z0[i];
        }

        // Sort nodes ascending with paired weights.
        for (int i = 1; i < n; i++)
        {
            double kn = nodes[i];
            double kw = weights[i];
            int j = i - 1;
            while (j >= 0 && nodes[j] > kn)
            {
                nodes[j + 1] = nodes[j];
                weights[j + 1] = weights[j];
                j--;
            }

            nodes[j + 1] = kn;
            weights[j + 1] = kw;
        }

        return MathErrorCode.None;
    }
}
