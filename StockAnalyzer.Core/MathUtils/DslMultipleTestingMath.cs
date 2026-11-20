using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Multiple-hypothesis p-value adjustment methods.
/// </summary>
public enum MultipleTestingMethod : byte
{
    Bonferroni = 0,
    Holm = 1,
    BenjaminiHochberg = 2,
    BenjaminiYekutieli = 3,
}

/// <summary>
/// Zero-allocation multiple-hypothesis p-value adjustment using indirect sorting
/// so caller ordering and input values remain untouched.
/// </summary>
public static class DslMultipleTestingMath
{
    public const int MaxPValues = 256;

    public static MathErrorCode PAdjust(
        scoped ReadOnlySpan<decimal> pValues,
        MultipleTestingMethod method,
        scoped Span<decimal> adjustedOut)
    {
        int m = pValues.Length;
        if (m < 1 || m > MaxPValues)
        {
            return MathErrorCode.DomainError;
        }

        if (adjustedOut.Length < m)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if ((byte)method > (byte)MultipleTestingMethod.BenjaminiYekutieli)
        {
            return MathErrorCode.DomainError;
        }

        for (int i = 0; i < m; i++)
        {
            decimal p = pValues[i];
            if (p < 0m || p > 1m)
            {
                return MathErrorCode.DomainError;
            }
        }

        if (method == MultipleTestingMethod.Bonferroni)
        {
            for (int i = 0; i < m; i++)
            {
                decimal scaled = pValues[i] * m;
                adjustedOut[i] = DslElementaryMath.NormalizeZero(scaled > 1m ? 1m : scaled);
            }

            return MathErrorCode.None;
        }

        Span<int> order = stackalloc int[m];
        for (int i = 0; i < m; i++)
        {
            order[i] = i;
        }

        // Stable insertion sort on indirect indices by pValues ascending.
        for (int i = 1; i < m; i++)
        {
            int keyIdx = order[i];
            decimal keyVal = pValues[keyIdx];
            int j = i - 1;
            while (j >= 0 && pValues[order[j]] > keyVal)
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = keyIdx;
        }

        if (method == MultipleTestingMethod.Holm)
        {
            decimal runningMax = 0m;
            for (int rank = 0; rank < m; rank++)
            {
                int idx = order[rank];
                decimal candidate = pValues[idx] * (m - rank);
                if (candidate > runningMax)
                {
                    runningMax = candidate;
                }

                decimal clamped = runningMax > 1m ? 1m : runningMax;
                adjustedOut[idx] = DslElementaryMath.NormalizeZero(clamped);
            }

            return MathErrorCode.None;
        }

        decimal factor = m;
        if (method == MultipleTestingMethod.BenjaminiYekutieli)
        {
            decimal harmonic = 0m;
            for (int j = 1; j <= m; j++)
            {
                harmonic += 1m / j;
            }

            factor *= harmonic;
        }

        decimal runningMin = 1m;
        for (int rank = m - 1; rank >= 0; rank--)
        {
            int idx = order[rank];
            decimal candidate = (pValues[idx] * factor) / (rank + 1);
            if (candidate < runningMin)
            {
                runningMin = candidate;
            }

            decimal clamped = runningMin < 0m ? 0m : (runningMin > 1m ? 1m : runningMin);
            adjustedOut[idx] = DslElementaryMath.NormalizeZero(clamped);
        }

        return MathErrorCode.None;
    }
}
