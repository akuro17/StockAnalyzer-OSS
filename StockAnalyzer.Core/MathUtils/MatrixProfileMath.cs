using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Matched historical subsequence descriptor produced by causal Left Matrix Profile search.
/// </summary>
public readonly record struct HistoricalMatch(int StartIndex, int EndIndex, double Distance, double Similarity);

/// <summary>
/// Causal Left Matrix Profile engine computing exact Z-normalized Euclidean distances between a query window
/// and historical subsequences strictly preceding the query, selecting Top-K non-overlapping matches with full continuation horizon.
/// </summary>
public static class MatrixProfileMath
{
    /// <summary>
    /// Computes the causal Left Matrix Profile distance vector <paramref name="distances"/> for query
    /// <c>prices[queryStart .. queryStart + queryLength - 1]</c> against all indices <c>j in [0 .. queryStart - ez]</c>.
    /// Entries inside or beyond the causal exclusion zone receive <see cref="double.PositiveInfinity"/>.
    /// </summary>
    public static bool ComputeCausalLeftDistanceProfile(
        ReadOnlySpan<double> prices,
        int queryStart,
        int queryLength,
        Span<double> distances,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (queryLength < IndicatorDefaultConstants.MatrixProfileMinQueryLength ||
            queryLength > IndicatorDefaultConstants.MatrixProfileMaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(nameof(queryLength));
        }

        if (queryStart < 0 || queryStart + queryLength > prices.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(queryStart));
        }

        distances.Fill(double.PositiveInfinity);

        ReadOnlySpan<double> querySlice = prices.Slice(queryStart, queryLength);
        for (int u = 0; u < queryLength; u++)
        {
            if (!double.IsFinite(querySlice[u]))
            {
                return false;
            }
        }

        Span<double> qNorm = stackalloc double[queryLength];
        if (!ZNormalization.TryNormalize(querySlice, qNorm))
        {
            return false;
        }

        int ez = Math.Max(1, queryLength / IndicatorDefaultConstants.MatrixProfileExclusionDivisor);
        int maxJ = Math.Min(distances.Length - 1, queryStart - ez);
        if (maxJ < 0)
        {
            return false;
        }

        double invM = 1.0 / queryLength;
        for (int j = 0; j <= maxJ; j++)
        {
            if ((j & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<double> cand = prices.Slice(j, queryLength);
            double sum = 0.0;
            bool finite = true;
            for (int u = 0; u < queryLength; u++)
            {
                double val = cand[u];
                if (!double.IsFinite(val))
                {
                    finite = false;
                    break;
                }
                sum += val;
            }

            if (!finite)
            {
                continue;
            }

            double meanJ = sum * invM;
            double varSum = 0.0;
            double cross = 0.0;
            for (int u = 0; u < queryLength; u++)
            {
                double dev = cand[u] - meanJ;
                varSum += dev * dev;
                cross += dev * qNorm[u];
            }

            double sigmaJ = Math.Sqrt(varSum * invM);
            if (sigmaJ <= ZNormalization.FlatSigmaEpsilon || !double.IsFinite(sigmaJ))
            {
                continue;
            }

            double corr = Math.Clamp(cross / (queryLength * sigmaJ), -1.0, 1.0);
            double distSq = Math.Max(0.0, 2.0 * queryLength * (1.0 - corr));
            distances[j] = Math.Sqrt(distSq);
        }

        return true;
    }

    /// <summary>
    /// Finds up to <paramref name="topK"/> non-overlapping historical subsequences separated by at least
    /// <c>ez = max(1, queryLength / 4)</c> bars whose <paramref name="horizon"/>-bar continuation completes strictly before
    /// <paramref name="queryStart"/> (<c>queryStart - (j + queryLength) &gt;= horizon</c>).
    /// </summary>
    public static void FindTopKMatches(
        ReadOnlySpan<double> prices,
        int queryStart,
        int queryLength,
        int horizon,
        int topK,
        IList<HistoricalMatch> matches,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(matches);
        matches.Clear();

        if (horizon < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(horizon));
        }

        if (topK < IndicatorDefaultConstants.DbaMinTopK || topK > IndicatorDefaultConstants.DbaMaxTopK)
        {
            throw new ArgumentOutOfRangeException(nameof(topK));
        }

        int ez = Math.Max(1, queryLength / IndicatorDefaultConstants.MatrixProfileExclusionDivisor);
        // Non-overlap with query window & exclusion zone holds automatically because queryLength >= 10 > ez and horizon >= 1.
        int maxValidJ = queryStart - queryLength - horizon;
        if (maxValidJ < 0)
        {
            return;
        }

        int profileLen = maxValidJ + 1;
        double[]? pooled = profileLen > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(profileLen) : null;
        try
        {
            Span<double> profile = pooled != null ? pooled.AsSpan(0, profileLen) : stackalloc double[profileLen];
            if (!ComputeCausalLeftDistanceProfile(prices, queryStart, queryLength, profile, cancellationToken))
            {
                return;
            }

            double invSqrtM = 1.0 / Math.Sqrt(queryLength);
            for (int k = 0; k < topK; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bestIdx = -1;
                double bestDist = double.PositiveInfinity;
                for (int j = 0; j < profileLen; j++)
                {
                    double d = profile[j];
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestIdx = j;
                    }
                }

                if (bestIdx < 0 || !double.IsFinite(bestDist))
                {
                    break;
                }

                double similarity = Math.Exp(-bestDist * invSqrtM);
                matches.Add(new HistoricalMatch(bestIdx, bestIdx + queryLength - 1, bestDist, similarity));

                int suppressStart = Math.Max(0, bestIdx - ez + 1);
                int suppressEnd = Math.Min(profileLen - 1, bestIdx + ez - 1);
                for (int j = suppressStart; j <= suppressEnd; j++)
                {
                    profile[j] = double.PositiveInfinity;
                }
            }
        }
        finally
        {
            if (pooled != null)
            {
                ArrayPool<double>.Shared.Return(pooled);
            }
        }
    }
}
