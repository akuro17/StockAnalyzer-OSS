namespace StockAnalyzer.Core.Models.Backtest
{
    public class BacktestStatistics
    {
        public int TotalTrades { get; set; }
        public double WinRate { get; set; }
        public double MaxDrawdown { get; set; }
        public double AverageProfit { get; set; }
        public double AverageLoss { get; set; }
        public double ProfitFactor { get; set; }
        public double TradeSharpeRatio { get; set; }
        public double TradeSortinoRatio { get; set; }
        public double TotalProfit { get; set; }

        /// <summary>Convention that produced the ratios. Absent in a serialized result = legacy V1, so it defaults to V1; the calculator always sets it.</summary>
        public TradeStatisticsVersion StatisticsVersion { get; set; } = TradeStatisticsVersion.V1PopulationStdDev;
    }
}
