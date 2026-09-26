using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>L1 cost conservation and cash / P&amp;L conservation laws (no money appears or disappears).</summary>
[Trait("Category", "BacktestVerification")]
public class L1_CostAndConservationTests
{
    [Fact]
    public void SlippageAndFees_NetEqualsGrossRefMinusCommissionMinusSlippageAmounts()
    {
        // Same bars as LongRoundTrip, slippage 1%: entry P0=100 -> 101.00 ; exit P0=110 -> 108.9.
        // Fill.SlippageAmount = |exec-P0|*qty = 1.00*100 = 100 (entry) ; 1.1*100 = 110 (exit).
        // gross = 100*(108.9-101) = 790 ; fees 200 ; net 590 ; identity: (110-100)*100 - 200 - (100+110) = 590.
        // Cash after entry = 1,000,000 - 10,100 - 100 = 989,800 ; final = 989,800 + 10,100 + 790 - 100 = 1,000,590.
        ScenarioRun run = VerificationScenarios.LongRoundTrip(VerificationScenarios.CostedConfig(slippageRatio: 0.01m));
        BacktestResult r = run.Result;

        BacktestTrade trade = Assert.Single(r.Trades);
        Eq(101m, trade.EntryPrice, "EntryPrice");
        Eq(108.9m, trade.ExitPrice, "ExitPrice");
        Eq(100m, r.Fills[0].SlippageAmount, "entry slippage");
        Eq(110m, r.Fills[1].SlippageAmount, "exit slippage");
        Eq(790m, trade.ClosedGross, "ClosedGross");
        Eq(590m, trade.ClosedNet, "ClosedNet");

        decimal commissions = r.Fills.Sum(f => f.Commission);
        decimal slippage = r.Fills.Sum(f => f.SlippageAmount);
        decimal identity = ((110m - 100m) * 100m) - commissions - slippage;
        Eq(590m, identity, "reference-price identity");
        Eq(identity, trade.ClosedNet, "ClosedNet vs identity");
        Eq(1_000_590m, r.EquityPoints[^1].Equity, "FinalEquity");
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }

    [Fact]
    public void ZeroPriceMove_RoundTripLosesExactlyTheRoundTripCost()
    {
        // Flat 100 x3, fees 100/fill: net = -200 ; final equity 999,800.
        ScenarioRun run = VerificationScenarios.FlatPriceRoundTrip(VerificationScenarios.CostedConfig());
        Eq(-200m, Assert.Single(run.Result.Trades).ClosedNet, "ClosedNet");
        Eq(999_800m, run.Result.EquityPoints[^1].Equity, "FinalEquity");
        Eq(-200m, run.Result.EquityPoints[^1].Equity - run.Config.InitialCapital, "P&L == -(round-trip cost)");
        AllInvariants(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void ZeroPriceMove_WithSlippage_LosesFeesPlusSlippage()
    {
        // s=1%: entry 101, exit 99 -> gross = 100*(99-101) = -200 ; fees 200 ; net -400 ;
        // slippage amounts 100 + 100 = 200 ; identity (100-100)*100 - 200 - 200 = -400 ; final 999,600.
        ScenarioRun run = VerificationScenarios.FlatPriceRoundTrip(VerificationScenarios.CostedConfig(slippageRatio: 0.01m));
        BacktestTrade trade = Assert.Single(run.Result.Trades);
        Eq(101m, trade.EntryPrice, "EntryPrice");
        Eq(99m, trade.ExitPrice, "ExitPrice");
        Eq(-200m, trade.ClosedGross, "ClosedGross");
        Eq(-400m, trade.ClosedNet, "ClosedNet");
        Eq(200m, run.Result.Fills.Sum(f => f.SlippageAmount), "slippage total");
        Eq(999_600m, run.Result.EquityPoints[^1].Equity, "FinalEquity");
        AllInvariants(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void ThreeRoundTrips_SumOfTradePnLEqualsFinalMinusInitialCapital()
    {
        // MultiTradeBars, costed config: 50 + 0.5*100 = 100 fee per fill. Trades: 100->105 (+500), 104->107 (+300), 101->102 (+100) => gross 900.
        // 6 fills * 100 = 600 fees ; net = 900 - 600 = 300 ; final equity = 1,000,300.
        ScenarioRun run = VerificationScenarios.MultiTrade(VerificationScenarios.CostedConfig(), leaveFourthOpen: false);
        BacktestResult r = run.Result;

        Assert.Equal(3, r.Trades.Length);
        Eq(500m, r.Trades[0].ClosedGross, "gross#1");
        Eq(300m, r.Trades[1].ClosedGross, "gross#2");
        Eq(100m, r.Trades[2].ClosedGross, "gross#3");
        Eq(300m, r.Trades.Sum(t => t.ClosedNet), "Sum(ClosedNet)");
        Eq(1_000_300m, r.EquityPoints[^1].Equity, "FinalEquity");
        Eq(r.Trades.Sum(t => t.ClosedNet), r.EquityPoints[^1].Equity - run.Config.InitialCapital, "conservation");
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }

    [Fact]
    public void ThreeRoundTripsPlusOpenFourth_OpenPositionForm_OfTheConservationIdentityHolds()
    {
        // As above plus a 4th Long: signal on bar8, fills @102 (Open of bar9), stays open, Close 102 => unrealized 0.
        // Entry fee 100 already paid: final equity = 1,000,300 - 100 = 1,000,200.
        ScenarioRun run = VerificationScenarios.MultiTrade(VerificationScenarios.CostedConfig(), leaveFourthOpen: true);
        BacktestResult r = run.Result;

        Assert.Equal(3, r.Trades.Length);
        Assert.Equal(7, r.Fills.Length);
        Eq(1_000_200m, r.EquityPoints[^1].Equity, "FinalEquity");
        Assert.True(r.EquityPoints[^1].MarketValue != 0m, "position must still be open");
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }

    [Fact]
    public void ThreeShortRoundTrips_ConservationHoldsForTheShortSide()
    {
        ScenarioRun run = VerificationScenarios.MultiTrade(VerificationScenarios.CostedConfig(slippageRatio: 0.002m), leaveFourthOpen: true,
            SignalType.ShortEntry, SignalType.ShortExit);
        AllInvariants(run.Input, run.Config, run.Result);
        TradePnLConservation(run.Input, run.Config, run.Result);
        Assert.All(run.Result.Trades, t => Assert.Equal(TradeSide.Short, t.Side));
    }

    [Fact]
    public void ForcedLiquidation_LossCutTrade_ConservesCash()
    {
        // Entry @100 (bar1): notional 2,000, margin 600, Cash 400. P_liq = (2000-400-600)/(20*0.8) = 62.5, no penalty, no fees.
        // Realized = 20*(62.5-100) = -750 ; Cash = 400 + 600 - 750 = 250 ; Equity 250 > 0 so the run continues (Completed).
        ScenarioRun run = VerificationScenarios.MarginLiquidationPartial();
        BacktestResult r = run.Result;

        Assert.Equal(RunStatus.Completed, r.Status);
        BacktestTrade trade = Assert.Single(r.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Eq(62.5m, trade.ExitPrice, "ExitPrice");
        Eq(-750m, trade.ClosedNet, "ClosedNet");
        Eq(250m, r.EquityPoints[^1].Equity, "FinalEquity");
        Eq(250m, r.EquityPoints[^1].Cash, "FinalCash");
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }
}
