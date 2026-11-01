using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Services.Backtest;

public sealed class PortfolioEquityComposerTests
{
    private static readonly DateTime BaseUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static EquityPoint Point(int index, int dayOffset, decimal equity, decimal cash = 0m, decimal marketValue = 0m, decimal margin = 0m)
        => new(index, BaseUtc.AddDays(dayOffset), equity, cash, marketValue, margin);

    private static BacktestTrade Trade(long id, decimal closedNet)
        => new(id, TradeSide.Long, 0, BaseUtc, 100m, 1, BaseUtc.AddDays(1), 105m, 1m, closedNet, closedNet, 0m, 0m, 1, false);

    // =========================================================================================
    // Proof (b): 1-symbol portfolio equals standalone run
    // =========================================================================================

    [Fact]
    public void ProofB_SingleSymbolPortfolio_EqualsSingleSleeveResultsExactly()
    {
        const decimal capital = 100000m;
        var points = ImmutableArray.Create(
            Point(0, 0, 105000m),
            Point(1, 1, 102000m),
            Point(2, 2, 108000m));

        var trades = ImmutableArray.Create(
            Trade(1, 5000m),
            Trade(2, -3000m),
            Trade(3, 6000m));

        var sleeve = new SleeveBacktestInput("TEST", capital, true, null, points, trades);
        var sleeves = new[] { sleeve };

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(capital, sleeves, BaseUtc);

        Assert.Equal(capital, result.AllocatedTotal);
        Assert.Equal(108000m, result.FinalEquity);
        Assert.Equal(0.08m, result.TotalReturn);
        Assert.Equal(3, result.PooledTradeCount);
        Assert.Equal(2m / 3m, result.PooledWinRate);

        // Composite equity points match single symbol equity points exactly
        Assert.Equal(3, result.CompositeEquityPoints.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(points[i].Timestamp, result.CompositeEquityPoints[i].Timestamp);
            Assert.Equal(points[i].Equity, result.CompositeEquityPoints[i].Equity);
            Assert.Equal(i, result.CompositeEquityPoints[i].BarIndex);
        }

        // Worst drawdown episode matches direct DrawdownEpisodeEnumerator call on single points
        ImmutableArray<RankedDrawdownEpisode> expectedEpisodes = DrawdownEpisodeEnumerator.Enumerate(capital, BaseUtc, points, 1);
        Assert.NotEmpty(expectedEpisodes);
        Assert.NotNull(result.WorstEpisode);
        Assert.Equal(expectedEpisodes[0].DepthRatio, result.WorstEpisode.DepthRatio);
        Assert.Equal(expectedEpisodes[0].PeakEquity, result.WorstEpisode.PeakEquity);
        Assert.Equal(expectedEpisodes[0].TroughEquity, result.WorstEpisode.TroughEquity);
        Assert.Equal(expectedEpisodes[0].PeakUtc, result.WorstEpisode.PeakUtc);
        Assert.Equal(expectedEpisodes[0].TroughUtc, result.WorstEpisode.TroughUtc);
        Assert.Equal(expectedEpisodes[0].RecoveryUtc, result.WorstEpisode.RecoveryUtc);
        Assert.Equal(expectedEpisodes[0].DepthRatio, result.MaxDrawdownRatio);
    }

    // =========================================================================================
    // Proof (c): Hand-calculated multi-sleeve scenarios
    // =========================================================================================

    [Fact]
    public void ProofC_TwoSymbols_ForwardFillAndCalendarUnion_MatchesHandCalculation()
    {
        // Scenario 1:
        // Capital = 200,000, 2 sleeves with 100,000 each.
        // Sleeve A:
        //   Day 0 (2024-01-01): 100,000
        //   Day 1 (2024-01-02): 105,000
        //   Day 3 (2024-01-04): 110,000 (Day 2 is missing -> forward-fill 105,000)
        // Sleeve B:
        //   Day 1 (2024-01-02): 100,000 (Day 0 is missing -> pre-bar cash 100,000)
        //   Day 2 (2024-01-03):  95,000
        //   Day 3 (2024-01-04):  90,000
        const decimal totalCapital = 200000m;
        const decimal sleeveCap = 100000m;

        var pointsA = ImmutableArray.Create(
            Point(0, 0, 100000m, 100000m, 0m),
            Point(1, 1, 105000m, 50000m, 55000m),
            Point(2, 3, 110000m, 40000m, 70000m));

        var pointsB = ImmutableArray.Create(
            Point(0, 1, 100000m, 100000m, 0m),
            Point(1, 2, 95000m, 60000m, 35000m),
            Point(2, 3, 90000m, 50000m, 40000m));

        var tradesA = ImmutableArray.Create(Trade(1, 5000m), Trade(2, 5000m));
        var tradesB = ImmutableArray.Create(Trade(3, -5000m), Trade(4, -5000m));

        var sleeveA = new SleeveBacktestInput("A", sleeveCap, true, null, pointsA, tradesA);
        var sleeveB = new SleeveBacktestInput("B", sleeveCap, true, null, pointsB, tradesB);

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(totalCapital, new[] { sleeveA, sleeveB }, BaseUtc);

        Assert.Equal(totalCapital, result.AllocatedTotal);
        Assert.Equal(4, result.CompositeEquityPoints.Length);

        // Day 0: A = 100,000, B = 100,000 (pre-start cash) => 200,000
        Assert.Equal(BaseUtc.AddDays(0), result.CompositeEquityPoints[0].Timestamp);
        Assert.Equal(200000m, result.CompositeEquityPoints[0].Equity);

        // Day 1: A = 105,000, B = 100,000 => 205,000 (Peak)
        Assert.Equal(BaseUtc.AddDays(1), result.CompositeEquityPoints[1].Timestamp);
        Assert.Equal(205000m, result.CompositeEquityPoints[1].Equity);

        // Day 2: A = 105,000 (fwd-fill), B = 95,000 => 200,000 (Trough)
        Assert.Equal(BaseUtc.AddDays(2), result.CompositeEquityPoints[2].Timestamp);
        Assert.Equal(200000m, result.CompositeEquityPoints[2].Equity);

        // Day 3: A = 110,000, B = 90,000 => 200,000
        Assert.Equal(BaseUtc.AddDays(3), result.CompositeEquityPoints[3].Timestamp);
        Assert.Equal(200000m, result.CompositeEquityPoints[3].Equity);

        Assert.Equal(200000m, result.FinalEquity);
        Assert.Equal(0m, result.TotalReturn);

        // Peak = 205,000, Trough = 200,000. Depth = 5,000 / 205,000
        decimal expectedDepth = 5000m / 205000m;
        Assert.Equal(expectedDepth, result.MaxDrawdownRatio);
        Assert.NotNull(result.WorstEpisode);
        Assert.Equal(205000m, result.WorstEpisode.PeakEquity);
        Assert.Equal(200000m, result.WorstEpisode.TroughEquity);
        Assert.Equal(BaseUtc.AddDays(1), result.WorstEpisode.PeakUtc);
        Assert.Equal(BaseUtc.AddDays(2), result.WorstEpisode.TroughUtc);
        Assert.Null(result.WorstEpisode.RecoveryUtc); // Never recovers above 205,000

        // Pooled trades: A has 2 wins, B has 2 losses => 2 wins / 4 trades = 0.5
        Assert.Equal(4, result.PooledTradeCount);
        Assert.Equal(0.5m, result.PooledWinRate);
    }

    [Fact]
    public void ProofC_FailedSleeveMixedWithSuccess_RemainsFlatCashPerD11b()
    {
        // Scenario 2:
        // Capital = 300,000, 3 sleeves with 100,000 each.
        // Sleeve A: Success, gains +20,000 (Final = 120,000)
        // Sleeve B: Failed ("InsufficientData"), remains 100,000 cash
        // Sleeve C: Success, gains +10,000 (Final = 110,000)
        const decimal totalCapital = 300000m;
        const decimal sleeveCap = 100000m;

        var pointsA = ImmutableArray.Create(Point(0, 0, 100000m), Point(1, 1, 120000m));
        var pointsC = ImmutableArray.Create(Point(0, 0, 100000m), Point(1, 1, 110000m));

        var sleeveA = new SleeveBacktestInput("A", sleeveCap, true, null, pointsA, ImmutableArray.Create(Trade(1, 20000m)));
        var sleeveB = new SleeveBacktestInput("B", sleeveCap, false, "InsufficientData", ImmutableArray<EquityPoint>.Empty, ImmutableArray<BacktestTrade>.Empty);
        var sleeveC = new SleeveBacktestInput("C", sleeveCap, true, null, pointsC, ImmutableArray.Create(Trade(2, 10000m)));

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(totalCapital, new[] { sleeveA, sleeveB, sleeveC }, BaseUtc);

        Assert.Equal(totalCapital, result.AllocatedTotal);
        Assert.Equal(2, result.CompositeEquityPoints.Length);

        // Day 0: 100k + 100k (B cash) + 100k = 300,000
        Assert.Equal(300000m, result.CompositeEquityPoints[0].Equity);
        // Day 1: 120k + 100k (B cash) + 110k = 330,000
        Assert.Equal(330000m, result.CompositeEquityPoints[1].Equity);

        Assert.Equal(330000m, result.FinalEquity);
        // Total return = (330,000 - 300,000) / 300,000 = 0.10 (+10%)
        Assert.Equal(0.10m, result.TotalReturn);
        Assert.Equal(0m, result.MaxDrawdownRatio);
        Assert.Null(result.WorstEpisode);

        Assert.Equal(2, result.PooledTradeCount);
        Assert.Equal(1.0m, result.PooledWinRate);
    }

    [Fact]
    public void AllSleevesFailed_ProducesZeroReturnAndNoDrawdown()
    {
        // Scenario 3: All sleeves fail
        const decimal totalCapital = 200000m;
        const decimal sleeveCap = 100000m;

        var sleeveA = new SleeveBacktestInput("A", sleeveCap, false, "InsufficientData", ImmutableArray<EquityPoint>.Empty, ImmutableArray<BacktestTrade>.Empty);
        var sleeveB = new SleeveBacktestInput("B", sleeveCap, false, "NoBars", ImmutableArray<EquityPoint>.Empty, ImmutableArray<BacktestTrade>.Empty);

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(totalCapital, new[] { sleeveA, sleeveB }, BaseUtc);

        Assert.Equal(totalCapital, result.AllocatedTotal);
        Assert.Equal(totalCapital, result.FinalEquity);
        Assert.Equal(0m, result.TotalReturn);
        Assert.Equal(0m, result.MaxDrawdownRatio);
        Assert.Null(result.WorstEpisode);
        Assert.Equal(0, result.PooledTradeCount);
        Assert.Null(result.PooledWinRate);
        Assert.Empty(result.CompositeEquityPoints);
    }

    [Fact]
    public void EmptySleevesList_ReturnsCleanDefault()
    {
        // Scenario 4: Empty sleeves
        CompositePortfolioResult result = PortfolioEquityComposer.Compose(100000m, Array.Empty<SleeveBacktestInput>());

        Assert.Equal(0m, result.AllocatedTotal);
        Assert.Equal(100000m, result.FinalEquity);
        Assert.Equal(0m, result.TotalReturn);
        Assert.Equal(0m, result.MaxDrawdownRatio);
        Assert.Null(result.WorstEpisode);
        Assert.Equal(0, result.PooledTradeCount);
        Assert.Null(result.PooledWinRate);
        Assert.Empty(result.CompositeEquityPoints);
    }

    [Fact]
    public void PooledTrades_CorrectlyCountsWinsLossesAndBreakevens()
    {
        // Scenario 5: Trade win definition (ClosedNet > 0m is Win, <= 0m is not)
        var tradesA = ImmutableArray.Create(
            Trade(1, 100m),   // Win
            Trade(2, -50m));  // Loss
        var tradesB = ImmutableArray.Create(
            Trade(3, 200m),   // Win
            Trade(4, 0m),     // Breakeven (ClosedNet == 0, not a win)
            Trade(5, 50m));   // Win

        var points = ImmutableArray.Create(Point(0, 0, 100000m));
        var sleeveA = new SleeveBacktestInput("A", 50000m, true, null, points, tradesA);
        var sleeveB = new SleeveBacktestInput("B", 50000m, true, null, points, tradesB);

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(100000m, new[] { sleeveA, sleeveB });

        Assert.Equal(5, result.PooledTradeCount);
        // Wins = 3 (Trade 1, 3, 5), Breakeven is not a win
        Assert.Equal(3m / 5m, result.PooledWinRate);
    }

    [Fact]
    public void SingleDateEvaluation_ReturnsOnePointAndCorrectTotalReturn()
    {
        // Scenario 6: Single evaluation day
        var points = ImmutableArray.Create(Point(0, 0, 110000m));
        var sleeve = new SleeveBacktestInput("A", 100000m, true, null, points, ImmutableArray<BacktestTrade>.Empty);

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(100000m, new[] { sleeve });

        Assert.Single(result.CompositeEquityPoints);
        Assert.Equal(110000m, result.FinalEquity);
        Assert.Equal(0.10m, result.TotalReturn);
    }

    [Fact]
    public void ExactDecimalDivision_AllocatedTotalPreservesDecimalPrecision_D11c()
    {
        // Scenario 7: D-11c exact decimal division
        // 1,000,000 / 3 = 333,333.3333333333333333333333m
        const decimal totalCapital = 1000000m;
        decimal sleeveCap = totalCapital / 3m;

        var points = ImmutableArray.Create(Point(0, 0, sleeveCap));
        var sleeve1 = new SleeveBacktestInput("A", sleeveCap, true, null, points, ImmutableArray<BacktestTrade>.Empty);
        var sleeve2 = new SleeveBacktestInput("B", sleeveCap, true, null, points, ImmutableArray<BacktestTrade>.Empty);
        var sleeve3 = new SleeveBacktestInput("C", sleeveCap, true, null, points, ImmutableArray<BacktestTrade>.Empty);

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(totalCapital, new[] { sleeve1, sleeve2, sleeve3 });

        decimal expectedAllocated = sleeveCap * 3m; // 999,999.9999999999999999999999m
        Assert.Equal(expectedAllocated, result.AllocatedTotal);
        Assert.Equal(expectedAllocated, result.FinalEquity);
        Assert.Equal(0m, result.TotalReturn);
    }

    [Fact]
    public void InitialTimestampNull_DefaultsToDates0()
    {
        var points = ImmutableArray.Create(Point(0, 5, 90000m), Point(1, 6, 110000m));
        var sleeve = new SleeveBacktestInput("A", 100000m, true, null, points, ImmutableArray<BacktestTrade>.Empty);

        CompositePortfolioResult result = PortfolioEquityComposer.Compose(100000m, new[] { sleeve }, initialTimestamp: null);

        Assert.NotNull(result.WorstEpisode);
        Assert.Equal(BaseUtc.AddDays(5), result.WorstEpisode.PeakUtc);
    }

    [Fact]
    public void ArgumentValidation_RejectsInvalidCapitalOrNullSleeves()
    {
        // Scenario 8: Argument validation
        Assert.Throws<ArgumentOutOfRangeException>(() => PortfolioEquityComposer.Compose(0m, Array.Empty<SleeveBacktestInput>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortfolioEquityComposer.Compose(-100m, Array.Empty<SleeveBacktestInput>()));
        Assert.Throws<ArgumentNullException>(() => PortfolioEquityComposer.Compose(1000m, null!));
    }
}
