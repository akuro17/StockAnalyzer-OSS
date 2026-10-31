using System;
using System.Linq;
using StockAnalyzer.Core.Services.Backtest.Optimization;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Optimization;

/// <summary>Plan 04 improvement (L3): the ranking and mean of a parameter sweep moved from the MCP tool into Core, behaviour unchanged.</summary>
public sealed class OptimizationRankingTests
{
    [Fact]
    public void TheHighestValueIsFirstAndEqualValuesKeepTheLowerIndexFirst()
    {
        decimal?[] values = { 1m, 3m, 2m, 3m };

        Assert.Equal(new[] { 1, 3, 2, 0 }, OptimizationRanking.Rank(values, minimize: false).ToArray());
    }

    [Fact]
    public void MinimizeRanksTheLowestValueFirstAndStillBreaksTiesByTheLowerIndex()
    {
        decimal?[] values = { 2m, 1m, 3m, 1m };

        Assert.Equal(new[] { 1, 3, 0, 2 }, OptimizationRanking.Rank(values, minimize: true).ToArray());
    }

    [Fact]
    public void AnEntryWithoutAValueIsNotRankedAndNothingRankedIsEmpty()
    {
        Assert.Equal(new[] { 2, 0 }, OptimizationRanking.Rank(new decimal?[] { 1m, null, 5m }, minimize: false).ToArray());
        Assert.Empty(OptimizationRanking.Rank(new decimal?[] { null, null }, minimize: false));
        Assert.Empty(OptimizationRanking.Rank(Array.Empty<decimal?>(), minimize: true));
    }

    [Fact]
    public void ANegativeValueIsRankedByItsSignedValue()
    {
        decimal?[] values = { -2m, -1m, 0m };

        Assert.Equal(new[] { 2, 1, 0 }, OptimizationRanking.Rank(values, minimize: false).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, OptimizationRanking.Rank(values, minimize: true).ToArray());
    }

    [Fact]
    public void TheMeanIsTheSumOverTheCountAndNullWhenThereIsNoValue()
    {
        Assert.Equal(2m, OptimizationRanking.Mean(new[] { 1m, 2m, 3m }));
        Assert.Equal(0.25m, OptimizationRanking.Mean(new[] { 0.5m, 0m }));
        Assert.Null(OptimizationRanking.Mean(Array.Empty<decimal>()));
    }

    [Fact]
    public void ANullArgumentIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => OptimizationRanking.Rank(null!, minimize: false).ToArray());
        Assert.Throws<ArgumentNullException>(() => OptimizationRanking.Mean(null!));
    }
}
