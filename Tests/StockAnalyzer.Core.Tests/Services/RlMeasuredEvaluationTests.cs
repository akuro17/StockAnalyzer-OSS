using System;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlMeasuredEvaluationTests
{
    private static RlEpisodeEvaluation Episode(string partition, string terminalReturn, string drawdown)
        => new("b", partition, terminalReturn, drawdown, 10, "Horizon", "0", "0", "0", "0", 0, 0);

    [Fact]
    public void FromEpisodes_UsesTheReportsOwnMeanReturnAndWorstDrawdown()
    {
        var measured = RlMeasuredEvaluation.FromEpisodes(new[]
        {
            Episode(RlReplayDefaults.OosPartition, "0.10", "0.02"),
            Episode(RlReplayDefaults.OosPartition, "-0.04", "0.07"),
            Episode(RlReplayDefaults.OosPartition, "0.03", "0.05"),
        });

        Assert.NotNull(measured);
        Assert.Equal(RlReplayDefaults.OosPartition, measured!.Partition);
        Assert.Equal(3, measured.Episodes);
        Assert.Equal(0.03m, measured.MeanReturn);
        Assert.Equal(0.07m, measured.WorstMaximumDrawdown);
        Assert.Equal(-0.04m, measured.MinReturn);
        Assert.Equal(2, measured.PositiveEpisodes);
    }

    [Fact]
    public void FromEpisodes_CountsOnlyReturnsAboveZeroAndTakesTheLowestReturn()
    {
        var single = RlMeasuredEvaluation.FromEpisodes(new[] { Episode("validation", "0.0831", "0.01") })!;
        Assert.Equal((0.0831m, 0.0831m, 1), (single.MeanReturn, single.MinReturn, single.PositiveEpisodes));

        var withZero = RlMeasuredEvaluation.FromEpisodes(new[]
        {
            Episode("validation", "0", "0.01"), Episode("validation", "-0.20", "0.30"), Episode("validation", "0.0000001", "0.02"),
        })!;
        Assert.Equal(-0.20m, withZero.MinReturn);
        Assert.Equal(1, withZero.PositiveEpisodes);
    }

    [Fact]
    public void FromEpisodes_ReturnsNothingForNoEpisodeAndRejectsNull()
    {
        Assert.Null(RlMeasuredEvaluation.FromEpisodes(Array.Empty<RlEpisodeEvaluation>()));
        Assert.Throws<ArgumentNullException>(() => RlMeasuredEvaluation.FromEpisodes(null!));
    }
}
