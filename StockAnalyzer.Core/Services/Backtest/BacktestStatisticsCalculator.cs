using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest;

namespace StockAnalyzer.Core.Services.Backtest
{
    /// <summary>
    /// Trade-series statistics computed in pure C#. Money aggregates (sums, means, cumulative P/L, drawdown) use
    /// <see cref="decimal"/>; only the ratio outputs pass through <see cref="double"/>. Units: WinRate = ratio 0..1,
    /// MaxDrawdown/AverageProfit/AverageLoss/TotalProfit = currency, ProfitFactor/Sharpe/Sortino = dimensionless per-trade ratios.
    /// </summary>
    public class BacktestStatisticsCalculator
    {
        /// <summary>A population standard deviation at or below this is treated as zero (ratio defined as 0).</summary>
        public const double MinimumStandardDeviation = 1e-12;

        /// <summary>Convention used for new results (sample standard deviation). V1 stays selectable so legacy numbers can be reproduced.</summary>
        public const TradeStatisticsVersion CurrentVersion = TradeStatisticsVersion.V2SampleStdDev;

        /// <summary>
        /// Wins are trades with ProfitLoss &gt; 0; every other trade (including break-even) is a loss.
        /// ProfitFactor = |sum(wins) / sum(losses)| and is <see cref="double.PositiveInfinity"/> when the losses sum to zero.
        /// Sharpe = mean(P/L) / std(P/L); Sortino = mean(P/L) / std of the trades with P/L &lt; 0 (this project's per-trade definition,
        /// not the time-series downside deviation). std uses divisor N (<see cref="TradeStatisticsVersion.V1PopulationStdDev"/>, NumPy ddof=0)
        /// or N-1 (<see cref="TradeStatisticsVersion.V2SampleStdDev"/>, ddof=1); with fewer values than the divisor allows, std is 0 and the
        /// ratio is defined as 0. The result records the version. An empty trade list yields all zeros.
        /// </summary>
        public BacktestStatistics Calculate(IEnumerable<Trade> trades, TradeStatisticsVersion version = CurrentVersion)
        {
            ArgumentNullException.ThrowIfNull(trades);
            if (!Enum.IsDefined(version))
            {
                throw new ArgumentOutOfRangeException(nameof(version));
            }
            int degreesOfFreedom = version == TradeStatisticsVersion.V2SampleStdDev ? 1 : 0;

            var profitLoss = new List<decimal>();
            foreach (var trade in trades)
            {
                profitLoss.Add(trade.ProfitLoss);
            }

            if (profitLoss.Count == 0)
            {
                return new BacktestStatistics { StatisticsVersion = version };
            }

            decimal total = 0m;
            decimal cumulative = 0m;
            decimal peak = decimal.MinValue;
            decimal maxDrawdown = 0m;
            decimal winSum = 0m;
            decimal lossSum = 0m;
            int winCount = 0;
            int lossCount = 0;
            var negatives = new List<decimal>();

            foreach (var pnl in profitLoss)
            {
                total += pnl;
                cumulative += pnl;
                peak = Math.Max(peak, cumulative);
                maxDrawdown = Math.Max(maxDrawdown, peak - cumulative);

                if (pnl > 0m)
                {
                    winSum += pnl;
                    winCount++;
                }
                else
                {
                    lossSum += pnl;
                    lossCount++;
                    if (pnl < 0m)
                    {
                        negatives.Add(pnl);
                    }
                }
            }

            int count = profitLoss.Count;
            decimal mean = total / count;

            double profitFactor = lossCount > 0 && lossSum != 0m
                ? (double)Math.Abs(winSum / lossSum)
                : double.PositiveInfinity;

            return new BacktestStatistics
            {
                TotalTrades = count,
                WinRate = (double)winCount / count,
                MaxDrawdown = (double)maxDrawdown,
                AverageProfit = winCount > 0 ? (double)(winSum / winCount) : 0.0,
                AverageLoss = lossCount > 0 ? (double)(lossSum / lossCount) : 0.0,
                ProfitFactor = profitFactor,
                TradeSharpeRatio = Ratio(mean, StandardDeviation(profitLoss, degreesOfFreedom)),
                TradeSortinoRatio = Ratio(mean, StandardDeviation(negatives, degreesOfFreedom)),
                TotalProfit = (double)total,
                StatisticsVersion = version
            };
        }

        private static double Ratio(decimal mean, double standardDeviation)
            => standardDeviation > MinimumStandardDeviation ? (double)mean / standardDeviation : 0.0;

        /// <summary>Standard deviation with divisor N - <paramref name="degreesOfFreedom"/> (NumPy ddof); 0 when that divisor is not positive.</summary>
        private static double StandardDeviation(List<decimal> values, int degreesOfFreedom)
        {
            if (values.Count - degreesOfFreedom <= 0)
            {
                return 0.0;
            }

            decimal mean = 0m;
            foreach (var value in values)
            {
                mean += value;
            }
            mean /= values.Count;

            decimal squares = 0m;
            foreach (var value in values)
            {
                decimal deviation = value - mean;
                squares += deviation * deviation;
            }

            return Math.Sqrt((double)(squares / (values.Count - degreesOfFreedom)));
        }
    }
}
