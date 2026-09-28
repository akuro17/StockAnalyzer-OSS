using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Reusable invariant assertions. All money comparisons are exact decimal equality (no tolerance).
/// </summary>
internal static class LedgerAssertions
{
    public static void Eq(decimal expected, decimal actual, string what)
        => Assert.True(expected == actual, $"{what}: expected {expected} but was {actual}");

    /// <summary>Convenience: run every invariant that must hold for ANY backtest result.</summary>
    public static void AllInvariants(BacktestInput input, BacktestConfiguration config, BacktestResult result)
    {
        ChronologyAndShape(input, result);
        EquityMatchesLedger(input, config, result);
        PnLIdentityEveryBar(input, config, result);
        FillSanity(input, config, result);
    }

    /// <summary>Equity points cover bars 0..n-1 in order (fewer only on an early Insolvent stop), timestamps match the bars, flat means Equity == Cash.</summary>
    public static void ChronologyAndShape(BacktestInput input, BacktestResult result)
    {
        Assert.True(result.EquityPoints.Length <= input.Bars.Length, "More equity points than bars.");
        if (result.Status == RunStatus.Completed)
        {
            Assert.Equal(input.Bars.Length, result.EquityPoints.Length);
        }

        for (int k = 0; k < result.EquityPoints.Length; k++)
        {
            EquityPoint p = result.EquityPoints[k];
            Assert.Equal(k, p.BarIndex);
            Assert.Equal(input.Bars[k].Timestamp, p.Timestamp);
            Assert.True(p.HeldMargin >= 0m, $"HeldMargin negative at bar {k}: {p.HeldMargin}");
            if (p.MarketValue == 0m)
            {
                Assert.True(p.HeldMargin == 0m, $"Flat but HeldMargin != 0 at bar {k}");
                Assert.True(p.Equity == p.Cash, $"Flat but Equity != Cash at bar {k}");
            }
        }
    }

    /// <summary>Every recorded EquityPoint field equals the independent ledger's reconstruction.</summary>
    public static void EquityMatchesLedger(BacktestInput input, BacktestConfiguration config, BacktestResult result)
    {
        List<LedgerState> states = IndependentLedger.Replay(input, config, result);
        Assert.Equal(result.EquityPoints.Length, states.Count);
        for (int k = 0; k < states.Count; k++)
        {
            EquityPoint p = result.EquityPoints[k];
            LedgerState s = states[k];
            Eq(s.Cash, p.Cash, $"Cash@{k}");
            Eq(s.HeldMargin, p.HeldMargin, $"HeldMargin@{k}");
            Eq(s.MarketValue, p.MarketValue, $"MarketValue@{k}");
            Eq(s.Equity, p.Equity, $"Equity@{k}");
        }
    }

    /// <summary>
    /// "No money from nowhere", expressed through the TRADE list (independent of the fill replay): at every bar
    /// Cash + HeldMargin == Initial + Sum(ClosedNet of trades closed so far) - (entry fee of the open position), and
    /// Equity additionally adds the open position's unrealized P&amp;L.
    /// </summary>
    public static void PnLIdentityEveryBar(BacktestInput input, BacktestConfiguration config, BacktestResult result)
    {
        List<LedgerState> states = IndependentLedger.Replay(input, config, result);
        int tradeCursor = 0;
        decimal closedNet = 0m;
        for (int k = 0; k < result.EquityPoints.Length; k++)
        {
            EquityPoint p = result.EquityPoints[k];
            while (tradeCursor < result.Trades.Length && result.Trades[tradeCursor].ExitBar <= p.BarIndex)
            {
                closedNet += result.Trades[tradeCursor].ClosedNet;
                tradeCursor++;
            }

            LedgerState s = states[k];
            decimal expectedCashPlusMargin = config.InitialCapital + closedNet - s.OpenEntryFee;
            Eq(expectedCashPlusMargin, p.Cash + p.HeldMargin, $"Cash+HeldMargin identity@{k}");
            decimal unrealized = s.Quantity * (input.Bars[k].Close - s.EntryPrice);
            Eq(expectedCashPlusMargin + unrealized, p.Equity, $"Equity identity@{k}");
        }
    }

    /// <summary>Order/fill consistency and "no unrealistic fill" checks.</summary>
    public static void FillSanity(BacktestInput input, BacktestConfiguration config, BacktestResult result)
    {
        for (int i = 0; i < result.Orders.Length; i++)
        {
            Assert.Equal(i + 1L, result.Orders[i].OrderId);
        }

        long previousFillId = 0;
        foreach (BacktestFill fill in result.Fills)
        {
            Assert.Equal(previousFillId + 1, fill.FillId);
            previousFillId = fill.FillId;

            CandleData bar = input.Bars[fill.BarIndex];
            Assert.Equal(bar.Timestamp, fill.FillTime);

            const long ForcedLiquidationOrderId = 0L;
            if (fill.OrderId == ForcedLiquidationOrderId)
            {
                // A forced liquidation executes at the maintenance-margin price minus/plus a penalty, which may
                // legitimately lie outside the bar range - excluded from the range/order checks by design.
                continue;
            }

            BacktestOrder order = result.Orders[(int)fill.OrderId - 1];
            Assert.Equal(order.Quantity, fill.Quantity);
            Assert.Equal(order.Side, fill.Side);
            Assert.True(fill.BarIndex >= order.EarliestFillBar, $"Fill {fill.FillId} before EarliestFillBar.");
            Assert.True(fill.BarIndex > order.SubmittedBar, $"Fill {fill.FillId} not strictly after the submission bar.");

            decimal s = config.SlippageRatio;
            if (fill.Side == OrderSide.Buy)
            {
                Assert.True(fill.Price <= bar.High * (1m + s), $"Buy fill {fill.FillId} above High*(1+s).");
            }
            else
            {
                Assert.True(fill.Price >= bar.Low * (1m - s), $"Sell fill {fill.FillId} below Low*(1-s).");
            }

            if (order.Type == OrderType.MarketOnClose)
            {
                Eq(fill.Side == OrderSide.Buy ? bar.Close * (1m + s) : bar.Close * (1m - s), fill.Price, $"MOC fill {fill.FillId}");
            }
        }
    }

    /// <summary>
    /// Flat at the end: Sum(ClosedNet) == FinalEquity - InitialCapital exactly. With an open position at the end:
    /// FinalEquity - Initial == Sum(ClosedNet) + unrealized - openEntryFee.
    /// </summary>
    public static void TradePnLConservation(BacktestInput input, BacktestConfiguration config, BacktestResult result)
    {
        EquityPoint last = result.EquityPoints[^1];
        decimal sumNet = 0m;
        foreach (BacktestTrade t in result.Trades) sumNet += t.ClosedNet;

        List<LedgerState> states = IndependentLedger.Replay(input, config, result);
        LedgerState s = states[^1];
        decimal unrealized = s.Quantity * (input.Bars[last.BarIndex].Close - s.EntryPrice);
        Eq(sumNet + unrealized - s.OpenEntryFee, last.Equity - config.InitialCapital, "FinalEquity - Initial");
        if (s.Quantity == 0m)
        {
            Eq(sumNet, last.Equity - config.InitialCapital, "Sum(trade P&L) vs equity change (flat)");
        }
    }
}
