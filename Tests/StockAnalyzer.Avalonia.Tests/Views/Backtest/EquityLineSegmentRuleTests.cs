using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

public class EquityLineSegmentRuleTests
{
    private static readonly DateTime BaseUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImmutableArray<EquityPoint> Points(params decimal[] equity) =>
        equity.Select((e, i) => new EquityPoint(i, BaseUtc.AddDays(i), e, e, 0m, 0m)).ToImmutableArray();

    private static bool[] Classes(BacktestEquityColorMode mode, decimal initialCapital, params decimal[] equity)
    {
        ImmutableArray<EquityPoint> points = Points(equity);
        ImmutableArray<bool> flags = EquityDrawdownClassifier.ComputeUnderwaterFlags(initialCapital, points);
        return Enumerable.Range(1, points.Length - 1).Select(k => EquityLineSegmentRule.IsUpSegment(mode, points, flags, k)).ToArray();
    }

    [Fact]
    public void MinimalSeries_HasExactlyOneSegment()
    {
        Assert.Single(Classes(BacktestEquityColorMode.PreviousBar, 100m, 101m, 102m));
        Assert.Single(Classes(BacktestEquityColorMode.Drawdown, 100m, 101m, 102m));
    }

    [Fact]
    public void PreviousBar_AllRising_IsAllUp_AllFalling_IsAllDown()
    {
        Assert.All(Classes(BacktestEquityColorMode.PreviousBar, 100m, 101m, 102m, 103m), up => Assert.True(up));
        Assert.All(Classes(BacktestEquityColorMode.PreviousBar, 100m, 99m, 98m, 97m), up => Assert.False(up));
    }

    [Fact]
    public void PreviousBar_EqualEquity_IsUp()
    {
        Assert.Equal(new[] { true, true }, Classes(BacktestEquityColorMode.PreviousBar, 100m, 100m, 100m, 100m));
    }

    [Fact]
    public void PreviousBar_Alternating_FollowsEachStep()
    {
        // 110 -> 105 (down) -> 108 (up) -> 107 (down) -> 107 (equal, up)
        Assert.Equal(new[] { false, true, false, true }, Classes(BacktestEquityColorMode.PreviousBar, 100m, 110m, 105m, 108m, 107m, 107m));
    }

    [Fact]
    public void Drawdown_OnlyNewHighs_IsAllUp()
    {
        Assert.All(Classes(BacktestEquityColorMode.Drawdown, 100m, 101m, 105m, 110m), up => Assert.True(up));
    }

    [Fact]
    public void Drawdown_SegmentColorFollowsItsEndPoint_SoTheRecoveryToANewHighIsUp()
    {
        // E[0..6] = 100,110,105,103,110,115,114 ; points = 110,105,103,110,115,114.
        // underwater = F,T,T,F,F,T  -> segments k=1..5 end flags: T,T,F,F,T
        // = down (falls from the peak), down (still below), up (recovers to the high), up (new high), down (falls again).
        Assert.Equal(
            new[] { false, false, true, true, false },
            Classes(BacktestEquityColorMode.Drawdown, 100m, 110m, 105m, 103m, 110m, 115m, 114m));
    }

    [Fact]
    public void Drawdown_HighTie_IsUp()
    {
        // 110 then a tie at 110 is "at the high", not drawdown.
        Assert.Equal(new[] { true, true }, Classes(BacktestEquityColorMode.Drawdown, 100m, 110m, 110m, 110m));
    }

    [Fact]
    public void Drawdown_StartsBelowInitialCapital_TheSegmentBackToTheInitialCapitalIsUp()
    {
        // 95 is in drawdown against the initial capital (the high starts there); the segment 95 -> 100 ends level with that high.
        Assert.Equal(new[] { true, true }, Classes(BacktestEquityColorMode.Drawdown, 100m, 95m, 100m, 101m));
        // 95 -> 90 stays below the high.
        Assert.Equal(new[] { false, true }, Classes(BacktestEquityColorMode.Drawdown, 100m, 95m, 90m, 100m));
    }

    [Fact]
    public void Drawdown_AClimbThatStillEndsBelowTheHigh_IsDown_OnlyTheSegmentReachingTheHighIsUp()
    {
        // high 120, then 100 -> 110 (below) -> 125 (new high).
        Assert.Equal(new[] { false, false, true }, Classes(BacktestEquityColorMode.Drawdown, 100m, 120m, 100m, 110m, 125m));
    }

    [Theory]
    [InlineData(BacktestEquityColorMode.PreviousBar)]
    [InlineData(BacktestEquityColorMode.Drawdown)]
    public void UpAndDownCounts_AlwaysSumToTheSegmentCount(BacktestEquityColorMode mode)
    {
        decimal[] equity = { 110m, 105m, 120m, 120m, 90m, 95m, 130m, 129m };

        bool[] classes = Classes(mode, 100m, equity);

        Assert.Equal(equity.Length - 1, classes.Count(c => c) + classes.Count(c => !c));
    }

    [Theory]
    [InlineData(BacktestEquityColorMode.PreviousBar)]
    [InlineData(BacktestEquityColorMode.Drawdown)]
    public void SegmentClass_DoesNotDependOnLaterBars(BacktestEquityColorMode mode)
    {
        decimal[] equity = { 110m, 105m, 103m, 110m, 115m, 114m, 90m, 200m };
        bool[] full = Classes(mode, 100m, equity);

        // Truncating the series after bar n must not change any segment that ends at or before bar n.
        for (int n = 2; n <= equity.Length; n++)
        {
            bool[] truncated = Classes(mode, 100m, equity.Take(n).ToArray());
            Assert.Equal(full.Take(truncated.Length), truncated);
        }
    }

    [Fact]
    public void Single_IsAlwaysUp_AndIsNotSegmented()
    {
        Assert.All(Classes(BacktestEquityColorMode.Single, 100m, 90m, 80m, 110m), up => Assert.True(up));
        Assert.False(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.Single, 5, ImmutableArray<bool>.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void SegmentIndexOutsideTheSeries_Throws(int k)
    {
        ImmutableArray<EquityPoint> points = Points(101m, 102m, 103m);
        ImmutableArray<bool> flags = EquityDrawdownClassifier.ComputeUnderwaterFlags(100m, points);

        Assert.Throws<ArgumentOutOfRangeException>(() => EquityLineSegmentRule.IsUpSegment(BacktestEquityColorMode.PreviousBar, points, flags, k));
    }

    [Fact]
    public void IsSegmented_DrawdownNeedsOneFlagPerPoint()
    {
        ImmutableArray<bool> three = ImmutableArray.Create(false, true, false);

        Assert.True(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.Drawdown, 3, three));
        Assert.False(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.Drawdown, 4, three));
        Assert.False(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.Drawdown, 3, default));
        Assert.False(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.Drawdown, 3, ImmutableArray<bool>.Empty));
    }

    [Fact]
    public void IsSegmented_PreviousBarNeedsNoFlags_ButTwoPoints()
    {
        Assert.True(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.PreviousBar, 2, default));
        Assert.False(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.PreviousBar, 1, default));
        Assert.False(EquityLineSegmentRule.IsSegmented(BacktestEquityColorMode.Drawdown, 1, ImmutableArray.Create(true)));
    }

    [Fact]
    public void Drawdown_WithUnusableFlags_ThrowsInsteadOfGuessing()
    {
        ImmutableArray<EquityPoint> points = Points(101m, 102m, 103m);

        Assert.Throws<ArgumentException>(() => EquityLineSegmentRule.IsUpSegment(BacktestEquityColorMode.Drawdown, points, default, 1));
        Assert.Throws<ArgumentException>(() => EquityLineSegmentRule.IsUpSegment(BacktestEquityColorMode.Drawdown, points, ImmutableArray.Create(false), 1));
    }
}
