using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L1 gap fills through the ENGINE (OrderFillEvaluatorTests already proves the evaluator in isolation): a Stop is a trigger
/// level, not a guaranteed price - when the market gaps through it, the fill is the gap Open (plus slippage), never the stop price.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L1_GapFillTests
{
    [Fact]
    public void BuyStop100_NextOpenGapsTo110_FillsAtOpen110_NotAtStop()
    {
        // bar0 flat 95 signal LongEntry Buy Stop @100 ; bar1 (110,112,109,111): Open already above the stop => fill @110.
        ScenarioRun run = VerificationScenarios.BuyStopEntry(VerificationScenarios.FreeConfig(), (110m, 112m, 109m, 111m));
        BacktestFill fill = Assert.Single(run.Result.Fills);
        Eq(110m, fill.Price, "fill price");
        Assert.Equal(1, fill.BarIndex);
        Eq(0m, fill.SlippageAmount, "slippage");
        AllInvariants(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void BuyStop100_GapOpen_WithOnePercentSlippage_FillsAt111Point1()
    {
        // 110 * 1.01 = 111.10 ; SlippageAmount = 1.10 * 100 = 110.
        ScenarioRun run = VerificationScenarios.BuyStopEntry(VerificationHarness.MakeConfig(slippageRatio: 0.01m), (110m, 112m, 109m, 111m));
        BacktestFill fill = Assert.Single(run.Result.Fills);
        Eq(111.10m, fill.Price, "fill price");
        Eq(110m, fill.SlippageAmount, "slippage amount");
        AllInvariants(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void BuyStop100_PriceTouchedInsideTheBar_FillsExactlyAtStop()
    {
        // bar1 (95,101,94,99): opens below, trades up through 100 => fill @100.
        ScenarioRun run = VerificationScenarios.BuyStopEntry(VerificationScenarios.FreeConfig(), (95m, 101m, 94m, 99m));
        Eq(100m, Assert.Single(run.Result.Fills).Price, "fill price");
        AllInvariants(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void BuyStop100_NeverReached_NoFill_OrderExpiresAtEndOfData()
    {
        // bar1 (95,99,94,98): High 99 < 100 => no fill ; the GTC order is still pending when data ends.
        ScenarioRun run = VerificationScenarios.BuyStopEntry(VerificationScenarios.FreeConfig(), (95m, 99m, 94m, 98m));
        Assert.Empty(run.Result.Fills);
        Assert.Empty(run.Result.Trades);
        BacktestOrder order = Assert.Single(run.Result.Orders);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ExpiredReason.EndOfData, order.ExpiredReason);
        AllInvariants(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void SellStopLoss90_NextOpenGapsTo80_ExitsAtOpen80_NotAtStop90()
    {
        // Long 100 @100 ; stop-loss Sell Stop @90 ; bar2 opens at 80 => exit @80.
        // gross = 100*(80-100) = -2,000 ; final equity = 1,000,000 - 2,000 = 998,000 (a 90 fill would have lost only 1,000).
        ScenarioRun run = VerificationScenarios.SellStopLossGap(VerificationScenarios.FreeConfig());
        BacktestTrade trade = Assert.Single(run.Result.Trades);
        Eq(100m, trade.EntryPrice, "EntryPrice");
        Eq(80m, trade.ExitPrice, "ExitPrice");
        Eq(-2_000m, trade.ClosedGross, "ClosedGross");
        Eq(998_000m, run.Result.EquityPoints[^1].Equity, "FinalEquity");
        AllInvariants(run.Input, run.Config, run.Result);
        TradePnLConservation(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void ShortCoverBuyStop110_NextOpenGapsTo120_CoversAtOpen120()
    {
        // Short 100 @100 ; Buy Stop @110 ; bar2 opens 120 => cover @120 ; realized = -100*(120-100) = -2,000.
        ScenarioRun run = VerificationScenarios.BuyStopCoverGap(VerificationScenarios.FreeConfig());
        BacktestTrade trade = Assert.Single(run.Result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Eq(120m, trade.ExitPrice, "ExitPrice");
        Eq(-2_000m, trade.ClosedGross, "ClosedGross");
        Eq(998_000m, run.Result.EquityPoints[^1].Equity, "FinalEquity");
        AllInvariants(run.Input, run.Config, run.Result);
        TradePnLConservation(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void BuyLimit100_NextOpenGapsDownTo90_FillsAtBetterOpen90()
    {
        // Contrast: a Limit is price-protected in the buyer's favor. Open 90 <= limit 100 => fill @90 (min(90,100)).
        ScenarioRun run = VerificationScenarios.BuyLimitEntry(VerificationScenarios.FreeConfig(), (90m, 95m, 88m, 92m));
        Eq(90m, Assert.Single(run.Result.Fills).Price, "fill price");
        AllInvariants(run.Input, run.Config, run.Result);
    }

    [Fact]
    public void BuyLimit100_PriceFallsThroughLimitInsideTheBar_FillsAtLimit()
    {
        // bar1 (102,103,99,101): trades down through 100 => fill @100.
        ScenarioRun run = VerificationScenarios.BuyLimitEntry(VerificationScenarios.FreeConfig(), (102m, 103m, 99m, 101m));
        Eq(100m, Assert.Single(run.Result.Fills).Price, "fill price");
        AllInvariants(run.Input, run.Config, run.Result);
    }
}
