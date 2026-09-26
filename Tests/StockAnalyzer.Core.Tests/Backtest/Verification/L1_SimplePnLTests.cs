using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L1 "simple P&amp;L match": buy 100 / sell 110, expected P&amp;L == (110-100)*qty - round-trip cost, to the last decimal.
/// Every number below is hand-calculated in the comment table BEFORE it appears in an assertion.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L1_SimplePnLTests
{
    [Fact]
    public void Smoke_NoSignals_EquityStaysInitialAndInvariantsHold()
    {
        var bars = SyntheticBars.Flat(3, 100m);
        var input = VerificationHarness.MakeInput(bars);
        var config = VerificationHarness.MakeConfig();

        BacktestResult result = VerificationHarness.Run(input, config, new ScriptedStrategy(new System.Collections.Generic.Dictionary<int, StrategyOrderRequest>()));

        Assert.Equal(3, result.EquityPoints.Length);
        Assert.All(result.EquityPoints, p => Eq(config.InitialCapital, p.Equity, "Equity"));
        AllInvariants(input, config, result);
    }

    [Fact]
    public void LongBuy100Sell110_PnLEqualsPriceDiffTimesQtyMinusRoundTripCost_ExactDecimals()
    {
        // Config: capital 1,000,000; qty 100; IMR=1; commission 50 flat + 0.5/unit => 100 per fill; slippage 0.
        // bar0 O100 H100 L100 C100  signal LongEntry
        // bar1 O100 H110 L100 C110  entry fills @100 (Open); signal LongExit
        // bar2 O110 H110 L110 C110  exit fills @110 (Open)
        //
        // gross = (110-100)*100 = 1000 ; fees = 100 + 100 = 200 ; net = 800
        // Cash after entry = 1,000,000 - (100*100*1) - 100 = 989,900 ; HeldMargin = 10,000
        // Equity: bar0 1,000,000 ; bar1 = 989,900 + 10,000 + 100*(110-100) = 1,000,900 ; bar2 = 989,900 + 10,000 + 1000 - 100 = 1,000,800
        ScenarioRun run = VerificationScenarios.LongRoundTrip(VerificationScenarios.CostedConfig());
        BacktestResult r = run.Result;

        decimal expectedPnL = ((110m - 100m) * 100m) - (2m * (50m + (0.5m * 100m)));
        Eq(800m, expectedPnL, "hand formula");

        Assert.Equal(RunStatus.Completed, r.Status);
        BacktestTrade trade = Assert.Single(r.Trades);
        Assert.Equal(TradeSide.Long, trade.Side);
        Eq(100m, trade.EntryPrice, "EntryPrice");
        Eq(110m, trade.ExitPrice, "ExitPrice");
        Eq(100m, trade.Quantity, "Quantity");
        Eq(1000m, trade.ClosedGross, "ClosedGross");
        Eq(100m, trade.EntryFee, "EntryFee");
        Eq(100m, trade.ExitFee, "ExitFee");
        Eq(expectedPnL, trade.ClosedNet, "ClosedNet");
        Assert.Equal(1, trade.EntryBar);
        Assert.Equal(2, trade.ExitBar);
        Assert.False(trade.IsForcedLiquidation);

        Assert.Equal(2, r.Fills.Length);
        Eq(100m, r.Fills[0].Price, "Fill0.Price");
        Eq(110m, r.Fills[1].Price, "Fill1.Price");
        Eq(100m, r.Fills[0].Commission, "Fill0.Commission");
        Eq(100m, r.Fills[1].Commission, "Fill1.Commission");
        Assert.Equal(OrderSide.Buy, r.Fills[0].Side);
        Assert.Equal(OrderSide.Sell, r.Fills[1].Side);

        Assert.Equal(3, r.EquityPoints.Length);
        AssertPoint(r.EquityPoints[0], equity: 1_000_000m, cash: 1_000_000m, marketValue: 0m, held: 0m);
        AssertPoint(r.EquityPoints[1], equity: 1_000_900m, cash: 989_900m, marketValue: 11_000m, held: 10_000m);
        AssertPoint(r.EquityPoints[2], equity: 1_000_800m, cash: 1_000_800m, marketValue: 0m, held: 0m);

        Assert.Equal(2, r.Signals.Length);
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }

    [Fact]
    public void LongRoundTrip_WithThirtyPercentInitialMargin_SamePnL_DifferentCashPath()
    {
        // IMR=0.30 / MMR=0.20: entry margin = 100*100*0.30 = 3,000 ; Cash after entry = 1,000,000 - 3,000 - 100 = 996,900.
        // Equity bar1 = 996,900 + 3,000 + 1,000 = 1,000,900 (identical to IMR=1) ; MarketValue stays 11,000 ; final equity 1,000,800.
        ScenarioRun run = VerificationScenarios.LongRoundTrip(VerificationScenarios.CostedConfig(initialMarginRatio: 0.30m, maintenanceMarginRatio: 0.20m));
        BacktestResult r = run.Result;

        Eq(800m, Assert.Single(r.Trades).ClosedNet, "ClosedNet");
        AssertPoint(r.EquityPoints[1], equity: 1_000_900m, cash: 996_900m, marketValue: 11_000m, held: 3_000m);
        AssertPoint(r.EquityPoints[2], equity: 1_000_800m, cash: 1_000_800m, marketValue: 0m, held: 0m);
        AllInvariants(run.Input, run.Config, r);
    }

    [Fact]
    public void ShortSell100Cover90_PnLIsMirrorOfLong()
    {
        // bar0 flat 100 signal ShortEntry | bar1 O100 H100 L90 C90 short fills @100, signal ShortExit | bar2 flat 90 cover @90.
        // realized = Q*(exit-entry) = -100*(90-100) = +1000 ; fees 200 ; net 800.
        // Cash after entry = 1,000,000 - 10,000 - 100 = 989,900 ; Equity bar1 = 989,900 + 10,000 + (-100)*(90-100) = 1,000,900 ; final 1,000,800.
        ScenarioRun run = VerificationScenarios.ShortRoundTrip(VerificationScenarios.CostedConfig());
        BacktestResult r = run.Result;

        BacktestTrade trade = Assert.Single(r.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Eq(100m, trade.EntryPrice, "EntryPrice");
        Eq(90m, trade.ExitPrice, "ExitPrice");
        Eq(1000m, trade.ClosedGross, "ClosedGross");
        Eq(800m, trade.ClosedNet, "ClosedNet");
        AssertPoint(r.EquityPoints[1], equity: 1_000_900m, cash: 989_900m, marketValue: -9_000m, held: 10_000m);
        AssertPoint(r.EquityPoints[2], equity: 1_000_800m, cash: 1_000_800m, marketValue: 0m, held: 0m);
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }

    private static void AssertPoint(EquityPoint p, decimal equity, decimal cash, decimal marketValue, decimal held)
    {
        Eq(equity, p.Equity, $"Equity@{p.BarIndex}");
        Eq(cash, p.Cash, $"Cash@{p.BarIndex}");
        Eq(marketValue, p.MarketValue, $"MarketValue@{p.BarIndex}");
        Eq(held, p.HeldMargin, $"HeldMargin@{p.BarIndex}");
    }
}
