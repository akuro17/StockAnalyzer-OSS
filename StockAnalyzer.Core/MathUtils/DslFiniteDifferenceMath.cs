using System;

namespace StockAnalyzer.Core.MathUtils;

public enum PdeScheme : byte
{
    ExplicitFtcs = 0,
    ImplicitBtcs = 1,
    CrankNicolson = 2,
}

public enum PdeBoundaryCondition : byte
{
    DirichletFixed = 0,
    NeumannZeroFlux = 1,
    LinearExtrapolation = 2,
}

/// <summary>
/// Zero-allocation discrete lag/percentage differences, higher-order finite-difference derivatives,
/// Richardson extrapolation, and 1D diffusion PDE solver using a stack-only Thomas tridiagonal solver.
/// </summary>
public static class DslFiniteDifferenceMath
{
    public const int MaxLag = 256;
    public const int MaxDiffOrder = 4;
    public const int MinGridPoints = 5;
    public const int MaxGridPoints = 128;
    public const int MaxPdeSteps = 500;
    public const double ExplicitCflMax = 0.5;
    public const double MinPdeSpacing = 1e-9;
    public const decimal MinPctBase = 1e-12m;

    private const double MinStepH = 1e-9;
    private const double MinSecondStepH = 1e-5;
    private const double MaxStepH = 1e-2;
    private const double MaxRichardsonStepH = 0.25;

    // ---- Discrete Lag & Percentage Differences (Causal Exact Decimal) ----

    public static MathResult<decimal> DiffLag(scoped ReadOnlySpan<decimal> series, int k = 1, int order = 1)
    {
        if (!TryValidateDiffLag(series.Length, k, order, out int outLen))
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> scratch = stackalloc decimal[series.Length];
        MathErrorCode err = DiffLagCore(series, k, order, scratch, out outLen);
        return err == MathErrorCode.None
            ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(scratch[outLen - 1]))
            : MathResult<decimal>.Fail(err);
    }

    public static MathErrorCode DiffLagSeries(
        scoped ReadOnlySpan<decimal> series,
        int k,
        int order,
        scoped Span<decimal> resultOut,
        out int resultLength)
    {
        if (!TryValidateDiffLag(series.Length, k, order, out resultLength))
        {
            resultLength = 0;
            return MathErrorCode.DomainError;
        }

        if (resultOut.Length < resultLength)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<decimal> scratch = stackalloc decimal[series.Length];
        MathErrorCode err = DiffLagCore(series, k, order, scratch, out int len);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        scratch.Slice(0, len).CopyTo(resultOut);
        resultLength = len;
        return MathErrorCode.None;
    }

    public static MathResult<decimal> DiffPct(scoped ReadOnlySpan<decimal> series, int k = 1)
    {
        int n = series.Length;
        if (n < 2 || n > DslArrayArena.MaxArrayLength || k < 1 || k > MaxLag || n <= k)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        decimal prev = series[n - 1 - k];
        if (Math.Abs(prev) < MinPctBase)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        try
        {
            decimal curr = series[n - 1];
            return MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(checked((curr - prev) / prev)));
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    private static bool TryValidateDiffLag(int n, int k, int order, out int resultLen)
    {
        resultLen = 0;
        if (n < 2 || n > DslArrayArena.MaxArrayLength || k < 1 || k > MaxLag || order < 1 || order > MaxDiffOrder)
        {
            return false;
        }

        int span = k * order;
        if (n <= span)
        {
            return false;
        }

        resultLen = n - span;
        return true;
    }

    private static MathErrorCode DiffLagCore(
        scoped ReadOnlySpan<decimal> series,
        int k,
        int order,
        scoped Span<decimal> buf,
        out int currentLen)
    {
        currentLen = series.Length;
        series.CopyTo(buf);
        try
        {
            for (int pass = 0; pass < order; pass++)
            {
                int nextLen = currentLen - k;
                for (int i = 0; i < nextLen; i++)
                {
                    buf[i] = DslElementaryMath.NormalizeZero(checked(buf[i + k] - buf[i]));
                }

                currentLen = nextLen;
            }

            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    // ---- Higher-Order Numerical Differentiation ----

    public static MathResult<double> FdForward<TFunc>(
        TFunc f,
        double x,
        double h,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!TryValidateStep(x, h, MinStepH, out MathErrorCode stepErr))
        {
            return MathResult<double>.Fail(stepErr);
        }

        if (!budget.TryConsume(1)) return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        if (!f.TryEvaluate(x + h, ref budget, out double fPlus)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        if (!f.TryEvaluate(x, ref budget, out double f0)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        if (!double.IsFinite(fPlus) || !double.IsFinite(f0)) return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);

        double deriv = (fPlus - f0) / h;
        return double.IsFinite(deriv) ? MathResult<double>.Ok(deriv) : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    public static MathResult<double> FdBackward<TFunc>(
        TFunc f,
        double x,
        double h,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!TryValidateStep(x, h, MinStepH, out MathErrorCode stepErr))
        {
            return MathResult<double>.Fail(stepErr);
        }

        if (!budget.TryConsume(1)) return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        if (!f.TryEvaluate(x, ref budget, out double f0)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        if (!f.TryEvaluate(x - h, ref budget, out double fMinus)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        if (!double.IsFinite(f0) || !double.IsFinite(fMinus)) return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);

        double deriv = (f0 - fMinus) / h;
        return double.IsFinite(deriv) ? MathResult<double>.Ok(deriv) : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    public static MathResult<double> FdCentral<TFunc>(
        TFunc f,
        double x,
        double h,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
        => DslNumericalAnalysisMath.DiffCentral(f, x, h, ref budget);

    public static MathResult<double> FdSecond<TFunc>(
        TFunc f,
        double x,
        double h,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (!TryValidateStep(x, h, MinSecondStepH, out MathErrorCode stepErr))
        {
            return MathResult<double>.Fail(stepErr);
        }

        if (!budget.TryConsume(1)) return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        if (!f.TryEvaluate(x + h, ref budget, out double fPlus)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        if (!f.TryEvaluate(x, ref budget, out double f0)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        if (!f.TryEvaluate(x - h, ref budget, out double fMinus)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
        if (!double.IsFinite(fPlus) || !double.IsFinite(f0) || !double.IsFinite(fMinus))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double deriv2 = (fPlus - (2.0 * f0) + fMinus) / (h * h);
        return double.IsFinite(deriv2) ? MathResult<double>.Ok(deriv2) : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
    }

    public static MathResult<double> FdRichardson<TFunc>(
        TFunc f,
        double x,
        double h,
        int order,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        if (order < 1 || order > MaxDiffOrder)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (!double.IsFinite(x) || !double.IsFinite(h))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        double scale = Math.Max(1.0, Math.Abs(x));
        double minOuterH = MinSecondStepH * (1 << order) * scale;
        if (h < minOuterH || h > MaxRichardsonStepH * scale)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        int levels = order + 1;
        Span<double> table = stackalloc double[levels];
        double curH = h;
        for (int k = 0; k < levels; k++)
        {
            if (!budget.TryConsume(1)) return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
            double xp = x + curH;
            double xm = x - curH;
            double actual2h = xp - xm;
            if (!double.IsFinite(actual2h) || !(actual2h > 0.0))
            {
                return MathResult<double>.Fail(MathErrorCode.DomainError);
            }

            if (!f.TryEvaluate(xp, ref budget, out double fPlus)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            if (!f.TryEvaluate(xm, ref budget, out double fMinus)) return MathResult<double>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
            if (!double.IsFinite(fPlus) || !double.IsFinite(fMinus)) return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);

            double d0 = (fPlus - fMinus) / actual2h;
            if (!double.IsFinite(d0)) return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            table[k] = d0;
            curH *= 0.5;
        }

        double factor = 1.0;
        for (int j = 1; j <= order; j++)
        {
            factor *= 4.0;
            double denom = factor - 1.0;
            for (int k = levels - 1; k >= j; k--)
            {
                table[k] = table[k] + ((table[k] - table[k - 1]) / denom);
                if (!double.IsFinite(table[k]))
                {
                    return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
                }
            }
        }

        return MathResult<double>.Ok(table[order]);
    }

    private static bool TryValidateStep(double x, double h, double minRelativeH, out MathErrorCode error)
    {
        if (!double.IsFinite(x) || !double.IsFinite(h))
        {
            error = MathErrorCode.NonFiniteResult;
            return false;
        }

        double scale = Math.Max(1.0, Math.Abs(x));
        if (h < minRelativeH * scale || h > MaxStepH * scale)
        {
            error = MathErrorCode.DomainError;
            return false;
        }

        error = MathErrorCode.None;
        return true;
    }

    // ---- 1D Heat / Diffusion PDE Solver (Stack-Only Thomas Algorithm) ----

    public static MathErrorCode SolvePde1D(
        scoped ReadOnlySpan<double> u0,
        double alpha,
        double dx,
        double dt,
        int nSteps,
        PdeScheme scheme,
        PdeBoundaryCondition bcType,
        scoped Span<double> uOut,
        ref DslExecutionBudget budget)
    {
        int m = u0.Length;
        if (m < MinGridPoints || m > MaxGridPoints)
        {
            return MathErrorCode.DomainError;
        }

        if (uOut.Length < m)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (nSteps < 1 || nSteps > MaxPdeSteps ||
            !double.IsFinite(alpha) || alpha < 0.0 ||
            !double.IsFinite(dx) || dx <= MinPdeSpacing ||
            !double.IsFinite(dt) || dt <= MinPdeSpacing ||
            (byte)scheme > (byte)PdeScheme.CrankNicolson ||
            (byte)bcType > (byte)PdeBoundaryCondition.LinearExtrapolation)
        {
            return MathErrorCode.DomainError;
        }

        for (int i = 0; i < m; i++)
        {
            if (!double.IsFinite(u0[i]))
            {
                return MathErrorCode.NonFiniteResult;
            }
        }

        double r = (alpha * dt) / (dx * dx);
        if (!double.IsFinite(r))
        {
            return MathErrorCode.ArithmeticOverflow;
        }

        if (scheme == PdeScheme.ExplicitFtcs && r > ExplicitCflMax)
        {
            return MathErrorCode.DomainError;
        }

        Span<double> uCurr = stackalloc double[m];
        Span<double> uNext = stackalloc double[m];
        u0.CopyTo(uCurr);

        double deltaLeft = bcType == PdeBoundaryCondition.LinearExtrapolation ? (u0[1] - u0[0]) : 0.0;
        double deltaRight = bcType == PdeBoundaryCondition.LinearExtrapolation ? (u0[m - 1] - u0[m - 2]) : 0.0;

        if (scheme == PdeScheme.ExplicitFtcs)
        {
            for (int step = 0; step < nSteps; step++)
            {
                if (!budget.TryConsume(1)) return MathErrorCode.BudgetExceeded;

                for (int i = 1; i < m - 1; i++)
                {
                    uNext[i] = uCurr[i] + (r * (uCurr[i + 1] - (2.0 * uCurr[i]) + uCurr[i - 1]));
                    if (!double.IsFinite(uNext[i])) return MathErrorCode.NonFiniteResult;
                }

                if (bcType == PdeBoundaryCondition.DirichletFixed)
                {
                    uNext[0] = u0[0];
                    uNext[m - 1] = u0[m - 1];
                }
                else
                {
                    // Second-order Ghost-Point Neumann (delta=0 for zero-flux, fixed initial slope for LinearExtrapolation).
                    uNext[0] = uCurr[0] + (2.0 * r * ((uCurr[1] - uCurr[0]) - deltaLeft));
                    uNext[m - 1] = uCurr[m - 1] + (2.0 * r * ((uCurr[m - 2] - uCurr[m - 1]) + deltaRight));
                }

                if (!double.IsFinite(uNext[0]) || !double.IsFinite(uNext[m - 1]))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                uNext.CopyTo(uCurr);
            }

            uCurr.CopyTo(uOut.Slice(0, m));
            return MathErrorCode.None;
        }

        double theta = scheme == PdeScheme.ImplicitBtcs ? 1.0 : 0.5;
        double rhsCoeff = (1.0 - theta) * r;
        double lhsOffDiag = -theta * r;
        double lhsDiag = 1.0 + (2.0 * theta * r);

        Span<double> aSub = stackalloc double[m];
        Span<double> bDiag = stackalloc double[m];
        Span<double> cSup = stackalloc double[m];
        Span<double> rhs = stackalloc double[m];
        Span<double> cPrime = stackalloc double[m];
        Span<double> dPrime = stackalloc double[m];

        for (int step = 0; step < nSteps; step++)
        {
            if (!budget.TryConsume(1)) return MathErrorCode.BudgetExceeded;

            for (int i = 1; i < m - 1; i++)
            {
                aSub[i] = lhsOffDiag;
                bDiag[i] = lhsDiag;
                cSup[i] = lhsOffDiag;
                rhs[i] = uCurr[i] + (rhsCoeff * (uCurr[i + 1] - (2.0 * uCurr[i]) + uCurr[i - 1]));
            }

            if (bcType == PdeBoundaryCondition.DirichletFixed)
            {
                bDiag[0] = 1.0;
                cSup[0] = 0.0;
                rhs[0] = u0[0];

                aSub[m - 1] = 0.0;
                bDiag[m - 1] = 1.0;
                rhs[m - 1] = u0[m - 1];
            }
            else
            {
                // 2nd-order Ghost-Point Neumann (delta = 0 for NeumannZeroFlux; fixed initial slope for LinearExtrapolation).
                bDiag[0] = 1.0 + (2.0 * theta * r);
                cSup[0] = -2.0 * theta * r;
                rhs[0] = uCurr[0] + (2.0 * rhsCoeff * (uCurr[1] - uCurr[0])) - (2.0 * r * deltaLeft);

                aSub[m - 1] = -2.0 * theta * r;
                bDiag[m - 1] = 1.0 + (2.0 * theta * r);
                rhs[m - 1] = uCurr[m - 1] + (2.0 * rhsCoeff * (uCurr[m - 2] - uCurr[m - 1])) + (2.0 * r * deltaRight);
            }

            if (!SolveTridiagonalThomas(aSub, bDiag, cSup, rhs, m, cPrime, dPrime, uNext))
            {
                return MathErrorCode.SingularMatrix;
            }

            for (int i = 0; i < m; i++)
            {
                if (!double.IsFinite(uNext[i])) return MathErrorCode.NonFiniteResult;
            }

            uNext.CopyTo(uCurr);
        }

        uCurr.CopyTo(uOut.Slice(0, m));
        return MathErrorCode.None;
    }

    private static bool SolveTridiagonalThomas(
        scoped ReadOnlySpan<double> a,
        scoped ReadOnlySpan<double> b,
        scoped ReadOnlySpan<double> c,
        scoped ReadOnlySpan<double> d,
        int m,
        scoped Span<double> cPrime,
        scoped Span<double> dPrime,
        scoped Span<double> x)
    {
        double pivot0 = b[0];
        if (!double.IsFinite(pivot0) || Math.Abs(pivot0) <= 1e-15)
        {
            return false;
        }

        cPrime[0] = c[0] / pivot0;
        dPrime[0] = d[0] / pivot0;

        for (int i = 1; i < m; i++)
        {
            double denom = b[i] - (a[i] * cPrime[i - 1]);
            if (!double.IsFinite(denom) || Math.Abs(denom) <= 1e-15)
            {
                return false;
            }

            cPrime[i] = i < m - 1 ? c[i] / denom : 0.0;
            dPrime[i] = (d[i] - (a[i] * dPrime[i - 1])) / denom;
        }

        x[m - 1] = dPrime[m - 1];
        for (int i = m - 2; i >= 0; i--)
        {
            x[i] = dPrime[i] - (cPrime[i] * x[i + 1]);
        }

        return true;
    }
}
