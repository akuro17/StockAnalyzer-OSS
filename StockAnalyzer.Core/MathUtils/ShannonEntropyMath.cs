using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// The Shannon entropy sum H = -sum p_i ln(p_i) (in nats) shared by the entropy indicators: <c>CoreEntropyIndicator</c> (p_i = share of the returns in bin i) and
/// <c>CoreSSAEntropyIndicator</c> (p_i = share of the spectrum in eigenvalue i). Each indicator keeps its own normalisation (by ln of the number of bins or of the embedding
/// dimension) and its own rounding; only the sum is common.
/// </summary>
public static class ShannonEntropyMath
{
    /// <summary>The share below which a spectrum component is treated as absent by <c>CoreSSAEntropyIndicator</c> (its behaviour before the sum was shared).</summary>
    public const double NegligibleShare = 1e-12;

    /// <summary>
    /// H in nats of the shares <c>counts[i] / total</c>; categories with a zero count contribute nothing. <paramref name="total"/> is the sum of the counts and is not checked.
    /// </summary>
    public static double Nats(ReadOnlySpan<int> counts, int total)
    {
        double entropy = 0.0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }

            double p = (double)counts[i] / total;
            entropy -= p * Math.Log(p);
        }

        return entropy;
    }

    /// <summary>
    /// H in nats of the shares <c>weights[i] / total</c>; a share not above <paramref name="minShare"/> contributes nothing. <paramref name="total"/> is the sum of the weights
    /// and is not checked (the caller handles a total of zero).
    /// </summary>
    public static double Nats(ReadOnlySpan<double> weights, double total, double minShare)
    {
        double entropy = 0.0;
        for (int i = 0; i < weights.Length; i++)
        {
            double p = weights[i] / total;
            if (p > minShare)
            {
                entropy -= p * Math.Log(p);
            }
        }

        return entropy;
    }

    /// <summary>
    /// Normalized Permutation Entropy (Bandt-Pompe 2002) in [0, 1] using Lehmer code ordinal pattern bijection and reusing <see cref="Nats(ReadOnlySpan{int}, int)"/>.
    /// Returns NaN when <paramref name="embeddingDimension"/> is outside [3, 5], window length &lt; d + 2, embedded vector count &lt; d!, or any value is non-finite.
    /// </summary>
    public static double PermutationEntropy(ReadOnlySpan<double> window, int embeddingDimension = 3)
    {
        if (embeddingDimension < 3 || embeddingDimension > 5)
        {
            return double.NaN;
        }

        int factorial = embeddingDimension switch
        {
            3 => 6,
            4 => 24,
            5 => 120,
            _ => 0
        };

        int n = window.Length;
        int patternCount = n - embeddingDimension + 1;
        if (n < embeddingDimension + 2 || patternCount < factorial)
        {
            return double.NaN;
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(window[i]))
            {
                return double.NaN;
            }
        }

        Span<int> counts = stackalloc int[120];
        Span<int> activeCounts = counts[..factorial];
        activeCounts.Clear();

        for (int t = 0; t < patternCount; t++)
        {
            int idx = 0;
            for (int i = 0; i < embeddingDimension; i++)
            {
                int smallerAfter = 0;
                double vi = window[t + i];
                for (int j = i + 1; j < embeddingDimension; j++)
                {
                    if (window[t + j] < vi)
                    {
                        smallerAfter++;
                    }
                }

                idx = idx * (embeddingDimension - i) + smallerAfter;
            }

            activeCounts[idx]++;
        }

        double hNats = Nats(activeCounts, patternCount);
        double normalized = hNats / Math.Log(factorial);
        return double.IsFinite(normalized) ? Math.Clamp(normalized, 0.0, 1.0) : double.NaN;
    }

    /// <summary>
    /// Sample Entropy (Richman-Moorman 2000) for template length <paramref name="m"/> in [1, 3] and tolerance <paramref name="toleranceMultiplier"/> * sample stddev.
    /// Applies Richman-Moorman finite-sample floor ln(2 * countB) when countB &gt; 0 and countA == 0. Returns 0 on constant series, NaN on invalid input or countB == 0.
    /// </summary>
    public static double SampleEntropy(ReadOnlySpan<double> window, int m = 2, double toleranceMultiplier = 0.2)
    {
        int n = window.Length;
        if (m < 1 || m > 3 || !double.IsFinite(toleranceMultiplier) || toleranceMultiplier <= 0.0 || n < m + 4)
        {
            return double.NaN;
        }

        double lowest = window[0];
        double highest = window[0];
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double value = window[i];
            if (!double.IsFinite(value))
            {
                return double.NaN;
            }

            if (value < lowest) lowest = value;
            if (value > highest) highest = value;
            sum += value;
        }

        if (lowest == highest)
        {
            return 0.0;
        }

        double mean = sum / n;
        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = window[i] - mean;
            sumSq += d * d;
        }

        double stdDev = Math.Sqrt(sumSq / (n - 1));
        if (!(stdDev > 1e-15) || !double.IsFinite(stdDev))
        {
            return 0.0;
        }

        double tolerance = toleranceMultiplier * stdDev;
        int templateCount = n - m;
        long countB = 0;
        long countA = 0;

        for (int i = 0; i < templateCount; i++)
        {
            for (int j = i + 1; j < templateCount; j++)
            {
                bool matchedM = true;
                for (int k = 0; k < m; k++)
                {
                    if (Math.Abs(window[i + k] - window[j + k]) > tolerance)
                    {
                        matchedM = false;
                        break;
                    }
                }

                if (!matchedM)
                {
                    continue;
                }

                countB++;
                if (Math.Abs(window[i + m] - window[j + m]) <= tolerance)
                {
                    countA++;
                }
            }
        }

        if (countB == 0)
        {
            return double.NaN;
        }

        if (countA == 0)
        {
            return Math.Log(2.0 * countB);
        }

        return -Math.Log((double)countA / countB);
    }
}
