using System;

namespace StockAnalyzer.Core.Models.Backtest
{
    public class Trade
    {
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal ProfitLoss { get; set; }
    }
}
