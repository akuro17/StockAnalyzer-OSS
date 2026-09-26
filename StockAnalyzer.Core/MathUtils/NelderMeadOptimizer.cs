using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>Tunables of one <see cref="NelderMeadOptimizer.Minimize"/> run (numerical-analysis settings, not domain values).</summary>
/// <param name="MaxIterations">Upper bound on simplex iterations per run.</param>
/// <param name="FunctionTolerance">Converged when the objective spread across the simplex is at most this (absolute).</param>
/// <param name="ParameterTolerance">Converged when every vertex is within this (absolute) of the best vertex in every coordinate.</param>
/// <param name="InitialRelativeStep">Initial simplex offset as a fraction of |x0[i]|.</param>
/// <param name="InitialAbsoluteStep">Initial simplex offset for a coordinate whose start value is 0.</param>
public readonly record struct NelderMeadOptions(
    int MaxIterations = NelderMeadOptions.DefaultMaxIterations,
    double FunctionTolerance = 1e-10,
    double ParameterTolerance = 1e-8,
    double InitialRelativeStep = 0.05,
    double InitialAbsoluteStep = 0.00025)
{
    /// <summary>The documented defaults (a parameterless <c>new NelderMeadOptions()</c> is all zeros, so use this).</summary>
    /// <summary>Iteration cap of <see cref="Default"/>.</summary>
    public const int DefaultMaxIterations = 1000;

    public static NelderMeadOptions Default { get; } = new(MaxIterations: DefaultMaxIterations);
}

/// <param name="Value">Objective value at the returned point.</param>
/// <param name="Iterations">Iterations performed.</param>
/// <param name="Converged">Both tolerances were met before the iteration cap.</param>
public readonly record struct NelderMeadResult(double Value, int Iterations, bool Converged);

/// <summary>
/// Derivative-free minimizer (Nelder &amp; Mead, 1965, Computer Journal 7(4), 308-313) with the standard coefficients.
/// The objective may return <see cref="double.PositiveInfinity"/> (or NaN) to reject a point, which is how box/linear
/// constraints are expressed. Allocates nothing on the GC heap for up to a few dozen parameters.
/// </summary>
public static class NelderMeadOptimizer
{
    /// <summary>Objective over a parameter vector.</summary>
    public delegate double Objective(ReadOnlySpan<double> x);

    private const double Reflection = 1.0;
    private const double Expansion = 2.0;
    private const double Contraction = 0.5;
    private const double Shrink = 0.5;

    /// <summary>
    /// Minimizes <paramref name="objective"/> starting from <paramref name="x"/>; on return <paramref name="x"/> holds the best point found.
    /// A start point the objective rejects is allowed (the simplex then walks to the feasible region if one is reachable).
    /// </summary>
    public static NelderMeadResult Minimize(Objective objective, Span<double> x, NelderMeadOptions? tunables = null)
    {
        var options = tunables ?? NelderMeadOptions.Default;
        int n = x.Length;
        if (n == 0)
        {
            throw new ArgumentException("At least one parameter is required.", nameof(x));
        }

        int vertices = n + 1;
        int scratchLength = vertices * n + vertices + 3 * n;
        double[]? rented = scratchLength > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(scratchLength) : null;
        Span<double> scratch = rented != null ? rented.AsSpan(0, scratchLength) : stackalloc double[scratchLength];

        try
        {
            Span<double> simplex = scratch[..(vertices * n)];
            Span<double> values = scratch.Slice(vertices * n, vertices);
            Span<double> centroid = scratch.Slice(vertices * n + vertices, n);
            Span<double> trial = scratch.Slice(vertices * n + vertices + n, n);
            Span<double> trial2 = scratch.Slice(vertices * n + vertices + 2 * n, n);

            x.CopyTo(simplex[..n]);
            values[0] = Evaluate(objective, simplex[..n]);
            for (int i = 0; i < n; i++)
            {
                Span<double> vertex = simplex.Slice((i + 1) * n, n);
                x.CopyTo(vertex);
                vertex[i] += x[i] != 0.0 ? options.InitialRelativeStep * Math.Abs(x[i]) : options.InitialAbsoluteStep;
                values[i + 1] = Evaluate(objective, vertex);
            }

            int iteration = 0;
            bool converged = false;
            for (; iteration < options.MaxIterations; iteration++)
            {
                SortVertices(simplex, values, n);

                if (HasConverged(simplex, values, n, options))
                {
                    converged = true;
                    break;
                }

                // Centroid of every vertex except the worst.
                centroid.Clear();
                for (int v = 0; v < n; v++)
                {
                    for (int d = 0; d < n; d++)
                    {
                        centroid[d] += simplex[v * n + d];
                    }
                }
                for (int d = 0; d < n; d++)
                {
                    centroid[d] /= n;
                }

                Span<double> worst = simplex.Slice(n * n, n);
                double worstValue = values[n];

                Combine(trial, centroid, worst, Reflection);
                double reflected = Evaluate(objective, trial);

                if (reflected < values[0])
                {
                    Combine(trial2, centroid, worst, Expansion);
                    double expanded = Evaluate(objective, trial2);
                    if (expanded < reflected)
                    {
                        trial2.CopyTo(worst);
                        values[n] = expanded;
                    }
                    else
                    {
                        trial.CopyTo(worst);
                        values[n] = reflected;
                    }
                }
                else if (reflected < values[n - 1])
                {
                    trial.CopyTo(worst);
                    values[n] = reflected;
                }
                else
                {
                    // Contract towards the better of the worst and reflected points.
                    bool outside = reflected < worstValue;
                    Combine(trial2, centroid, worst, outside ? Contraction : -Contraction);
                    double contracted = Evaluate(objective, trial2);
                    if (contracted < (outside ? reflected : worstValue))
                    {
                        trial2.CopyTo(worst);
                        values[n] = contracted;
                    }
                    else
                    {
                        Span<double> best = simplex[..n];
                        for (int v = 1; v < vertices; v++)
                        {
                            Span<double> vertex = simplex.Slice(v * n, n);
                            for (int d = 0; d < n; d++)
                            {
                                vertex[d] = best[d] + Shrink * (vertex[d] - best[d]);
                            }
                            values[v] = Evaluate(objective, vertex);
                        }
                    }
                }
            }

            SortVertices(simplex, values, n);
            simplex[..n].CopyTo(x);
            return new NelderMeadResult(values[0], iteration, converged);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }

    private static double Evaluate(Objective objective, ReadOnlySpan<double> point)
    {
        double value = objective(point);
        return double.IsNaN(value) ? double.PositiveInfinity : value;
    }

    /// <summary>target = centroid + coefficient·(centroid − worst).</summary>
    private static void Combine(Span<double> target, ReadOnlySpan<double> centroid, ReadOnlySpan<double> worst, double coefficient)
    {
        for (int d = 0; d < target.Length; d++)
        {
            target[d] = centroid[d] + coefficient * (centroid[d] - worst[d]);
        }
    }

    /// <summary>Insertion sort by objective value (n is tiny); ties keep their order so runs are deterministic.</summary>
    private static void SortVertices(Span<double> simplex, Span<double> values, int n)
    {
        for (int i = 1; i < values.Length; i++)
        {
            int j = i;
            while (j > 0 && values[j] < values[j - 1])
            {
                (values[j], values[j - 1]) = (values[j - 1], values[j]);
                for (int d = 0; d < n; d++)
                {
                    (simplex[j * n + d], simplex[(j - 1) * n + d]) = (simplex[(j - 1) * n + d], simplex[j * n + d]);
                }
                j--;
            }
        }
    }

    private static bool HasConverged(ReadOnlySpan<double> simplex, ReadOnlySpan<double> values, int n, NelderMeadOptions options)
    {
        double best = values[0];
        if (double.IsPositiveInfinity(best))
        {
            return false;
        }

        for (int v = 1; v < values.Length; v++)
        {
            if (!(Math.Abs(values[v] - best) <= options.FunctionTolerance))
            {
                return false;
            }

            for (int d = 0; d < n; d++)
            {
                if (Math.Abs(simplex[v * n + d] - simplex[d]) > options.ParameterTolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
