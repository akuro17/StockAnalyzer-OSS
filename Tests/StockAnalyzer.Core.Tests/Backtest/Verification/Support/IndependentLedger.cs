using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>Account state reconstructed for one recorded bar.</summary>
internal readonly record struct LedgerState(
    int BarIndex, decimal Quantity, decimal EntryPrice, decimal Cash, decimal HeldMargin,
    decimal MarketValue, decimal Equity, decimal OpenEntryFee);

/// <summary>
/// Replays <see cref="BacktestResult.Fills"/> in FillId order against the input bars with its OWN arithmetic (written from
/// the margin-account model table of the plan, not by calling any engine internals) and yields the account state per bar.
///
/// Limitation (by design): the ledger shares the margin-account specification with the engine, so it cannot detect a wrong
/// SPECIFICATION. The hand-calculated scenarios (L1_SimplePnLTests, L1_PositionReversalTests, ...) are the independent anchor;
/// this ledger proves the engine's per-bar bookkeeping is internally consistent for arbitrary (fuzzed) inputs.
///
/// Model: a fill while flat is an entry (margin = qty*price*InitialMarginRatio, Cash -= margin + fee); a fill while holding is a
/// full close (Cash += HeldMargin + Q*(price-entry) - fee). Equity = Cash + HeldMargin + Q*(Close-entry).
/// </summary>
internal static class IndependentLedger
{
    public static List<LedgerState> Replay(BacktestInput input, BacktestConfiguration config, BacktestResult result)
    {
        var states = new List<LedgerState>(result.EquityPoints.Length);
        decimal cash = config.InitialCapital;
        decimal held = 0m;
        decimal quantity = 0m;
        decimal entryPrice = 0m;
        decimal openEntryFee = 0m;
        int cursor = 0;

        foreach (EquityPoint point in result.EquityPoints)
        {
            while (cursor < result.Fills.Length && result.Fills[cursor].BarIndex <= point.BarIndex)
            {
                BacktestFill fill = result.Fills[cursor++];
                if (quantity == 0m)
                {
                    decimal margin = fill.Quantity * fill.Price * config.InitialMarginRatio;
                    cash -= margin + fill.Commission;
                    held = margin;
                    quantity = fill.Side == OrderSide.Buy ? fill.Quantity : -fill.Quantity;
                    entryPrice = fill.Price;
                    openEntryFee = fill.Commission;
                }
                else
                {
                    Assert.True(Math.Abs(quantity) == fill.Quantity,
                        $"Fill {fill.FillId} closes {fill.Quantity} but the ledger holds {quantity} (exit must be full-quantity).");
                    decimal realized = quantity * (fill.Price - entryPrice);
                    cash += held + realized - fill.Commission;
                    held = 0m;
                    quantity = 0m;
                    entryPrice = 0m;
                    openEntryFee = 0m;
                }
            }

            CandleData bar = input.Bars[point.BarIndex];
            decimal marketValue = quantity * bar.Close;
            decimal equity = cash + held + (quantity * (bar.Close - entryPrice));
            states.Add(new LedgerState(point.BarIndex, quantity, entryPrice, cash, held, marketValue, equity, openEntryFee));
        }

        return states;
    }
}
