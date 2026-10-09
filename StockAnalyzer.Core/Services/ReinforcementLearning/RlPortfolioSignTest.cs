using System;
using System.Numerics;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// The exact one-sided sign test over the paired block deltas of a comparison, as a REFERENCE value only: the blocks of a cell are not independent
/// (they share the market path and the model), so no p-value here is evidence of anything. Pure integer arithmetic (<see cref="BigInteger"/> binomial
/// counts over <c>2^n</c>); the result is converted to <see cref="double"/> at the very end. A tie (delta exactly zero) is not a pair.
/// </summary>
public static class RlPortfolioSignTest
{
    /// <summary>The two-sided level the smallest attainable p-value is measured against (the common convention); a reference level, not a decision threshold.</summary>
    public const double ReferenceLevel = 0.05;

    /// <summary>
    /// The smallest number of effective (non-tied) pairs whose smallest attainable two-sided p-value is below <see cref="ReferenceLevel"/>. With fewer pairs
    /// no sign test can reach the level whatever the data are, so the comparison states "inference unavailable". Derived, never typed (6 at the 0.05 level).
    /// </summary>
    public static readonly int MinimumEffectivePairs = FirstCountBelowLevel();

    /// <summary><c>P(X &gt;= wins)</c> for <c>X ~ Binomial(effective, 1/2)</c>: the exact one-sided p-value that the first report wins at least that often.</summary>
    public static double OneSidedP(int wins, int effective)
    {
        if (effective < 0) throw new ArgumentOutOfRangeException(nameof(effective));
        if (wins < 0 || wins > effective) throw new ArgumentOutOfRangeException(nameof(wins));
        BigInteger tail = BigInteger.Zero, term = BigInteger.One;            // term = C(effective, k), k from 0
        // tail = sum_{k = wins}^{effective} C(effective, k)
        for (int k = 0; k <= effective; k++)
        {
            if (k >= wins) tail += term;
            term = term * (effective - k) / (k + 1);
        }
        return Ratio(tail, effective);
    }

    /// <summary>The smallest two-sided p-value any data can give with <paramref name="effective"/> pairs: <c>min(1, 2 * 2^-effective)</c>.</summary>
    public static double MinimumAttainableTwoSidedP(int effective)
    {
        if (effective < 1) throw new ArgumentOutOfRangeException(nameof(effective));
        return Math.Min(1d, Ratio(2, effective));
    }

    /// <summary><c>numerator / 2^exponent</c> without overflow of either part (the numerator is scaled down to 62 significant bits first).</summary>
    private static double Ratio(BigInteger numerator, int exponent)
    {
        int shift = (int)Math.Max(0, (long)numerator.GetBitLength() - 62);
        return Math.ScaleB((double)(numerator >> shift), shift - exponent);
    }

    private static int FirstCountBelowLevel()
    {
        int n = 1;
        while (Ratio(2, n) >= ReferenceLevel) n++;
        return n;
    }
}
