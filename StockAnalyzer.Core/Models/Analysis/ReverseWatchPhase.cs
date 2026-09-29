namespace StockAnalyzer.Core.Models.Analysis;

public enum ReverseWatchPhase
{
    None,
    Phase1, // South: Bull Reversal (陽転) - Price Low, Vol Avg
    Phase2, // SE: Buy Signal (買い信号) - Price Low, Vol High
    Phase3, // East: Strong Buy (買い乗せ) - Price Avg, Vol High
    Phase4, // NE: Caution (天井警戒) - Price High, Vol High
    Phase5, // North: Bear Reversal (陰転) - Price High, Vol Avg
    Phase6, // NW: Sell Signal (売り信号) - Price High, Vol Low
    Phase7, // West: Strong Sell (売り乗せ) - Price Avg, Vol Low
    Phase8  // SW: Bottoming (底入れ) - Price Low, Vol Low
}
