using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L1 position reversal. StockAnalyzer's reversal model (BacktestEngine dual pending slots, Exit resolved before Entry) is TWO
/// separate orders/fills/trades: a full-quantity close of the held side plus a NEW entry sized by the SizingModel. A single
/// netting fill ("Sell 150 while Long 100") is not an engine capability and is intentionally not tested.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L1_PositionReversalTests
{
    [Fact]
    public void SameQuantityReversal_ExitFillsBeforeEntry_AndEveryNumberMatchesTheHandTable()
    {
        // Fixed qty 100, capital 1,000,000, IMR=1, no fees. Bars (flat): 100, 100, 110, 105.
        // bar0 LongEntry signal | bar1 long fills @100 ; reversal pair signalled (ShortEntry + accompanying LongExit)
        // bar2 @110: LongExit fills first (realized 100*(110-100)=+1000), then ShortEntry fills @110 ; signal ShortExit
        // bar3 @105: cover fills (realized -100*(105-110)=+500)
        // Cash: after long entry 990,000 (held 10,000) ; after exit 990,000+10,000+1,000 = 1,001,000 ;
        //       after short entry 1,001,000 - 11,000 = 990,000 (held 11,000) ; equity@bar2 = 1,001,000 ; final = 990,000+11,000+500 = 1,001,500.
        ScenarioRun run = VerificationScenarios.ReversalSameQuantity(VerificationScenarios.FreeConfig());
        BacktestResult r = run.Result;

        Assert.Equal(RunStatus.Completed, r.Status);
        Assert.Equal(4, r.Orders.Length);
        Assert.Equal(4, r.Fills.Length);
        Assert.Equal(new[] { OrderSide.Buy, OrderSide.Sell, OrderSide.Sell, OrderSide.Buy }, new[] { r.Fills[0].Side, r.Fills[1].Side, r.Fills[2].Side, r.Fills[3].Side });
        Assert.Equal(2, r.Fills[1].BarIndex);
        Assert.Equal(2, r.Fills[2].BarIndex);
        // Order ids: 1 = LongEntry, 2 = ShortEntry (Evaluate), 3 = LongExit (EvaluateExit), 4 = ShortExit.
        Assert.Equal(3L, r.Fills[1].OrderId); // the Exit order fills first
        Assert.Equal(2L, r.Fills[2].OrderId); // then the Entry order

        Assert.Equal(2, r.Trades.Length);
        Assert.Equal(TradeSide.Long, r.Trades[0].Side);
        Eq(100m, r.Trades[0].EntryPrice, "trade1 entry");
        Eq(110m, r.Trades[0].ExitPrice, "trade1 exit");
        Eq(1000m, r.Trades[0].ClosedGross, "trade1 gross");
        Assert.Equal(TradeSide.Short, r.Trades[1].Side);
        Eq(110m, r.Trades[1].EntryPrice, "trade2 entry (new average price of the short)");
        Eq(105m, r.Trades[1].ExitPrice, "trade2 exit");
        Eq(100m, r.Trades[1].Quantity, "trade2 quantity");
        Eq(500m, r.Trades[1].ClosedGross, "trade2 gross");

        Eq(1_001_000m, r.EquityPoints[2].Equity, "Equity@2");
        Eq(990_000m, r.EquityPoints[2].Cash, "Cash@2");
        Eq(11_000m, r.EquityPoints[2].HeldMargin, "HeldMargin@2");
        Eq(1_001_500m, r.EquityPoints[3].Equity, "Equity@3");
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }

    [Fact]
    public void DifferentQuantityReversal_Long50BecomesShort42_RealizedPnLAndAveragePricesAreExact()
    {
        // Config: capital 10,000 ; PercentOfEquity 0.5 ; IMR=1 / MMR=0.5 ; no fees, no slippage.
        //  bar  O    H    L    C    events
        //   0  100  100  100  100   signal LongEntry: qty = floor(10,000*0.5 / (100*1)) = 50
        //   1  100  140  100  140   long 50 fills @100 (margin 5,000, Cash 5,000). Equity@Close = 5,000+5,000+50*40 = 12,000.
        //                           reversal signalled: ShortEntry qty = floor(12,000*0.5 / (140*1)) = floor(42.857) = 42 ; + LongExit(50)
        //   2  140  140  140  140   LongExit fills @140: realized 50*(140-100) = +2,000 -> Cash 12,000
        //                           ShortEntry fills @140: margin 42*140 = 5,880 -> Cash 6,120, held 5,880 ; signal ShortExit
        //   3  130  130  130  130   ShortExit fills @130: realized -42*(130-140) = +420 -> Cash 6,120+5,880+420 = 12,420
        ScenarioRun run = VerificationScenarios.ReversalLong50ToShort42();
        BacktestResult r = run.Result;

        Assert.Equal(RunStatus.Completed, r.Status);
        Eq(50m, r.Orders[0].Quantity, "long order qty");
        Eq(42m, r.Orders[1].Quantity, "short order qty");
        Eq(50m, r.Orders[2].Quantity, "long-exit order qty (full close of the held quantity)");

        Assert.Equal(2, r.Trades.Length);
        BacktestTrade longTrade = r.Trades[0];
        Assert.Equal(TradeSide.Long, longTrade.Side);
        Eq(50m, longTrade.Quantity, "long qty");
        Eq(100m, longTrade.EntryPrice, "long entry");
        Eq(140m, longTrade.ExitPrice, "long exit");
        Eq(2_000m, longTrade.ClosedGross, "long gross");

        BacktestTrade shortTrade = r.Trades[1];
        Assert.Equal(TradeSide.Short, shortTrade.Side);
        Eq(42m, shortTrade.Quantity, "short qty");
        Eq(140m, shortTrade.EntryPrice, "short average entry price");
        Eq(130m, shortTrade.ExitPrice, "short exit");
        Eq(420m, shortTrade.ClosedGross, "short gross");

        Eq(12_000m, r.EquityPoints[1].Equity, "Equity@1");
        Eq(12_000m, r.EquityPoints[2].Equity, "Equity@2");
        Eq(6_120m, r.EquityPoints[2].Cash, "Cash@2");
        Eq(5_880m, r.EquityPoints[2].HeldMargin, "HeldMargin@2");
        Eq(12_420m, r.EquityPoints[3].Equity, "Equity@3");
        Eq(run.Config.InitialCapital + 2_000m + 420m, r.EquityPoints[3].Equity, "10,000 + 2,000 + 420");
        AllInvariants(run.Input, run.Config, r);
        TradePnLConservation(run.Input, run.Config, r);
    }

    [Fact]
    public void ShortEntryWhileLong_WithoutAccompanyingExit_IsNeverExecuted_AccountStateUnchanged()
    {
        // bar1: long fills @100 and a ShortEntry is signalled WITHOUT the LongExit. The engine only considers the Entry slot
        // while flat, so the order stays pending until the data ends (Expired/EndOfData) and the Long is untouched.
        ScenarioRun run = VerificationScenarios.EntryWithoutAccompanyingExit(VerificationScenarios.FreeConfig());
        BacktestResult r = run.Result;

        Assert.Single(r.Fills);
        Assert.Empty(r.Trades);
        Assert.Equal(2, r.Orders.Length);
        Assert.Equal(OrderStatus.Expired, r.Orders[1].Status);
        Assert.Equal(ExpiredReason.EndOfData, r.Orders[1].ExpiredReason);
        Eq(10_000m, r.EquityPoints[^1].HeldMargin, "Long margin still held");
        Eq(10_000m, r.EquityPoints[^1].MarketValue, "Long still open at Close 100");
        AllInvariants(run.Input, run.Config, r);
    }
}
