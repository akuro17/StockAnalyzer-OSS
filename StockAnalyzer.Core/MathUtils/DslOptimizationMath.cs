using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation derivative-free and quasi-Newton minimizers for the StockAnalyzer DSL
/// (Brent and golden-section 1D minimization, Nelder-Mead and BFGS for 1..8 variables).
/// All working storage is stack-allocated; every objective evaluation consumes one <see cref="DslExecutionBudget"/> unit.
/// </summary>
public static partial class DslOptimizationMath
{
    /// <summary>Maximum number of variables of <see cref="OptNelderMead{TFunc}"/> and <see cref="OptBfgs{TFunc}"/>.</summary>
    public const int MaxOptDimensions = 8;

    /// <summary>Iteration cap of the 1D minimizers.</summary>
    public const int Max1dIterations = 200;

    /// <summary>Iteration cap of the N-dimensional minimizers.</summary>
    public const int MaxNdIterations = 500;

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

    /// <summary>
    /// Safety multiplier of the function-value noise of <see cref="OptBfgs{TFunc}"/>: two function values each carry a rounding error of about
    /// <c>MathConstants.MachineEpsilon * |f|</c> (a central difference loses up to <c>MathConstants.MachineEpsilon * |f| / (2 h)</c>), and the objective itself (a decimal-to-double conversion,
    /// a few operations) adds a few more roundings, hence 2^3 (owner decision Q-6, 2026-11-21).
    /// </summary>
    private const double GradientNoiseSafetyFactor = 8.0;

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
    /// Stops when both the standard deviation of the vertex values is <c>&lt;= tol * max(1, |f_best|)</c> and the largest vertex distance (L-infinity) from the best vertex
    /// is <c>&lt;= tol * max(1, ||x_best||inf)</c>: <c>tol</c> is a relative target (an absolute one would be below the spacing of doubles for coordinates beyond about 1e7).
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

        // Both tests are relative to the size of what they measure, so that tol keeps its meaning where an absolute tol is below the spacing of doubles
        // (ulp(1e7) = 1.9e-9, ulp(1e9) = 1.2e-7): "std(f) <= tol * max(1, |f_best|)" and "max_i ||v_i - v_best||inf <= tol * max(1, ||v_best||inf)".
        if (Math.Sqrt(variance / vertexCount) > tol * Math.Max(1.0, Math.Abs(values[0])))
        {
            return false;
        }

        double bestNorm = 0.0;
        for (int j = 0; j < dim; j++)
        {
            bestNorm = Math.Max(bestNorm, Math.Abs(simplex[j]));
        }

        double distanceLimit = tol * Math.Max(1.0, bestNorm);
        for (int i = 1; i < vertexCount; i++)
        {
            for (int j = 0; j < dim; j++)
            {
                if (Math.Abs(simplex[i * dim + j] - simplex[j]) > distanceLimit)
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
    /// Same dimension contract as <see cref="OptNelderMead{TFunc}"/>. Stops when <c>||grad||_inf &lt;= tol</c>, or when the gradient is below the noise floor of the function values
    /// (<c>|g_j| &lt;= max(tol, 8 * eps * max(1, |f|) / (2 h_j))</c>, <see cref="IsGradientWithinNoiseFloor"/>) and either the line search fails at the identity Hessian or an accepted step
    /// lowers <c>f</c> by no more than that noise.
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
                    // Rescue 1: no descent is possible even along the steepest descent; if the gradient at x is only noise, x is the answer.
                    if (IsGradientWithinNoiseFloor(x, grad, fx, dim, tol))
                    {
                        x.Slice(0, dim).CopyTo(xOut);
                        return MathErrorCode.None;
                    }

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
            double fPrevious = fx;
            fx = fNew;

            err = Gradient(f, x, gradNew, probe, dim, ref budget);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            // Rescue 2 (stagnation): the accepted step did not lower f beyond its rounding noise and the gradient AT THE NEW POINT is within its noise floor.
            // Both conditions are needed: a slope whose gradient is merely small but whose steps still lower f resolvably is not converged.
            if (fNew >= fPrevious - FunctionNoiseFloor(fPrevious, fNew) && IsGradientWithinNoiseFloor(x, gradNew, fx, dim, tol))
            {
                x.Slice(0, dim).CopyTo(xOut);
                return MathErrorCode.None;
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

    /// <summary>Central-difference step of one coordinate (<see cref="BfgsGradientStep"/> relative to <c>max(|x|, 1)</c>).</summary>
    private static double GradientStep(double coordinate) => BfgsGradientStep * Math.Max(Math.Abs(coordinate), 1.0);

    /// <summary>Rounding noise of a function value, <c>GradientNoiseSafetyFactor * MathConstants.MachineEpsilon * max(1, |f_a|, |f_b|)</c>.</summary>
    private static double FunctionNoiseFloor(double fA, double fB)
        => GradientNoiseSafetyFactor * MathConstants.MachineEpsilon * Math.Max(1.0, Math.Max(Math.Abs(fA), Math.Abs(fB)));

    /// <summary>
    /// True when every component satisfies <c>|g_j| &lt;= max(tol, noise(f) / (2 h_j))</c>, i.e. the central-difference gradient at <paramref name="x"/> cannot be distinguished
    /// from the rounding noise of the function values. Shared by both rescue paths of <see cref="OptBfgs{TFunc}"/> so that the noise model has one definition.
    /// </summary>
    private static bool IsGradientWithinNoiseFloor(ReadOnlySpan<double> x, ReadOnlySpan<double> gradient, double f, int dim, double tol)
    {
        double noise = FunctionNoiseFloor(f, f);
        for (int j = 0; j < dim; j++)
        {
            double gradientTolerance = Math.Max(tol, noise / (2.0 * GradientStep(x[j])));
            if (!(Math.Abs(gradient[j]) <= gradientTolerance))
            {
                return false;
            }
        }

        return true;
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
            double hj = GradientStep(x[j]);
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

        return a >= b || tol < DslTolerance.MinTol || tol > DslTolerance.MaxTol
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

        return tol < DslTolerance.MinTol || tol > DslTolerance.MaxTol ? MathErrorCode.DomainError : MathErrorCode.None;
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

    private static double ComputeBoxKktResidualNorm(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> g,
        ReadOnlySpan<double> lb,
        ReadOnlySpan<double> ub,
        int dim)
    {
        double maxAbs = 0.0;
        for (int i = 0; i < dim; i++)
        {
            double widthFloor = MathConstants.MachineEpsilon * Math.Max(Math.Abs(x[i]), 1.0);
            double ri;
            if (ub[i] - lb[i] <= widthFloor)
            {
                ri = 0.0;
            }
            else if (x[i] <= lb[i])
            {
                ri = Math.Min(0.0, g[i]);
            }
            else if (x[i] >= ub[i])
            {
                ri = Math.Max(0.0, g[i]);
            }
            else
            {
                ri = g[i];
            }

            double absRi = Math.Abs(ri);
            if (absRi > maxAbs)
            {
                maxAbs = absRi;
            }
        }

        return maxAbs;
    }

    private static MathErrorCode BoundedGradient<TFunc>(
        TFunc f,
        scoped ReadOnlySpan<double> x,
        double fx,
        scoped ReadOnlySpan<double> lb,
        scoped ReadOnlySpan<double> ub,
        scoped Span<double> grad,
        scoped Span<double> probe,
        int dim,
        ref DslExecutionBudget budget)
        where TFunc : struct, INdScalarFunction
    {
        x.Slice(0, dim).CopyTo(probe);
        for (int j = 0; j < dim; j++)
        {
            double widthFloor = MathConstants.MachineEpsilon * Math.Max(Math.Abs(x[j]), 1.0);
            double boxWidth = ub[j] - lb[j];
            if (boxWidth <= widthFloor)
            {
                grad[j] = 0.0;
                continue;
            }

            double hj = GradientStep(x[j]);
            double deriv;
            if (x[j] - hj >= lb[j] && x[j] + hj <= ub[j])
            {
                probe[j] = x[j] + hj;
                MathErrorCode err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double fPlus);
                if (err != MathErrorCode.None) return err;

                probe[j] = x[j] - hj;
                err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double fMinus);
                if (err != MathErrorCode.None) return err;

                probe[j] = x[j];
                deriv = (fPlus - fMinus) / (2.0 * hj);
            }
            else if (boxWidth >= 2.0 * hj)
            {
                double gapRight = ub[j] - x[j];
                double gapLeft = x[j] - lb[j];
                if (gapRight >= gapLeft)
                {
                    double dh = Math.Min(hj, 0.5 * gapRight);
                    probe[j] = x[j] + dh;
                    MathErrorCode err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double f1);
                    if (err != MathErrorCode.None) return err;

                    probe[j] = Math.Min(ub[j], x[j] + (2.0 * dh));
                    err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double f2);
                    if (err != MathErrorCode.None) return err;

                    probe[j] = x[j];
                    deriv = ((-3.0 * fx) + (4.0 * f1) - f2) / (2.0 * dh);
                }
                else
                {
                    double dh = Math.Min(hj, 0.5 * gapLeft);
                    probe[j] = x[j] - dh;
                    MathErrorCode err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double f1);
                    if (err != MathErrorCode.None) return err;

                    probe[j] = Math.Max(lb[j], x[j] - (2.0 * dh));
                    err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double f2);
                    if (err != MathErrorCode.None) return err;

                    probe[j] = x[j];
                    deriv = ((3.0 * fx) - (4.0 * f1) + f2) / (2.0 * dh);
                }
            }
            else
            {
                double gapRight = ub[j] - x[j];
                double gapLeft = x[j] - lb[j];
                if (gapRight >= gapLeft)
                {
                    double dh = Math.Min(hj, gapRight);
                    probe[j] = x[j] + dh;
                    MathErrorCode err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double f1);
                    if (err != MathErrorCode.None) return err;

                    probe[j] = x[j];
                    deriv = (f1 - fx) / dh;
                }
                else
                {
                    double dh = Math.Min(hj, gapLeft);
                    probe[j] = x[j] - dh;
                    MathErrorCode err = EvaluateNd(f, probe.Slice(0, dim), ref budget, out double f1);
                    if (err != MathErrorCode.None) return err;

                    probe[j] = x[j];
                    deriv = (fx - f1) / dh;
                }
            }

            if (!double.IsFinite(deriv))
            {
                return MathErrorCode.NonFiniteResult;
            }

            grad[j] = deriv;
        }

        return MathErrorCode.None;
    }

    // ---- DSL v6: Box-Constrained QP & Projected L-BFGS-B ----

    /// <summary>
    /// Minimizes <c>0.5 * x^T H x + c^T x</c> subject to <c>lb[i] &lt;= x[i] &lt;= ub[i]</c> via scale-normalized
    /// Spectral Projected Gradient (BB1 with GLL non-monotone Armijo line search, <c>1 &lt;= n &lt;= 16</c>, symmetric PSD <c>H</c>,
    /// <c>tol in [1e-12, 1e-2]</c>, max 500 iters) with box-width-independent KKT stopping residual.
    /// </summary>
    public static MathErrorCode OptQp(
        scoped ReadOnlySpan<double> h,
        scoped ReadOnlySpan<double> c,
        int n,
        scoped ReadOnlySpan<double> lb,
        scoped ReadOnlySpan<double> ub,
        double tol,
        scoped Span<double> xOut,
        ref DslExecutionBudget budget)
    {
        const int nonMonotoneMemory = 10;
        int nn = n * n;
        if (!DslLinearAlgebraMath.IsValidDim(n) ||
            h.Length < nn ||
            c.Length < n ||
            lb.Length < n ||
            ub.Length < n ||
            xOut.Length < n)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(tol) || tol < DslTolerance.MinTol || tol > DslTolerance.MaxTol)
        {
            return MathErrorCode.DomainError;
        }

        if (DslLinearAlgebraMath.ValidateFinite(h.Slice(0, nn)) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(c.Slice(0, n)) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(lb.Slice(0, n)) != MathErrorCode.None ||
            DslLinearAlgebraMath.ValidateFinite(ub.Slice(0, n)) != MathErrorCode.None)
        {
            return MathErrorCode.NonFiniteResult;
        }

        for (int i = 0; i < n; i++)
        {
            if (lb[i] > ub[i])
            {
                return MathErrorCode.DomainError;
            }
        }

        double normF = DslLinearAlgebraMath.ComputeFrobeniusNorm(h, nn);
        double symScale = Math.Max(1.0, normF);
        double symTol = DslLinearAlgebraMath.SymmetryTolerance * symScale;
        Span<double> hSym = stackalloc double[nn];
        for (int i = 0; i < n; i++)
        {
            hSym[i * n + i] = h[i * n + i];
            for (int j = i + 1; j < n; j++)
            {
                double hij = h[i * n + j];
                double hji = h[j * n + i];
                if (Math.Abs(hij - hji) > symTol)
                {
                    return MathErrorCode.DomainError;
                }

                double avg = 0.5 * (hij + hji);
                hSym[i * n + j] = avg;
                hSym[j * n + i] = avg;
            }
        }

        Span<double> evals = stackalloc double[n];
        Span<double> evecs = stackalloc double[nn];
        MathErrorCode eigErr = DslLinearAlgebraMath.MatEigenSym(hSym, n, evals, evecs, ref budget);
        if (eigErr != MathErrorCode.None)
        {
            return eigErr;
        }

        // MatEigenSym sorts descending: evals[n - 1] is lambda_min.
        if (evals[n - 1] < -symTol)
        {
            return MathErrorCode.DomainError;
        }

        Span<double> x = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = Math.Clamp(0.0, lb[i], ub[i]);
        }

        double rawNormF = DslLinearAlgebraMath.ComputeFrobeniusNorm(hSym, nn);
        double rawCInf = InfinityNorm(c, n);
        double scaleFactor = Math.Max(rawNormF, rawCInf);
        if (scaleFactor == 0.0)
        {
            for (int i = 0; i < n; i++)
            {
                xOut[i] = x[i] == 0.0 ? 0.0 : x[i];
            }

            return MathErrorCode.None;
        }

        Span<double> hScaled = stackalloc double[nn];
        Span<double> cScaled = stackalloc double[n];
        for (int i = 0; i < nn; i++)
        {
            hScaled[i] = hSym[i] / scaleFactor;
        }

        for (int i = 0; i < n; i++)
        {
            cScaled[i] = c[i] / scaleFactor;
        }

        Span<double> g = stackalloc double[n];
        Span<double> hx = stackalloc double[n];
        Span<double> xTrial = stackalloc double[n];
        Span<double> gNew = stackalloc double[n];
        Span<double> dx = stackalloc double[n];
        Span<double> xBest = stackalloc double[n];
        Span<double> fHist = stackalloc double[nonMonotoneMemory];

        double f = EvaluateQpObjectiveAndGrad(hScaled, cScaled, x, hx, g, n);
        if (!double.IsFinite(f))
        {
            return MathErrorCode.NonFiniteResult;
        }

        x.CopyTo(xBest);
        double fBest = f;
        fHist[0] = f;
        int fHistCount = 1;
        int fHistHead = 1 % nonMonotoneMemory;

        double hScaledInf = DslLinearAlgebraMath.ComputeInfinityNorm(hScaled, n, n);
        double cScaledInf = InfinityNorm(cScaled, n);
        double hScaledF = DslLinearAlgebraMath.ComputeFrobeniusNorm(hScaled, nn);
        double defaultAlpha = 1.0 / Math.Max(1.0, hScaledF);
        double alphaBb = defaultAlpha;

        for (int iter = 0; iter < MaxNdIterations; iter++)
        {
            if (!budget.TryConsume(Math.Max(1, (n * n) / DslExecutionBudget.CheapOperationsPerUnit)))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double xInf = InfinityNorm(x, n);
            double stopTol = tol * Math.Max(hScaledInf * Math.Max(1.0, xInf), cScaledInf);
            double kktNorm = ComputeBoxKktResidualNorm(x, g, lb, ub, n);
            if (kktNorm <= stopTol)
            {
                for (int i = 0; i < n; i++)
                {
                    xOut[i] = x[i] == 0.0 ? 0.0 : x[i];
                }

                return MathErrorCode.None;
            }

            double fRef = fHist[0];
            for (int k = 1; k < fHistCount; k++)
            {
                if (fHist[k] > fRef)
                {
                    fRef = fHist[k];
                }
            }

            double step = Math.Clamp(alphaBb, 1e-12, 1e6);
            bool accepted = false;
            double fTrial = f;
            for (int ls = 0; ls < 30; ls++)
            {
                double dirDeriv = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double xt = Math.Clamp(x[i] - step * g[i], lb[i], ub[i]);
                    xTrial[i] = xt;
                    double dxi = xt - x[i];
                    dx[i] = dxi;
                    dirDeriv += g[i] * dxi;
                }

                fTrial = EvaluateQpObjectiveAndGrad(hScaled, cScaled, xTrial, hx, gNew, n);
                if (double.IsFinite(fTrial) && fTrial <= fRef + BfgsArmijoC1 * dirDeriv)
                {
                    accepted = true;
                    break;
                }

                step *= BfgsBacktrackFactor;
            }

            if (!accepted)
            {
                if (kktNorm <= Math.Max(stopTol, GradientNoiseSafetyFactor * MathConstants.MachineEpsilon * Math.Max(1.0, Math.Abs(f))))
                {
                    ReadOnlySpan<double> retSrc = f <= fBest ? x : xBest;
                    for (int i = 0; i < n; i++)
                    {
                        xOut[i] = retSrc[i] == 0.0 ? 0.0 : retSrc[i];
                    }

                    return MathErrorCode.None;
                }

                return MathErrorCode.ConvergenceFailure;
            }

            double sTs = 0.0;
            double sTy = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dgi = gNew[i] - g[i];
                sTs += dx[i] * dx[i];
                sTy += dx[i] * dgi;
                x[i] = xTrial[i];
                g[i] = gNew[i];
            }

            f = fTrial;
            if (f < fBest)
            {
                fBest = f;
                x.CopyTo(xBest);
            }

            fHist[fHistHead] = f;
            fHistHead = (fHistHead + 1) % nonMonotoneMemory;
            if (fHistCount < nonMonotoneMemory)
            {
                fHistCount++;
            }

            alphaBb = sTy > 1e-30 * sTs ? Math.Clamp(sTs / sTy, 1e-12, 1e6) : defaultAlpha;
        }

        double finalKkt = ComputeBoxKktResidualNorm(x, g, lb, ub, n);
        double finalStopTol = tol * Math.Max(hScaledInf * Math.Max(1.0, InfinityNorm(x, n)), cScaledInf);
        if (finalKkt <= Math.Max(finalStopTol, GradientNoiseSafetyFactor * MathConstants.MachineEpsilon * Math.Max(1.0, Math.Abs(f))))
        {
            ReadOnlySpan<double> retSrc = f <= fBest ? x : xBest;
            for (int i = 0; i < n; i++)
            {
                xOut[i] = retSrc[i] == 0.0 ? 0.0 : retSrc[i];
            }

            return MathErrorCode.None;
        }

        return MathErrorCode.ConvergenceFailure;
    }

    private static double EvaluateQpObjectiveAndGrad(
        ReadOnlySpan<double> hSym,
        ReadOnlySpan<double> c,
        ReadOnlySpan<double> x,
        Span<double> hx,
        Span<double> g,
        int n)
    {
        double quad = 0.0;
        double lin = 0.0;
        for (int i = 0; i < n; i++)
        {
            double sum = 0.0;
            int row = i * n;
            for (int j = 0; j < n; j++)
            {
                sum += hSym[row + j] * x[j];
            }

            hx[i] = sum;
            g[i] = sum + c[i];
            quad += x[i] * sum;
            lin += c[i] * x[i];
        }

        return 0.5 * quad + lin;
    }

    /// <summary>
    /// Box-constrained projected L-BFGS (<c>m = 7</c> history pairs, <c>1 &lt;= dim &lt;= 8</c>) with central
    /// finite-difference gradient and projected Armijo backtracking line search.
    /// </summary>
    public static MathErrorCode OptLbfgsb<TFunc>(
        TFunc f,
        scoped ReadOnlySpan<double> x0,
        scoped ReadOnlySpan<double> lb,
        scoped ReadOnlySpan<double> ub,
        double tol,
        scoped Span<double> xOut,
        ref DslExecutionBudget budget)
        where TFunc : struct, INdScalarFunction
    {
        const int historySize = 7;
        int dim = f.Dimension;
        if (dim < 1 || dim > MaxOptDimensions || x0.Length < dim || lb.Length < dim || ub.Length < dim || xOut.Length < dim)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(tol) || tol < DslTolerance.MinTol || tol > DslTolerance.MaxTol)
        {
            return MathErrorCode.DomainError;
        }

        for (int i = 0; i < dim; i++)
        {
            if (!double.IsFinite(x0[i]) || !double.IsFinite(lb[i]) || !double.IsFinite(ub[i]))
            {
                return MathErrorCode.NonFiniteResult;
            }

            if (lb[i] > x0[i] || x0[i] > ub[i])
            {
                return MathErrorCode.DomainError;
            }
        }

        Span<double> x = stackalloc double[dim];
        x0.Slice(0, dim).CopyTo(x);
        MathErrorCode evalErr = EvaluateNd(f, x, ref budget, out double fx);
        if (evalErr != MathErrorCode.None)
        {
            return evalErr;
        }

        Span<double> g = stackalloc double[dim];
        Span<double> probe = stackalloc double[dim];
        evalErr = BoundedGradient(f, x, fx, lb, ub, g, probe, dim, ref budget);
        if (evalErr != MathErrorCode.None)
        {
            return evalErr;
        }

        Span<double> sHist = stackalloc double[historySize * dim];
        Span<double> yHist = stackalloc double[historySize * dim];
        Span<double> rhoHist = stackalloc double[historySize];
        Span<double> alphaHist = stackalloc double[historySize];
        int histCount = 0;
        int histHead = 0;

        Span<double> q = stackalloc double[dim];
        Span<double> dir = stackalloc double[dim];
        Span<double> xTrial = stackalloc double[dim];
        Span<double> gNew = stackalloc double[dim];
        Span<double> s = stackalloc double[dim];
        Span<double> y = stackalloc double[dim];
        Span<bool> activeMask = stackalloc bool[dim];

        for (int iter = 0; iter < MaxNdIterations; iter++)
        {
            double kktNorm = ComputeBoxKktResidualNorm(x, g, lb, ub, dim);
            for (int j = 0; j < dim; j++)
            {
                double widthFloor = MathConstants.MachineEpsilon * Math.Max(Math.Abs(x[j]), 1.0);
                activeMask[j] = (ub[j] - lb[j] <= widthFloor)
                    || (x[j] <= lb[j] && g[j] >= 0.0)
                    || (x[j] >= ub[j] && g[j] <= 0.0);
            }

            double noiseFloor = ComputeGradientNoiseFloor(x, fx, dim);

            if (kktNorm <= tol)
            {
                for (int j = 0; j < dim; j++)
                {
                    xOut[j] = x[j] == 0.0 ? 0.0 : x[j];
                }

                return MathErrorCode.None;
            }

            for (int j = 0; j < dim; j++)
            {
                q[j] = activeMask[j] ? 0.0 : g[j];
            }

            for (int k = histCount - 1; k >= 0; k--)
            {
                int slot = (histHead - histCount + k + historySize * 2) % historySize;
                double dotSq = 0.0;
                for (int j = 0; j < dim; j++)
                {
                    if (!activeMask[j])
                    {
                        dotSq += sHist[slot * dim + j] * q[j];
                    }
                }

                double alpha = rhoHist[slot] * dotSq;
                alphaHist[k] = alpha;
                for (int j = 0; j < dim; j++)
                {
                    if (!activeMask[j])
                    {
                        q[j] -= alpha * yHist[slot * dim + j];
                    }
                }
            }

            double gammaScale = 1.0;
            if (histCount > 0)
            {
                int lastSlot = (histHead - 1 + historySize) % historySize;
                double sy = 0.0;
                double yy = 0.0;
                for (int j = 0; j < dim; j++)
                {
                    if (!activeMask[j])
                    {
                        double sj = sHist[lastSlot * dim + j];
                        double yj = yHist[lastSlot * dim + j];
                        sy += sj * yj;
                        yy += yj * yj;
                    }
                }

                if (yy > 1e-30 && sy > 0.0)
                {
                    gammaScale = Math.Clamp(sy / yy, 1e-12, 1e6);
                }
            }

            for (int j = 0; j < dim; j++)
            {
                dir[j] = activeMask[j] ? 0.0 : gammaScale * q[j];
            }

            for (int k = 0; k < histCount; k++)
            {
                int slot = (histHead - histCount + k + historySize * 2) % historySize;
                double dotYr = 0.0;
                for (int j = 0; j < dim; j++)
                {
                    if (!activeMask[j])
                    {
                        dotYr += yHist[slot * dim + j] * dir[j];
                    }
                }

                double beta = rhoHist[slot] * dotYr;
                double diff = alphaHist[k] - beta;
                for (int j = 0; j < dim; j++)
                {
                    if (!activeMask[j])
                    {
                        dir[j] += diff * sHist[slot * dim + j];
                    }
                }
            }

            double gDotDir = 0.0;
            for (int j = 0; j < dim; j++)
            {
                dir[j] = activeMask[j] ? 0.0 : -dir[j];
                gDotDir += g[j] * dir[j];
            }

            if (!(gDotDir < 0.0))
            {
                histCount = 0;
                for (int j = 0; j < dim; j++)
                {
                    dir[j] = activeMask[j] ? 0.0 : -g[j];
                }
            }

            double step = 1.0;
            bool accepted = false;
            double fTrial = fx;
            for (int ls = 0; ls < BfgsMaxLineSearchSteps; ls++)
            {
                double dirDeriv = 0.0;
                for (int j = 0; j < dim; j++)
                {
                    double xt = Math.Clamp(x[j] + step * dir[j], lb[j], ub[j]);
                    xTrial[j] = xt;
                    dirDeriv += g[j] * (xt - x[j]);
                }

                if (!(dirDeriv < 0.0))
                {
                    break;
                }

                evalErr = EvaluateNd(f, xTrial, ref budget, out fTrial);
                if (evalErr != MathErrorCode.None)
                {
                    return evalErr;
                }

                if (fTrial <= fx + BfgsArmijoC1 * dirDeriv)
                {
                    accepted = true;
                    break;
                }

                step *= BfgsBacktrackFactor;
            }

            if (!accepted)
            {
                if (histCount > 0)
                {
                    histCount = 0;
                    continue;
                }

                if (kktNorm <= Math.Max(tol, noiseFloor))
                {
                    for (int j = 0; j < dim; j++)
                    {
                        xOut[j] = x[j] == 0.0 ? 0.0 : x[j];
                    }

                    return MathErrorCode.None;
                }

                return MathErrorCode.ConvergenceFailure;
            }

            evalErr = BoundedGradient(f, xTrial, fTrial, lb, ub, gNew, probe, dim, ref budget);
            if (evalErr != MathErrorCode.None)
            {
                return evalErr;
            }

            double sTy = 0.0;
            double sNormSq = 0.0;
            double yNormSq = 0.0;
            for (int j = 0; j < dim; j++)
            {
                double sj = xTrial[j] - x[j];
                double yj = gNew[j] - g[j];
                s[j] = sj;
                y[j] = yj;
                sTy += sj * yj;
                sNormSq += sj * sj;
                yNormSq += yj * yj;
                x[j] = xTrial[j];
                g[j] = gNew[j];
            }

            double fPrev = fx;
            fx = fTrial;

            double kktNormNew = ComputeBoxKktResidualNorm(x, g, lb, ub, dim);
            double noiseFloorNew = ComputeGradientNoiseFloor(x, fx, dim);
            if (kktNormNew <= tol ||
                (kktNormNew <= Math.Max(tol, noiseFloorNew) &&
                 Math.Abs(fPrev - fx) <= GradientNoiseSafetyFactor * MathConstants.MachineEpsilon * Math.Max(1.0, Math.Abs(fx))))
            {
                for (int j = 0; j < dim; j++)
                {
                    xOut[j] = x[j] == 0.0 ? 0.0 : x[j];
                }

                return MathErrorCode.None;
            }

            if (sTy > BfgsCurvatureTolerance * Math.Sqrt(sNormSq * yNormSq))
            {
                int slot = histHead;
                for (int j = 0; j < dim; j++)
                {
                    sHist[slot * dim + j] = s[j];
                    yHist[slot * dim + j] = y[j];
                }

                rhoHist[slot] = 1.0 / sTy;
                histHead = (histHead + 1) % historySize;
                if (histCount < historySize)
                {
                    histCount++;
                }
            }
        }

        return MathErrorCode.ConvergenceFailure;
    }

    private static double ComputeGradientNoiseFloor(scoped ReadOnlySpan<double> x, double fx, int dim)
    {
        double noiseFloor = 0.0;
        double absFxScale = GradientNoiseSafetyFactor * MathConstants.MachineEpsilon * Math.Max(1.0, Math.Abs(fx));
        for (int j = 0; j < dim; j++)
        {
            double hj = BfgsGradientStep * Math.Max(Math.Abs(x[j]), 1.0);
            double ng = absFxScale / (2.0 * hj);
            if (ng > noiseFloor)
            {
                noiseFloor = ng;
            }
        }

        return noiseFloor;
    }
}
