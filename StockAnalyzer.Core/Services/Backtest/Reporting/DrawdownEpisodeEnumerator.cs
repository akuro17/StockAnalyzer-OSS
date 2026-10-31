using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// One drawdown episode of the ranked list. Indices address the series E[0..m] with E[0] = initial capital and E[j] = points[j - 1]
/// (the same index space as the evaluation artifact's drawdown episode). <see cref="DepthRatio"/> is the unitless ratio
/// <c>(PeakEquity - TroughEquity) / PeakEquity</c> (at least 0; it may exceed 1 for a short position). Bar counts are index differences;
/// the recovery fields are null while the episode is still underwater at the last evaluated bar.
/// </summary>
public sealed record RankedDrawdownEpisode(
    int Rank,
    int PeakIndex,
    int TroughIndex,
    int? RecoveryIndex,
    DateTime PeakUtc,
    DateTime TroughUtc,
    DateTime? RecoveryUtc,
    decimal PeakEquity,
    decimal TroughEquity,
    decimal DepthRatio,
    int DeclineBars,
    int? RecoveryBars,
    int? UnderwaterBars);

/// <summary>
/// Lists every drawdown episode of the evaluated equity series and returns the deepest K. It applies the single drawdown definition of
/// <see cref="DrawdownSeriesCalculator"/> (spec §5.4) to every episode instead of only the deepest one: an episode starts at a peak
/// (a bar whose equity is at least every earlier equity; equal highs move the peak to the latest) followed by a lower bar, the trough is the
/// FIRST index of the maximal depth inside the episode, and recovery is the first later bar with equity at least the peak (null when it never
/// happens). Depth is ranked on the same double ratio series the evaluation artifact uses, so rank 1 equals that artifact's
/// <c>RatioDrawdown.Episode</c> down to PeakIndex/TroughIndex/RecoveryIndex; ties on depth rank the earlier peak first.
/// </summary>
public static class DrawdownEpisodeEnumerator
{
    /// <param name="initialCapital">E[0]; must be positive (guaranteed by <c>BacktestConfiguration</c>).</param>
    /// <param name="initialTimestamp">The date of E[0] (the evaluation start), used for the date of an episode that peaks at index 0.</param>
    /// <param name="points">Evaluated points only (warm-up removed), chronological.</param>
    /// <param name="maxEpisodes">How many of the deepest episodes to return; 0 returns none, <see cref="int.MaxValue"/> returns all.</param>
    public static ImmutableArray<RankedDrawdownEpisode> Enumerate(
        decimal initialCapital,
        DateTime initialTimestamp,
        ImmutableArray<EquityPoint> points,
        int maxEpisodes)
    {
        if (initialCapital <= 0m) throw new ArgumentOutOfRangeException(nameof(initialCapital), initialCapital, "Initial capital must be positive.");
        if (maxEpisodes < 0) throw new ArgumentOutOfRangeException(nameof(maxEpisodes), maxEpisodes, "The episode count must not be negative.");
        if (maxEpisodes == 0 || points.IsDefaultOrEmpty)
        {
            return ImmutableArray<RankedDrawdownEpisode>.Empty;
        }

        ImmutableArray<decimal>.Builder equityBuilder = ImmutableArray.CreateBuilder<decimal>(points.Length + 1);
        equityBuilder.Add(initialCapital);
        foreach (EquityPoint point in points)
        {
            equityBuilder.Add(point.Equity);
        }

        ImmutableArray<decimal> equity = equityBuilder.MoveToImmutable();
        ImmutableArray<double> depth = DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(equity);

        var found = new List<(double Depth, int Peak, int Trough, int? Recovery)>();
        int peak = 0;
        int i = 1;
        while (i < equity.Length)
        {
            if (equity[i] >= equity[peak])
            {
                peak = i;
                i++;
                continue;
            }

            // Inside an episode the running high is equity[peak], so depth[] of the whole series is this episode's depth.
            int trough = i;
            int j = i + 1;
            while (j < equity.Length && equity[j] < equity[peak])
            {
                if (depth[j] > depth[trough]) trough = j;
                j++;
            }

            int? recovery = j < equity.Length ? j : null;
            if (depth[trough] > 0d) found.Add((depth[trough], peak, trough, recovery));
            if (recovery is not { } recovered) break;
            peak = recovered;
            i = recovered + 1;
        }

        found.Sort((a, b) =>
        {
            int byDepth = b.Depth.CompareTo(a.Depth);
            return byDepth != 0 ? byDepth : a.Peak.CompareTo(b.Peak);
        });

        int count = Math.Min(maxEpisodes, found.Count);
        ImmutableArray<RankedDrawdownEpisode>.Builder ranked = ImmutableArray.CreateBuilder<RankedDrawdownEpisode>(count);
        for (int rank = 0; rank < count; rank++)
        {
            (_, int p, int t, int? r) = found[rank];
            ranked.Add(new RankedDrawdownEpisode(
                rank + 1,
                p,
                t,
                r,
                TimestampAt(p),
                TimestampAt(t),
                r is { } recoveryIndex ? TimestampAt(recoveryIndex) : null,
                equity[p],
                equity[t],
                (equity[p] - equity[t]) / equity[p],
                t - p,
                r - t,
                r - p));
        }

        return ranked.MoveToImmutable();

        DateTime TimestampAt(int index) => index == 0 ? initialTimestamp : points[index - 1].Timestamp;
    }
}
