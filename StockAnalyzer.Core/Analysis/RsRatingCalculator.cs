using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Five closing prices sampled in reverse chronological order (most recent non-null closes)
/// for computing the <see cref="RsRatingCalculator"/> raw score.
/// </summary>
/// <param name="C0">Latest non-null close (1st from latest).</param>
/// <param name="C63">Close 63 trading bars back (64th from latest).</param>
/// <param name="C126">Close 126 trading bars back (127th from latest).</param>
/// <param name="C189">Close 189 trading bars back (190th from latest).</param>
/// <param name="C252">Close 252 trading bars back (253rd from latest).</param>
public readonly record struct RsRatingCloses(
    decimal C0,
    decimal C63,
    decimal C126,
    decimal C189,
    decimal C252);

/// <summary>
/// Pure cross-sectional calculator for <c>RS Rating (IBD-style approximation)</c>.
/// </summary>
/// <remarks>
/// IBD's proprietary Relative Strength Rating formula is not published. This calculator implements
/// a documented approximation:
/// <list type="bullet">
///   <item><description>Raw score (owner decision D-22=B): <c>0.4 * (C0/C63 - 1) + 0.2 * (C0/C126 - 1) + 0.2 * (C0/C189 - 1) + 0.2 * (C0/C252 - 1)</c> in <c>decimal</c> arithmetic.</description></item>
///   <item><description>Rank: 1-based strictly-less-than count + 1 across eligible universe symbols (ties share the same rank, order-independent).</description></item>
///   <item><description>Rating (owner decision U-20=A): 50 when <c>M == 1</c>; <c>round(1 + 98 * (rank - 1) / (M - 1))</c> via <see cref="MidpointRounding.AwayFromZero"/> when <c>M &gt;= 2</c>.</description></item>
/// </list>
/// Output unit: dimensionless ordinal rating in <c>[1, 99]</c>.
/// </remarks>
public static class RsRatingCalculator
{
    public const string DisplayTitle = "RS Rating (IBD-style approximation)";

    // 1-based reverse-chronological positions (non-null closes only):
    // C252 requires 252 prior intervals -> 253 closes including latest bar C0.
    public const int RankLatestClose = 1;
    public const int RankQuarter1Close = 64;
    public const int RankQuarter2Close = 127;
    public const int RankQuarter3Close = 190;
    public const int RankQuarter4Close = 253;
    public const int RequiredCloseCount = RankQuarter4Close;
    public const int CheckpointCount = 5;

    public const int MinRating = 1;
    public const int MaxRating = 99;
    public const int SingleSymbolRating = 50;

    private const decimal WeightRecentQuarter = 0.4m;
    private const decimal WeightOlderQuarter = 0.2m;
    private const decimal RatingSpan = MaxRating - MinRating;

    /// <summary>
    /// Computes <c>RS Rating (IBD-style approximation)</c> on the inclusive <c>[1, 99]</c> scale
    /// for all eligible symbols in <paramref name="closesBySymbol"/>.
    /// Symbols with any non-positive close or <see cref="decimal"/> overflow are excluded from the result and universe count.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Calculate(
        IReadOnlyDictionary<string, RsRatingCloses>? closesBySymbol)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (closesBySymbol == null || closesBySymbol.Count == 0)
        {
            return result;
        }

        var scored = new List<(string Symbol, decimal RawScore)>(closesBySymbol.Count);
        foreach (var (symbol, closes) in closesBySymbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            if (TryComputeRawScore(closes, out decimal rawScore))
            {
                scored.Add((symbol, rawScore));
            }
        }

        int m = scored.Count;
        if (m == 0)
        {
            return result;
        }

        if (m == 1)
        {
            result[scored[0].Symbol] = SingleSymbolRating;
            return result;
        }

        // Sorting by raw score ascending lets us compute rank = (# strictly smaller) + 1 in O(M log M):
        // at the first element of any tied run, its 0-based sorted index equals the count of strictly smaller scores.
        scored.Sort(static (a, b) => a.RawScore.CompareTo(b.RawScore));

        int currentRank = 1;
        decimal denominator = m - 1;
        for (int i = 0; i < m; i++)
        {
            if (i > 0 && scored[i].RawScore != scored[i - 1].RawScore)
            {
                currentRank = i + 1;
            }

            decimal scaled = MinRating + (RatingSpan * (currentRank - 1) / denominator);
            int rating = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            result[scored[i].Symbol] = rating;
        }

        return result;
    }

    private static bool TryComputeRawScore(RsRatingCloses closes, out decimal rawScore)
    {
        rawScore = 0m;
        if (closes.C0 <= 0m || closes.C63 <= 0m || closes.C126 <= 0m || closes.C189 <= 0m || closes.C252 <= 0m)
        {
            return false;
        }

        try
        {
            decimal roc63 = (closes.C0 / closes.C63) - 1m;
            decimal roc126 = (closes.C0 / closes.C126) - 1m;
            decimal roc189 = (closes.C0 / closes.C189) - 1m;
            decimal roc252 = (closes.C0 / closes.C252) - 1m;

            rawScore = (WeightRecentQuarter * roc63)
                     + (WeightOlderQuarter * roc126)
                     + (WeightOlderQuarter * roc189)
                     + (WeightOlderQuarter * roc252);
            return true;
        }
        catch (OverflowException)
        {
            rawScore = 0m;
            return false;
        }
    }
}
