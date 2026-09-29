using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest;
using StockAnalyzer.Core.Services.Backtest;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest
{
    public class BacktestStatisticsCalculatorTests
    {
        private readonly BacktestStatisticsCalculator _calculator = new();

        private static List<Trade> Trades(params decimal[] profitLoss)
        {
            var trades = new List<Trade>();
            foreach (var pnl in profitLoss)
            {
                trades.Add(new Trade { EntryPrice = 100, ExitPrice = 100 + pnl, Quantity = 1, ProfitLoss = pnl });
            }
            return trades;
        }

        [Fact]
        public void Calculate_WithValidTrades_V1_ReturnsCorrectStatistics()
        {
            // Wins = 10, 15, 12 (sum 37). Losses = -5, -2 (sum -7). Total = 30.
            var stats = _calculator.Calculate(Trades(10, -5, 15, -2, 12), TradeStatisticsVersion.V1PopulationStdDev);
            Assert.Equal(TradeStatisticsVersion.V1PopulationStdDev, stats.StatisticsVersion);

            Assert.Equal(5, stats.TotalTrades);
            Assert.Equal(3.0 / 5.0, stats.WinRate, 12);
            Assert.Equal(30, stats.TotalProfit);
            Assert.Equal(37.0 / 3.0, stats.AverageProfit, 12);
            Assert.Equal(-7.0 / 2.0, stats.AverageLoss, 12);
            Assert.Equal(37.0 / 7.0, stats.ProfitFactor, 12);
            // Cumulative P/L: 10, 5, 20, 18, 30 -> the deepest drop below a running peak is 10 -> 5.
            Assert.Equal(5, stats.MaxDrawdown);
            // Population std of [10,-5,15,-2,12]: mean 6, variance (16+121+81+64+36)/5 = 63.6.
            Assert.Equal(6.0 / System.Math.Sqrt(63.6), stats.TradeSharpeRatio, 12);
            // Population std of the losing trades [-5,-2]: mean -3.5, variance 2.25 -> std 1.5.
            Assert.Equal(6.0 / 1.5, stats.TradeSortinoRatio, 12);
        }

        [Fact]
        public void Calculate_WithValidTrades_V2_UsesSampleStandardDeviation()
        {
            var stats = _calculator.Calculate(Trades(10, -5, 15, -2, 12), TradeStatisticsVersion.V2SampleStdDev);

            Assert.Equal(TradeStatisticsVersion.V2SampleStdDev, stats.StatisticsVersion);
            Assert.Equal(5, stats.TotalTrades);
            Assert.Equal(30, stats.TotalProfit);
            // Sample variance of [10,-5,15,-2,12]: 318 / 4 = 79.5. Sample variance of [-5,-2]: 4.5 / 1 = 4.5.
            Assert.Equal(6.0 / System.Math.Sqrt(79.5), stats.TradeSharpeRatio, 12);
            Assert.Equal(6.0 / System.Math.Sqrt(4.5), stats.TradeSortinoRatio, 12);
        }

        [Fact]
        public void Calculate_DefaultVersion_IsTheCurrentVersion()
        {
            var trades = Trades(10, -5, 15, -2, 12);

            var byDefault = _calculator.Calculate(trades);
            var explicitCurrent = _calculator.Calculate(trades, BacktestStatisticsCalculator.CurrentVersion);

            Assert.Equal(TradeStatisticsVersion.V2SampleStdDev, BacktestStatisticsCalculator.CurrentVersion);
            Assert.Equal(explicitCurrent.StatisticsVersion, byDefault.StatisticsVersion);
            Assert.Equal(explicitCurrent.TradeSharpeRatio, byDefault.TradeSharpeRatio);
            Assert.Equal(explicitCurrent.TradeSortinoRatio, byDefault.TradeSortinoRatio);
        }

        [Fact]
        public void Calculate_V2_WithSingleTradeOrSingleLoss_DefinesRatiosAsZero()
        {
            var single = _calculator.Calculate(Trades(7), TradeStatisticsVersion.V2SampleStdDev);
            var oneLoss = _calculator.Calculate(Trades(10, -4, 6), TradeStatisticsVersion.V2SampleStdDev);

            Assert.Equal(0.0, single.TradeSharpeRatio);
            Assert.Equal(0.0, oneLoss.TradeSortinoRatio);
            Assert.True(oneLoss.TradeSharpeRatio > 0.0);
        }

        [Fact]
        public void Calculate_V1_WithSingleTrade_KeepsLegacyZeroBecauseStdIsZero()
        {
            Assert.Equal(0.0, _calculator.Calculate(Trades(7), TradeStatisticsVersion.V1PopulationStdDev).TradeSharpeRatio);
        }

        [Fact]
        public void Calculate_UndefinedVersion_Throws()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _calculator.Calculate(Trades(1), (TradeStatisticsVersion)99));
        }

        [Fact]
        public void BacktestStatistics_WithoutRecordedVersion_IsLegacyV1()
        {
            Assert.Equal(TradeStatisticsVersion.V1PopulationStdDev, new BacktestStatistics().StatisticsVersion);
        }

        [Fact]
        public void Calculate_WithNoTrades_ReturnsAllZeros()
        {
            var stats = _calculator.Calculate(new List<Trade>());

            Assert.Equal(0, stats.TotalTrades);
            Assert.Equal(0.0, stats.WinRate);
            Assert.Equal(0.0, stats.MaxDrawdown);
            Assert.Equal(0.0, stats.AverageProfit);
            Assert.Equal(0.0, stats.AverageLoss);
            Assert.Equal(0.0, stats.ProfitFactor);
            Assert.Equal(0.0, stats.TradeSharpeRatio);
            Assert.Equal(0.0, stats.TradeSortinoRatio);
            Assert.Equal(0.0, stats.TotalProfit);
            Assert.Equal(BacktestStatisticsCalculator.CurrentVersion, stats.StatisticsVersion);
        }

        [Fact]
        public void Calculate_WithOnlyWinningTrades_HasInfiniteProfitFactorAndZeroLossMetrics()
        {
            var stats = _calculator.Calculate(Trades(5, 10));

            Assert.True(double.IsPositiveInfinity(stats.ProfitFactor));
            Assert.Equal(0.0, stats.AverageLoss);
            Assert.Equal(0.0, stats.TradeSortinoRatio);
            Assert.Equal(1.0, stats.WinRate);
        }

        [Fact]
        public void Calculate_WithIdenticalTrades_HasZeroSharpe()
        {
            var stats = _calculator.Calculate(Trades(0.1m, 0.1m, 0.1m));

            Assert.Equal(0.0, stats.TradeSharpeRatio);
        }

        [Fact]
        public void Calculate_BreakevenTradeCountsAsLoss()
        {
            var stats = _calculator.Calculate(Trades(10, 0));

            Assert.Equal(0.5, stats.WinRate);
            Assert.Equal(0.0, stats.AverageLoss);
            Assert.True(double.IsPositiveInfinity(stats.ProfitFactor));
        }

        [Fact]
        public void Calculate_NullTrades_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => _calculator.Calculate(null!));
        }
    }
}
