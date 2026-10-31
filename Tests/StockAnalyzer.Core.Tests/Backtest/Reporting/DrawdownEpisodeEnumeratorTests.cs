using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class DrawdownEpisodeEnumeratorTests
{
    private static readonly DateTime BaseUtc = ReportTestHelpers.BaseUtc;

    /// <summary>Points as <see cref="ReportTestHelpers.BuildResult"/> builds them: point i is dated BaseUtc + i days.</summary>
    private static ImmutableArray<EquityPoint> Points(params decimal[] equity) =>
        equity.Select((e, i) => new EquityPoint(i, BaseUtc.AddDays(i), e, e, 0m, 0m)).ToImmutableArray();

    private static ImmutableArray<RankedDrawdownEpisode> All(decimal initial, params decimal[] equity) =>
        DrawdownEpisodeEnumerator.Enumerate(initial, BaseUtc, Points(equity), int.MaxValue);

    [Fact]
    public void EmptyDefaultAndZeroCount_YieldEmpty()
    {
        Assert.Empty(DrawdownEpisodeEnumerator.Enumerate(100m, BaseUtc, ImmutableArray<EquityPoint>.Empty, 5));
        ImmutableArray<RankedDrawdownEpisode> fromDefault = DrawdownEpisodeEnumerator.Enumerate(100m, BaseUtc, default, 5);
        Assert.False(fromDefault.IsDefault);
        Assert.Empty(fromDefault);
        Assert.Empty(DrawdownEpisodeEnumerator.Enumerate(100m, BaseUtc, Points(90m), 0));
    }

    [Fact]
    public void LengthOne_AtOrAboveInitialCapital_HasNoEpisode()
    {
        Assert.Empty(All(100m, 100m));
        Assert.Empty(All(100m, 130m));
    }

    [Fact]
    public void LengthOne_BelowInitialCapital_IsOneUnrecoveredEpisodeFromTheInitialCapital()
    {
        RankedDrawdownEpisode episode = Assert.Single(All(100m, 90m));

        Assert.Equal((1, 0, 1, (int?)null), (episode.Rank, episode.PeakIndex, episode.TroughIndex, episode.RecoveryIndex));
        Assert.Equal(BaseUtc, episode.PeakUtc);
        Assert.Equal(100m, episode.PeakEquity);
        Assert.Equal(90m, episode.TroughEquity);
        Assert.Equal(0.1m, episode.DepthRatio);
        Assert.Equal(1, episode.DeclineBars);
        Assert.Null(episode.RecoveryBars);
        Assert.Null(episode.UnderwaterBars);
        Assert.Null(episode.RecoveryUtc);
    }

    [Fact]
    public void AllEqualAndMonotonicIncrease_HaveNoEpisode()
    {
        Assert.Empty(All(100m, 100m, 100m, 100m));
        Assert.Empty(All(100m, 101m, 102m, 150m));
    }

    [Fact]
    public void TwoEpisodes_AreRankedByDepth_AndOneRecoversWhileTheDeeperStaysUnderwater()
    {
        // E = 100 | 110 99 105 110 120 108 90 100. Episode A: peak 1 (110), trough 2 (99), recovery 4 (110 >= 110).
        // Episode B: peak 5 (120), trough 7 (90), never recovered.
        ImmutableArray<RankedDrawdownEpisode> episodes = All(100m, 110m, 99m, 105m, 110m, 120m, 108m, 90m, 100m);

        Assert.Equal(2, episodes.Length);
        RankedDrawdownEpisode deepest = episodes[0];
        Assert.Equal((1, 5, 7, (int?)null), (deepest.Rank, deepest.PeakIndex, deepest.TroughIndex, deepest.RecoveryIndex));
        Assert.Equal(0.25m, deepest.DepthRatio);
        Assert.Equal(2, deepest.DeclineBars);
        Assert.Null(deepest.UnderwaterBars);

        RankedDrawdownEpisode shallow = episodes[1];
        Assert.Equal((2, 1, 2, (int?)4), (shallow.Rank, shallow.PeakIndex, shallow.TroughIndex, shallow.RecoveryIndex));
        Assert.Equal(0.1m, shallow.DepthRatio);
        Assert.Equal((1, (int?)2, (int?)3), (shallow.DeclineBars, shallow.RecoveryBars, shallow.UnderwaterBars));
        Assert.Equal(BaseUtc.AddDays(0), shallow.PeakUtc);
        Assert.Equal(BaseUtc.AddDays(1), shallow.TroughUtc);
        Assert.Equal(BaseUtc.AddDays(3), shallow.RecoveryUtc);
        Assert.Equal(110m, shallow.PeakEquity);
        Assert.Equal(99m, shallow.TroughEquity);
    }

    [Fact]
    public void EqualHighs_MoveThePeakToTheLatest()
    {
        // E = 100 | 100 90 100: the first bar equals the high, so the peak is index 1 (not 0).
        RankedDrawdownEpisode episode = Assert.Single(All(100m, 100m, 90m, 100m));

        Assert.Equal((1, 2, (int?)3), (episode.PeakIndex, episode.TroughIndex, episode.RecoveryIndex));
    }

    [Fact]
    public void EqualDepth_RanksTheEarlierPeakFirst_AndTheTroughIsTheFirstMaximalIndex()
    {
        // E = 100 | 90 100 90 100 | 80 80 100: two 10% episodes, then one 20% episode whose trough is the FIRST 80.
        ImmutableArray<RankedDrawdownEpisode> episodes = All(100m, 90m, 100m, 90m, 100m, 80m, 80m, 100m);

        Assert.Equal(new[] { 4, 0, 2 }, episodes.Select(e => e.PeakIndex).ToArray());
        Assert.Equal(5, episodes[0].TroughIndex);
        Assert.Equal(new[] { 1, 2, 3 }, episodes.Select(e => e.Rank).ToArray());
    }

    [Fact]
    public void EpisodeStillUnderwaterAtTheEnd_HasNullRecoveryFields()
    {
        RankedDrawdownEpisode episode = Assert.Single(All(100m, 120m, 100m, 110m));

        Assert.Null(episode.RecoveryIndex);
        Assert.Null(episode.RecoveryUtc);
        Assert.Null(episode.RecoveryBars);
        Assert.Null(episode.UnderwaterBars);
    }

    [Fact]
    public void NegativeEquity_GivesADepthAboveOne()
    {
        RankedDrawdownEpisode episode = Assert.Single(All(100m, 50m, -20m, 30m));

        Assert.Equal(2, episode.TroughIndex);
        Assert.Equal(1.2m, episode.DepthRatio);
    }

    [Fact]
    public void TheCount_LimitsTheListToTheDeepest_AndLargerCountsReturnAll()
    {
        decimal[] equity = { 110m, 99m, 105m, 110m, 120m, 108m, 90m, 100m };

        ImmutableArray<RankedDrawdownEpisode> one = DrawdownEpisodeEnumerator.Enumerate(100m, BaseUtc, Points(equity), 1);
        ImmutableArray<RankedDrawdownEpisode> many = DrawdownEpisodeEnumerator.Enumerate(100m, BaseUtc, Points(equity), 50);

        Assert.Equal(5, Assert.Single(one).PeakIndex);
        Assert.Equal(2, many.Length);
    }

    [Fact]
    public void ARatioBeyondTheDecimalRange_PropagatesAnOverflow_ForTheCallerToReport()
    {
        Assert.Throws<OverflowException>(() => DrawdownEpisodeEnumerator.Enumerate(0.01m, BaseUtc, Points(-70_000_000_000_000_000_000_000_000_000m), 1));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DrawdownEpisodeEnumerator.Enumerate(0m, BaseUtc, Points(90m), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DrawdownEpisodeEnumerator.Enumerate(100m, BaseUtc, Points(90m), -1));
    }

    /// <summary>
    /// Proof (c): on random paths with many ties, rank 1 equals the evaluation artifact's ratio episode down to indices, bar counts and dates,
    /// and the list is ordered by depth and made of disjoint, chronologically ordered spans.
    /// </summary>
    [Fact]
    public void RankOne_EqualsTheEvaluationArtifactsRatioEpisode_OnRandomPaths()
    {
        var random = new Random(20261030);
        for (int path = 0; path < 300; path++)
        {
            int length = random.Next(1, 80);
            decimal[] equity = new decimal[length];
            decimal level = 100m;
            for (int i = 0; i < length; i++)
            {
                level = Math.Max(70m, Math.Min(130m, level + random.Next(-6, 7)));
                equity[i] = level;
            }

            BacktestResult result = ReportTestHelpers.BuildResult(100m, equity);
            (DrawdownEpisodeResult ratio, _) = DrawdownEpisodeCalculator.Compute(result, ReportTestHelpers.Options(), CancellationToken.None);
            ImmutableArray<RankedDrawdownEpisode> episodes = DrawdownEpisodeEnumerator.Enumerate(100m, BaseUtc, result.EquityPoints, int.MaxValue);
            string context = $"path {path}: {string.Join(",", equity)}";

            if (ratio.Status == DrawdownEpisodeStatus.NoDrawdown)
            {
                Assert.True(episodes.IsEmpty, context);
                continue;
            }

            DrawdownEpisode expected = ratio.Episode!;
            RankedDrawdownEpisode top = episodes[0];
            Assert.True((expected.PeakIndex, expected.TroughIndex, expected.RecoveryIndex) == (top.PeakIndex, top.TroughIndex, top.RecoveryIndex), context);
            Assert.True((expected.DeclineBars, expected.RecoveryBars, expected.UnderwaterBars) == (top.DeclineBars, top.RecoveryBars, top.UnderwaterBars), context);
            Assert.True((expected.PeakUtc, expected.TroughUtc, expected.RecoveryUtc) == (top.PeakUtc, top.TroughUtc, top.RecoveryUtc), context);
            Assert.True((expected.PeakEquity, expected.TroughEquity) == (top.PeakEquity, top.TroughEquity), context);
            Assert.True(Math.Abs(expected.Depth - top.DepthRatio) < 0.000000001m, context);

            for (int k = 1; k < episodes.Length; k++)
            {
                Assert.True(episodes[k - 1].DepthRatio >= episodes[k].DepthRatio, context);
            }

            RankedDrawdownEpisode[] byPeak = episodes.OrderBy(e => e.PeakIndex).ToArray();
            for (int k = 1; k < byPeak.Length; k++)
            {
                Assert.True(byPeak[k - 1].RecoveryIndex is { } end && end <= byPeak[k].PeakIndex, context);
            }
        }
    }
}
