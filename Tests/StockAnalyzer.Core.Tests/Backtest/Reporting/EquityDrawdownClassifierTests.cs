using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// <see cref="EquityDrawdownClassifier"/> must agree with the single drawdown definition (spec §5.4): it only delegates to
/// <see cref="DrawdownSeriesCalculator"/>, so these tests compare it against that calculator and the episode artifact.
/// </summary>
public class EquityDrawdownClassifierTests
{
    private static readonly DateTime BaseUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImmutableArray<EquityPoint> Points(params decimal[] equity) =>
        equity.Select((e, i) => new EquityPoint(i, BaseUtc.AddDays(i), e, e, 0m, 0m)).ToImmutableArray();

    private static bool[] Flags(decimal initialCapital, params decimal[] equity) =>
        EquityDrawdownClassifier.ComputeUnderwaterFlags(initialCapital, Points(equity)).ToArray();

    [Theory]
    [InlineData(100, new[] { 120, 90, 108 })]
    [InlineData(100, new[] { 120, 120, 90, 90, 120, 130, 125, 130, 131 })]
    [InlineData(100, new[] { 90, 80, 95, 100, 99, 100, 101 })]
    [InlineData(50, new[] { 40, 60, 60, 59, 61, 61, 20, 21 })]
    public void Flags_AlwaysEqualTheDrawdownAmountSeriesBeingPositive(int initial, int[] equityInts)
    {
        decimal[] equity = equityInts.Select(e => (decimal)e).ToArray();
        var full = ImmutableArray.CreateRange(new[] { (decimal)initial }.Concat(equity));

        ImmutableArray<decimal> amount = DrawdownSeriesCalculator.ComputeDrawdownAmountSeries(full);
        bool[] flags = Flags(initial, equity);

        Assert.Equal(equity.Length, flags.Length);
        for (int j = 0; j < flags.Length; j++)
        {
            Assert.Equal(amount[j + 1] > 0m, flags[j]);
        }
    }

    [Fact]
    public void HighTie_IsNotDrawdown()
    {
        // E = 100, 120, 120(tie), 120(tie): ties are "at the high".
        Assert.Equal(new[] { false, false, false }, Flags(100m, 120m, 120m, 120m));
    }

    [Fact]
    public void FirstBarBelowInitialCapital_IsDrawdown_BecauseTheHighStartsAtInitialCapital()
    {
        bool[] flags = Flags(100m, 95m, 100m);

        Assert.True(flags[0]);
        Assert.False(flags[1]); // back at the initial-capital high
    }

    [Fact]
    public void FirstBarEqualToInitialCapital_IsNotDrawdown()
    {
        Assert.False(Flags(100m, 100m)[0]);
    }

    [Fact]
    public void MonotonicIncrease_HasNoDrawdown()
    {
        Assert.All(Flags(100m, 101m, 102m, 150m), flag => Assert.False(flag));
    }

    [Fact]
    public void EmptyAndDefaultInput_YieldEmpty()
    {
        Assert.Empty(EquityDrawdownClassifier.ComputeUnderwaterFlags(100m, ImmutableArray<EquityPoint>.Empty));
        ImmutableArray<bool> fromDefault = EquityDrawdownClassifier.ComputeUnderwaterFlags(100m, default);
        Assert.False(fromDefault.IsDefault);
        Assert.Empty(fromDefault);
    }

    [Fact]
    public void DecimalOverflow_ReturnsUnavailable_InsteadOfThrowing()
    {
        ImmutableArray<bool> flags = EquityDrawdownClassifier.ComputeUnderwaterFlags(
            decimal.MaxValue,
            Points(decimal.MinValue));

        Assert.True(flags.IsDefault);
    }

    [Fact]
    public void AnyFlag_IsTrueExactlyWhenTheReportedMaxDrawdownAmountIsPositive()
    {
        foreach (decimal[] series in new[]
        {
            new[] { 110m, 120m, 130m },
            new[] { 110m, 105m, 130m },
            new[] { 90m, 100m, 110m },
            new[] { 100m, 100m },
        })
        {
            BacktestResult result = ReportTestHelpers.BuildResult(100m, series);
            var sample = EquitySample.Build(result, ReportTestHelpers.Options());
            decimal maxAmount = DrawdownSeriesCalculator
                .ComputeMaxDrawdownAmount(DrawdownSeriesCalculator.ComputeDrawdownAmountSeries(sample.Equity)).Value!.Value;

            bool[] flags = EquityDrawdownClassifier.ComputeUnderwaterFlags(100m, result.EquityPoints).ToArray();

            Assert.Equal(maxAmount > 0m, flags.Any(f => f));
        }
    }

    [Fact]
    public void FlagsBetweenPeakAndRecovery_AreTrue_AndRecoveryIsFalse_LikeTheEpisodeArtifact()
    {
        // E[0..5] = 100, 120, 120, 90, 90, 120 -> episode peak 2, trough 3, recovery 5 (first later E >= peak).
        BacktestResult result = ReportTestHelpers.BuildResult(100m, new[] { 120m, 120m, 90m, 90m, 120m });
        BacktestReportOptions options = ReportTestHelpers.Options();
        (DrawdownEpisodeResult ratio, _) = DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);
        DrawdownEpisode episode = ratio.Episode!;

        bool[] flags = EquityDrawdownClassifier.ComputeUnderwaterFlags(100m, result.EquityPoints).ToArray();

        // Drawn point j is full-series index j + 1.
        for (int full = episode.PeakIndex + 1; full < episode.RecoveryIndex!.Value; full++)
        {
            Assert.True(flags[full - 1], $"full index {full} lies inside the episode");
        }
        Assert.False(flags[episode.RecoveryIndex.Value - 1]);
        Assert.False(flags[episode.PeakIndex - 1]);
    }
}
