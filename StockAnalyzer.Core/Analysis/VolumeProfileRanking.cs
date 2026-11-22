using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// One ranked price bin of a volume profile or a time-at-price profile.
/// <see cref="Volume"/> is <see cref="VolumeBin.TotalVolume"/> (traded volume; the bar count for Time at Price).
/// Unit of <see cref="Share"/> and <see cref="CumulativeShare"/>: ratio in (0, 1].
/// </summary>
/// <param name="Rank">1-based ordinal rank, unique within one ranking (no shared ranks).</param>
/// <param name="BinIndex">0-based index of the bin in the source list (0 = lowest price).</param>
/// <param name="LowerBound">Lower price bound of the bin (price-native).</param>
/// <param name="UpperBound">Upper price bound of the bin (price-native).</param>
/// <param name="Price">Centre price of the bin (price-native).</param>
/// <param name="Volume">Weight of the bin: volume, or bar count for Time at Price.</param>
/// <param name="Share"><see cref="Volume"/> divided by the sum of the weights of ALL bins of the profile.</param>
/// <param name="CumulativeShare">Sum of the weights of the rows 1..<see cref="Rank"/> divided by the same total.</param>
public readonly record struct VolumeBinRank(
    int Rank,
    int BinIndex,
    decimal LowerBound,
    decimal UpperBound,
    decimal Price,
    long Volume,
    decimal Share,
    decimal CumulativeShare);

/// <summary>
/// Ranks the price bins of a profile by weight (Volume Profile: volume, Time at Price: bar count).
/// A separate function by design: the existing profile and Point of Control functions of <see cref="VolumeAnalysis"/> are unchanged.
/// </summary>
public static class VolumeProfileRanking
{
    /// <summary>
    /// Ranks <paramref name="bins"/> by <see cref="VolumeBin.TotalVolume"/> in descending order; bins of equal weight keep ascending <see cref="VolumeBinRank.BinIndex"/>
    /// (the lower price first), the tie rule of <see cref="VolumeAnalysis.FindPointOfControlIndex(IReadOnlyList{VolumeBin}?)"/>, so row 1 is the Point of Control whenever any bin has weight.
    /// The order is a total order, so the result does not depend on the stability of the sort.
    /// <para>
    /// Total = decimal sum of the weights of ALL bins (also those cut off by <paramref name="limit"/>). Share = weight / Total and CumulativeShare = (exact sum of the weights
    /// of rows 1..k) / Total; no rounding is done here and no rounded value is ever summed, so the last row of the whole ranking is exactly 1m.
    /// </para>
    /// <para>
    /// Bins with weight 0 are not ranked. A null or empty list, or a Total of 0, gives an empty result.
    /// <paramref name="limit"/> null returns every ranked row; a value above the row count returns all rows; the input is never modified.
    /// </para>
    /// </summary>
    /// <param name="bins">Bins in ascending price order, or null.</param>
    /// <param name="limit">Maximum number of rows to return (1 or more), or null for all rows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is below 1.</exception>
    /// <exception cref="ArgumentException">A bin has a negative weight.</exception>
    public static IReadOnlyList<VolumeBinRank> Rank(IReadOnlyList<VolumeBin>? bins, int? limit = null)
    {
        if (limit.HasValue && limit.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit.Value, "limit must be 1 or more.");
        }

        if (bins == null || bins.Count == 0) return Array.Empty<VolumeBinRank>();

        decimal total = 0m;
        int positiveCount = 0;
        for (int i = 0; i < bins.Count; i++)
        {
            long weight = bins[i].TotalVolume;
            if (weight < 0)
            {
                throw new ArgumentException($"Bin {i} has a negative weight ({weight}).", nameof(bins));
            }

            if (weight > 0)
            {
                total += weight;
                positiveCount++;
            }
        }

        if (positiveCount == 0) return Array.Empty<VolumeBinRank>();

        int[] order = new int[positiveCount];
        int next = 0;
        for (int i = 0; i < bins.Count; i++)
        {
            if (bins[i].TotalVolume > 0) order[next++] = i;
        }

        // Weight descending, then BinIndex ascending: a total order, so Array.Sort needs no stability.
        Array.Sort(order, (a, b) =>
        {
            int byWeight = bins[b].TotalVolume.CompareTo(bins[a].TotalVolume);
            return byWeight != 0 ? byWeight : a.CompareTo(b);
        });

        int rowCount = limit.HasValue && limit.Value < positiveCount ? limit.Value : positiveCount;
        var rows = new VolumeBinRank[rowCount];
        decimal running = 0m;
        for (int r = 0; r < rowCount; r++)
        {
            int index = order[r];
            VolumeBin bin = bins[index];
            running += bin.TotalVolume;
            rows[r] = new VolumeBinRank(
                Rank: r + 1,
                BinIndex: index,
                LowerBound: bin.LowerBound,
                UpperBound: bin.UpperBound,
                Price: bin.Price,
                Volume: bin.TotalVolume,
                Share: bin.TotalVolume / total,
                CumulativeShare: running / total);
        }

        return rows;
    }
}
