using System;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Every trade-derived aggregate that the BasicMetrics trade metrics share, produced by ONE pass over <c>BacktestResult.Trades</c>.
/// It holds raw numbers only: the status / reason rules (K == 0, no wins, all breakeven, ...) stay in <see cref="BasicMetricsCalculator"/>.
/// Losses are accumulated as non-negative magnitudes, so <see cref="GrossProfit"/> / <see cref="GrossLoss"/> equals ProfitFactor.
/// A breakeven trade (ClosedNet == 0) counts towards <see cref="Breakevens"/> and resets both streaks.
/// </summary>
/// <param name="Count">K = Trades.Length.</param>
/// <param name="GrossOverflowed">A decimal overflow happened while summing <see cref="GrossProfit"/> or <see cref="GrossLoss"/>; both sums are then unusable.</param>
/// <param name="NetSumOverflowed">A decimal overflow happened while summing <see cref="NetSum"/>.</param>
internal readonly record struct TradeStatistics(
    int Count,
    int Wins,
    int Losses,
    int Breakevens,
    decimal GrossProfit,
    decimal GrossLoss,
    decimal LargestWin,
    decimal LargestLoss,
    decimal NetSum,
    long HoldingBarsSum,
    int MaxWinStreak,
    int MaxLossStreak,
    bool GrossOverflowed,
    bool NetSumOverflowed)
{
    /// <summary>
    /// Single pass, cancellation checked every <see cref="MetricCalculation.CheckCancellation"/> interval. An overflow never escapes: it is
    /// recorded per accumulator so that, exactly as when each metric scanned the trades on its own, only the metrics that depend on the
    /// overflowed sum fail (see <see cref="BasicMetricsCalculator"/>), while counts, extremes, holding bars and streaks stay valid.
    /// </summary>
    public static TradeStatistics Compute(ImmutableArray<BacktestTrade> trades, CancellationToken cancellationToken)
    {
        int wins = 0;
        int losses = 0;
        decimal grossProfit = 0m;
        decimal grossLoss = 0m;
        decimal largestWin = 0m;
        decimal largestLoss = 0m;
        decimal netSum = 0m;
        long holdingBarsSum = 0;
        int winStreak = 0;
        int maxWinStreak = 0;
        int lossStreak = 0;
        int maxLossStreak = 0;
        bool grossOverflowed = false;
        bool netSumOverflowed = false;

        for (int i = 0; i < trades.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            BacktestTrade trade = trades[i];
            decimal net = trade.ClosedNet;
            holdingBarsSum += trade.HoldingBars;
            AddOrFlag(ref netSum, net, ref netSumOverflowed);

            if (net > 0m)
            {
                wins++;
                AddOrFlag(ref grossProfit, net, ref grossOverflowed);
                if (net > largestWin) largestWin = net;
                if (++winStreak > maxWinStreak) maxWinStreak = winStreak;
                lossStreak = 0;
            }
            else if (net < 0m)
            {
                losses++;
                AddOrFlag(ref grossLoss, -net, ref grossOverflowed);
                if (-net > largestLoss) largestLoss = -net;
                if (++lossStreak > maxLossStreak) maxLossStreak = lossStreak;
                winStreak = 0;
            }
            else
            {
                winStreak = 0;
                lossStreak = 0;
            }
        }

        return new TradeStatistics(
            trades.Length, wins, losses, trades.Length - wins - losses,
            grossProfit, grossLoss, largestWin, largestLoss, netSum, holdingBarsSum,
            maxWinStreak, maxLossStreak, grossOverflowed, netSumOverflowed);
    }

    private static void AddOrFlag(ref decimal sum, decimal value, ref bool overflowed)
    {
        if (overflowed) return;
        try
        {
            sum += value;
        }
        catch (OverflowException)
        {
            overflowed = true;
        }
    }
}
