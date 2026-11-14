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
}
