using System;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Near-ties and cross-checks of the drawdown durations against the evaluation artifact's episode
/// (Y:\Temp\sa_implementation_plan_BacktestReportVerificationOracle.md, T9). Both use the same double-precision ratio series and the
/// spec §5.4 tie rules, so they must always agree on WHICH episode is the deepest, including when the depths differ by less than a double can resolve.
/// </summary>
public class DrawdownDurationConsistencyTests
{
    /// <summary>Post-warm-up equity E[1..m]; E[0] is the 100 initial capital of <see cref="ReportTestHelpers.BuildResult"/>.</summary>
    private static (BacktestReport Report, DrawdownEpisodeResult Ratio, DrawdownEpisodeResult Amount) Evaluate(params decimal[] postWarmupEquity)
    {
        BacktestResult result = ReportTestHelpers.BuildResult(100m, postWarmupEquity);
        BacktestReportOptions options = ReportTestHelpers.Options();
        BacktestReport report = new BacktestReportGenerator().Generate(result, options);
        (DrawdownEpisodeResult ratio, DrawdownEpisodeResult amount) = DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);
        return (report, ratio, amount);
    }

    private static decimal DepthDuration(BacktestReport report) => report.MaxDepthDrawdownDuration!.Value.Value!.Value;

    // ---- Near-ties -----------------------------------------------------------------------------------------------------------------

    // 1e-18 below 90: the ratio (100-E)/100 differs from 0.1 by 1e-20, far below the spacing of doubles near 0.1 (about 1.4e-17).
    private const decimal MarginallyDeeper = 89.999999999999999999m;

    [Fact]
    public void NearTie_BelowDoublePrecision_FirstMaximalTroughWins_WhenTheFirstEpisodeComesFirst()
    {
        // E = 100,90,95,100 | 100,MarginallyDeeper,100:  episode A (3 bars, depth 0.1) then episode B (2 bars, depth 0.10000000000000000001).
        // In double the two depths are identical, so the FIRST maximal trough (A, 3 bars) is the documented winner even though B is deeper in decimal.
        var (report, ratio, _) = Evaluate(90m, 95m, 100m, MarginallyDeeper, 100m);

        Assert.Equal(3m, DepthDuration(report));
        Assert.Equal(0, ratio.Episode!.PeakIndex);
        Assert.Equal(3, ratio.Episode.UnderwaterBars);
        Assert.Equal(ratio.Episode.UnderwaterBars, (int)DepthDuration(report));
    }

    [Fact]
    public void NearTie_BelowDoublePrecision_FirstMaximalTroughWins_WhenTheDeeperInDecimalComesFirst()
    {
        // E = 100,MarginallyDeeper,100 | 100,90,95,100: B-like episode first (2 bars, decimal-deeper), then a 3-bar one with the same double depth.
        var (report, ratio, _) = Evaluate(MarginallyDeeper, 100m, 90m, 95m, 100m);

        Assert.Equal(2m, DepthDuration(report));
        Assert.Equal(2, ratio.Episode!.UnderwaterBars);
        Assert.Equal(ratio.Episode.UnderwaterBars, (int)DepthDuration(report));
    }

    [Fact]
    public void Depths_ThatADoubleCanResolve_PickTheTrulyDeeperEpisode_InEitherOrder()
    {
        // 1e-6 apart: resolvable in double, so the deeper episode wins regardless of position.
        var (laterDeeper, laterRatio, _) = Evaluate(90m, 95m, 100m, 89.999999m, 100m);
        var (earlierDeeper, earlierRatio, _) = Evaluate(89.999999m, 100m, 90m, 95m, 100m);

        Assert.Equal(2m, DepthDuration(laterDeeper));
        Assert.Equal(laterRatio.Episode!.UnderwaterBars, (int)DepthDuration(laterDeeper));
        Assert.Equal(2m, DepthDuration(earlierDeeper));
        Assert.Equal(earlierRatio.Episode!.UnderwaterBars, (int)DepthDuration(earlierDeeper));
    }

    [Fact]
    public void EqualDepthsInDecimal_FirstTroughWins_AndMatchesTheArtifact()
    {
        // E = 100,90,95,100 | 100,90,100: identical depth 0.1: the first (3 bars) wins.
        var (report, ratio, _) = Evaluate(90m, 95m, 100m, 90m, 100m);

        Assert.Equal(3m, DepthDuration(report));
        Assert.Equal(3, ratio.Episode!.UnderwaterBars);
    }

    // ---- Artifact consistency ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RecoveredEpisode_MetricEqualsTheArtifactsUnderwaterBars_AndIsNotCensored()
    {
        var (report, ratio, _) = Evaluate(110m, 105m, 100m, 110m, 120m, 118m, 119m, 117m, 121m);

        Assert.Equal(3, ratio.Episode!.UnderwaterBars);
        Assert.Equal(3m, DepthDuration(report));
        Assert.False(report.MaxDepthDrawdownDurationRightCensored);
    }

    [Fact]
    public void UnrecoveredEpisode_ArtifactHasNoUnderwaterBars_WhileTheMetricCountsToTheLastBar()
    {
        // The documented, intentional difference: the artifact leaves an unrecovered episode open (null), the metric counts it to E[m].
        var (report, ratio, _) = Evaluate(90m, 95m, 92m);

        Assert.Null(ratio.Episode!.RecoveryIndex);
        Assert.Null(ratio.Episode.UnderwaterBars);
        Assert.Equal(3m, DepthDuration(report));                                  // m - peak = 3 - 0
        Assert.Equal(3 - ratio.Episode.PeakIndex, (int)DepthDuration(report));
        Assert.True(report.MaxDepthDrawdownDurationRightCensored);
        Assert.True(report.LongestDrawdownDurationRightCensored);
    }

    [Fact]
    public void RecoveryIndexOfTheArtifact_TellsTheSameCensoringAsTheFlag()
    {
        foreach (decimal[] path in new[]
        {
            new[] { 90m, 100m },                  // recovered
            new[] { 90m, 95m },                   // unrecovered
            new[] { 101m, 102m },                 // no drawdown
            new[] { 90m, 100m, 95m },             // an older episode recovered, the deepest one is not the open one
        })
        {
            var (report, ratio, _) = Evaluate(path);
            bool artifactSaysOpen = ratio.Status == DrawdownEpisodeStatus.Available && ratio.Episode!.RecoveryIndex is null;

            Assert.Equal(artifactSaysOpen, report.MaxDepthDrawdownDurationRightCensored);
        }
    }

    // ---- Ratio basis versus amount basis -----------------------------------------------------------------------------------------------------

    [Fact]
    public void RatioDeepestAndAmountDeepestEpisodesDiffer_TheMetricFollowsTheRatioEpisode()
    {
        // E = 100,50,100,1000,900,950,1000.
        // Episode A (peak idx0 -> recovers idx2): amount 50, ratio 0.5  -> deepest by RATIO, 2 bars.
        // Episode B (peak idx3 -> recovers idx6): amount 100, ratio 0.1 -> deepest by AMOUNT, 3 bars.
        var (report, ratio, amount) = Evaluate(50m, 100m, 1_000m, 900m, 950m, 1_000m);

        Assert.Equal(0, ratio.Episode!.PeakIndex);
        Assert.Equal(3, amount.Episode!.PeakIndex);
        Assert.Equal(2m, DepthDuration(report));                                   // follows the ratio episode (basis of MaxDrawdown)
        Assert.Equal(ratio.Episode.UnderwaterBars, (int)DepthDuration(report));
        Assert.NotEqual(amount.Episode.UnderwaterBars, (int)DepthDuration(report)); // the amount-basis episode lasts 3 bars: there is no amount-basis duration metric
        Assert.Equal(3m, report.LongestDrawdownDuration!.Value.Value);              // the longest episode is B, whatever its depth
    }
}
