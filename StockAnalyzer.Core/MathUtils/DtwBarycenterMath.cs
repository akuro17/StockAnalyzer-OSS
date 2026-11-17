using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Petitjean et al. (2011) DTW Barycenter Averaging (DBA) and weighted quantile ensemble envelope
/// over top-K matched historical continuation trajectories.
/// </summary>
public static class DtwBarycenterMath
{
    /// <summary>
    /// Computes the weighted DBA barycenter and weighted 15% / 85% quantile envelope across
    /// <paramref name="sequences"/> (each of length <paramref name="horizon"/>).
    /// </summary>
    public static bool ComputeEnsemble(
        IReadOnlyList<double[]> sequences,
        ReadOnlySpan<double> distances,
        int horizon,
        Span<double> barycenter,
        Span<double> lowerBand,
        Span<double> upperBand,
        int iterations = IndicatorDefaultConstants.DbaDefaultIterations,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sequences);
        int kCount = sequences.Count;
        if (kCount == 0 || distances.Length < kCount || horizon < 1 ||
            barycenter.Length < horizon || lowerBand.Length < horizon || upperBand.Length < horizon)
        {
            return false;
        }

        for (int k = 0; k < kCount; k++)
        {
            if (sequences[k] == null || sequences[k].Length < horizon)
            {
                return false;
            }
        }

        double[]? pooledWeights = (4 * kCount) > MathBufferLimits.StackAllocThreshold
            ? ArrayPool<double>.Shared.Rent(4 * kCount)
            : null;
        try
        {
            Span<double> weightBuf = pooledWeights != null
                ? pooledWeights.AsSpan(0, 4 * kCount)
                : stackalloc double[4 * kCount];
            Span<double> weights = weightBuf.Slice(0, kCount);
            Span<double> stepValues = weightBuf.Slice(kCount, kCount);
            Span<double> stepWeights = weightBuf.Slice(2 * kCount, kCount);
            Span<double> cumMid = weightBuf.Slice(3 * kCount, kCount);

            double weightSum = 0.0;
            for (int k = 0; k < kCount; k++)
            {
                double w = 1.0 / (Math.Max(0.0, distances[k]) + IndicatorDefaultConstants.DbaDistanceEpsilon);
                weights[k] = w;
                weightSum += w;
            }

            if (!(weightSum > 0.0) || !double.IsFinite(weightSum))
            {
                return false;
            }

            for (int k = 0; k < kCount; k++)
            {
                weights[k] /= weightSum;
            }

            int bandRadius = Math.Max(1, horizon / IndicatorDefaultConstants.DbaSakoeChibaDivisor);

            // Initialize B^(0) as the weighted DTW medoid sequence.
            int medoidIdx = 0;
            if (kCount > 1)
            {
                double bestCost = double.PositiveInfinity;
                for (int i = 0; i < kCount; i++)
                {
                    double cost = 0.0;
                    ReadOnlySpan<double> sI = sequences[i].AsSpan(0, horizon);
                    for (int j = 0; j < kCount; j++)
                    {
                        if (i == j) continue;
                        cost += weights[j] * DtwMath.Calculate(sI, sequences[j].AsSpan(0, horizon), bandRadius);
                    }
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        medoidIdx = i;
                    }
                }
            }

            sequences[medoidIdx].AsSpan(0, horizon).CopyTo(barycenter.Slice(0, horizon));

            int alignedLen = kCount * horizon;
            double[]? pooledAligned = alignedLen > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(alignedLen) : null;
            double[]? pooledSeqAcc = horizon > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(horizon) : null;
            int[]? pooledSeqCount = horizon > MathBufferLimits.StackAllocThreshold ? ArrayPool<int>.Shared.Rent(horizon) : null;
            try
            {
                Span<double> aligned = pooledAligned != null ? pooledAligned.AsSpan(0, alignedLen) : stackalloc double[alignedLen];
                Span<double> seqSum = pooledSeqAcc != null ? pooledSeqAcc.AsSpan(0, horizon) : stackalloc double[horizon];
                Span<int> seqCount = pooledSeqCount != null ? pooledSeqCount.AsSpan(0, horizon) : stackalloc int[horizon];

                if (kCount > 1 && iterations > 0)
                {
                    int matLen = horizon * horizon;
                    double[]? pooledDp = matLen > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(matLen) : null;
                    double[]? pooledAcc = (2 * horizon) > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(2 * horizon) : null;
                    try
                    {
                        Span<double> dp = pooledDp != null ? pooledDp.AsSpan(0, matLen) : stackalloc double[matLen];
                        Span<double> accBuf = pooledAcc != null ? pooledAcc.AsSpan(0, 2 * horizon) : stackalloc double[2 * horizon];
                        Span<double> assocSum = accBuf.Slice(0, horizon);
                        Span<double> assocWeight = accBuf.Slice(horizon, horizon);

                        for (int it = 0; it < iterations; it++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bool isFinalIteration = it == iterations - 1;
                            assocSum.Clear();
                            assocWeight.Clear();

                            for (int k = 0; k < kCount; k++)
                            {
                                if (isFinalIteration)
                                {
                                    seqSum.Clear();
                                    seqCount.Clear();
                                }

                                AccumulateWarpingPath(
                                    barycenter.Slice(0, horizon),
                                    sequences[k].AsSpan(0, horizon),
                                    weights[k],
                                    bandRadius,
                                    dp,
                                    assocSum,
                                    assocWeight,
                                    isFinalIteration ? seqSum : Span<double>.Empty,
                                    isFinalIteration ? seqCount : Span<int>.Empty);

                                if (isFinalIteration)
                                {
                                    int rowOffset = k * horizon;
                                    for (int u = 0; u < horizon; u++)
                                    {
                                        aligned[rowOffset + u] = seqCount[u] > 0 ? seqSum[u] / seqCount[u] : sequences[k][u];
                                    }
                                }
                            }

                            for (int u = 0; u < horizon; u++)
                            {
                                if (assocWeight[u] > 0.0)
                                {
                                    barycenter[u] = assocSum[u] / assocWeight[u];
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (pooledDp != null) ArrayPool<double>.Shared.Return(pooledDp);
                        if (pooledAcc != null) ArrayPool<double>.Shared.Return(pooledAcc);
                    }
                }
                else
                {
                    for (int k = 0; k < kCount; k++)
                    {
                        sequences[k].AsSpan(0, horizon).CopyTo(aligned.Slice(k * horizon, horizon));
                    }
                }

                // Compute weighted 15th and 85th percentile bands across DTW-warped sequences at each horizon step
                // and enforce LowerBand <= Barycenter <= UpperBand envelope invariance.
                for (int h = 0; h < horizon; h++)
                {
                    for (int k = 0; k < kCount; k++)
                    {
                        stepValues[k] = aligned[k * horizon + h];
                        stepWeights[k] = weights[k];
                    }

                    SortPairsAscending(stepValues, stepWeights, kCount);
                    double lower = WeightedQuantileSorted(stepValues, stepWeights, cumMid, kCount, IndicatorDefaultConstants.DbaLowerQuantile);
                    double upper = WeightedQuantileSorted(stepValues, stepWeights, cumMid, kCount, IndicatorDefaultConstants.DbaUpperQuantile);
                    double center = barycenter[h];
                    lowerBand[h] = Math.Min(lower, center);
                    upperBand[h] = Math.Max(upper, center);
                }
            }
            finally
            {
                if (pooledAligned != null) ArrayPool<double>.Shared.Return(pooledAligned);
                if (pooledSeqAcc != null) ArrayPool<double>.Shared.Return(pooledSeqAcc);
                if (pooledSeqCount != null) ArrayPool<int>.Shared.Return(pooledSeqCount);
            }

            return true;
        }
        finally
        {
            if (pooledWeights != null) ArrayPool<double>.Shared.Return(pooledWeights);
        }
    }

    private static void AccumulateWarpingPath(
        ReadOnlySpan<double> barycenter,
        ReadOnlySpan<double> seq,
        double weight,
        int radius,
        Span<double> dp,
        Span<double> assocSum,
        Span<double> assocWeight,
        Span<double> seqSum,
        Span<int> seqCount)
    {
        int h = barycenter.Length;
        dp.Fill(double.PositiveInfinity);

        for (int i = 0; i < h; i++)
        {
            int jStart = Math.Max(0, i - radius);
            int jEnd = Math.Min(h - 1, i + radius);
            double bi = barycenter[i];
            int rowOffset = i * h;

            for (int j = jStart; j <= jEnd; j++)
            {
                double diff = bi - seq[j];
                double d2 = diff * diff;
                if (i == 0 && j == 0)
                {
                    dp[0] = d2;
                    continue;
                }

                double bestPrev = double.PositiveInfinity;
                if (i > 0 && j > 0) bestPrev = Math.Min(bestPrev, dp[(i - 1) * h + (j - 1)]);
                if (i > 0) bestPrev = Math.Min(bestPrev, dp[(i - 1) * h + j]);
                if (j > 0) bestPrev = Math.Min(bestPrev, dp[rowOffset + (j - 1)]);
                dp[rowOffset + j] = d2 + bestPrev;
            }
        }

        bool captureAligned = !seqSum.IsEmpty;
        int u = h - 1;
        int v = h - 1;
        while (true)
        {
            assocSum[u] += weight * seq[v];
            assocWeight[u] += weight;
            if (captureAligned)
            {
                seqSum[u] += seq[v];
                seqCount[u]++;
            }

            if (u == 0 && v == 0)
            {
                break;
            }

            if (u == 0)
            {
                v--;
                continue;
            }

            if (v == 0)
            {
                u--;
                continue;
            }

            double cDiag = dp[(u - 1) * h + (v - 1)];
            double cUp = dp[(u - 1) * h + v];
            double cLeft = dp[u * h + (v - 1)];

            if (cDiag <= cUp && cDiag <= cLeft)
            {
                u--;
                v--;
            }
            else if (cUp <= cLeft)
            {
                u--;
            }
            else
            {
                v--;
            }
        }
    }

    private static void SortPairsAscending(Span<double> values, Span<double> weights, int count)
    {
        for (int i = 1; i < count; i++)
        {
            double val = values[i];
            double w = weights[i];
            int j = i - 1;
            while (j >= 0 && values[j] > val)
            {
                values[j + 1] = values[j];
                weights[j + 1] = weights[j];
                j--;
            }
            values[j + 1] = val;
            weights[j + 1] = w;
        }
    }

    private static double WeightedQuantileSorted(
        ReadOnlySpan<double> values,
        ReadOnlySpan<double> weights,
        Span<double> cumMid,
        int count,
        double q)
    {
        if (count == 1)
        {
            return values[0];
        }

        double cum = 0.0;
        for (int i = 0; i < count; i++)
        {
            cumMid[i] = cum + 0.5 * weights[i];
            cum += weights[i];
        }

        if (q <= cumMid[0]) return values[0];
        if (q >= cumMid[count - 1]) return values[count - 1];

        for (int i = 1; i < count; i++)
        {
            if (q <= cumMid[i])
            {
                double span = cumMid[i] - cumMid[i - 1];
                double alpha = span > 1e-12 ? (q - cumMid[i - 1]) / span : 0.0;
                return values[i - 1] + alpha * (values[i] - values[i - 1]);
            }
        }

        return values[count - 1];
    }
}
