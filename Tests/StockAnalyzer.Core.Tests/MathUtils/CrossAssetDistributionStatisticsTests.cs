using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Statistical functions plan, Run R8 (M2): <see cref="CrossAssetDistributionStatistics"/> on hand-built aligned returns. The symbol figures are pinned to scipy / numpy
/// (the Cornish-Fisher VaR to the closed formula of plan section 3.5), the conditional betas to values solved by hand.
/// </summary>
public class CrossAssetDistributionStatisticsTests
{
    private static readonly decimal[] Asymmetric = { 0.01m, -0.02m, 0.03m, -0.01m, 0.02m, 0.0m, -0.015m, 0.05m };

    private static CrossAssetStatsResult Result(string[] symbols, decimal[][] aligned, decimal[]? benchmark = null, BetaAlphaEntry[]? betaAlpha = null) => new()
    {
        Symbols = symbols,
        AlignedReturns = aligned,
        BenchmarkAlignedReturns = benchmark,
        BetaAlpha = betaAlpha ?? Array.Empty<BetaAlphaEntry>(),
        CommonDateCount = aligned.Length == 0 ? 0 : aligned[0].Length,
    };

    [Fact]
    public void SymbolStats_MatchScipyAndTheClosedFormulaOfTheCornishFisherVar()
    {
        IReadOnlyList<SymbolDistributionStats> stats = CrossAssetDistributionStatistics.SymbolStats(Result(new[] { "AAA" }, new[] { Asymmetric }));

        SymbolDistributionStats s = Assert.Single(stats);
        Assert.Equal("AAA", s.Symbol);
        Assert.Equal(0.008125, s.Mean!.Value, 14);
        Assert.Equal(0.02419230987613331, s.StdDev!.Value, 14);
        Assert.Equal(0.009519716382329886, s.DownsideDeviation!.Value, 14);
        Assert.Equal(0.598276240864672, s.Skewness!.Value, 12);
        Assert.Equal(-0.5483026040875747, s.ExcessKurtosis!.Value, 12);
        Assert.Equal(0.5937157836613376, s.JarqueBeraStatistic!.Value, 12);
        Assert.Equal(0.7431496124894118, s.JarqueBeraPValue!.Value, 12);
        Assert.Equal(-0.02765860631284299, s.CornishFisherVar95!.Value, 12);
        Assert.True(s.CornishFisherVar95 < 0.0, "a loss is a negative return quantile");
        Assert.Equal(0.95, CrossAssetDistributionStatistics.CornishFisherConfidence);
    }

    [Fact]
    public void SymbolStats_FollowTheSymbolOrder_AndKeepTheMinimumCounts()
    {
        decimal[] three = { 0.01m, -0.02m, 0.03m };
        decimal[] five = { 0.01m, -0.02m, 0.03m, -0.01m, 0.02m };

        IReadOnlyList<SymbolDistributionStats> stats = CrossAssetDistributionStatistics.SymbolStats(Result(new[] { "B", "A", "C" }, new[] { three, five, Asymmetric }));

        Assert.Equal(new[] { "B", "A", "C" }, new[] { stats[0].Symbol, stats[1].Symbol, stats[2].Symbol });
        // 3 returns: mean, stdDev and skewness; no kurtosis, no JB (8), no CF VaR (4).
        Assert.NotNull(stats[0].StdDev);
        Assert.NotNull(stats[0].Skewness);
        Assert.Null(stats[0].ExcessKurtosis);
        Assert.Null(stats[0].JarqueBeraStatistic);
        Assert.Null(stats[0].JarqueBeraPValue);
        Assert.Null(stats[0].CornishFisherVar95);
        // 5 returns: kurtosis, CF VaR (4+), still no JB.
        Assert.NotNull(stats[1].ExcessKurtosis);
        Assert.Null(stats[1].JarqueBeraStatistic);
        // 8 returns: everything.
        Assert.NotNull(stats[2].JarqueBeraStatistic);
        Assert.NotNull(stats[2].JarqueBeraPValue);
    }

    [Fact]
    public void AFlatSeries_KeepsTheMagnitudes_AndHasNothingThatDividesByTheDeviation()
    {
        decimal[] flat = { 0.01m, 0.01m, 0.01m, 0.01m, 0.01m, 0.01m, 0.01m, 0.01m };

        SymbolDistributionStats s = Assert.Single(CrossAssetDistributionStatistics.SymbolStats(Result(new[] { "F" }, new[] { flat })));

        Assert.Equal(0.01, s.Mean!.Value, 15);
        Assert.NotNull(s.StdDev);
        Assert.True(s.StdDev < 1e-12);
        Assert.Equal(0.0, s.DownsideDeviation!.Value);
        Assert.Null(s.Skewness);
        Assert.Null(s.ExcessKurtosis);
        Assert.Null(s.JarqueBeraStatistic);
        Assert.Null(s.JarqueBeraPValue);
        Assert.Null(s.CornishFisherVar95);
    }

    [Fact]
    public void SymbolStats_OfAnEmptyResult_AreEmpty()
    {
        Assert.Empty(CrossAssetDistributionStatistics.SymbolStats(Result(Array.Empty<string>(), Array.Empty<decimal[]>())));
    }

    [Fact]
    public void ConditionalBetas_AreTheOlsSlopesInsideTheDownAndUpDates_AZeroBenchmarkDayBelongsToNeither()
    {
        // Benchmark: 3 down days, 3 up days, 1 zero day. The asset moves 1.5 x the benchmark on down days and 0.5 x on up days; on the zero day it moves a lot (and must not count).
        decimal[] benchmark = { -0.02m, -0.01m, 0.01m, 0.02m, -0.03m, 0.0m, 0.04m };
        decimal[] asset = { -0.03m, -0.015m, 0.005m, 0.01m, -0.045m, 0.09m, 0.02m };
        var entry = new BetaAlphaEntry("AAA", 1.0, 0.0, 1.0);

        IReadOnlyList<BetaAlphaEntry> entries = CrossAssetDistributionStatistics.WithConditionalBetas(
            Result(new[] { "AAA" }, new[] { asset }, benchmark, new[] { entry }));

        BetaAlphaEntry result = Assert.Single(entries);
        Assert.Equal(1.5, result.DownsideBeta!.Value, 12);
        Assert.Equal(0.5, result.UpsideBeta!.Value, 12);
        Assert.Equal(entry.Beta, result.Beta);
        Assert.Equal(entry.Alpha, result.Alpha);
        Assert.Equal(entry.RSquared, result.RSquared);
        Assert.Equal("AAA", result.Symbol);
    }

    [Fact]
    public void ConditionalBetas_AreNullBelowThreeDatesOfTheRegime_AndEntriesAreUnchangedWithoutBenchmarkReturns()
    {
        decimal[] benchmark = { -0.02m, 0.01m, 0.02m, 0.03m, 0.04m };
        decimal[] asset = { -0.04m, 0.02m, 0.01m, 0.05m, 0.02m };
        var entry = new BetaAlphaEntry("AAA", 1.0, 0.0, 1.0);

        BetaAlphaEntry few = Assert.Single(CrossAssetDistributionStatistics.WithConditionalBetas(Result(new[] { "AAA" }, new[] { asset }, benchmark, new[] { entry })));
        Assert.Null(few.DownsideBeta); // one down day
        Assert.NotNull(few.UpsideBeta); // four up days

        IReadOnlyList<BetaAlphaEntry> unchanged = CrossAssetDistributionStatistics.WithConditionalBetas(Result(new[] { "AAA" }, new[] { asset }, null, new[] { entry }));
        Assert.Equal(new[] { entry }, unchanged);
        Assert.Null(unchanged[0].DownsideBeta);
        Assert.Null(unchanged[0].UpsideBeta);
    }

    [Fact]
    public void TheBenchmarkItself_HasConditionalBetasOfOne()
    {
        decimal[] benchmark = { -0.02m, -0.01m, 0.01m, 0.02m, -0.03m, 0.0m, 0.04m };
        var entry = new BetaAlphaEntry("BM", 1.0, 0.0, 1.0);

        BetaAlphaEntry result = Assert.Single(CrossAssetDistributionStatistics.WithConditionalBetas(Result(new[] { "BM" }, new[] { benchmark }, benchmark, new[] { entry })));

        Assert.Equal(1.0, result.DownsideBeta!.Value, 12);
        Assert.Equal(1.0, result.UpsideBeta!.Value, 12);
    }
}
