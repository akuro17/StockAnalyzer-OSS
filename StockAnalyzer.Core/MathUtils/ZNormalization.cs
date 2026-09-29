using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Single source of truth for Z-normalization (population standard deviation) of a window and the
/// threshold below which a window is treated as flat.
/// </summary>
public static class ZNormalization
{
    /// <summary>Population sigma at or below which a window is treated as flat (cannot be normalized).</summary>
    public const double FlatSigmaEpsilon = 1e-12;

    /// <summary>
    /// Z-normalizes <paramref name="input"/> into <paramref name="output"/> (may alias the input) using the population sigma.
    /// Returns false, leaving the output unchanged for an aliased call, when sigma is at or below <see cref="FlatSigmaEpsilon"/>.
    /// </summary>
    public static bool TryNormalize(ReadOnlySpan<double> input, Span<double> output)
    {
        double sum = 0.0;
        for (int i = 0; i < input.Length; i++)
        {
            sum += input[i];
        }
        double mean = sum / input.Length;

        double sqDiffSum = 0.0;
        for (int i = 0; i < input.Length; i++)
        {
            double d = input[i] - mean;
            sqDiffSum += d * d;
        }
        double std = Math.Sqrt(sqDiffSum / input.Length);
        if (std <= FlatSigmaEpsilon)
        {
            return false;
        }

        for (int i = 0; i < input.Length; i++)
        {
            output[i] = (input[i] - mean) / std;
        }
        return true;
    }
}
