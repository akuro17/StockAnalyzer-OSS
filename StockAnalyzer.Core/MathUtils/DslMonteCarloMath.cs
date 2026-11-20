using System;
using System.Numerics;

namespace StockAnalyzer.Core.MathUtils;

public enum MonteCarloMode : byte
{
    QuasiSobol = 0,
    PseudoRng = 1,
    QuasiHalton = 2,
}

public readonly record struct McIntegrateResult(double Value, double StandardError);

/// <summary>
/// Zero-allocation 1D and N-D deterministic Monte Carlo / Randomized Quasi-Monte Carlo integration
/// using Sobol (Joe-Kuo D6 up to 8D), Halton, or Xoshiro256** sequences and R=8 independent RQMC shifts.
/// </summary>
public static class DslMonteCarloMath
{
    public const int MaxDimensions = 8;
    public const int MinSamples = 64;
    public const int MaxSamples = 2_048;
    public const int RqmcReplicates = 8;
    public const double MaxIntervalWidth = 1e12;

    private static readonly int[] HaltonPrimes = [2, 3, 5, 7, 11, 13, 17, 19];

    // Joe & Kuo (2008) primitive polynomials and initial direction numbers for dims 2..8 (0-based 1..7).
    private static readonly int[] SobolDegrees = [1, 1, 2, 3, 3, 4, 4, 5];
    private static readonly uint[] SobolPolys = [0u, 0u, 1u, 1u, 2u, 1u, 4u, 2u];
    private static readonly uint[][] SobolInitialM =
    [
        [],
        [1u],
        [1u, 3u],
        [1u, 3u, 1u],
        [1u, 1u, 1u],
        [1u, 1u, 3u, 3u],
        [1u, 3u, 5u, 13u],
        [1u, 1u, 5u, 5u, 17u],
    ];

    private static readonly uint[] SobolDirectionsFlat = BuildSobolDirections();

    private readonly struct ScalarToNdAdapter<TFunc> : INdScalarFunction
        where TFunc : struct, IScalarFunction
    {
        private readonly TFunc _inner;

        public ScalarToNdAdapter(TFunc inner)
        {
            _inner = inner;
        }

        public int Dimension => 1;

        public bool TryEvaluate(scoped ReadOnlySpan<double> x, ref DslExecutionBudget budget, out double y)
            => _inner.TryEvaluate(x[0], ref budget, out y);
    }

    public static MathResult<McIntegrateResult> McIntegrate<TFunc>(
        TFunc f,
        double a,
        double b,
        int nSamples,
        ulong seed,
        MonteCarloMode mode,
        ref DslExecutionBudget budget,
        bool requireStandardError = false)
        where TFunc : struct, IScalarFunction
    {
        Span<double> lower = [a];
        Span<double> upper = [b];
        var adapter = new ScalarToNdAdapter<TFunc>(f);
        return McIntegrateNd(adapter, lower, upper, nSamples, seed, mode, ref budget, requireStandardError);
    }

    public static MathResult<McIntegrateResult> McIntegrateNd<TFunc>(
        TFunc f,
        scoped ReadOnlySpan<double> lower,
        scoped ReadOnlySpan<double> upper,
        int nSamples,
        ulong seed,
        MonteCarloMode mode,
        ref DslExecutionBudget budget,
        bool requireStandardError = false)
        where TFunc : struct, INdScalarFunction
    {
        int dim = f.Dimension;
        if (lower.Length != upper.Length || lower.Length != dim)
        {
            return MathResult<McIntegrateResult>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (dim < 1 || dim > MaxDimensions ||
            nSamples < MinSamples || nSamples > MaxSamples ||
            (byte)mode > (byte)MonteCarloMode.QuasiHalton)
        {
            return MathResult<McIntegrateResult>.Fail(MathErrorCode.DomainError);
        }

        if (mode != MonteCarloMode.PseudoRng && seed == 0UL && requireStandardError)
        {
            return MathResult<McIntegrateResult>.Fail(MathErrorCode.DomainError);
        }

        Span<double> width = stackalloc double[dim];
        double volume = 1.0;
        for (int d = 0; d < dim; d++)
        {
            double lo = lower[d];
            double hi = upper[d];
            if (!double.IsFinite(lo) || !double.IsFinite(hi))
            {
                return MathResult<McIntegrateResult>.Fail(MathErrorCode.NonFiniteResult);
            }

            if (!(lo < hi))
            {
                return MathResult<McIntegrateResult>.Fail(MathErrorCode.DomainError);
            }

            double w = hi - lo;
            if (!double.IsFinite(w) || w > MaxIntervalWidth)
            {
                return MathResult<McIntegrateResult>.Fail(MathErrorCode.DomainError);
            }

            width[d] = w;
            volume *= w;
            if (!double.IsFinite(volume))
            {
                return MathResult<McIntegrateResult>.Fail(MathErrorCode.ArithmeticOverflow);
            }
        }

        var rng = new DeterministicRng(seed);
        Span<double> point = stackalloc double[dim];

        if (mode == MonteCarloMode.PseudoRng)
        {
            double mean = 0.0;
            double m2 = 0.0;
            for (int i = 0; i < nSamples; i++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<McIntegrateResult>.Fail(MathErrorCode.BudgetExceeded);
                }

                for (int d = 0; d < dim; d++)
                {
                    point[d] = lower[d] + (rng.NextUnitDouble() * width[d]);
                }

                if (!f.TryEvaluate(point, ref budget, out double fx))
                {
                    return MathResult<McIntegrateResult>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
                }

                if (!double.IsFinite(fx))
                {
                    return MathResult<McIntegrateResult>.Fail(MathErrorCode.NonFiniteResult);
                }

                int k = i + 1;
                double delta = fx - mean;
                mean += delta / k;
                m2 += delta * (fx - mean);
            }

            double integral = mean * volume;
            double se = Math.Sqrt(Math.Max(0.0, (m2 / (nSamples - 1)) / nSamples)) * volume;
            return ValidateOutput(integral, se);
        }

        const double Inv2Pow32 = 1.0 / 4294967296.0;
        Span<uint> sobolState = stackalloc uint[dim];

        // Unshifted deterministic QMC (seed == 0): integrate with i = 1..nSamples to avoid origin singularities.
        if (seed == 0UL)
        {
            double sum = 0.0;
            for (int i = 0; i < nSamples; i++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<McIntegrateResult>.Fail(MathErrorCode.BudgetExceeded);
                }

                if (mode == MonteCarloMode.QuasiSobol)
                {
                    StepSobol(i + 1, sobolState, dim, ReadOnlySpan<double>.Empty, Inv2Pow32, lower, width, point);
                }
                else
                {
                    StepHalton(i + 1, dim, ReadOnlySpan<double>.Empty, lower, width, point);
                }

                if (!f.TryEvaluate(point, ref budget, out double fx))
                {
                    return MathResult<McIntegrateResult>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
                }

                if (!double.IsFinite(fx))
                {
                    return MathResult<McIntegrateResult>.Fail(MathErrorCode.NonFiniteResult);
                }

                sum += fx;
            }

            double integral = (sum / nSamples) * volume;
            return ValidateOutput(integral, 0.0);
        }

        // Seeded Randomized QMC (seed != 0): R = 8 independent Cranley-Patterson shifts with 0-based i = 0..m-1.
        int perShiftSamples = nSamples / RqmcReplicates;
        Span<double> cpShift = stackalloc double[dim];
        double repMean = 0.0;
        double repM2 = 0.0;

        for (int r = 0; r < RqmcReplicates; r++)
        {
            for (int d = 0; d < dim; d++)
            {
                cpShift[d] = rng.NextUnitDouble();
            }

            sobolState.Clear();
            double shiftSum = 0.0;
            for (int i = 0; i < perShiftSamples; i++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<McIntegrateResult>.Fail(MathErrorCode.BudgetExceeded);
                }

                if (mode == MonteCarloMode.QuasiSobol)
                {
                    if (i > 0)
                    {
                        StepSobol(i, sobolState, dim, cpShift, Inv2Pow32, lower, width, point);
                    }
                    else
                    {
                        for (int d = 0; d < dim; d++)
                        {
                            point[d] = lower[d] + (cpShift[d] * width[d]);
                        }
                    }
                }
                else
                {
                    StepHalton(i, dim, cpShift, lower, width, point);
                }

                if (!f.TryEvaluate(point, ref budget, out double fx))
                {
                    return MathResult<McIntegrateResult>.Fail(DslNumericalAnalysisMath.ResolveFunctorError(in budget));
                }

                if (!double.IsFinite(fx))
                {
                    return MathResult<McIntegrateResult>.Fail(MathErrorCode.NonFiniteResult);
                }

                shiftSum += fx;
            }

            double estimateR = (shiftSum / perShiftSamples) * volume;
            double delta = estimateR - repMean;
            repMean += delta / (r + 1);
            repM2 += delta * (estimateR - repMean);
        }

        double rqmcSe = Math.Sqrt(Math.Max(0.0, (repM2 / (RqmcReplicates - 1)) / RqmcReplicates));
        return ValidateOutput(repMean, rqmcSe);
    }

    private static void StepSobol(
        int grayIndex,
        scoped Span<uint> sobolState,
        int dim,
        scoped ReadOnlySpan<double> cpShift,
        double inv2Pow32,
        scoped ReadOnlySpan<double> lower,
        scoped ReadOnlySpan<double> width,
        scoped Span<double> point)
    {
        int c = BitOperations.TrailingZeroCount((uint)grayIndex);
        if (c >= 32)
        {
            c = 31;
        }

        for (int d = 0; d < dim; d++)
        {
            sobolState[d] ^= SobolDirectionsFlat[(d * 32) + c];
            double u = sobolState[d] * inv2Pow32;
            if (!cpShift.IsEmpty)
            {
                u += cpShift[d];
                if (u >= 1.0)
                {
                    u -= 1.0;
                }
            }

            point[d] = lower[d] + (u * width[d]);
        }
    }

    private static void StepHalton(
        int index,
        int dim,
        scoped ReadOnlySpan<double> cpShift,
        scoped ReadOnlySpan<double> lower,
        scoped ReadOnlySpan<double> width,
        scoped Span<double> point)
    {
        for (int d = 0; d < dim; d++)
        {
            double u = RadicalInverse(index, HaltonPrimes[d]);
            if (!cpShift.IsEmpty)
            {
                u += cpShift[d];
                if (u >= 1.0)
                {
                    u -= 1.0;
                }
            }

            point[d] = lower[d] + (u * width[d]);
        }
    }

    private static MathResult<McIntegrateResult> ValidateOutput(double integral, double se)
        => double.IsFinite(integral) && double.IsFinite(se)
            ? MathResult<McIntegrateResult>.Ok(new McIntegrateResult(integral, se))
            : MathResult<McIntegrateResult>.Fail(MathErrorCode.NonFiniteResult);

    private static double RadicalInverse(int index, int primeBase)
    {
        double f = 1.0;
        double r = 0.0;
        int i = index;
        while (i > 0)
        {
            f /= primeBase;
            int rem = i % primeBase;
            r += f * rem;
            i /= primeBase;
        }

        return r;
    }

    private static uint[] BuildSobolDirections()
    {
        var dirs = new uint[MaxDimensions * 32];

        for (int k = 0; k < 32; k++)
        {
            dirs[k] = 1u << (31 - k);
        }

        Span<uint> v = stackalloc uint[32];
        for (int d = 1; d < MaxDimensions; d++)
        {
            int s = SobolDegrees[d];
            uint a = SobolPolys[d];
            uint[] init = SobolInitialM[d];

            for (int k = 0; k < s; k++)
            {
                v[k] = init[k] << (31 - k);
            }

            for (int k = s; k < 32; k++)
            {
                uint vk = v[k - s] ^ (v[k - s] >> s);
                for (int j = 1; j < s; j++)
                {
                    if (((a >> (s - 1 - j)) & 1u) != 0u)
                    {
                        vk ^= v[k - j];
                    }
                }

                v[k] = vk;
            }

            v.CopyTo(dirs.AsSpan(d * 32, 32));
        }

        return dirs;
    }
}
