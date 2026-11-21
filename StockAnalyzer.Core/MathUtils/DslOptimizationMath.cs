using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation derivative-free and quasi-Newton minimizers for the StockAnalyzer DSL
/// (Brent and golden-section 1D minimization, Nelder-Mead and BFGS for 1..8 variables).
/// All working storage is stack-allocated; every objective evaluation consumes one <see cref="DslExecutionBudget"/> unit.
/// </summary>
public static class DslOptimizationMath
{
    /// <summary>Maximum number of variables of <see cref="OptNelderMead{TFunc}"/> and <see cref="OptBfgs{TFunc}"/>.</summary>
    public const int MaxOptDimensions = 8;

    /// <summary>Iteration cap of the 1D minimizers.</summary>
    public const int Max1dIterations = 200;

    /// <summary>Iteration cap of the N-dimensional minimizers.</summary>
    public const int MaxNdIterations = 500;

    // TEMPORARY SHARING (decided 2026-11-21): the DSL tolerance contract [1e-12, 1e-2] / default 1e-8 is defined here and is also used by ode_rk45
    // (CalculatorHigherOrderFunctions.EvaluateOde). Optimization and ODE solving are separate domains; the planned follow-up (SAで改善, docs/ja/Future_Improvements.md)
    // moves these values to a shared contract or per-domain constants. A unit test pins the ode_rk45 range so a one-sided change is noticed.

    /// <summary>Smallest accepted tolerance (also the lower bound of <c>ode_rk45</c>'s tolerance, see the note above).</summary>
    public const double MinTol = 1e-12;

    /// <summary>Largest accepted tolerance (also the upper bound of <c>ode_rk45</c>'s tolerance, see the note above).</summary>
    public const double MaxTol = 1e-2;

    /// <summary>Tolerance used by the DSL when the caller omits it (also the <c>ode_rk45</c> default, see the note above).</summary>
    public const double DefaultTol = 1e-8;

    /// <summary><c>(3 - sqrt(5)) / 2</c>: the golden-section fraction of Brent's first probe.</summary>
    private const double GoldenSectionC = 0.38196601125010515;

    /// <summary><c>(sqrt(5) - 1) / 2</c>: the golden-section shrink ratio.</summary>
    private const double InvPhi = 0.61803398874989485;

    /// <summary>Absolute tolerance floor of Brent's stopping rule.</summary>
    private const double BrentAbsoluteTolerance = 1e-11;

    private const double NmReflection = 1.0;
    private const double NmExpansion = 2.0;
    private const double NmContraction = 0.5;
    private const double NmShrink = 0.5;
    private const double NmInitialRelativeStep = 0.05;
    private const double NmInitialAbsoluteStep = 0.00025;
    private const double NmZeroThreshold = 1e-8;

    private const double BfgsGradientStep = 1e-6;
    private const double BfgsArmijoC1 = 1e-4;
    private const double BfgsBacktrackFactor = 0.5;
    private const int BfgsMaxLineSearchSteps = 20;
    private const double BfgsCurvatureTolerance = 1e-12;
    private const double BfgsStagnationStep = 1e-15;
    private const double BfgsStagnationGradientFactor = 10.0;

    // ---- 1D: Brent ----

    /// <summary>
    /// Minimizes <paramref name="f"/> on <c>[a, b]</c> with Brent's method (parabolic interpolation with golden-section safeguard).
    /// Requires <c>a &lt; b</c> and <c>tol</c> in <c>[1e-12, 1e-2]</c> (<see cref="MathErrorCode.DomainError"/> otherwise);
    /// returns <see cref="MathErrorCode.ConvergenceFailure"/> after <see cref="Max1dIterations"/> iterations.
    /// </summary>
    public static MathResult<double> OptBrent<TFunc>(TFunc f, double a, double b, double tol, ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        MathErrorCode argErr = ValidateInterval(a, b, tol);
        if (argErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(argErr);
        }

        double x = a + GoldenSectionC * (b - a);
        double w = x;
        double v = x;
        double e = 0.0;
        double d = 0.0;
        MathErrorCode evalErr = Evaluate(f, x, ref budget, out double fx);
        if (evalErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(evalErr);
        }

        double fw = fx;
        double fv = fx;

        for (int iter = 0; iter < Max1dIterations; iter++)
        {
            double m = 0.5 * (a + b);
            double tol1 = tol * Math.Abs(x) + BrentAbsoluteTolerance;
            double tol2 = 2.0 * tol1;
            if (Math.Abs(x - m) <= tol2 - 0.5 * (b - a))
            {
                return MathResult<double>.Ok(x == 0.0 ? 0.0 : x);
            }

            bool goldenStep = true;
            if (Math.Abs(e) > tol1)
            {
                double r = (x - w) * (fx - fv);
                double q = (x - v) * (fx - fw);
                double p = (x - v) * q - (x - w) * r;
                q = 2.0 * (q - r);
                if (q > 0.0)
                {
                    p = -p;
                }

                q = Math.Abs(q);
                double eTemp = e;
                e = d;
                if (!(Math.Abs(p) >= Math.Abs(0.5 * q * eTemp) || p <= q * (a - x) || p >= q * (b - x)))
                {
                    d = p / q;
                    double uTrial = x + d;
                    if (uTrial - a < tol2 || b - uTrial < tol2)
                    {
                        d = Math.CopySign(tol1, m - x);
                    }

                    goldenStep = false;
                }
            }

            if (goldenStep)
            {
                e = x >= m ? a - x : b - x;
                d = GoldenSectionC * e;
            }

            double u = Math.Abs(d) >= tol1 ? x + d : x + Math.CopySign(tol1, d);
            evalErr = Evaluate(f, u, ref budget, out double fu);
            if (evalErr != MathErrorCode.None)
            {
                return MathResult<double>.Fail(evalErr);
            }

            if (fu <= fx)
            {
                if (u >= x)
                {
                    a = x;
                }
                else
                {
                    b = x;
                }

                v = w;
                w = x;
                x = u;
                fv = fw;
                fw = fx;
                fx = fu;
            }
            else
            {
                if (u < x)
                {
                    a = u;
                }
                else
                {
                    b = u;
                }

                if (fu <= fw || w == x)
                {
                    v = w;
                    w = u;
                    fv = fw;
                    fw = fu;
                }
                else if (fu <= fv || v == x || v == w)
                {
                    v = u;
                    fv = fu;
                }
            }
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    // ---- 1D: Golden Section ----

    /// <summary>
    /// Minimizes <paramref name="f"/> on <c>[a, b]</c> with golden-section search; returns the midpoint of the final bracket
    /// once <c>|b - a| &lt;= tol * max(|a|, |b|, 1)</c>. Same argument contract as <see cref="OptBrent{TFunc}"/>.
    /// </summary>
    public static MathResult<double> OptGolden<TFunc>(TFunc f, double a, double b, double tol, ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        MathErrorCode argErr = ValidateInterval(a, b, tol);
        if (argErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(argErr);
        }

        double x1 = b - InvPhi * (b - a);
        double x2 = a + InvPhi * (b - a);
        MathErrorCode evalErr = Evaluate(f, x1, ref budget, out double f1);
        if (evalErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(evalErr);
        }

        evalErr = Evaluate(f, x2, ref budget, out double f2);
        if (evalErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(evalErr);
        }

        for (int iter = 0; iter < Max1dIterations; iter++)
        {
            if (Math.Abs(b - a) <= tol * Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1.0))
            {
                double mid = 0.5 * (a + b);
                return MathResult<double>.Ok(mid == 0.0 ? 0.0 : mid);
            }

            if (f1 < f2)
            {
                b = x2;
                x2 = x1;
                f2 = f1;
                x1 = b - InvPhi * (b - a);
                evalErr = Evaluate(f, x1, ref budget, out f1);
            }
            else
            {
                a = x1;
                x1 = x2;
                f1 = f2;
                x2 = a + InvPhi * (b - a);
                evalErr = Evaluate(f, x2, ref budget, out f2);
            }

            if (evalErr != MathErrorCode.None)
            {
                return MathResult<double>.Fail(evalErr);
            }
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    // ---- N-D: Nelder-Mead ----

    /// <summary>
    /// Minimizes the objective with the Nelder-Mead simplex method (reflection 1, expansion 2, contraction 0.5, shrink 0.5).
    /// <paramref name="f"/>.Dimension must be <c>1..8</c> and equal to <paramref name="x0"/>.Length; the minimizer is written to <paramref name="xOut"/>.
    /// Stops when both the standard deviation of the vertex values and the largest vertex distance (L-infinity) from the best vertex are <c>&lt;= tol</c>.
    /// </summary>
    public static MathErrorCode OptNelderMead<TFunc>(
        TFunc f,
        scoped ReadOnlySpan<double> x0,
        double tol,
        scoped Span<double> xOut,
        ref DslExecutionBudget budget)
        where TFunc : struct, INdScalarFunction
    {
        int dim = f.Dimension;
        MathErrorCode argErr = ValidateNdArguments(dim, x0, tol, xOut.Length);
        if (argErr != MathErrorCode.None)
        {
            return argErr;
        }

        int vertexCount = dim + 1;
        Span<double> simplex = stackalloc double[(MaxOptDimensions + 1) * MaxOptDimensions];
        Span<double> values = stackalloc double[MaxOptDimensions + 1];
        Span<double> centroid = stackalloc double[MaxOptDimensions];
        Span<double> reflected = stackalloc double[MaxOptDimensions];
        Span<double> trial = stackalloc double[MaxOptDimensions];

        x0.Slice(0, dim).CopyTo(simplex.Slice(0, dim));
        for (int j = 0; j < dim; j++)
        {
            Span<double> vertex = simplex.Slice((j + 1) * dim, dim);
            x0.Slice(0, dim).CopyTo(vertex);
            double xj = x0[j];
            vertex[j] = Math.Abs(xj) > NmZeroThreshold ? xj + NmInitialRelativeStep * xj : NmInitialAbsoluteStep;
        }

        for (int i = 0; i < vertexCount; i++)
        {
            MathErrorCode err = EvaluateNd(f, simplex.Slice(i * dim, dim), ref budget, out values[i]);
            if (err != MathErrorCode.None)
            {
                return err;
            }
        }

        for (int iter = 0; iter < MaxNdIterations; iter++)
        {
            SortSimplex(simplex, values, dim, vertexCount);

            if (HasConverged(simplex, values, dim, vertexCount, tol))
            {
                simplex.Slice(0, dim).CopyTo(xOut);
                return MathErrorCode.None;
            }

            Span<double> worst = simplex.Slice(dim * dim, dim);
            double fWorst = values[dim];
            double fSecondWorst = values[dim - 1];
            double fBest = values[0];

            centroid.Slice(0, dim).Clear();
            for (int i = 0; i < dim; i++)
            {
                ReadOnlySpan<double> vertex = simplex.Slice(i * dim, dim);
                for (int j = 0; j < dim; j++)
                {
                    centroid[j] += vertex[j];
                }
            }

            for (int j = 0; j < dim; j++)
            {
                centroid[j] /= dim;
                reflected[j] = centroid[j] + NmReflection * (centroid[j] - worst[j]);
            }

            MathErrorCode err = EvaluateNd(f, reflected.Slice(0, dim), ref budget, out double fReflected);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            if (fReflected < fBest)
            {
                for (int j = 0; j < dim; j++)
                {
                    trial[j] = centroid[j] + NmExpansion * (reflected[j] - centroid[j]);
                }

                err = EvaluateNd(f, trial.Slice(0, dim), ref budget, out double fExpanded);
                if (err != MathErrorCode.None)
                {
                    return err;
                }

                if (fExpanded < fReflected)
                {
                    trial.Slice(0, dim).CopyTo(worst);
                    values[dim] = fExpanded;
                }
                else
                {
                    reflected.Slice(0, dim).CopyTo(worst);
                    values[dim] = fReflected;
                }

                continue;
            }

            if (fReflected < fSecondWorst)
            {
                reflected.Slice(0, dim).CopyTo(worst);
                values[dim] = fReflected;
                continue;
            }

            bool outside = fReflected < fWorst;
            for (int j = 0; j < dim; j++)
            {
                trial[j] = outside
                    ? centroid[j] + NmContraction * (reflected[j] - centroid[j])
                    : centroid[j] + NmContraction * (worst[j] - centroid[j]);
            }

            err = EvaluateNd(f, trial.Slice(0, dim), ref budget, out double fContracted);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            if (outside ? fContracted <= fReflected : fContracted < fWorst)
            {
                trial.Slice(0, dim).CopyTo(worst);
                values[dim] = fContracted;
                continue;
            }

            // Shrink every vertex but the best towards the best vertex.
            ReadOnlySpan<double> best = simplex.Slice(0, dim);
            for (int i = 1; i < vertexCount; i++)
            {
                Span<double> vertex = simplex.Slice(i * dim, dim);
                for (int j = 0; j < dim; j++)
                {
                    vertex[j] = best[j] + NmShrink * (vertex[j] - best[j]);
                }

                err = EvaluateNd(f, vertex, ref budget, out values[i]);
                if (err != MathErrorCode.None)
                {
                    return err;
                }
            }
        }

        return MathErrorCode.ConvergenceFailure;
    }

    private static void SortSimplex(Span<double> simplex, Span<double> values, int dim, int vertexCount)
    {
        for (int i = 1; i < vertexCount; i++)
        {
            double keyValue = values[i];
            int j = i - 1;
            while (j >= 0 && values[j] > keyValue)
            {
                SwapVertices(simplex, values, dim, j, j + 1);
                j--;
            }
        }
    }

    private static void SwapVertices(Span<double> simplex, Span<double> values, int dim, int first, int second)
    {
        (values[first], values[second]) = (values[second], values[first]);
        Span<double> a = simplex.Slice(first * dim, dim);
        Span<double> b = simplex.Slice(second * dim, dim);
        for (int k = 0; k < dim; k++)
        {
            (a[k], b[k]) = (b[k], a[k]);
        }
    }

    private static bool HasConverged(ReadOnlySpan<double> simplex, ReadOnlySpan<double> values, int dim, int vertexCount, double tol)
    {
        double mean = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            mean += values[i];
        }

        mean /= vertexCount;
        double variance = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            double delta = values[i] - mean;
            variance += delta * delta;
        }

        if (Math.Sqrt(variance / vertexCount) > tol)
        {
            return false;
        }

        for (int i = 1; i < vertexCount; i++)
        {
            for (int j = 0; j < dim; j++)
            {
                if (Math.Abs(simplex[i * dim + j] - simplex[j]) > tol)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ---- N-D: BFGS ----

    /// <summary>
    /// Minimizes the objective with BFGS (inverse-Hessian update, central-difference gradient, Armijo backtracking line search).
    /// Same dimension contract as <see cref="OptNelderMead{TFunc}"/>. Stops when <c>||grad||_inf &lt;= tol</c>.
    /// A failed line search resets the inverse Hessian to the identity and retries on the next iteration (at most once per iteration);
    /// when it fails while the inverse Hessian already is the identity, <see cref="MathErrorCode.ConvergenceFailure"/> is returned immediately.
    /// A search direction that is not a descent direction (<c>grad . p &gt;= 0</c>) also resets the inverse Hessian to the identity.
    /// Every evaluation and every iteration is charged as usual.
    /// </summary>
    public static MathErrorCode OptBfgs<TFunc>(
        TFunc f,
        scoped ReadOnlySpan<double> x0,
        double tol,
        scoped Span<double> xOut,
        ref DslExecutionBudget budget)
        where TFunc : struct, INdScalarFunction
    {
        int dim = f.Dimension;
        MathErrorCode argErr = ValidateNdArguments(dim, x0, tol, xOut.Length);
        if (argErr != MathErrorCode.None)
        {
            return argErr;
        }

        Span<double> x = stackalloc double[MaxOptDimensions];
        Span<double> xNew = stackalloc double[MaxOptDimensions];
        Span<double> grad = stackalloc double[MaxOptDimensions];
        Span<double> gradNew = stackalloc double[MaxOptDimensions];
        Span<double> direction = stackalloc double[MaxOptDimensions];
        Span<double> step = stackalloc double[MaxOptDimensions];
        Span<double> gradDelta = stackalloc double[MaxOptDimensions];
        Span<double> hy = stackalloc double[MaxOptDimensions];
        Span<double> probe = stackalloc double[MaxOptDimensions];
        Span<double> h = stackalloc double[MaxOptDimensions * MaxOptDimensions];

        x0.Slice(0, dim).CopyTo(x);
        SetIdentity(h, dim);
        bool hIsIdentity = true;

        MathErrorCode err = EvaluateNd(f, x.Slice(0, dim), ref budget, out double fx);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        err = Gradient(f, x, grad, probe, dim, ref budget);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        for (int iter = 0; iter < MaxNdIterations; iter++)
        {
            if (InfinityNorm(grad, dim) <= tol)
            {
                x.Slice(0, dim).CopyTo(xOut);
                return MathErrorCode.None;
            }

            double slope = SearchDirection(h, grad, direction, dim);
            if (slope >= 0.0)
            {
                SetIdentity(h, dim);
                hIsIdentity = true;
                slope = SearchDirection(h, grad, direction, dim);
            }

            bool accepted = false;
            double fNew = fx;
            double alpha = 1.0;
            for (int ls = 0; ls < BfgsMaxLineSearchSteps; ls++)
            {
                for (int j = 0; j < dim; j++)
                {
                    xNew[j] = x[j] + alpha * direction[j];
                }

                err = EvaluateNd(f, xNew.Slice(0, dim), ref budget, out fNew);
                if (err != MathErrorCode.None)
                {
                    return err;
                }

                if (fNew <= fx + BfgsArmijoC1 * alpha * slope)
                {
                    accepted = true;
                    break;
                }

                alpha *= BfgsBacktrackFactor;
            }

            if (!accepted)
            {
                if (hIsIdentity)
                {
                    return MathErrorCode.ConvergenceFailure;
                }

                SetIdentity(h, dim);
                hIsIdentity = true;
                continue;
            }

            for (int j = 0; j < dim; j++)
            {
                step[j] = xNew[j] - x[j];
            }

            xNew.Slice(0, dim).CopyTo(x);
            fx = fNew;

            err = Gradient(f, x, gradNew, probe, dim, ref budget);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            if (InfinityNorm(step, dim) <= BfgsStagnationStep * Math.Max(1.0, InfinityNorm(x, dim)) &&
                InfinityNorm(gradNew, dim) <= BfgsStagnationGradientFactor * tol)
            {
                x.Slice(0, dim).CopyTo(xOut);
                return MathErrorCode.None;
            }

            double sy = 0.0;
            for (int j = 0; j < dim; j++)
            {
                gradDelta[j] = gradNew[j] - grad[j];
                sy += step[j] * gradDelta[j];
            }

            if (sy > BfgsCurvatureTolerance * Norm2(step, dim) * Norm2(gradDelta, dim))
            {
                UpdateInverseHessian(h, step, gradDelta, hy, sy, dim);
                hIsIdentity = false;
            }
            else
            {
                SetIdentity(h, dim);
                hIsIdentity = true;
            }

            gradNew.Slice(0, dim).CopyTo(grad);
        }

        return MathErrorCode.ConvergenceFailure;
    }

    private static MathErrorCode Gradient<TFunc>(
        TFunc f,
        scoped Span<double> x,
        scoped Span<double> grad,
        scoped Span<double> probe,
        int dim,
        ref DslExecutionBudget budget)
        where TFunc : struct, INdScalarFunction
    {
        x.Slice(0, dim).CopyTo(probe);
        for (int j = 0; j < dim; j++)
        {
            double hj = BfgsGradientStep * Math.Max(Math.Abs(x[j]), 1.0);
            probe[j] = x[j] + hj;
            MathErrorCode err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double fPlus);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            probe[j] = x[j] - hj;
            err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double fMinus);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            probe[j] = x[j];
            double g = (fPlus - fMinus) / (2.0 * hj);
            if (!double.IsFinite(g))
            {
                return MathErrorCode.NonFiniteResult;
            }

            grad[j] = g;
        }

        return MathErrorCode.None;
    }

    /// <summary>Writes <c>p = -H g</c> and returns the directional derivative <c>p . g</c>.</summary>
    private static double SearchDirection(ReadOnlySpan<double> h, ReadOnlySpan<double> grad, Span<double> direction, int dim)
    {
        double slope = 0.0;
        for (int i = 0; i < dim; i++)
        {
            double sum = 0.0;
            for (int j = 0; j < dim; j++)
            {
                sum += h[i * dim + j] * grad[j];
            }

            direction[i] = -sum;
            slope += direction[i] * grad[i];
        }

        return slope;
    }

    /// <summary>
    /// BFGS inverse-Hessian update <c>H' = (I - rho s y^T) H (I - rho y s^T) + rho s s^T</c> in its expanded symmetric form
    /// <c>H - rho (s (Hy)^T + (Hy) s^T) + (rho^2 y^T H y + rho) s s^T</c>.
    /// </summary>
    private static void UpdateInverseHessian(
        Span<double> h,
        ReadOnlySpan<double> s,
        ReadOnlySpan<double> y,
        Span<double> hy,
        double sy,
        int dim)
    {
        double rho = 1.0 / sy;
        double yHy = 0.0;
        for (int i = 0; i < dim; i++)
        {
            double sum = 0.0;
            for (int j = 0; j < dim; j++)
            {
                sum += h[i * dim + j] * y[j];
            }

            hy[i] = sum;
            yHy += y[i] * sum;
        }

        double ssCoefficient = rho * rho * yHy + rho;
        for (int i = 0; i < dim; i++)
        {
            for (int j = 0; j < dim; j++)
            {
                h[i * dim + j] += -rho * (s[i] * hy[j] + hy[i] * s[j]) + ssCoefficient * s[i] * s[j];
            }
        }
    }

    // ---- Shared Helpers ----

    private static MathErrorCode ValidateInterval(double a, double b, double tol)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(tol))
        {
            return MathErrorCode.NonFiniteResult;
        }

        return a >= b || tol < MinTol || tol > MaxTol
            ? MathErrorCode.DomainError
            : MathErrorCode.None;
    }

    private static MathErrorCode ValidateNdArguments(int dim, ReadOnlySpan<double> x0, double tol, int outputLength)
    {
        if (dim < 1 || dim > MaxOptDimensions || x0.Length != dim || outputLength < dim)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(tol))
        {
            return MathErrorCode.NonFiniteResult;
        }

        for (int i = 0; i < dim; i++)
        {
            if (!double.IsFinite(x0[i]))
            {
                return MathErrorCode.NonFiniteResult;
            }
        }

        return tol < MinTol || tol > MaxTol ? MathErrorCode.DomainError : MathErrorCode.None;
    }

    private static MathErrorCode Evaluate<TFunc>(TFunc f, double x, ref DslExecutionBudget budget, out double y)
        where TFunc : struct, IScalarFunction
    {
        y = double.NaN;
        if (!budget.TryConsume(1))
        {
            return MathErrorCode.BudgetExceeded;
        }

        if (!f.TryEvaluate(x, ref budget, out y))
        {
            return DslNumericalAnalysisMath.ResolveFunctorError(in budget);
        }

        return double.IsFinite(y) ? MathErrorCode.None : MathErrorCode.NonFiniteResult;
    }

    private static MathErrorCode EvaluateNd<TFunc>(TFunc f, scoped ReadOnlySpan<double> x, ref DslExecutionBudget budget, out double y)
        where TFunc : struct, INdScalarFunction
    {
        y = double.NaN;
        if (!budget.TryConsume(1))
        {
            return MathErrorCode.BudgetExceeded;
        }

        if (!f.TryEvaluate(x, ref budget, out y))
        {
            return DslNumericalAnalysisMath.ResolveFunctorError(in budget);
        }

        return double.IsFinite(y) ? MathErrorCode.None : MathErrorCode.NonFiniteResult;
    }

    private static void SetIdentity(Span<double> matrix, int dim)
    {
        matrix.Slice(0, dim * dim).Clear();
        for (int i = 0; i < dim; i++)
        {
            matrix[i * dim + i] = 1.0;
        }
    }

    private static double InfinityNorm(ReadOnlySpan<double> v, int dim)
    {
        double max = 0.0;
        for (int i = 0; i < dim; i++)
        {
            double abs = Math.Abs(v[i]);
            if (abs > max)
            {
                max = abs;
            }
        }

        return max;
    }

    private static double Norm2(ReadOnlySpan<double> v, int dim)
    {
        double sum = 0.0;
        for (int i = 0; i < dim; i++)
        {
            sum += v[i] * v[i];
        }

        return Math.Sqrt(sum);
    }
}
